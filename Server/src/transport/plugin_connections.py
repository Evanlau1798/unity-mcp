"""WebSocket hub for Unity plugin communication."""

from __future__ import annotations

import asyncio
import logging
import time
from typing import Any

from starlette.websockets import WebSocket, WebSocketState

from core.config import config
from core.constants import API_KEY_HEADER
from services.api_key_service import ApiKeyService

from transport.models import WelcomeMessage, PingMessage, RegisterMessage, RegisterToolsMessage, PongMessage, CommandResultMessage

logger = logging.getLogger(__name__)


from transport.plugin_errors import PluginDisconnectedError

class PluginConnectionsMixin:
    async def on_connect(self, websocket: WebSocket) -> None:
        # Validate API key in remote-hosted mode (fail closed)
        if config.http_remote_hosted:
            if not ApiKeyService.is_initialized():
                logger.debug(
                    "WebSocket connection rejected: auth service not initialized")
                await websocket.close(code=1013, reason="Try again later")
                return

            api_key = websocket.headers.get(API_KEY_HEADER)

            if not api_key:
                logger.debug("WebSocket connection rejected: API key required")
                await websocket.close(code=4401, reason="API key required")
                return

            service = ApiKeyService.get_instance()
            result = await service.validate(api_key)

            if not result.valid:
                # Transient auth failures are retryable (1013)
                if result.error and any(
                    indicator in result.error.lower()
                    for indicator in ("unavailable", "timeout", "service error")
                ):
                    logger.debug(
                        "WebSocket connection rejected: auth service unavailable")
                    await websocket.close(code=1013, reason="Try again later")
                    return

                logger.debug("WebSocket connection rejected: invalid API key")
                await websocket.close(code=4403, reason="Invalid API key")
                return

            # Both valid and user_id must be present to accept
            if not result.user_id:
                logger.debug(
                    "WebSocket connection rejected: validated key missing user_id")
                await websocket.close(code=4403, reason="Invalid API key")
                return

            # Store user_id in websocket state for later use during registration
            websocket.state.user_id = result.user_id
            websocket.state.api_key_metadata = result.metadata

        await websocket.accept()
        msg = WelcomeMessage(
            serverTimeout=self.SERVER_TIMEOUT,
            keepAliveInterval=self.KEEP_ALIVE_INTERVAL,
        )
        await websocket.send_json(msg.model_dump())


    async def on_receive(self, websocket: WebSocket, data: Any) -> None:
        if not isinstance(data, dict):
            logger.warning(f"Received non-object payload from plugin: {data}")
            return

        message_type = data.get("type")
        try:
            if message_type == "register":
                await self._handle_register(websocket, RegisterMessage(**data))
            elif message_type == "register_tools":
                await self._handle_register_tools(websocket, RegisterToolsMessage(**data))
            elif message_type == "pong":
                await self._handle_pong(PongMessage(**data))
            elif message_type == "command_result":
                await self._handle_command_result(CommandResultMessage(**data))
            else:
                logger.debug(f"Ignoring plugin message: {data}")
        except Exception as e:
            logger.error(f"Error handling message type {message_type}: {e}")


    async def on_disconnect(self, websocket: WebSocket, close_code: int) -> None:
        cls = type(self)
        lock = cls._lock
        if lock is None:
            return
        async with lock:
            session_id = next(
                (sid for sid, ws in cls._connections.items() if ws is websocket), None)
            if session_id:
                cls._connections.pop(session_id, None)
                # Stop the ping loop for this session
                ping_task = cls._ping_tasks.pop(session_id, None)
                if ping_task and not ping_task.done():
                    ping_task.cancel()
                # Clean up last pong tracking
                cls._last_pong.pop(session_id, None)
                # Fail-fast any in-flight commands for this session to avoid waiting for COMMAND_TIMEOUT.
                pending_ids = [
                    command_id
                    for command_id, entry in cls._pending.items()
                    if entry.get("session_id") == session_id
                ]
                if pending_ids:
                    logger.debug(f"Cancelling {len(pending_ids)} pending commands for disconnected session")
                for command_id in pending_ids:
                    entry = cls._pending.get(command_id)
                    future = entry.get("future") if isinstance(
                        entry, dict) else None
                    if future and not future.done():
                        future.set_exception(
                            PluginDisconnectedError(
                                f"Unity plugin session {session_id} disconnected while awaiting command_result"
                            )
                        )
                if cls._registry:
                    await cls._registry.unregister(session_id)
                logger.info(
                    f"Plugin session {session_id} disconnected ({close_code})")


    async def _handle_command_result(self, payload: CommandResultMessage) -> None:
        cls = type(self)
        lock = cls._lock
        if lock is None:
            return
        command_id = payload.id
        result = payload.result

        if not command_id:
            logger.warning(f"Command result missing id: {payload}")
            return

        async with lock:
            entry = cls._pending.get(command_id)
        future = entry.get("future") if isinstance(entry, dict) else None
        if future and not future.done():
            future.set_result(result)


    async def _handle_pong(self, payload: PongMessage) -> None:
        cls = type(self)
        registry = cls._registry
        lock = cls._lock
        if registry is None:
            return
        session_id = payload.session_id
        if session_id:
            await registry.touch(session_id)
            # Record last pong time for staleness detection (under lock for consistency)
            if lock is not None:
                async with lock:
                    cls._last_pong[session_id] = time.monotonic()


    @classmethod
    async def _ping_loop(cls, session_id: str, websocket: WebSocket) -> None:
        """Server-initiated ping loop to detect dead connections.

        Sends periodic pings to the Unity client. If no pong is received within
        PING_TIMEOUT seconds, the connection is considered dead and closed.
        This helps detect connections that die silently (e.g., Windows OSError 64).
        """
        logger.debug(f"[Ping] Starting ping loop for session {session_id}")
        try:
            while True:
                await asyncio.sleep(cls.PING_INTERVAL)

                # Check if we're still supposed to be running and get last pong time (under lock)
                lock = cls._lock
                if lock is None:
                    break
                async with lock:
                    if session_id not in cls._connections:
                        logger.debug(f"[Ping] Session {session_id} no longer in connections, stopping ping loop")
                        break
                    # Read last pong time under lock for consistency
                    last_pong = cls._last_pong.get(session_id, 0)

                # Check staleness: has it been too long since we got a pong?
                elapsed = time.monotonic() - last_pong
                if elapsed > cls.PING_TIMEOUT:
                    logger.warning(
                        f"[Ping] Session {session_id} stale: no pong for {elapsed:.1f}s "
                        f"(timeout={cls.PING_TIMEOUT}s). Closing connection."
                    )
                    try:
                        await websocket.close(code=1001)  # Going away
                    except Exception as close_ex:
                        logger.debug(f"[Ping] Error closing stale websocket: {close_ex}")
                    break

                # Send a ping to the client
                try:
                    ping_msg = PingMessage()
                    await websocket.send_json(ping_msg.model_dump())
                    logger.debug(f"[Ping] Sent ping to session {session_id}")
                except Exception as send_ex:
                    # Send failed - connection is dead
                    logger.warning(
                        f"[Ping] Failed to send ping to session {session_id}: {send_ex}. "
                        "Connection likely dead."
                    )
                    try:
                        await websocket.close(code=1006)  # Abnormal closure
                    except Exception:
                        pass
                    break

        except asyncio.CancelledError:
            logger.debug(f"[Ping] Ping loop cancelled for session {session_id}")
        except Exception as ex:
            logger.warning(f"[Ping] Ping loop error for session {session_id}: {ex}")
        finally:
            logger.debug(f"[Ping] Ping loop ended for session {session_id}")


    @classmethod
    async def _get_connection(cls, session_id: str) -> WebSocket:
        lock = cls._lock
        if lock is None:
            raise RuntimeError("PluginHub not configured")
        async with lock:
            websocket = cls._connections.get(session_id)
        if websocket is None:
            raise RuntimeError(f"Plugin session {session_id} not connected")
        return websocket


    @classmethod
    async def _evict_connection(cls, session_id: str, reason: str) -> None:
        """Drop a stale session from in-memory maps and registry."""
        lock = cls._lock
        if lock is None:
            return

        websocket: WebSocket | None = None
        ping_task: asyncio.Task | None = None
        pending_futures: list[asyncio.Future] = []
        async with lock:
            websocket = cls._connections.pop(session_id, None)
            ping_task = cls._ping_tasks.pop(session_id, None)
            cls._last_pong.pop(session_id, None)
            keys_to_remove: list[object] = []
            for key, entry in list(cls._pending.items()):
                if entry.get("session_id") == session_id:
                    future = entry.get("future")
                    if future and not future.done():
                        pending_futures.append(future)
                    keys_to_remove.append(key)
            for key in keys_to_remove:
                cls._pending.pop(key, None)

        if ping_task is not None and not ping_task.done():
            ping_task.cancel()

        for future in pending_futures:
            if not future.done():
                future.set_exception(
                    PluginDisconnectedError(
                        f"Unity plugin session {session_id} disconnected while awaiting command_result"
                    )
                )

        if websocket is not None:
            try:
                await websocket.close(code=1001)
            except Exception as close_ex:
                logger.debug("Error closing evicted WebSocket for session %s: %s", session_id, close_ex)

        if cls._registry is not None:
            try:
                await cls._registry.unregister(session_id)
            except Exception:
                logger.debug(
                    "Failed to unregister evicted plugin session %s",
                    session_id,
                    exc_info=True,
                )

        logger.debug("Evicted plugin session %s (%s)", session_id, reason)


    @classmethod
    async def _ensure_live_connection(cls, session_id: str) -> bool:
        """Best-effort pre-send liveness check for a plugin WebSocket."""
        try:
            websocket = await cls._get_connection(session_id)
        except RuntimeError:
            await cls._evict_connection(session_id, "missing_websocket")
            return False

        if (
            websocket.client_state == WebSocketState.CONNECTED
            and websocket.application_state == WebSocketState.CONNECTED
        ):
            return True

        logger.debug(
            "Detected stale plugin connection before send: session=%s app_state=%s client_state=%s",
            session_id,
            websocket.application_state,
            websocket.client_state,
        )
        await cls._evict_connection(session_id, "stale_websocket_state")
        return False
