"""Refresh preflight must protect dispatch until the requested work is complete."""
from unittest.mock import AsyncMock

import pytest

from models import MCPResponse
import services.resources.editor_state as state_mod
import services.tools.preflight as preflight_mod
import services.tools.refresh_unity as refresh_mod
from .test_helpers import DummyContext


def state(*, dirty=False, compiling=False, running=False, stale=False, refreshing=False):
    return {
        "success": True,
        "data": {
            "assets": {"external_changes_dirty": dirty, "refresh": {"is_refresh_in_progress": refreshing}},
            "compilation": {
                "is_compiling": compiling,
                "is_domain_reload_pending": False,
            },
            "tests": {"is_running": running},
            "staleness": {"is_stale": stale},
        },
    }


@pytest.fixture
def guards(monkeypatch):
    monkeypatch.setattr(preflight_mod, "_in_pytest", lambda: False)
    get_state = AsyncMock(return_value=state(dirty=True))
    refresh = AsyncMock(return_value=MCPResponse(
        success=True, data={"completed": True, "operation_id": "refresh-A"},
    ))
    monkeypatch.setattr(state_mod, "get_editor_state", get_state)
    monkeypatch.setattr(refresh_mod, "refresh_unity", refresh)
    return get_state, refresh


@pytest.mark.asyncio
async def test_failed_refresh_is_returned_without_allowing_dispatch(guards):
    _, refresh = guards
    refresh.return_value = MCPResponse(
        success=False, error="compile_failed",
        data={"operation_id": "refresh-A", "completed": True},
    )
    result = await preflight_mod.preflight(DummyContext(), refresh_if_dirty=True)
    assert result is not None
    assert result.success is False
    assert result.error == "compile_failed"
    assert result.data["operation_id"] == "refresh-A"


@pytest.mark.asyncio
async def test_accepted_refresh_does_not_allow_dispatch(guards):
    _, refresh = guards
    refresh.return_value = MCPResponse(
        success=True, data={"operation_id": "refresh-A", "completed": False},
    )
    result = await preflight_mod.preflight(DummyContext(), refresh_if_dirty=True)
    assert result is not None
    assert result.success is False
    assert result.data["reason"] == "refresh_pending"


@pytest.mark.asyncio
async def test_refresh_exception_is_not_best_effort_success(guards):
    _, refresh = guards
    refresh.side_effect = RuntimeError("transport unavailable")
    result = await preflight_mod.preflight(DummyContext(), refresh_if_dirty=True)
    assert result is not None
    assert result.success is False
    assert result.data["reason"] == "refresh_unconfirmed"


@pytest.mark.asyncio
async def test_fresh_state_after_refresh_is_used_for_compile_guard(guards):
    get_state, _ = guards
    get_state.side_effect = [state(dirty=True), state(compiling=True)]
    result = await preflight_mod.preflight(
        DummyContext(), refresh_if_dirty=True, wait_for_no_compile=True, max_wait_s=0,
    )
    assert result is not None
    assert result.data["reason"] == "compiling"
    assert get_state.await_count == 2


@pytest.mark.asyncio
async def test_new_dirty_generation_is_not_dispatched_or_refreshed_again(guards):
    get_state, refresh = guards
    get_state.side_effect = [state(dirty=True), state(dirty=True)]
    result = await preflight_mod.preflight(DummyContext(), refresh_if_dirty=True)
    assert result is not None
    assert result.data["reason"] == "external_changes_pending"
    assert refresh.await_count == 1


@pytest.mark.asyncio
@pytest.mark.parametrize("fresh", [
    {"success": False, "error": "disconnected"},
    state(stale=True),
])
async def test_unavailable_or_stale_post_refresh_state_blocks(guards, fresh):
    get_state, _ = guards
    get_state.side_effect = [state(dirty=True), fresh]
    result = await preflight_mod.preflight(DummyContext(), refresh_if_dirty=True)
    assert result is not None
    assert result.data["reason"] == "editor_state_unconfirmed"


@pytest.mark.asyncio
async def test_refresh_in_progress_reattaches_and_preserves_pending_receipt(guards):
    get_state, refresh = guards
    get_state.return_value = state(dirty=True, refreshing=True)
    refresh.return_value = MCPResponse(
        success=True, data={"completed": False, "operation_id": "refresh-A"},
    )
    result = await preflight_mod.preflight(DummyContext(), refresh_if_dirty=True)
    assert result.success is False
    assert result.data["reason"] == "refresh_pending"
    assert result.data["refresh"]["data"]["operation_id"] == "refresh-A"
    refresh.assert_awaited_once()


@pytest.mark.asyncio
@pytest.mark.parametrize("dirty_scopes,expected_scope,expected_compile", [
    (("assets",), "assets", "none"),
    (("scripts",), "scripts", "request"),
    (("assets", "scripts"), "all", "request"),
])
async def test_preflight_refreshes_only_observed_dirty_scopes(
    guards, dirty_scopes, expected_scope, expected_compile,
):
    get_state, refresh = guards
    before = state(dirty=True)
    before["data"]["assets"].update({
        "external_changes_scan_complete": True,
        "external_changes_generations": {
            name: int(name in dirty_scopes) for name in ("assets", "scripts")
        },
        "external_changes_acknowledged_generations": {"assets": 0, "scripts": 0},
    })
    get_state.side_effect = [before, state(dirty=False)]
    result = await preflight_mod.preflight(DummyContext(), refresh_if_dirty=True)
    assert result is None
    refresh.assert_awaited_once()
    assert refresh.await_args.kwargs["scope"] == expected_scope
    assert refresh.await_args.kwargs["compile"] == expected_compile


@pytest.mark.asyncio
async def test_unknown_scan_uses_conservative_full_refresh(guards):
    get_state, refresh = guards
    before = state(dirty=True)
    before["data"]["assets"].update({
        "external_changes_scan_complete": False,
        "external_changes_generations": {"assets": 2, "scripts": 0},
        "external_changes_acknowledged_generations": {"assets": 0, "scripts": 0},
    })
    get_state.side_effect = [before, state(dirty=False)]
    assert await preflight_mod.preflight(DummyContext(), refresh_if_dirty=True) is None
    assert refresh.await_args.kwargs["scope"] == "all"
    assert refresh.await_args.kwargs["compile"] == "request"


@pytest.mark.asyncio
async def test_post_refresh_new_assets_only_generation_is_acked_before_test_admission(guards):
    state_read, refresh = guards

    initial = state(dirty=True)
    initial["data"]["assets"].update({
        "external_changes_scan_complete": True,
        "external_changes_generations": {"scripts": 1, "assets": 1},
        "external_changes_acknowledged_generations": {"scripts": 0, "assets": 0},
    })
    # First full refresh ACKed scripts, but a *new* assets generation arrived.
    assets_only = state(dirty=True)
    assets_only["data"]["assets"].update({
        "external_changes_scan_complete": True,
        "external_changes_generations": {"scripts": 1, "assets": 2},
        "external_changes_acknowledged_generations": {"scripts": 1, "assets": 1},
    })
    clean = state()
    clean["data"]["assets"].update({
        "external_changes_scan_complete": True,
        "external_changes_generations": {"scripts": 1, "assets": 2},
        "external_changes_acknowledged_generations": {"scripts": 1, "assets": 2},
    })
    state_read.side_effect = [initial, assets_only, clean]
    refresh.side_effect = [
        MCPResponse(success=True, data={"completed": True, "status": "succeeded", "operation_id": "refresh-1", "dirty_snapshot_acknowledged": True}),
        MCPResponse(success=True, data={"completed": True, "status": "succeeded", "operation_id": "refresh-2", "dirty_snapshot_acknowledged": True}),
    ]
    result = await preflight_mod.preflight(
        DummyContext(), refresh_if_dirty=True, requires_no_tests=True, wait_for_no_compile=True,
    )
    assert result is None
    assert state_read.await_count == 3
    assert refresh.await_count == 2
    assert (refresh.await_args_list[0].kwargs["scope"], refresh.await_args_list[0].kwargs["compile"]) == ("all", "request")
    assert (refresh.await_args_list[1].kwargs["scope"], refresh.await_args_list[1].kwargs["compile"]) == ("assets", "none")


@pytest.mark.asyncio
@pytest.mark.parametrize("dirty_scripts,scan_complete,compiling", [
    (True, True, False),
    (False, False, False),
    (False, True, True),
])
async def test_post_refresh_does_not_auto_refresh_if_not_safe_assets_only(
    guards, dirty_scripts, scan_complete, compiling,
):
    state_read, refresh = guards
    new = state(dirty=True, compiling=compiling)
    new["data"]["assets"].update({
        "external_changes_scan_complete": scan_complete,
        "external_changes_generations": {"scripts": 2 if dirty_scripts else 1, "assets": 2},
        "external_changes_acknowledged_generations": {"scripts": 1, "assets": 1},
    })
    state_read.side_effect = [state(dirty=True), new]
    result = await preflight_mod.preflight(
        DummyContext(), refresh_if_dirty=True, requires_no_tests=True, wait_for_no_compile=True,
    )
    assert result is not None
    assert result.success is False
    assert refresh.await_count == 1

@pytest.mark.asyncio
@pytest.mark.parametrize("catchup_case,expected_reason", [
    ("unacknowledged", "external_changes_pending"),
    ("pending", "refresh_pending"),
    ("new_dirty", "external_changes_pending"),
    ("stale_state", "editor_state_unconfirmed"),
])
async def test_asset_only_catchup_never_treats_uncertain_receipt_or_new_generation_as_ready(
    guards, catchup_case, expected_reason,
):
    get_state, refresh = guards
    new_assets = state(dirty=True)
    new_assets["data"]["assets"].update({
        "external_changes_scan_complete": True,
        "external_changes_generations": {"scripts": 1, "assets": 2},
        "external_changes_acknowledged_generations": {"scripts": 1, "assets": 1},
    })
    remaining = new_assets if catchup_case == "new_dirty" else state(stale=catchup_case == "stale_state")
    get_state.side_effect = [state(dirty=True), new_assets, remaining]
    second_receipt = {"completed": True, "status": "succeeded", "operation_id": "refresh-2",
                      "dirty_snapshot_acknowledged": catchup_case != "unacknowledged"}
    if catchup_case == "pending":
        second_receipt.update(completed=False, status="accepted")
    refresh.side_effect = [
        MCPResponse(success=True, data={"completed": True, "status": "succeeded", "operation_id": "refresh-1",
                                        "dirty_snapshot_acknowledged": True}),
        MCPResponse(success=True, data=second_receipt),
    ]
    blocked = await preflight_mod.preflight(
        DummyContext(), refresh_if_dirty=True, requires_no_tests=True,
    )
    assert blocked.success is False
    assert blocked.data["reason"] == expected_reason
    assert refresh.await_count == 2


@pytest.mark.asyncio
async def test_unacknowledged_initial_refresh_does_not_start_assets_catchup(guards):
    get_state, refresh = guards
    dirty = state(dirty=True)
    dirty["data"]["assets"].update({
        "external_changes_scan_complete": True,
        "external_changes_generations": {"scripts": 1, "assets": 2},
        "external_changes_acknowledged_generations": {"scripts": 1, "assets": 1},
    })
    get_state.side_effect = [state(dirty=True), dirty]
    refresh.return_value = MCPResponse(
        success=True, data={"completed": True, "status": "succeeded",
                            "operation_id": "refresh-1", "dirty_snapshot_acknowledged": False},
    )
    blocked = await preflight_mod.preflight(DummyContext(), refresh_if_dirty=True, requires_no_tests=True)
    assert blocked.success is False
    assert blocked.data["reason"] == "external_changes_pending"
    assert refresh.await_count == 1


@pytest.mark.asyncio
async def test_existing_test_owner_is_checked_before_refresh(guards):
    get_state, refresh = guards
    get_state.return_value = state(dirty=True, running=True)
    result = await preflight_mod.preflight(
        DummyContext(), refresh_if_dirty=True, requires_no_tests=True,
    )
    assert result is not None
    assert result.data["reason"] == "tests_running"
    refresh.assert_not_awaited()


@pytest.mark.asyncio
async def test_completed_refresh_and_fresh_ready_state_allows_dispatch(guards):
    get_state, refresh = guards
    get_state.side_effect = [state(dirty=True), state()]
    result = await preflight_mod.preflight(
        DummyContext(), refresh_if_dirty=True, requires_no_tests=True,
        wait_for_no_compile=True,
    )
    assert result is None
    assert get_state.await_count == 2
    assert refresh.await_count == 1

@pytest.mark.asyncio
async def test_pending_refresh_is_reattached_even_without_external_dirty(guards):
    get_state, refresh = guards
    get_state.side_effect = [state(refreshing=True), state()]
    result = await preflight_mod.preflight(DummyContext(), refresh_if_dirty=True)
    assert result is None
    assert refresh.await_count == 1
    assert get_state.await_count == 2


@pytest.mark.asyncio
async def test_pending_refresh_blocks_guard_that_does_not_request_refresh(guards):
    get_state, refresh = guards
    get_state.return_value = state(refreshing=True)
    result = await preflight_mod.preflight(DummyContext(), wait_for_no_compile=True)
    assert result is not None
    assert result.data["reason"] == "refresh_pending"
    refresh.assert_not_awaited()

@pytest.mark.asyncio
@pytest.mark.parametrize("unconfirmed", [
    None, {"success": False, "error": "disconnected"},
    {"success": True, "data": None}, state(stale=True), RuntimeError("state unavailable"),
])
async def test_initial_unconfirmed_state_does_not_authorize_mutation(guards, unconfirmed):
    get_state, refresh = guards
    if isinstance(unconfirmed, Exception):
        get_state.side_effect = unconfirmed
    else:
        get_state.return_value = unconfirmed
    result = await preflight_mod.preflight(
        DummyContext(), refresh_if_dirty=True, requires_no_tests=True, wait_for_no_compile=True,
    )
    assert result is not None
    assert result.data["reason"] == "editor_state_unconfirmed"
    refresh.assert_not_awaited()


@pytest.mark.asyncio
@pytest.mark.parametrize("after_refresh", [False, True])
async def test_asset_import_blocks_dispatch(guards, after_refresh):
    get_state, _ = guards
    importing = state()
    importing["data"]["assets"]["is_updating"] = True
    get_state.side_effect = [state(dirty=True), importing] if after_refresh else [importing]
    result = await preflight_mod.preflight(
        DummyContext(), refresh_if_dirty=after_refresh, wait_for_no_compile=True,
    )
    assert result is not None
    assert result.data["reason"] == "asset_import"


@pytest.mark.asyncio
async def test_refresh_starting_during_compile_wait_still_blocks(guards):
    get_state, _ = guards
    get_state.side_effect = [state(compiling=True), state(refreshing=True)]
    result = await preflight_mod.preflight(DummyContext(), wait_for_no_compile=True, max_wait_s=1)
    assert result is not None
    assert result.data["reason"] == "refresh_pending"
