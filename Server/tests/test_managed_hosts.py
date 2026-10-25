"""Hosting policy matches Editor local-launch hosts without probing live services."""
from contextlib import asynccontextmanager
from types import SimpleNamespace

import pytest

from core.config import config
from transport import http_server, managed_lifetime


@pytest.mark.asyncio
@pytest.mark.parametrize("host", ["127.0.0.1", "127.0.0.2", "localhost", "::1", "0.0.0.0", "::"])
@pytest.mark.parametrize("remote", [False, True])
async def test_managed_environment_is_honored_for_local_launch_hosts(monkeypatch, host, remote):
    monkeypatch.setattr(config, "http_remote_hosted", remote)
    monkeypatch.setenv("UNITY_MCP_INSTANCE_TOKEN", "a" * 32)
    monkeypatch.setenv("UNITY_MCP_EDITOR_PID", "101")
    monkeypatch.setenv("UNITY_MCP_EDITOR_STARTED_MS", "1000")
    observed = []

    class Server:
        def __init__(self, _config):
            pass

        async def serve(self, sockets=None):
            observed.append(managed_lifetime.active_lifetime)

    @asynccontextmanager
    async def lifespan():
        yield

    mcp = SimpleNamespace(custom_route=lambda *a, **k: lambda fn: fn,
                          http_app=lambda **k: None, _lifespan_manager=lifespan)
    monkeypatch.setattr(http_server.uvicorn, "Server", Server)
    await http_server.serve_http(mcp, host, 0)
    assert (observed[0] is not None) is (not remote)


@pytest.mark.asyncio
@pytest.mark.parametrize("peer,unknown", [("127.0.0.2", False), ("::1", False),
                                          ("192.0.2.1", True), (None, True)])
async def test_remote_websocket_identity_is_never_probed_as_local(peer, unknown):
    lifetime = managed_lifetime.ManagedServerLifetime("token", 101, 1000, lambda *_: False)

    async def app(*_):
        pass

    scope = {"type": "websocket", "client": (peer, 123) if peer else None}
    await http_server.ManagedConnections(app, lifetime)(scope, None, None)
    assert lifetime.unknown_owner is unknown
    assert lifetime.can_exit() is (not unknown)
