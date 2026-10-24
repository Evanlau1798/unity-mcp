"""Regression checks for orphaned command futures during WebSocket send failures."""
import asyncio
import gc
from unittest.mock import AsyncMock

import pytest
from starlette.websockets import WebSocketDisconnect

from transport.plugin_hub import PluginDisconnectedError, PluginHub


@pytest.fixture
def isolated_hub(monkeypatch):
    monkeypatch.setattr(PluginHub, "_pending", {})
    monkeypatch.setattr(PluginHub, "_lock", asyncio.Lock())
    monkeypatch.setattr(PluginHub, "COMMAND_TIMEOUT", 1.0)


@pytest.mark.asyncio
@pytest.mark.parametrize("disconnect_during_send", [False, True])
async def test_websocket_send_failure_does_not_leave_orphaned_future(
    isolated_hub, monkeypatch, disconnect_during_send,
):
    event_loop = asyncio.get_running_loop()
    messages = []
    previous_handler = event_loop.get_exception_handler()
    event_loop.set_exception_handler(lambda _loop, ctx: messages.append(ctx))
    websocket = AsyncMock()

    async def failed_send(_payload):
        if disconnect_during_send:
            # The disconnect cleanup can race with the send_json failure
            # and complete the very same future before the sender awaits it.
            for entry in list(PluginHub._pending.values()):
                future = entry["future"]
                if not future.done():
                    future.set_exception(PluginDisconnectedError("plugin disconnected"))
        raise WebSocketDisconnect(code=1006)

    websocket.send_json.side_effect = failed_send
    monkeypatch.setattr(PluginHub, "_get_connection", AsyncMock(return_value=websocket))
    try:
        with pytest.raises(WebSocketDisconnect):
            await PluginHub.send_command("dead-session", "get_test_job", {"job_id": "job-1"})
        assert PluginHub._pending == {}
        gc.collect()
        await asyncio.sleep(0)
        assert not any("Future exception was never retrieved" in c.get("message", "") for c in messages)
        websocket.send_json.assert_awaited_once()
    finally:
        event_loop.set_exception_handler(previous_handler)


@pytest.mark.asyncio
async def test_send_cancellation_leaves_no_pending_command(isolated_hub, monkeypatch):
    websocket = AsyncMock()
    entered = asyncio.Event()

    async def pending_send(_payload):
        entered.set()
        await asyncio.Future()

    websocket.send_json.side_effect = pending_send
    monkeypatch.setattr(PluginHub, "_get_connection", AsyncMock(return_value=websocket))
    task = asyncio.create_task(PluginHub.send_command("session", "get_test_job", {"job_id": "job-1"}))
    await asyncio.wait_for(entered.wait(), timeout=1)
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    assert PluginHub._pending == {}
