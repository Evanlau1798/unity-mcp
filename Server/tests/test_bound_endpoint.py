"""HTTP MCP protocol routing: bound targets survive session replacement."""
import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

from fastmcp import Client, Context, FastMCP
from fastmcp.client.transports import StreamableHttpTransport
import httpx
import pytest

from core.config import config
from transport import http_server
from transport.unity_instance_middleware import UnityInstanceMiddleware
from transport.plugin_hub import PluginHub
from transport.plugin_registry import PluginRegistry
from transport.unity_transport import send_with_unity_instance
from starlette.websockets import WebSocketState


@pytest.mark.asyncio
@pytest.mark.parametrize("network", [False, True])
async def test_bound_http_tools_resources_reconnect_and_conflicts(monkeypatch, network):
    monkeypatch.setattr(config, "transport_mode", "http")
    monkeypatch.setattr(config, "http_remote_hosted", False)
    for name in ("UNITY_MCP_INSTANCE_TOKEN", "UNITY_MCP_EDITOR_PID", "UNITY_MCP_EDITOR_STARTED_MS"):
        monkeypatch.delenv(name, raising=False)
    mcp = FastMCP("bound-http-contract")
    mw = UnityInstanceMiddleware()
    mcp.add_middleware(mw)
    instances = [SimpleNamespace(id=f"{name}@{hash}", hash=hash) for name, hash in [("A", "aaa"), ("B", "bbb")]]
    monkeypatch.setattr(mw, "_discover_instances", AsyncMock(side_effect=lambda _: list(instances)))
    monkeypatch.setattr(mw, "_maybe_autoselect_instance", AsyncMock(return_value=None))
    writes = []
    registry = PluginRegistry()
    for key, value in {"_registry": registry, "_lock": asyncio.Lock(), "_connections": {}, "_pending": {}}.items():
        monkeypatch.setattr(PluginHub, key, value)

    class PluginSocket:
        client_state = application_state = WebSocketState.CONNECTED

        def __init__(self, target):
            self.target = target

        async def send_json(self, payload):
            if payload["name"] == "mutation":
                writes.append(self.target)
            PluginHub._pending[payload["id"]]["future"].set_result(
                {"status": "success", "result": {"target": self.target}})

    for instance in instances:
        name, hash_value = instance.id.split("@")
        await registry.register(hash_value, name, hash_value, "2022.3.22f1")
        PluginHub._connections[hash_value] = PluginSocket(instance.id)

    async def dispatch(ctx, command):
        response = await send_with_unity_instance(None, await ctx.get_state("unity_instance"), command, {})
        if not response["success"]:
            raise ValueError(response["error"])
        return response["data"]["target"]

    @mcp.tool
    async def mutate(ctx: Context) -> dict:
        await asyncio.sleep(0.01)
        return {"target": await dispatch(ctx, "mutation")}

    @mcp.tool
    async def select(instance: str, ctx: Context) -> dict:
        await mw.set_active_instance(ctx, instance)
        return {"target": instance}

    @mcp.resource("unity://state")
    async def state(ctx: Context) -> str:
        return await dispatch(ctx, "state")

    async def exercise(client):
        async with client("/mcp/instance/A@aaa") as a, client("/mcp/instance/B@bbb") as b:
            initial_session_id = a.transport.get_session_id()
            assert a.transport.get_session_id() != b.transport.get_session_id()
            results = await asyncio.gather(a.call_tool("mutate"), b.call_tool("mutate"))
            assert [r.data["target"] for r in results] == ["A@aaa", "B@bbb"]
            assert (await a.read_resource("unity://state"))[0].text == "A@aaa"
            assert (await b.read_resource("unity://state"))[0].text == "B@bbb"
            before = list(writes)
            conflict = await a.call_tool("mutate", {"unity_instance": "B@bbb"}, raise_on_error=False)
            assert conflict.is_error and writes == before
            conflict = await a.call_tool("select", {"instance": "B@bbb"}, raise_on_error=False)
            assert conflict.is_error
            instances.pop(0)
            disconnected = await a.call_tool("mutate", raise_on_error=False)
            assert disconnected.is_error and writes == before
            instances.insert(0, SimpleNamespace(id="A@aaa", hash="aaa"))
        async with client("/mcp/instance/A@aaa") as reconnected:
            assert reconnected.transport.get_session_id() != initial_session_id
            assert (await reconnected.call_tool("mutate")).data["target"] == "A@aaa"
        async with client("/mcp") as legacy:
            before = list(writes)
            unselected = await legacy.call_tool("mutate", raise_on_error=False)
            assert unselected.is_error and writes == before
            await legacy.call_tool("select", {"instance": "A@aaa"})
            results = await asyncio.gather(*[legacy.call_tool("mutate", {"unity_instance": target})
                                            for target in ("A@aaa", "B@bbb")])
            assert [r.data["target"] for r in results] == ["A@aaa", "B@bbb"]
            assert (await legacy.read_resource("unity://state"))[0].text == "A@aaa"
    if network:
        import socket
        held = socket.socket()
        held.bind(("127.0.0.1", 0))
        held.listen()
        base_url = f"http://127.0.0.1:{held.getsockname()[1]}"
        real_server = http_server.uvicorn.Server

        class Server(real_server):
            async def serve(self, sockets=None):
                task = asyncio.create_task(super().serve(sockets=[held]))
                try:
                    async def ready():
                        while not self.started:
                            await asyncio.sleep(0.01)
                    await asyncio.wait_for(ready(), 5)
                    await exercise(lambda path: Client(base_url + path))
                finally:
                    self.should_exit = True
                    await asyncio.wait_for(task, 5)
                    held.close()
    else:
        class Server:
            def __init__(self, server_config):
                self.app = server_config.app

            async def serve(self, sockets=None):
                starlette = self.app.app
                async with starlette.lifespan(starlette):
                    def client(path):
                        return Client(StreamableHttpTransport("http://isolated" + path,
                            httpx_client_factory=lambda **kw: httpx.AsyncClient(
                                transport=httpx.ASGITransport(app=self.app), **kw)))
                    await exercise(client)

    monkeypatch.setattr(http_server.uvicorn, "Server", Server)
    await http_server.serve_http(mcp, "127.0.0.1", 0)
