"""WebSocket hub for Unity plugin communication."""

from __future__ import annotations

import asyncio
import logging
import os
import time
import uuid
import weakref
from typing import TYPE_CHECKING, Any, ClassVar

from starlette.endpoints import WebSocketEndpoint
from starlette.websockets import WebSocket, WebSocketState

from core.config import config
from core.constants import API_KEY_HEADER
from models.models import MCPResponse
from transport.plugin_registry import PluginRegistry
from transport.blender_timeout import blender_command_timeout, SERVER_RESPONSE_GRACE
from services.api_key_service import ApiKeyService

if TYPE_CHECKING:
    from fastmcp import FastMCP
from transport.models import (
    WelcomeMessage,
    RegisteredMessage,
    ExecuteCommandMessage,
    PingMessage,
    RegisterMessage,
    RegisterToolsMessage,
    PongMessage,
    CommandResultMessage,
    SessionList,
    SessionDetails,
)

logger = logging.getLogger(__name__)


def _read_bounded_wait_env(name: str, default_s: float, max_s: float) -> float:
    """Read a wait-seconds env override, clamped to [0, max_s].

    The ceiling exists to keep a typo from stalling every command, but it must sit
    well above the default so explicit overrides actually take effect (#1207).
    """
    raw = os.environ.get(name)
    if raw is None:
        return max(0.0, min(default_s, max_s))
    try:
        value = float(raw)
    except ValueError as e:
        logger.warning("Invalid %s=%r, using default %s: %s", name, raw, default_s, e)
        value = default_s
    return max(0.0, min(value, max_s))


# ---------- MCP session tracking ----------
# FastMCP doesn't expose active MCP client sessions.  We patch
# ``MiddlewareServerSession.__aenter__`` once to register every new
# session so we can send ``tools/list_changed`` notifications later.
_active_mcp_sessions: weakref.WeakSet = weakref.WeakSet()
_session_tracking_installed = False


def _install_session_tracking() -> None:
    """Patch *MiddlewareServerSession* to track active MCP client sessions."""
    global _session_tracking_installed
    if _session_tracking_installed:
        return
    _session_tracking_installed = True

    from fastmcp.server.low_level import MiddlewareServerSession

    _original_aenter = MiddlewareServerSession.__aenter__

    async def _tracking_aenter(self):  # type: ignore[override]
        result = await _original_aenter(self)
        _active_mcp_sessions.add(self)
        return result

    MiddlewareServerSession.__aenter__ = _tracking_aenter  # type: ignore[assignment]


from transport.plugin_errors import PluginDisconnectedError, NoUnitySessionError, InstanceSelectionRequiredError, CommandDeadlineError
from transport.plugin_connections import PluginConnectionsMixin
from transport.plugin_registration import PluginRegistrationMixin
from transport.plugin_routing import PluginRoutingMixin

class PluginHub(PluginConnectionsMixin, PluginRegistrationMixin, PluginRoutingMixin, WebSocketEndpoint):
    """Manages persistent WebSocket connections to Unity plugins."""

    encoding = "json"
    KEEP_ALIVE_INTERVAL = 15
    SERVER_TIMEOUT = 30
    COMMAND_TIMEOUT = 30
    # Server-side ping interval (seconds) - how often to send pings to Unity
    PING_INTERVAL = 10
    # Max time (seconds) to wait for pong before considering connection dead
    PING_TIMEOUT = 20
    # Timeout (seconds) for fast-fail commands like ping/read_console/get_editor_state.
    # Keep short so MCP clients aren't blocked during Unity compilation/reload/unfocused throttling.
    FAST_FAIL_TIMEOUT = 2.0
    # Fast-path commands should never block the client for long; return a retry hint instead.
    # This helps avoid the Cursor-side ~30s tool-call timeout when Unity is compiling/reloading
    # or is throttled while unfocused.
    _FAST_FAIL_COMMANDS: set[str] = {
        "read_console", "get_editor_state", "ping"}

    _registry: PluginRegistry | None = None
    _mcp: FastMCP | None = None
    # Index into mcp._transforms where Unity's server-level overrides start.
    # Transforms before this index are startup defaults; at and after are Unity syncs.
    _unity_transform_start: int | None = None
    _connections: dict[str, WebSocket] = {}
    # command_id -> {"future": Future, "session_id": str}
    _pending: dict[str, dict[str, Any]] = {}
    _lock: asyncio.Lock | None = None
    _loop: asyncio.AbstractEventLoop | None = None
    # session_id -> last pong timestamp (monotonic)
    _last_pong: ClassVar[dict[str, float]] = {}
    # session_id -> ping task
    _ping_tasks: ClassVar[dict[str, asyncio.Task]] = {}

    @classmethod
    def configure(
        cls,
        registry: PluginRegistry,
        loop: asyncio.AbstractEventLoop | None = None,
        mcp: FastMCP | None = None,
    ) -> None:
        cls._registry = registry
        cls._mcp = mcp
        cls._loop = loop or asyncio.get_running_loop()
        # Ensure coordination primitives are bound to the configured loop
        cls._lock = asyncio.Lock()
        # Start tracking MCP client sessions for tool-change notifications
        if mcp is not None:
            _install_session_tracking()

    @classmethod
    def is_configured(cls) -> bool:
        return cls._registry is not None and cls._lock is not None


    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------
    @classmethod
    async def send_command(cls, session_id: str, command_type: str, params: dict[str, Any]) -> dict[str, Any]:
        websocket = await cls._get_connection(session_id)
        command_id = str(uuid.uuid4())
        future: asyncio.Future = asyncio.get_running_loop().create_future()
        # Compute a per-command timeout:
        # - fast-path commands: short timeout (encourage retry)
        # - long-running commands: allow caller to request a longer timeout via params
        unity_timeout_s = float(cls.COMMAND_TIMEOUT)
        server_wait_s = float(cls.COMMAND_TIMEOUT)
        if command_type == "blender_bridge":
            # The socket's default is 180s even when the caller omits the field.
            # Importing/placing its result in Unity happens after that socket call.
            unity_timeout_s = blender_command_timeout(params)
            server_wait_s = unity_timeout_s + SERVER_RESPONSE_GRACE
        elif command_type in cls._FAST_FAIL_COMMANDS:
            fast_timeout = float(cls.FAST_FAIL_TIMEOUT)
            unity_timeout_s = fast_timeout
            server_wait_s = fast_timeout
        else:
            # Common tools pass a requested timeout in seconds (e.g., timeout_seconds=900).
            requested = None
            try:
                if isinstance(params, dict):
                    requested = params.get("timeout_seconds", None)
                    if requested is None:
                        requested = params.get("timeoutSeconds", None)
            except Exception:
                requested = None

            if requested is not None:
                try:
                    requested_s = float(requested)
                    # Clamp to a sane upper bound to avoid accidental infinite hangs.
                    requested_s = max(1.0, min(requested_s, 60.0 * 60.0))
                    unity_timeout_s = max(unity_timeout_s, requested_s)
                    # Give the server a small cushion beyond the Unity-side timeout to account for transport overhead.
                    server_wait_s = max(server_wait_s, requested_s + 5.0)
                except Exception:
                    pass

        lock = cls._lock
        if lock is None:
            raise RuntimeError("PluginHub not configured")

        async with lock:
            if command_id in cls._pending:
                raise RuntimeError(
                    f"Duplicate command id generated: {command_id}")
            cls._pending[command_id] = {
                "future": future, "session_id": session_id}

        started = time.monotonic()
        deadline = started + server_wait_s
        phase = "transport_send"
        try:
            msg = ExecuteCommandMessage(
                id=command_id,
                name=command_type,
                params=params,
                timeout=unity_timeout_s,
            )
            await asyncio.wait_for(websocket.send_json(msg.model_dump()), timeout=max(0, deadline - time.monotonic()))
            phase = "command_response"
            result = await asyncio.wait_for(future, timeout=max(0, deadline - time.monotonic()))
            if time.monotonic() - started >= 2:
                logger.info("Unity command completed: id=%s session=%s command=%s elapsed_ms=%d",
                            command_id, session_id, command_type, (time.monotonic() - started) * 1000)
            return result
        except PluginDisconnectedError as exc:
            return MCPResponse(success=False, error=str(exc), hint="check_state_before_retry",
                               data={"phase": phase, "execution_may_have_started": True,
                                     "command_id": command_id, "session_id": session_id}).model_dump()
        except asyncio.TimeoutError as exc:
            diagnostics = {"phase": phase, "command_id": command_id, "session_id": session_id,
                           "elapsed_ms": round((time.monotonic() - started) * 1000),
                           "timeout_ms": round(server_wait_s * 1000), "execution_may_have_started": True}
            if command_type in cls._FAST_FAIL_COMMANDS:
                return MCPResponse(
                    success=False,
                    error=f"Unity did not respond to '{command_type}' within {server_wait_s:.1f}s; please retry",
                    hint="retry", data=diagnostics,
                ).model_dump()
            raise CommandDeadlineError(command_type, diagnostics) from exc
        finally:
            # Cancellation/send failure can arrive before this Future has ever been awaited.
            if not future.done():
                future.cancel()
            elif not future.cancelled():
                future.exception()
            async with lock:
                cls._pending.pop(command_id, None)

    @classmethod
    async def get_sessions(cls, user_id: str | None = None) -> SessionList:
        """Get all active plugin sessions.

        Args:
            user_id: If provided (remote-hosted mode), only return sessions for this user.
        """
        if cls._registry is None:
            return SessionList(sessions={})
        sessions = await cls._registry.list_sessions(user_id=user_id)
        return SessionList(
            sessions={
                session_id: SessionDetails(
                    project=session.project_name,
                    hash=session.project_hash,
                    unity_version=session.unity_version,
                    connected_at=session.connected_at.isoformat(),
                )
                for session_id, session in sessions.items()
            }
        )

    @classmethod
    async def get_tools_for_project(
        cls,
        project_hash: str,
        user_id: str | None = None,
    ) -> list[Any]:
        """Retrieve tools registered for an active project hash."""
        if cls._registry is None:
            return []

        session_id = await cls._registry.get_session_id_by_hash(project_hash, user_id=user_id)
        if not session_id:
            return []

        session = await cls._registry.get_session(session_id)
        if not session:
            return []

        return list(session.tools.values())

    @classmethod
    async def get_tool_definition(
        cls,
        project_hash: str,
        tool_name: str,
        user_id: str | None = None,
    ) -> Any | None:
        """Retrieve a specific tool definition for an active project hash."""
        if cls._registry is None:
            return None

        session_id = await cls._registry.get_session_id_by_hash(project_hash, user_id=user_id)
        if not session_id:
            return None

        session = await cls._registry.get_session(session_id)
        if not session:
            return None

        return session.tools.get(tool_name)


def send_command_to_plugin(
    *,
    unity_instance: str | None,
    command_type: str,
    params: dict[str, Any],
) -> dict[str, Any]:
    return PluginHub.send_command_blocking(unity_instance, command_type, params)
