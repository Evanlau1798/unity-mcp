"""The shared mutation helper must observe refresh ownership before dispatch."""
import asyncio
from types import SimpleNamespace

import pytest

import services.tools.preflight as preflight_mod
import services.tools.refresh_unity as refresh_mod
from .test_helpers import DummyContext


def editor_state(*, refreshing=False, dirty=False):
    return {"success": True, "data": {
        "assets": {"external_changes_dirty": dirty,
                   "refresh": {"is_refresh_in_progress": refreshing}},
        "compilation": {"is_compiling": False, "is_domain_reload_pending": False},
        "tests": {"is_running": False},
        "staleness": {"is_stale": False},
        "advice": {"ready_for_tools": not refreshing,
                   "blocking_reasons": ["asset_refresh"] if refreshing else []},
    }}


@pytest.fixture
def real_guards(monkeypatch):
    monkeypatch.setattr(preflight_mod, "_in_pytest", lambda: False)
    monkeypatch.setattr(refresh_mod, "_in_pytest", lambda: False)


@pytest.mark.asyncio
async def test_pending_receipt_blocks_first_mutation_and_verification(real_guards, monkeypatch):
    sent, verified = [], []

    async def state(_ctx):
        return editor_state(refreshing=True)

    async def send(*_args, **_kwargs):
        sent.append(True)
        return {"success": False, "error": "connection closed after send"}

    async def verify():
        verified.append(True)
        return {"success": True}

    monkeypatch.setattr(refresh_mod.editor_state, "get_editor_state", state)
    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", send)
    result = await refresh_mod.send_mutation(
        DummyContext(), "Unity@mutation", "manage_script", {"action": "update"},
        verify_after_disconnect=verify,
    )
    assert sent == []
    assert verified == []
    assert result["success"] is False
    assert result["data"]["reason"] == "refresh_pending"


@pytest.mark.asyncio
async def test_wait_reads_past_asset_refresh_before_reporting_ready(real_guards, monkeypatch):
    reads = []

    async def state(_ctx):
        reads.append(True)
        return editor_state(refreshing=len(reads) == 1)

    async def immediate_sleep(_delay):
        return None

    monkeypatch.setattr(refresh_mod.editor_state, "get_editor_state", state)
    monkeypatch.setattr(refresh_mod.asyncio, "sleep", immediate_sleep)
    ready, _elapsed = await refresh_mod.wait_for_editor_ready(DummyContext(), timeout_s=0.1)
    assert ready is True
    assert len(reads) == 2


@pytest.mark.asyncio
async def test_persistent_refresh_timeout_does_not_resend_rejected_mutation(real_guards, monkeypatch):
    sent, verified = [], []
    now = [0.0]
    original_sleep = asyncio.sleep
    rejection = {"success": False, "error": "Unity is reloading; please retry", "hint": "retry",
                 "data": {"reason": "reloading"}}

    async def state(_ctx):
        return editor_state(refreshing=bool(sent))

    async def send(*_args, **_kwargs):
        sent.append(True)
        return rejection

    async def verify():
        verified.append(True)
        return {"success": True}

    async def advance(delay):
        now[0] += delay
        await original_sleep(0)

    monkeypatch.setattr(refresh_mod, "time", SimpleNamespace(monotonic=lambda: now[0]))
    monkeypatch.setattr(refresh_mod.asyncio, "sleep", advance)
    monkeypatch.setattr(refresh_mod.editor_state, "get_editor_state", state)
    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", send)
    result = await refresh_mod.send_mutation(
        DummyContext(), "Unity@mutation", "manage_script", {"action": "update"},
        verify_after_disconnect=verify,
    )
    assert sent == [True]
    assert verified == []
    assert result == rejection
    assert now[0] >= 30.0


@pytest.mark.asyncio
async def test_dirty_batch_mutation_does_not_force_a_refresh(real_guards, monkeypatch):
    commands = []

    async def state(_ctx):
        return editor_state(dirty=True)

    async def send(_fn, _instance, command, _params, **_kwargs):
        commands.append(command)
        return {"success": True, "message": "mutation applied"}

    monkeypatch.setattr(refresh_mod.editor_state, "get_editor_state", state)
    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", send)
    result = await refresh_mod.send_mutation(
        DummyContext(), "Unity@mutation", "manage_script", {"action": "update"},
    )
    assert result["success"] is True
    assert commands == ["manage_script"]


@pytest.mark.asyncio
async def test_disconnected_mutation_is_verified_without_duplicate_dispatch(real_guards, monkeypatch):
    sent, verified = [], []

    async def state(_ctx):
        return editor_state()

    async def send(*_args, **_kwargs):
        sent.append(True)
        return {"success": False, "error": "connection closed after send"}

    async def verify():
        verified.append(True)
        return {"success": True, "message": "verified original mutation"}

    monkeypatch.setattr(refresh_mod.editor_state, "get_editor_state", state)
    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", send)
    result = await refresh_mod.send_mutation(
        DummyContext(), "Unity@mutation", "manage_script", {"action": "update"},
        verify_after_disconnect=verify,
    )
    assert sent == [True]
    assert verified == [True]
    assert result == {"success": True, "message": "verified original mutation"}
