"""Managed HTTP admission and real OS-process liveness without a Unity runtime."""
import asyncio
from contextlib import suppress
import os
import subprocess
import sys
from types import SimpleNamespace

import httpx
import pytest
from starlette.applications import Starlette
from starlette.routing import Route

from transport import managed_lifetime
from transport.http_server import install_managed_stop, ManagedConnections
from transport.managed_lifetime import ManagedServerLifetime, process_is_alive
from transport.plugin_registry import PluginRegistry


def build_app(lifetime, stops):
    routes = []

    def custom_route(path, methods):
        def register(fn):
            routes.append(Route(path, fn, methods=methods))
            return fn
        return register
    install_managed_stop(SimpleNamespace(custom_route=custom_route), lifetime, lambda: stops.append(True))
    return Starlette(routes=routes)


@pytest.mark.asyncio
async def test_control_endpoint_checks_process_token_peers_and_request_size():
    alive = {(101, 1000): True, (202, 2000): True}
    lifetime = ManagedServerLifetime("token", 101, 1000, lambda pid, start: alive.get((pid, start)))
    stops = []
    transport = httpx.ASGITransport(app=build_app(lifetime, stops), client=("127.0.0.1", 1))
    async with httpx.AsyncClient(transport=transport, base_url="http://isolated") as client:
        payload = {"token": "token", "editor_pid": 101, "editor_started_ms": 1000, "server_pid": os.getpid()}
        assert (await client.post("/api/managed/stop", json=payload | {"token": "wrong"})).status_code == 409
        assert (await client.post("/api/managed/stop", json=payload | {"server_pid": os.getpid() + 1})).status_code == 409
        assert (await client.post("/api/managed/stop", content=b"x" * 4097)).status_code == 413
        lifetime.remember_owner(202, 2000)
        assert (await client.post("/api/managed/stop", json=payload)).status_code == 409
        assert stops == []
        alive[(202, 2000)] = False
        assert (await client.post("/api/managed/stop", json=payload)).status_code == 202
        assert stops == [True]


@pytest.mark.asyncio
@pytest.mark.parametrize("managed,source", [(False, "127.0.0.1"), (True, "192.0.2.1")])
async def test_external_or_remote_control_never_stops_server(managed, source):
    stops = []
    lifetime = ManagedServerLifetime("token", 101, 1000, lambda *_: True) if managed else None
    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=build_app(lifetime, stops), client=(source, 1)),
        base_url="http://isolated",
    ) as client:
        assert (await client.post("/api/managed/stop", json={})).status_code == 403
    assert stops == []


@pytest.mark.asyncio
async def test_registration_remembers_pid_across_disconnect(monkeypatch):
    lifetime = ManagedServerLifetime("token", 101, 1000, lambda *_: False)
    monkeypatch.setattr(managed_lifetime, "active_lifetime", lifetime)
    registry = PluginRegistry()
    await registry.register("peer", "B", "hash-b", "2022.3", editor_pid=202, editor_started_ms=2000)
    await registry.unregister("peer")
    assert (202, 2000) in lifetime.owners
    await registry.register("old-plugin", "C", "hash-c", "2022.3")
    await registry.unregister("old-plugin")
    assert lifetime.unknown_owner
    assert not lifetime.can_exit()


@pytest.mark.asyncio
async def test_watch_is_bounded_cancellable_and_cleans_last_owner():
    lifetime = ManagedServerLifetime("token", 101, 1000, lambda *_: False)
    stopped = asyncio.Event()
    lifetime.connections = 1
    watcher = asyncio.create_task(lifetime.watch(stopped.set, interval=0.01))
    await asyncio.sleep(0.03)
    assert not stopped.is_set()
    lifetime.connections = 0
    await asyncio.wait_for(stopped.wait(), 0.5)
    await watcher


@pytest.mark.asyncio
async def test_connection_admission_is_released_even_on_failure():
    lifetime = ManagedServerLifetime("token", 101, 1000, lambda *_: False)

    async def fails(*_):
        assert lifetime.connections == 1
        assert not lifetime.can_exit()
        raise ValueError("closed")
    with pytest.raises(ValueError):
        await ManagedConnections(fails, lifetime)({"type": "websocket", "client": ("127.0.0.1", 1)}, None, None)
    assert lifetime.connections == 0
    assert lifetime.can_exit()


def test_external_launch_has_no_lifetime_without_complete_editor_identity(monkeypatch):
    for name in ("UNITY_MCP_EDITOR_PID", "UNITY_MCP_EDITOR_STARTED_MS", "UNITY_MCP_INSTANCE_TOKEN"):
        monkeypatch.delenv(name, raising=False)
    assert ManagedServerLifetime.from_environment() is None
    monkeypatch.setenv("UNITY_MCP_INSTANCE_TOKEN", "a" * 32)
    assert ManagedServerLifetime.from_environment() is None
    monkeypatch.setenv("UNITY_MCP_EDITOR_PID", "101")
    monkeypatch.setenv("UNITY_MCP_EDITOR_STARTED_MS", "1000")
    assert ManagedServerLifetime.from_environment() is not None


@pytest.mark.skipif(sys.platform != "win32", reason="Windows creation-time and process-handle contract")
def test_real_child_process_identity_exit_and_pid_mismatch():
    # Own short-lived child only. No Unity process or installed server is queried or signalled.
    script = """
import ctypes, os, time
from ctypes import wintypes
k = ctypes.WinDLL('kernel32')
k.GetCurrentProcess.restype = wintypes.HANDLE
k.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
t = [wintypes.FILETIME() for _ in range(4)]
assert k.GetProcessTimes(k.GetCurrentProcess(), *(ctypes.byref(v) for v in t))
print(((t[0].dwHighDateTime << 32) | t[0].dwLowDateTime) // 10000 - 11644473600000, flush=True)
time.sleep(30)
"""
    child = subprocess.Popen([sys._base_executable, "-c", script], stdout=subprocess.PIPE, text=True,
                             creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        started = int(child.stdout.readline())
        assert process_is_alive(child.pid, started) is True
        assert process_is_alive(child.pid, started + 1) is False
    finally:
        child.terminate()
        child.wait(timeout=5)
        child.stdout.close()
    assert process_is_alive(child.pid, started) is False
