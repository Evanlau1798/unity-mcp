"""MCP startup/shutdown and shared Unity registry."""
import asyncio
import logging
import os
import threading
import time
from contextlib import asynccontextmanager
from typing import AsyncIterator, Any
from fastmcp import FastMCP
from core.config import config
from core.telemetry import record_milestone, record_telemetry, MilestoneType, RecordType, get_package_version
from transport.legacy.unity_connection import get_unity_connection_pool, UnityConnectionPool
from transport.plugin_hub import PluginHub
from transport.plugin_registry import PluginRegistry
logger = logging.getLogger("mcp-for-unity-server")

# Global connection pool
_unity_connection_pool: UnityConnectionPool | None = None
_plugin_registry: PluginRegistry | None = None

# Cached server version (set at startup to avoid repeated I/O)
_server_version: str | None = None



@asynccontextmanager
async def server_lifespan(server: FastMCP) -> AsyncIterator[dict[str, Any]]:
    """Handle server startup and shutdown."""
    global _unity_connection_pool, _server_version
    _server_version = get_package_version()
    logger.info(f"MCP for Unity Server v{_server_version} starting up")

    # Register custom tool management endpoints with FastMCP
    # Routes are declared globally below after FastMCP initialization

    # Note: When using HTTP transport, FastMCP handles the HTTP server
    # Tool registration will be handled through FastMCP endpoints
    enable_http_server = os.environ.get(
        "UNITY_MCP_ENABLE_HTTP_SERVER", "").lower() in ("1", "true", "yes", "on")
    if enable_http_server:
        http_host = os.environ.get("UNITY_MCP_HTTP_HOST", "localhost")
        http_port = int(os.environ.get("UNITY_MCP_HTTP_PORT", "8080"))
        logger.info(
            f"HTTP tool registry will be available on http://{http_host}:{http_port}")

    global _plugin_registry
    if _plugin_registry is None:
        _plugin_registry = PluginRegistry()
        loop = asyncio.get_running_loop()
        PluginHub.configure(_plugin_registry, loop, mcp=server)

    # Record server startup telemetry
    start_time = time.time()
    start_clk = time.perf_counter()
    # Defer initial telemetry by 1s to avoid stdio handshake interference

    def _emit_startup():
        try:
            record_telemetry(RecordType.STARTUP, {
                "server_version": _server_version,
                "startup_time": start_time,
            })
            record_milestone(MilestoneType.FIRST_STARTUP)
        except Exception:
            logger.debug("Deferred startup telemetry failed", exc_info=True)
    threading.Timer(1.0, _emit_startup).start()

    try:
        skip_connect = os.environ.get(
            "UNITY_MCP_SKIP_STARTUP_CONNECT", "").lower() in ("1", "true", "yes", "on")
        if skip_connect:
            logger.info(
                "Skipping Unity connection on startup (UNITY_MCP_SKIP_STARTUP_CONNECT=1)")
        else:
            # Initialize connection pool and discover instances
            _unity_connection_pool = get_unity_connection_pool()
            instances = _unity_connection_pool.discover_all_instances()

            if instances:
                logger.info(
                    f"Discovered {len(instances)} Unity instance(s): {[i.id for i in instances]}")

                # Try to connect to default instance
                try:
                    _unity_connection_pool.get_connection()
                    logger.info(
                        "Connected to default Unity instance on startup")

                    # In stdio mode, query Unity for tool enabled states and sync
                    # server-level visibility. In HTTP mode this is handled by
                    # register_tools via WebSocket in PluginHub.
                    if (config.transport_mode or "stdio").lower() != "http":
                        try:
                            from services.tools import sync_tool_visibility_from_unity
                            sync_result = await sync_tool_visibility_from_unity(notify=False)
                            if sync_result.get("synced"):
                                logger.info(
                                    "Stdio startup: synced tool visibility from Unity — "
                                    "enabled=[%s], disabled=[%s]",
                                    ", ".join(sync_result.get("enabled_groups", [])),
                                    ", ".join(sync_result.get("disabled_groups", [])),
                                )
                            else:
                                # Unsupported command = old Unity package; just debug-log
                                log_fn = logger.debug if sync_result.get("unsupported") else logger.warning
                                log_fn(
                                    "Stdio startup: could not sync tool visibility: %s",
                                    sync_result.get("error", "unknown"),
                                )
                        except Exception as sync_exc:
                            logger.debug(
                                "Stdio startup: tool visibility sync failed: %s", sync_exc)

                    # Record successful Unity connection (deferred)
                    threading.Timer(1.0, lambda: record_telemetry(
                        RecordType.UNITY_CONNECTION,
                        {
                            "status": "connected",
                            "connection_time_ms": (time.perf_counter() - start_clk) * 1000,
                            "instance_count": len(instances)
                        }
                    )).start()
                except Exception as e:
                    logger.warning(
                        f"Could not connect to default Unity instance: {e}")
            else:
                logger.warning("No Unity instances found on startup")

    except ConnectionError as e:
        logger.warning(f"Could not connect to Unity on startup: {e}")

        # Record connection failure (deferred)
        _err_msg = str(e)[:200]
        threading.Timer(1.0, lambda: record_telemetry(
            RecordType.UNITY_CONNECTION,
            {
                "status": "failed",
                "error": _err_msg,
                "connection_time_ms": (time.perf_counter() - start_clk) * 1000,
            }
        )).start()
    except Exception as e:
        logger.warning(f"Unexpected error connecting to Unity on startup: {e}")
        _err_msg = str(e)[:200]
        threading.Timer(1.0, lambda: record_telemetry(
            RecordType.UNITY_CONNECTION,
            {
                "status": "failed",
                "error": _err_msg,
                "connection_time_ms": (time.perf_counter() - start_clk) * 1000,
            }
        )).start()

    try:
        # Yield shared state for lifespan consumers (e.g., middleware)
        yield {
            "pool": _unity_connection_pool,
            "plugin_registry": _plugin_registry,
        }
    finally:
        if _unity_connection_pool:
            _unity_connection_pool.disconnect_all()
        logger.info("MCP for Unity Server shut down")
