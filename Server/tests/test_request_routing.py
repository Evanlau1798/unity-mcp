"""Real FastMCP session store contracts for parallel request routing."""
import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

from fastmcp import Context, FastMCP
import httpx
import pytest
from starlette.applications import Starlette
from starlette.routing import Route

from core.config import config
from transport import http_routes
from transport.unity_instance_middleware import UnityInstanceMiddleware


class RequestContext(Context):
    def __init__(self, mcp, session_id):
        super().__init__(mcp)
        self._test_session_id = session_id

    @property
    def session_id(self):
        return self._test_session_id


@pytest.mark.asyncio
async def test_parallel_calls_do_not_share_request_target_or_leak_into_next_call(monkeypatch):
    mcp = FastMCP("request-state-contract")
    mw = UnityInstanceMiddleware()
    monkeypatch.setattr(config, "transport_mode", "stdio")
    monkeypatch.setattr(config, "http_remote_hosted", False)
    monkeypatch.setattr(mw, "_resolve_instance_value", AsyncMock(side_effect=lambda value, _: value))
    monkeypatch.setattr(mw, "_maybe_autoselect_instance", AsyncMock(return_value=None))
    a, b, later = [RequestContext(mcp, "shared-session") for _ in range(3)]

    async def inject(ctx, target):
        await mw._inject_unity_instance(SimpleNamespace(fastmcp_context=ctx,
            message=SimpleNamespace(arguments={"unity_instance": target})))

    await asyncio.gather(inject(a, "A@aaa"), inject(b, "B@bbb"))
    assert await a.get_state("unity_instance") == "A@aaa"
    assert await b.get_state("unity_instance") == "B@bbb"
    await mw._inject_unity_instance(SimpleNamespace(fastmcp_context=later, message=SimpleNamespace()))
    assert await later.get_state("unity_instance") is None


@pytest.mark.asyncio
async def test_active_selection_is_session_local_and_survives_new_request_not_new_session(monkeypatch):
    mcp = FastMCP("session-state-contract")
    mw = UnityInstanceMiddleware()
    a, next_a, b = [RequestContext(mcp, sid) for sid in ("a", "a", "b")]
    await mw.set_active_instance(a, "A@aaa")
    assert await mw.get_active_instance(next_a) == "A@aaa"
    assert await mw.get_active_instance(b) is None


@pytest.mark.asyncio
async def test_unselected_request_shadows_legacy_session_injection(monkeypatch):
    mcp = FastMCP("old-injection-contract")
    ctx = RequestContext(mcp, "a")
    await ctx.set_state("unity_instance", "departed@old")
    await ctx.set_state("unity_session_id", "old-websocket")
    mw = UnityInstanceMiddleware()
    monkeypatch.setattr(mw, "_resolve_user_id", AsyncMock(return_value=None))
    monkeypatch.setattr(config, "http_remote_hosted", False)
    monkeypatch.setattr(mw, "_maybe_autoselect_instance", AsyncMock(return_value=None))
    await mw._inject_unity_instance(SimpleNamespace(fastmcp_context=ctx, message=SimpleNamespace()))
    assert await ctx.get_state("unity_instance") is None
    assert await ctx.get_state("unity_session_id") is None


@pytest.mark.asyncio
@pytest.mark.parametrize("target,expected", [(None, 409), ("Game@missing", 404), ("Game", 409),
                                           ("Game@bbb", 200), ("bbb", 200)])
@pytest.mark.parametrize("route", ["command", "custom-tools"])
async def test_cli_targeting_is_exact_and_ambiguous_mutation_never_dispatches(monkeypatch, target, expected, route):
    monkeypatch.setattr(config, "http_remote_hosted", False)
    sessions = {sid: SimpleNamespace(project="Game", hash=h) for sid, h in [("a", "aaa"), ("b", "bbb")]}
    monkeypatch.setattr(http_routes.PluginHub, "get_sessions", AsyncMock(return_value=SimpleNamespace(sessions=sessions)))
    send = AsyncMock(return_value={"success": True})
    monkeypatch.setattr(http_routes.PluginHub, "send_command", send)
    monkeypatch.setattr(http_routes, "resolve_project_id_for_unity_instance", lambda _: "isolated")
    monkeypatch.setattr(http_routes.CustomToolService, "get_instance", lambda: SimpleNamespace(list_registered_tools=AsyncMock(return_value=[])))
    routes = []

    def register(path, methods):
        def decorator(fn):
            routes.append(Route(path, fn, methods=methods))
            return fn
        return decorator

    http_routes.register_http_routes(SimpleNamespace(custom_route=register), lambda: "test")
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=Starlette(routes=routes)), base_url="http://isolated") as client:
        if route == "command":
            response = await client.post("/api/command", json={"type": "mutation", "unity_instance": target})
        else:
            response = await client.get("/api/custom-tools", params={"instance": target} if target else {})
    assert response.status_code == expected
    if route == "command":
        if expected == 200:
            send.assert_awaited_once_with("b", "mutation", {})
        else:
            send.assert_not_awaited()
