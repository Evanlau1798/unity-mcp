"""WebSocket hub for Unity plugin communication."""

from __future__ import annotations

import asyncio
import logging
import time
import uuid

from starlette.websockets import WebSocket


from transport.models import RegisteredMessage, RegisterMessage, RegisterToolsMessage

logger = logging.getLogger(__name__)


from transport.plugin_errors import PluginDisconnectedError

class PluginRegistrationMixin:
    async def _handle_register(self, websocket: WebSocket, payload: RegisterMessage) -> None:
        cls = type(self)
        registry = cls._registry
        lock = cls._lock
        if registry is None or lock is None:
            await websocket.close(code=1011)
            raise RuntimeError("PluginHub not configured")

        project_name = payload.project_name
        project_hash = payload.project_hash
        unity_version = payload.unity_version
        project_path = payload.project_path

        if not project_hash:
            await websocket.close(code=4400)
            raise ValueError(
                "Plugin registration missing project_hash")

        # Get user_id from websocket state (set during API key validation)
        user_id = getattr(websocket.state, "user_id", None)

        session_id = str(uuid.uuid4())
        # Inform the plugin of its assigned session ID
        response = RegisteredMessage(session_id=session_id)
        await websocket.send_json(response.model_dump())

        session, evicted_session_id = await registry.register(
            session_id, project_name, project_hash, unity_version, project_path, user_id=user_id,
            editor_pid=payload.editor_pid, editor_started_ms=payload.editor_started_ms)
        evicted_ws = None
        async with lock:
            # Clean up the evicted session's connection, ping loop, and pending commands
            # so they don't linger as orphans after a domain-reload reconnection race.
            if evicted_session_id:
                evicted_ws = cls._connections.pop(evicted_session_id, None)
                old_ping = cls._ping_tasks.pop(evicted_session_id, None)
                if old_ping and not old_ping.done():
                    old_ping.cancel()
                cls._last_pong.pop(evicted_session_id, None)
                cancelled_commands = []
                for command_id, entry in list(cls._pending.items()):
                    if entry.get("session_id") == evicted_session_id:
                        future = entry.get("future")
                        if future and not future.done():
                            future.set_exception(
                                PluginDisconnectedError(
                                    f"Unity plugin session {evicted_session_id} superseded by {session_id}"
                                )
                            )
                            cancelled_commands.append(command_id)
                        cls._pending.pop(command_id, None)
                if cancelled_commands:
                    logger.info(
                        "Evicted session %s: cancelled pending commands %s",
                        evicted_session_id,
                        cancelled_commands,
                    )
                logger.info(f"Evicted previous session {evicted_session_id} for same instance")

            cls._connections[session.session_id] = websocket
            # Initialize last pong time and start ping loop for this session
            cls._last_pong[session_id] = time.monotonic()
            # Cancel any existing ping task for this session (shouldn't happen, but be safe)
            old_task = cls._ping_tasks.pop(session_id, None)
            if old_task and not old_task.done():
                old_task.cancel()
            # Start the server-side ping loop
            ping_task = asyncio.create_task(cls._ping_loop(session_id, websocket))
            cls._ping_tasks[session_id] = ping_task

        # Close evicted WebSocket outside the lock to avoid blocking
        if evicted_ws is not None:
            try:
                await evicted_ws.close(code=1001)
            except Exception:
                logger.debug(
                    "Failed to close evicted WebSocket for session %s",
                    evicted_session_id,
                    exc_info=True,
                )

        if user_id:
            logger.info(f"Plugin registered: {project_name} ({project_hash}) for user {user_id}")
        else:
            logger.info(f"Plugin registered: {project_name} ({project_hash})")


    async def _handle_register_tools(self, websocket: WebSocket, payload: RegisterToolsMessage) -> None:
        cls = type(self)
        registry = cls._registry
        lock = cls._lock
        if registry is None or lock is None:
            return

        # Find session_id for this websocket
        async with lock:
            session_id = next(
                (sid for sid, ws in cls._connections.items() if ws is websocket), None)

        if not session_id:
            logger.warning("Received register_tools from unknown connection")
            return

        await registry.register_tools_for_session(session_id, payload.tools)
        logger.info(
            f"Registered {len(payload.tools)} tools for session {session_id}")

        # Sync server-level FastMCP visibility so new MCP client sessions
        # (e.g. new Claude Code conversations) see the correct tool set.
        self._sync_server_tool_visibility(payload.tools)

        # Notify any already-connected MCP clients (e.g. CC over stdio) that
        # the tool list has changed so they re-fetch.
        await cls._notify_mcp_tool_list_changed()

        try:
            from services.custom_tool_service import CustomToolService

            service = CustomToolService.get_instance()
            service.register_global_tools(payload.tools)
        except RuntimeError as exc:
            logger.debug(
                "Skipping global custom tool registration: CustomToolService not initialized yet (%s)",
                exc,
            )
        except Exception as exc:
            logger.warning(
                "Unexpected error during global custom tool registration; "
                "custom tools may not be available globally",
                exc_info=exc,
            )


    @classmethod
    def _sync_server_tool_visibility(cls, registered_tools: list) -> None:
        """Sync FastMCP server-level tool group visibility to match Unity's state.

        When Unity sends ``register_tools``, some groups may have been toggled
        on/off via the Unity Editor GUI.  We mirror that state at the FastMCP
        server level so that **new** MCP client sessions (e.g. a fresh Claude
        Code conversation) see the correct tool set without requiring
        ``manage_tools`` activation.

        The startup ``register_all_tools()`` disables non-default groups via
        ``mcp.disable(tags=...)``.  Here we append ``mcp.enable(tags=...)``
        transforms for groups that Unity has enabled, effectively overriding
        the startup defaults.  FastMCP processes transforms in order so later
        ``enable`` calls override earlier ``disable`` calls.
        """
        mcp = cls._mcp
        if mcp is None:
            return

        try:
            from services.registry import get_group_tool_names, TOOL_GROUPS

            registered_names: set[str] = set()
            for tool in registered_tools:
                name = getattr(tool, "name", None) if not isinstance(tool, dict) else tool.get("name")
                if isinstance(name, str) and name:
                    registered_names.add(name)

            group_tools = get_group_tool_names()

            # Reset Unity overrides: trim transforms back to where Unity started,
            # then re-apply based on current registered tools.
            if cls._unity_transform_start is not None:
                mcp._transforms = mcp._transforms[:cls._unity_transform_start]
            else:
                # First time: record where startup transforms end.
                cls._unity_transform_start = len(mcp._transforms)

            enabled_groups: list[str] = []
            disabled_groups: list[str] = []

            for group_name in sorted(TOOL_GROUPS.keys()):
                tool_names = group_tools.get(group_name, [])
                has_any_registered = any(n in registered_names for n in tool_names)

                if has_any_registered:
                    # Override the startup disable with an enable.
                    tag = f"group:{group_name}"
                    mcp.enable(tags={tag}, components={"tool"})
                    enabled_groups.append(group_name)
                else:
                    # Group not present in Unity's registered tools — disable it.
                    tag = f"group:{group_name}"
                    mcp.disable(tags={tag}, components={"tool"})
                    disabled_groups.append(group_name)

            if enabled_groups or disabled_groups:
                logger.info(
                    "Server-level tool visibility synced from Unity: "
                    "enabled=[%s], disabled=[%s], total_transforms=%d, unity_start=%d",
                    ", ".join(enabled_groups),
                    ", ".join(disabled_groups),
                    len(mcp._transforms),
                    cls._unity_transform_start or 0,
                )
        except Exception:
            logger.debug(
                "Failed to sync server-level tool visibility",
                exc_info=True,
            )


    @classmethod
    async def _notify_mcp_tool_list_changed(cls) -> None:
        """Send ``tools/list_changed`` to every connected MCP client session.

        After server-level tool visibility is updated (e.g. when Unity reports
        its registered tools), existing MCP clients (especially stdio-based
        ones like Claude Code) must be told to re-fetch the tool list.
        FastMCP's ``mcp.enable()``/``mcp.disable()`` update the server-level
        transforms but do **not** push notifications to already-connected
        sessions — we do that here.
        """
        from transport.plugin_hub import _active_mcp_sessions
        sessions = list(_active_mcp_sessions)
        if not sessions:
            return
        for session in sessions:
            try:
                await session.send_tool_list_changed()
            except Exception:
                logger.debug(
                    "Failed to notify MCP session of tool list change",
                    exc_info=True,
                )
        logger.info(
            "Sent tools/list_changed notification to %d MCP session(s)",
            len(sessions),
        )
