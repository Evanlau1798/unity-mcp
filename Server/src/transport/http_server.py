"""HTTP hosting and verified managed-server shutdown."""
from __future__ import annotations

import asyncio
from contextlib import suppress
import json
import ipaddress
import os
from typing import Any

import uvicorn
from starlette.responses import JSONResponse

from core.config import config
from transport import managed_lifetime
from transport.instance_endpoint import attach_instance_route


def is_loopback_peer(host) -> bool:
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False


class ManagedConnections:
    def __init__(self, app, lifetime):
        self.app, self.lifetime = app, lifetime

    async def __call__(self, scope, receive, send):
        tracked = scope["type"] == "websocket" and self.lifetime is not None
        if tracked:
            self.lifetime.connections += 1
            peer = scope.get("client")
            if not peer or not is_loopback_peer(peer[0]):
                # A LAN peer's PID belongs to another OS; retain rather than infer its exit.
                self.lifetime.remember_owner(None, None)
        try:
            await self.app(scope, receive, send)
        finally:
            if tracked:
                self.lifetime.connections -= 1


def install_managed_stop(mcp, lifetime, shutdown) -> None:
    @mcp.custom_route("/api/managed/stop", methods=["POST"])
    async def managed_stop(request):
        if (lifetime is None or config.http_remote_hosted or request.client is None
                or not is_loopback_peer(request.client.host)):
            return JSONResponse({"success": False}, status_code=403)
        try:
            body = bytearray()
            async for chunk in request.stream():
                body.extend(chunk)
                if len(body) > 4096:
                    return JSONResponse({"success": False}, status_code=413)
            payload = json.loads(body)
            if type(payload["server_pid"]) is not int or payload["server_pid"] != os.getpid():
                return JSONResponse({"success": False}, status_code=409)
            token, pid, started = payload["token"], payload["editor_pid"], payload["editor_started_ms"]
            if type(pid) is not int or type(started) is not int or not lifetime.can_stop(token, pid, started):
                return JSONResponse({"success": False}, status_code=409)
        except (ValueError, KeyError, TypeError):
            return JSONResponse({"success": False}, status_code=400)
        shutdown()
        return JSONResponse({"success": True}, status_code=202)


async def serve_http(mcp, host: str, port: int, sockets=None) -> None:
    lifetime = None
    if not config.http_remote_hosted:
        lifetime = managed_lifetime.ManagedServerLifetime.from_environment()
    server = None

    def shutdown():
        server.should_exit = True

    install_managed_stop(mcp, lifetime, shutdown)
    http_app = mcp.http_app(transport="http")
    attach_instance_route(http_app)
    app = ManagedConnections(http_app, lifetime)
    server = uvicorn.Server(uvicorn.Config(
        app, host=host, port=port, log_level=config.log_level.lower(),
        lifespan="on", ws="websockets-sansio", timeout_graceful_shutdown=0,
    ))
    previous = managed_lifetime.active_lifetime
    managed_lifetime.active_lifetime = lifetime
    watcher = None
    try:
        # Match FastMCP.run_http_async's outer lifespan, including registry configuration.
        async with mcp._lifespan_manager():
            if lifetime:
                watcher = asyncio.create_task(lifetime.watch(shutdown))
            await server.serve(sockets=sockets)
    finally:
        if watcher:
            watcher.cancel()
            with suppress(asyncio.CancelledError):
                await watcher
        managed_lifetime.active_lifetime = previous


def run_http_server(mcp, host: str, port: int) -> None:
    asyncio.run(serve_http(mcp, host, port))
