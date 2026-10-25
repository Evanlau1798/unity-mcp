"""Real HTTP/WebSocket + OS-process smoke on an ephemeral port; no Unity or installed MCP.

Run: Server/.venv/Scripts/python.exe tools/ownership-contracts/shared_http_smoke.py
"""
import asyncio
from contextlib import asynccontextmanager, suppress
import json
import os
from pathlib import Path
import socket
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "Server/src"))

import httpx
from fastmcp import FastMCP
from starlette.routing import WebSocketRoute
from websockets.asyncio.client import connect

from core.config import config
from transport.http_server import serve_http
from transport.plugin_hub import PluginHub
from transport.plugin_registry import PluginRegistry


EDITOR = """
import ctypes, json, os, sys
from ctypes import wintypes
k = ctypes.WinDLL('kernel32')
k.GetCurrentProcess.restype = wintypes.HANDLE
k.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
t = [wintypes.FILETIME() for _ in range(4)]
assert k.GetProcessTimes(k.GetCurrentProcess(), *(ctypes.byref(v) for v in t))
started = ((t[0].dwHighDateTime << 32) | t[0].dwLowDateTime) // 10000 - 11644473600000
print(json.dumps({'editor_pid': os.getpid(), 'editor_started_ms': started}), flush=True)
sys.stdin.readline()
"""


class Editor:
    def __init__(self):
        self.process = subprocess.Popen(
            [sys._base_executable, "-c", EDITOR], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            text=True, creationflags=subprocess.CREATE_NO_WINDOW,
        )
        self.identity = json.loads(self.process.stdout.readline())
        assert self.identity["editor_pid"] == self.process.pid

    def quit(self):
        if self.process.poll() is None:
            self.process.stdin.write("quit\n")
            self.process.stdin.flush()
            self.process.wait(timeout=5)

    def close(self):
        self.quit()
        self.process.stdin.close()
        self.process.stdout.close()


async def register(port, editor, name):
    ws = await connect(f"ws://127.0.0.1:{port}/hub/plugin")
    assert json.loads(await ws.recv())["type"] == "welcome"
    await ws.send(json.dumps({"type": "register", "project_name": name, "project_hash": name,
                             "unity_version": "2022.3.22f1", **editor.identity}))
    assert json.loads(await ws.recv())["type"] == "registered"
    return ws


async def run_order(first):
    a, b = Editor(), Editor()
    held_socket = socket.socket()
    held_socket.bind(("127.0.0.1", 0))
    port = held_socket.getsockname()[1]
    held_socket.listen()
    os.environ.update(UNITY_MCP_INSTANCE_TOKEN="a" * 32,
                      UNITY_MCP_EDITOR_PID=str(a.process.pid),
                      UNITY_MCP_EDITOR_STARTED_MS=str(a.identity["editor_started_ms"]))
    config.http_remote_hosted = False
    config.transport_mode = "http"

    @asynccontextmanager
    async def lifespan(_):
        PluginHub.configure(PluginRegistry(), asyncio.get_running_loop())
        yield {}

    mcp = FastMCP("offline-shared-server", lifespan=lifespan)
    mcp._additional_http_routes.append(WebSocketRoute("/hub/plugin", PluginHub))
    server = asyncio.create_task(serve_http(mcp, "127.0.0.1", port, sockets=[held_socket]))
    peers = {}
    try:
        # Wait on this isolated listener, never the configured/installed 8080 server.
        async with httpx.AsyncClient(timeout=0.3) as client:
            for _ in range(60):
                if server.done():
                    await server
                    raise AssertionError("server exited during startup")
                try:
                    await client.get(f"http://127.0.0.1:{port}/health-does-not-exist")
                    break
                except httpx.HTTPError:
                    await asyncio.sleep(0.05)
            else:
                raise AssertionError("isolated HTTP server did not start")
            peers["A"] = await register(port, a, "A")
            peers["B"] = await register(port, b, "B")
            payload = {"token": "a" * 32, "server_pid": os.getpid(), **a.identity}
            refused = await client.post(f"http://127.0.0.1:{port}/api/managed/stop", json=payload)
            assert refused.status_code == 409, refused.text
            # Both WebSockets may disappear during reload; live OS identities still retain the server.
            await peers["B"].close()
            await asyncio.sleep(2.2)
            assert not server.done(), "reload closed a live peer's server"
            peers["B"] = await register(port, b, "B")
            editors = {"A": a, "B": b}
            last = "B" if first == "A" else "A"
            await peers[first].close()
            editors[first].quit()
            await asyncio.sleep(2.2)
            assert not server.done(), f"{first} quit stopped live {last}"
            await peers[last].close()
            editors[last].quit()
            await asyncio.wait_for(server, 6)
            print(f"PASS {first} quits -> {last} quits; reload + shared Stop guard + last-owner cleanup")
    finally:
        for ws in peers.values():
            with suppress(Exception):
                await ws.close()
        a.close()
        b.close()
        if not server.done():
            server.cancel()
            with suppress(asyncio.CancelledError):
                await server
        held_socket.close()


async def main():
    await run_order("A")
    await run_order("B")


if __name__ == "__main__":
    if sys.platform != "win32":
        raise SystemExit("This smoke verifies the Windows Editor-process identity boundary.")
    asyncio.run(main())
