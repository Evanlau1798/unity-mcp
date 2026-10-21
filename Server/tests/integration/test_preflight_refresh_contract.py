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
