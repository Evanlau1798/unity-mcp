from starlette.requests import Request
from transport.unity_instance_middleware import (
    UnityInstanceMiddleware,
    get_unity_instance_middleware
)
from services.api_key_service import ApiKeyService
from transport.legacy.unity_connection import get_unity_connection_pool, UnityConnectionPool
from services.tools import register_all_tools
from core.telemetry import record_milestone, record_telemetry, MilestoneType, RecordType, get_package_version
from services.resources import register_all_resources
from transport.plugin_registry import PluginRegistry
from transport.plugin_hub import PluginHub
from services.custom_tool_service import (
    CustomToolService,
    resolve_project_id_for_unity_instance,
)
from core.config import config
from starlette.routing import WebSocketRoute
from starlette.responses import JSONResponse
import argparse
import asyncio

# Fix to IPV4 Connection Issue #853
# Will disable features in ProactorEventLoop including subprocess pipes and named pipes
import sys
if sys.platform == "win32":
    asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())

import logging
from contextlib import asynccontextmanager
import os
import threading
import time
from typing import AsyncIterator, Any
from urllib.parse import urlparse

# Workaround for environments where tool signature evaluation runs with a globals
# dict that does not include common `typing` names (e.g. when annotations are strings
# and evaluated via `eval()` during schema generation).
# Making these names available in builtins avoids `NameError: Annotated/Literal/... is not defined`.
try:  # pragma: no cover - startup safety guard
    import builtins
    import typing as _typing

    _typing_names = (
        "Annotated",
        "Literal",
        "Any",
        "Union",
        "Optional",
        "Dict",
        "List",
        "Tuple",
        "Set",
        "FrozenSet",
    )
    for _name in _typing_names:
        if not hasattr(builtins, _name) and hasattr(_typing, _name):
            # type: ignore[attr-defined]
            setattr(builtins, _name, getattr(_typing, _name))
except Exception:
    pass

from fastmcp import FastMCP
from logging.handlers import RotatingFileHandler


from core.server_logging import WindowsSafeRotatingFileHandler, configure_logging
from core import server_lifecycle
from core.server_lifecycle import server_lifespan
from services.server_instructions import _build_instructions
from transport.http_routes import register_http_routes, _normalize_instance_token

logger = configure_logging()
custom_tool_service: CustomToolService | None = None

def create_mcp_server(project_scoped_tools: bool) -> FastMCP:
    mcp = FastMCP(
        name="mcp-for-unity-server",
        lifespan=server_lifespan,
        instructions=_build_instructions(project_scoped_tools),
    )

    global custom_tool_service
    custom_tool_service = CustomToolService(
        mcp, project_scoped_tools=project_scoped_tools)

    register_http_routes(mcp, lambda: server_lifecycle._server_version)

    # Initialize and register middleware for session-based Unity instance routing
    # Using the singleton getter ensures we use the same instance everywhere
    unity_middleware = get_unity_instance_middleware()
    mcp.add_middleware(unity_middleware)
    logger.info("Registered Unity instance middleware for session-based routing")

    # Initialize API key authentication if in remote-hosted mode
    if config.http_remote_hosted and config.api_key_validation_url:
        ApiKeyService(
            validation_url=config.api_key_validation_url,
            cache_ttl=config.api_key_cache_ttl,
            service_token_header=config.api_key_service_token_header,
            service_token=config.api_key_service_token,
        )
        logger.info(
            "Initialized API key authentication service (validation URL: %s, TTL: %.0fs)",
            config.api_key_validation_url,
            config.api_key_cache_ttl,
        )

    # Mount plugin websocket hub at /hub/plugin when HTTP transport is active.
    # NOTE: Uses FastMCP private API because custom_route() only supports HTTP
    # methods, not WebSocket. _additional_http_routes accepts Starlette Route
    # objects and is still present in FastMCP 3.x.
    existing_routes = [
        route for route in mcp._get_additional_http_routes()
        if isinstance(route, WebSocketRoute) and route.path == "/hub/plugin"
    ]
    if not existing_routes:
        mcp._additional_http_routes.append(
            WebSocketRoute("/hub/plugin", PluginHub))

    # Register all tools
    register_all_tools(mcp, project_scoped_tools=project_scoped_tools)

    # Register all resources
    register_all_resources(mcp, project_scoped_tools=project_scoped_tools)

    return mcp


def main():
    """Entry point for uvx and console scripts."""
    parser = argparse.ArgumentParser(
        description="MCP for Unity Server",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Environment Variables:
  UNITY_MCP_DEFAULT_INSTANCE   Default Unity instance to target (project name, hash, or 'Name@hash')
  UNITY_MCP_SKIP_STARTUP_CONNECT   Skip initial Unity connection attempt (set to 1/true/yes/on)
  UNITY_MCP_TELEMETRY_ENABLED   Enable telemetry (set to 1/true/yes/on)
  UNITY_MCP_TRANSPORT   Transport protocol: stdio or http (default: stdio)
  UNITY_MCP_HTTP_URL   HTTP server URL (default: http://127.0.0.1:8080)
  UNITY_MCP_HTTP_HOST   HTTP server host (overrides URL host)
  UNITY_MCP_HTTP_PORT   HTTP server port (overrides URL port)

Examples:
  # Use specific Unity project as default
  python -m src.server --default-instance "MyProject"

  # Start with HTTP transport
  python -m src.server --transport http --http-url http://127.0.0.1:8080

  # Start with stdio transport (default)
  python -m src.server --transport stdio

  # Use environment variable for transport
  UNITY_MCP_TRANSPORT=http UNITY_MCP_HTTP_URL=http://localhost:9000 python -m src.server
        """
    )
    parser.add_argument(
        "--default-instance",
        type=str,
        metavar="INSTANCE",
        help="Default Unity instance to target (project name, hash, or 'Name@hash'). "
             "Overrides UNITY_MCP_DEFAULT_INSTANCE environment variable."
    )
    parser.add_argument(
        "--transport",
        type=str,
        choices=["stdio", "http"],
        default="stdio",
        help="Transport protocol to use: stdio or http (default: stdio). "
             "Overrides UNITY_MCP_TRANSPORT environment variable."
    )
    parser.add_argument(
        "--http-url",
        type=str,
        default="http://127.0.0.1:8080",
        metavar="URL",
        help="HTTP server URL (default: http://127.0.0.1:8080). "
             "Can also set via UNITY_MCP_HTTP_URL environment variable."
    )
    parser.add_argument(
        "--http-host",
        type=str,
        default=None,
        metavar="HOST",
        help="HTTP server host (overrides URL host). "
             "Overrides UNITY_MCP_HTTP_HOST environment variable."
    )
    parser.add_argument(
        "--http-port",
        type=int,
        default=None,
        metavar="PORT",
        help="HTTP server port (overrides URL port). "
             "Overrides UNITY_MCP_HTTP_PORT environment variable."
    )
    parser.add_argument(
        "--http-remote-hosted",
        action="store_true",
        help="Treat HTTP transport as remotely hosted (forces explicit Unity instance selection). "
             "Can also set via UNITY_MCP_HTTP_REMOTE_HOSTED=true."
    )
    parser.add_argument(
        "--api-key-validation-url",
        type=str,
        default=None,
        metavar="URL",
        help="External URL to validate API keys (POST with {'api_key': '...'}). "
             "Required when --http-remote-hosted is set. "
             "Can also set via UNITY_MCP_API_KEY_VALIDATION_URL."
    )
    parser.add_argument(
        "--api-key-login-url",
        type=str,
        default=None,
        metavar="URL",
        help="URL where users can obtain/manage API keys. "
             "Returned by /api/auth/login-url endpoint. "
             "Can also set via UNITY_MCP_API_KEY_LOGIN_URL."
    )
    parser.add_argument(
        "--api-key-cache-ttl",
        type=float,
        default=300.0,
        metavar="SECONDS",
        help="Cache TTL for validated API keys in seconds (default: 300). "
             "Can also set via UNITY_MCP_API_KEY_CACHE_TTL."
    )
    parser.add_argument(
        "--api-key-service-token-header",
        type=str,
        default=None,
        metavar="HEADER",
        help="Header name for service token sent to validation endpoint (e.g. X-Service-Token). "
             "Can also set via UNITY_MCP_API_KEY_SERVICE_TOKEN_HEADER."
    )
    parser.add_argument(
        "--api-key-service-token",
        type=str,
        default=None,
        metavar="TOKEN",
        help="Service token value sent to validation endpoint for server authentication. "
             "WARNING: Prefer UNITY_MCP_API_KEY_SERVICE_TOKEN env var in production to avoid process listing exposure."
    )
    parser.add_argument(
        "--unity-instance-token",
        type=str,
        default=None,
        metavar="TOKEN",
        help="Optional per-launch token set by Unity for deterministic lifecycle management. "
             "Used by Unity to validate it is stopping the correct process."
    )
    parser.add_argument(
        "--pidfile",
        type=str,
        default=None,
        metavar="PATH",
        help="Optional path where the server will write its PID on startup. "
             "Used by Unity to stop the exact process it launched when running in a terminal."
    )
    parser.add_argument(
        "--project-scoped-tools",
        action="store_true",
        help="Keep custom tools scoped to the active Unity project and enable the custom tools resource. "
             "Can also set via UNITY_MCP_PROJECT_SCOPED_TOOLS=true."
    )

    args = parser.parse_args()

    # Set environment variables from command line args
    if args.default_instance:
        os.environ["UNITY_MCP_DEFAULT_INSTANCE"] = args.default_instance
        logger.info(
            f"Using default Unity instance from command-line: {args.default_instance}")

    # Set transport mode
    config.transport_mode = args.transport or os.environ.get(
        "UNITY_MCP_TRANSPORT", "stdio")
    logger.info(f"Transport mode: {config.transport_mode}")

    config.http_remote_hosted = (
        bool(args.http_remote_hosted)
        or os.environ.get("UNITY_MCP_HTTP_REMOTE_HOSTED", "").lower() in ("true", "1", "yes", "on")
    )

    # API key authentication configuration
    config.api_key_validation_url = (
        args.api_key_validation_url
        or os.environ.get("UNITY_MCP_API_KEY_VALIDATION_URL")
    )
    config.api_key_login_url = (
        args.api_key_login_url
        or os.environ.get("UNITY_MCP_API_KEY_LOGIN_URL")
    )
    try:
        cache_ttl_env = os.environ.get("UNITY_MCP_API_KEY_CACHE_TTL")
        config.api_key_cache_ttl = (
            float(cache_ttl_env) if cache_ttl_env else args.api_key_cache_ttl
        )
    except ValueError:
        logger.warning(
            "Invalid UNITY_MCP_API_KEY_CACHE_TTL value, using default 300.0"
        )
        config.api_key_cache_ttl = 300.0

    # Service token for authenticating to validation endpoint
    config.api_key_service_token_header = (
        args.api_key_service_token_header
        or os.environ.get("UNITY_MCP_API_KEY_SERVICE_TOKEN_HEADER")
    )
    config.api_key_service_token = (
        args.api_key_service_token
        or os.environ.get("UNITY_MCP_API_KEY_SERVICE_TOKEN")
    )

    # Validate: remote-hosted HTTP mode requires API key validation URL
    if config.http_remote_hosted and config.transport_mode == "http" and not config.api_key_validation_url:
        logger.error(
            "--http-remote-hosted requires --api-key-validation-url or "
            "UNITY_MCP_API_KEY_VALIDATION_URL environment variable"
        )
        raise SystemExit(1)

    http_url = os.environ.get("UNITY_MCP_HTTP_URL", args.http_url)
    parsed_url = urlparse(http_url)

    # Allow individual host/port to override URL components
    http_host = args.http_host or os.environ.get(
        "UNITY_MCP_HTTP_HOST") or parsed_url.hostname or "127.0.0.1"

    # Safely parse optional environment port (may be None or non-numeric)
    _env_port_str = os.environ.get("UNITY_MCP_HTTP_PORT")
    try:
        _env_port = int(_env_port_str) if _env_port_str is not None else None
    except ValueError:
        logger.warning(
            "Invalid UNITY_MCP_HTTP_PORT value '%s', ignoring", _env_port_str)
        _env_port = None

    http_port = args.http_port or _env_port or parsed_url.port or 8080

    os.environ["UNITY_MCP_HTTP_HOST"] = http_host
    os.environ["UNITY_MCP_HTTP_PORT"] = str(http_port)

    # Optional lifecycle handshake for Unity-managed terminal launches
    if args.unity_instance_token:
        os.environ["UNITY_MCP_INSTANCE_TOKEN"] = args.unity_instance_token
    if args.pidfile:
        try:
            pid_dir = os.path.dirname(args.pidfile)
            if pid_dir:
                os.makedirs(pid_dir, exist_ok=True)
            with open(args.pidfile, "w", encoding="ascii") as f:
                f.write(str(os.getpid()))
        except Exception as exc:
            logger.warning(
                "Failed to write pidfile '%s': %s", args.pidfile, exc)

    if args.http_url != "http://127.0.0.1:8080":
        logger.info(f"HTTP URL set to: {http_url}")
    if args.http_host:
        logger.info(f"HTTP host override: {http_host}")
    if args.http_port:
        logger.info(f"HTTP port override: {http_port}")

    # Explicit CLI/env overrides always win
    project_scoped_tools_explicit = (
        bool(args.project_scoped_tools)
        or os.environ.get("UNITY_MCP_PROJECT_SCOPED_TOOLS", "").lower() in ("true", "1", "yes", "on")
    )

    # If not explicitly set, check Unity status files for the default instance.
    # In stdio mode there is typically only one instance, so "first match wins" is fine.
    project_scoped_tools = project_scoped_tools_explicit
    if not project_scoped_tools_explicit:
        try:
            from transport.legacy.unity_connection import get_unity_connection_pool
            pool = get_unity_connection_pool()
            instances = pool.discover_all_instances()
            # If ANY discovered instance requests project-scoped tools, enable them
            for inst in instances:
                if getattr(inst, "project_scoped_tools", False):
                    project_scoped_tools = True
                    logger.info(
                        "Enabling project-scoped tools because Unity instance %s requested it",
                        inst.id,
                    )
                    break
        except Exception:
            logger.debug("Could not discover Unity instances for project-scoped tool default", exc_info=True)

    mcp = create_mcp_server(project_scoped_tools)

    # Determine transport mode
    if config.transport_mode == 'http':
        # Use HTTP transport for FastMCP
        transport = 'http'
        # Use the parsed host and port from URL/args
        http_url = os.environ.get("UNITY_MCP_HTTP_URL", args.http_url)
        parsed_url = urlparse(http_url)
        host = args.http_host or os.environ.get(
            "UNITY_MCP_HTTP_HOST") or parsed_url.hostname or "127.0.0.1"
        port = args.http_port or _env_port or parsed_url.port or 8080
        logger.info(f"Starting FastMCP with HTTP transport on {host}:{port}")
        from transport.http_server import run_http_server
        run_http_server(mcp, host, port)
    else:
        # Use stdio transport for traditional MCP
        logger.info("Starting FastMCP with stdio transport")
        mcp.run(transport='stdio')


# Run the server
if __name__ == "__main__":
    main()
