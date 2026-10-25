"""Separate transport/result/readiness deadlines without replaying mutations."""
import asyncio
import time
from unittest.mock import AsyncMock

import pytest
from transport.plugin_hub import PluginHub


@pytest.mark.asyncio
async def test_middleware_does_not_spend_transport_reconnect_budget(monkeypatch):
    from types import SimpleNamespace
    from fastmcp import Context, FastMCP
    from core.config import config
    from transport.unity_instance_middleware import UnityInstanceMiddleware
    class RequestContext(Context):
        @property
        def session_id(self):
            return "a"

    mcp = FastMCP("no-double-resolve")
    ctx = RequestContext(mcp)
    mw = UnityInstanceMiddleware()
    await mw.set_active_instance(ctx, "A@aaa")
    monkeypatch.setattr(config, "transport_mode", "http")
    monkeypatch.setattr(config, "http_remote_hosted", False)
    monkeypatch.setattr(PluginHub, "is_configured", lambda: True)
    resolve = AsyncMock(return_value="old-socket")
    monkeypatch.setattr(PluginHub, "_resolve_session_id", resolve)
    await mw._inject_unity_instance(SimpleNamespace(fastmcp_context=ctx, message=SimpleNamespace()))
    resolve.assert_not_awaited()


@pytest.mark.asyncio
async def test_readiness_probe_cannot_outlive_its_remaining_budget(monkeypatch):
    monkeypatch.setattr(PluginHub, "_resolve_session_id", AsyncMock(return_value="a"))
    monkeypatch.setattr(PluginHub, "_ensure_live_connection", AsyncMock(return_value=True))
    monkeypatch.setenv("UNITY_MCP_SESSION_READY_WAIT_SECONDS", "0.04")

    async def slow_ping(*_):
        await asyncio.sleep(0.25)
        return {"success": False}

    send = AsyncMock(side_effect=slow_ping)
    monkeypatch.setattr(PluginHub, "send_command", send)
    start = time.monotonic()
    result = await PluginHub.send_command_for_instance("A@aaa", "get_editor_state", {})
    assert time.monotonic() - start < 0.18
    assert result["data"]["phase"] == "editor_readiness"
    assert all(call.args[1] == "ping" for call in send.await_args_list)


@pytest.mark.asyncio
@pytest.mark.parametrize("phase", ["transport_send", "command_response"])
async def test_command_deadline_covers_send_and_response_and_never_replays(monkeypatch, phase):
    monkeypatch.setattr(PluginHub, "COMMAND_TIMEOUT", 0.04)
    monkeypatch.setattr(PluginHub, "_lock", asyncio.Lock())
    monkeypatch.setattr(PluginHub, "_pending", {})

    async def send_json(_):
        if phase == "transport_send":
            await asyncio.sleep(0.25)

    socket = AsyncMock()
    socket.send_json.side_effect = send_json
    monkeypatch.setattr(PluginHub, "_get_connection", AsyncMock(return_value=socket))
    start = time.monotonic()
    try:
        result = await PluginHub.send_command("a", "mutation", {})
    except TimeoutError as exc:
        diagnostics = getattr(exc, "diagnostics", {})
    else:
        diagnostics = result.get("data", {})
    assert time.monotonic() - start < 0.18
    assert diagnostics["phase"] == phase
    assert diagnostics["execution_may_have_started"] is True
    assert PluginHub._pending == {}
    socket.send_json.assert_awaited_once()


@pytest.mark.asyncio
@pytest.mark.parametrize("deadline", [False, True])
async def test_tool_transport_preserves_uncertain_mutation_outcome(monkeypatch, deadline):
    from core.config import config
    from transport.unity_transport import send_with_unity_instance
    from transport.plugin_errors import CommandDeadlineError
    monkeypatch.setattr(config, "transport_mode", "http")
    monkeypatch.setattr(config, "http_remote_hosted", False)
    error = CommandDeadlineError("mutation", {"phase": "command_response", "execution_may_have_started": True}) if deadline else ConnectionError("send interrupted")
    send = AsyncMock(side_effect=error)
    monkeypatch.setattr(PluginHub, "send_command_for_instance", send)
    response = await send_with_unity_instance(None, "A@aaa", "mutation", {})
    assert response["success"] is False
    assert response["hint"] == "check_state_before_retry"
    assert response["data"]["phase"] == ("command_response" if deadline else "transport_unknown")
    assert response["data"]["execution_may_have_started"] is True
    send.assert_awaited_once()


@pytest.mark.parametrize("phase,started,hint", [
    ("editor_queue", False, "retry"),
    ("editor_execution", True, "check_state_before_retry"),
])
@pytest.mark.parametrize("caller", ["resource", "custom_tool"])
def test_resource_and_custom_tool_preserve_native_deadline_metadata(phase, started, hint, caller):
    from models.models import MCPResponse
    from models.unity_response import normalize_unity_response, parse_resource_response
    from services.custom_tool_service import CustomToolService
    native = {"status": "error", "error": "deadline exceeded", "result": {
        "success": False, "error": "deadline exceeded", "hint": hint,
        "data": {"phase": phase, "execution_may_have_started": started},
    }}
    normalized = normalize_unity_response(native)
    response = (parse_resource_response(normalized, MCPResponse) if caller == "resource"
                else CustomToolService._normalize_response(None, normalized))
    assert response.success is False
    assert response.hint == hint
    assert response.data == native["result"]["data"]


@pytest.mark.asyncio
@pytest.mark.parametrize("phase,started,hint", [("editor_queue", False, "retry"),
    ("editor_execution", True, "check_state_before_retry")])
async def test_tool_transport_preserves_raw_native_deadline_reply(monkeypatch, phase, started, hint):
    from core.config import config
    from transport.unity_transport import send_with_unity_instance
    monkeypatch.setattr(config, "transport_mode", "http")
    monkeypatch.setattr(config, "http_remote_hosted", False)
    native = {"status": "error", "error": "deadline exceeded", "result": {
        "success": False, "error": "deadline exceeded", "hint": hint,
        "data": {"phase": phase, "execution_may_have_started": started}}}
    send = AsyncMock(return_value=native)
    monkeypatch.setattr(PluginHub, "send_command_for_instance", send)
    response = await send_with_unity_instance(None, "A@aaa", "mutation", {})
    assert response == native["result"]
    send.assert_awaited_once()


@pytest.mark.asyncio
async def test_cli_deadline_preserves_diagnostics_without_replay(monkeypatch):
    from types import SimpleNamespace
    import httpx
    from starlette.applications import Starlette
    from starlette.routing import Route
    from core.config import config
    from transport.http_routes import register_http_routes
    from transport.plugin_errors import CommandDeadlineError
    monkeypatch.setattr(config, "http_remote_hosted", False)
    monkeypatch.setattr(PluginHub, "get_sessions", AsyncMock(return_value=SimpleNamespace(
        sessions={"a": SimpleNamespace(project="A", hash="aaa")})))
    diagnostics = {"phase": "command_response", "command_id": "cmd", "session_id": "a",
                   "execution_may_have_started": True}
    send = AsyncMock(side_effect=CommandDeadlineError("deadline exceeded", diagnostics))
    monkeypatch.setattr(PluginHub, "send_command", send)
    routes = []

    def register(path, methods):
        def decorate(fn):
            routes.append(Route(path, fn, methods=methods))
            return fn
        return decorate

    register_http_routes(SimpleNamespace(custom_route=register), lambda: "test")
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=Starlette(routes=routes)),
                                base_url="http://isolated") as client:
        response = await client.post("/api/command", json={"type": "mutation", "unity_instance": "A@aaa"})
    assert response.status_code == 504
    assert response.json()["hint"] == "check_state_before_retry"
    assert response.json()["data"] == diagnostics
    send.assert_awaited_once_with("a", "mutation", {})
