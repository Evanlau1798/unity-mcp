"""WebSocket hub for Unity plugin communication."""

from __future__ import annotations

import asyncio
import logging
import time
from typing import Any


from core.config import config
from models.models import MCPResponse

from transport.models import SessionList

logger = logging.getLogger(__name__)


from transport.plugin_errors import NoUnitySessionError, InstanceSelectionRequiredError

class PluginRoutingMixin:
    @staticmethod
    def _unavailable_retry_response(reason: str = "no_unity_session") -> dict[str, Any]:
        return MCPResponse(
            success=False,
            error="Unity session not available; please retry",
            hint="retry",
            data={"reason": reason, "retry_after_ms": 250, "phase": "session_resolve",
                  "execution_may_have_started": False},
        ).model_dump()


    @classmethod
    async def _resolve_session_id(
        cls,
        unity_instance: str | None,
        user_id: str | None = None,
        retry_on_reload: bool = True,
    ) -> str:
        """Resolve a project hash (Unity instance id) to an active plugin session.

        During Unity domain reloads the plugin's WebSocket session is torn down
        and reconnected shortly afterwards. Instead of failing immediately when
        no sessions are available, we wait for a bounded period for a plugin
        to reconnect so in-flight MCP calls can succeed transparently.

        Args:
            unity_instance: Target instance (Name@hash or hash)
            user_id: User ID from API key validation (for remote-hosted mode session isolation)
            retry_on_reload: If False, do not wait for reconnects when no session is present.
        """
        from transport import plugin_hub
        from transport.plugin_hub import _read_bounded_wait_env
        config = plugin_hub.config
        if cls._registry is None:
            raise RuntimeError("Plugin registry not configured")

        # Bound waiting for Unity sessions. Default to 20s to handle domain reloads
        # (which can take 10-20s after test runs or script changes).
        #
        # NOTE: This wait can impact agentic workflows where domain reloads happen
        # frequently (e.g., after test runs, script compilation). The 20s default
        # balances handling slow reloads vs. avoiding unnecessary delays.
        #
        # TODO: Make this more deterministic by detecting Unity's actual reload state
        # (e.g., via status file, heartbeat, or explicit "reloading" signal from Unity)
        # rather than blindly waiting up to 20s. See Issue #657.
        #
        # Configurable via: UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S (default: 20.0, max: 120.0).
        # The ceiling used to equal the default, which silently neutered the override for
        # projects whose reloads/test boundaries legitimately exceed 20s (#1207).
        max_wait_s = _read_bounded_wait_env(
            "UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S", default_s=20.0, max_s=120.0)
        if not retry_on_reload:
            max_wait_s = 0.0
        retry_ms = float(getattr(config, "reload_retry_ms", 250))
        sleep_seconds = max(0.05, min(0.25, retry_ms / 1000.0))

        # Allow callers to provide either just the hash or Name@hash
        target_hash: str | None = None
        if unity_instance:
            if "@" in unity_instance:
                _, _, suffix = unity_instance.rpartition("@")
                target_hash = suffix or None
            else:
                target_hash = unity_instance

        async def _try_once() -> tuple[str | None, int, bool]:
            explicit_required = config.http_remote_hosted
            # Prefer a specific Unity instance if one was requested
            if target_hash:
                # In remote-hosted mode with user_id, use user-scoped lookup
                if config.http_remote_hosted and user_id:
                    session_id = await cls._registry.get_session_id_by_hash(target_hash, user_id)
                    sessions = await cls._registry.list_sessions(user_id=user_id)
                else:
                    session_id = await cls._registry.get_session_id_by_hash(target_hash)
                    sessions = await cls._registry.list_sessions(user_id=user_id)
                return session_id, len(sessions), explicit_required

            # No target provided: determine if we can auto-select
            # In remote-hosted mode, filter sessions by user_id
            sessions = await cls._registry.list_sessions(user_id=user_id)
            count = len(sessions)
            if count == 0:
                return None, count, explicit_required
            if explicit_required:
                return None, count, explicit_required
            if count == 1:
                return next(iter(sessions.keys())), count, explicit_required
            # Multiple sessions but no explicit target is ambiguous
            return None, count, explicit_required

        async def _available_instance_ids() -> list[str]:
            # Error path only; one extra registry read keeps the refusal actionable.
            try:
                sessions = await cls._registry.list_sessions(user_id=user_id)
                return sorted(
                    f"{s.project_name}@{s.project_hash}" for s in sessions.values())
            except Exception:
                return []

        session_id, session_count, explicit_required = await _try_once()
        if session_id is None and explicit_required and not target_hash and session_count > 0:
            raise InstanceSelectionRequiredError(
                available_instances=await _available_instance_ids())
        deadline = time.monotonic() + max_wait_s
        wait_started = None

        # If there is no active plugin yet (e.g., Unity starting up or reloading),
        # wait politely for a session to appear before surfacing an error.
        while session_id is None and time.monotonic() < deadline:
            if not target_hash and session_count > 1:
                raise InstanceSelectionRequiredError(
                    InstanceSelectionRequiredError._MULTIPLE_INSTANCES,
                    available_instances=await _available_instance_ids())
            if session_id is None and explicit_required and not target_hash and session_count > 0:
                raise InstanceSelectionRequiredError(
                    available_instances=await _available_instance_ids())
            if wait_started is None:
                wait_started = time.monotonic()
                logger.debug(
                    "No plugin session available (instance=%s); waiting up to %.2fs",
                    unity_instance or "default",
                    max_wait_s,
                )
            await asyncio.sleep(sleep_seconds)
            session_id, session_count, explicit_required = await _try_once()

        if session_id is not None and wait_started is not None:
            logger.debug(
                "Plugin session restored after %.3fs (instance=%s)",
                time.monotonic() - wait_started,
                unity_instance or "default",
            )
        if session_id is None and not target_hash and session_count > 1:
            raise InstanceSelectionRequiredError(
                InstanceSelectionRequiredError._MULTIPLE_INSTANCES)

        if session_id is None and explicit_required and not target_hash and session_count > 0:
            raise InstanceSelectionRequiredError()

        if session_id is None:
            logger.warning(
                "No Unity plugin reconnected within %.2fs (instance=%s)",
                max_wait_s,
                unity_instance or "default",
            )
            # At this point we've given the plugin ample time to reconnect; surface
            # a clear error so the client can prompt the user to open Unity.
            raise NoUnitySessionError(
                "No Unity plugins are currently connected")

        return session_id


    @classmethod
    async def send_command_for_instance(
        cls,
        unity_instance: str | None,
        command_type: str,
        params: dict[str, Any],
        user_id: str | None = None,
        retry_on_reload: bool = True,
    ) -> dict[str, Any]:
        """Send a command to a Unity instance.

        Args:
            unity_instance: Target instance (Name@hash or hash)
            command_type: Command type to execute
            params: Command parameters
            user_id: User ID for session isolation in remote-hosted mode
            retry_on_reload: If False, do not wait for session reconnect on reload.
        """
        from transport.plugin_hub import _read_bounded_wait_env
        try:
            session_id = await cls._resolve_session_id(
                unity_instance,
                user_id=user_id,
                retry_on_reload=retry_on_reload,
            )
        except NoUnitySessionError:
            logger.debug(
                "Unity session unavailable; returning retry: command=%s instance=%s",
                command_type,
                unity_instance or "default",
            )
            return cls._unavailable_retry_response("no_unity_session")

        if not await cls._ensure_live_connection(session_id):
            if not retry_on_reload:
                return cls._unavailable_retry_response("stale_connection")
            try:
                session_id = await cls._resolve_session_id(
                    unity_instance,
                    user_id=user_id,
                    retry_on_reload=True,
                )
            except NoUnitySessionError:
                return cls._unavailable_retry_response("no_unity_session")
            if not await cls._ensure_live_connection(session_id):
                return cls._unavailable_retry_response("stale_connection")

        # During domain reload / immediate reconnect windows, the plugin may be connected but not yet
        # ready to process execute commands on the Unity main thread (which can be further delayed when
        # the Unity Editor is unfocused). For fast-path commands, we do a bounded readiness probe using
        # a main-thread ping command (handled by TransportCommandDispatcher) rather than waiting on
        # register_tools (which can be delayed by EditorApplication.delayCall).
        if retry_on_reload and command_type in cls._FAST_FAIL_COMMANDS and command_type != "ping":
            max_wait_s = _read_bounded_wait_env(
                "UNITY_MCP_SESSION_READY_WAIT_SECONDS", default_s=6.0, max_s=120.0)
            if max_wait_s > 0:
                readiness_started = time.monotonic()
                deadline = readiness_started + max_wait_s
                while time.monotonic() < deadline:
                    try:
                        probe = await asyncio.wait_for(cls.send_command(session_id, "ping", {}),
                                                       timeout=max(0, deadline - time.monotonic()))
                    except Exception:
                        probe = None

                    # The Unity-side dispatcher responds with {status:"success", result:{message:"pong"}}
                    if isinstance(probe, dict) and probe.get("status") == "success":
                        result = probe.get("result") if isinstance(
                            probe.get("result"), dict) else {}
                        if result.get("message") == "pong":
                            break
                    await asyncio.sleep(min(0.1, max(0, deadline - time.monotonic())))
                else:
                    # Not ready within the bounded window: return retry hint without sending.
                    return MCPResponse(
                        success=False,
                        error=f"Unity session not ready for '{command_type}' (ping not answered); please retry",
                        hint="retry",
                        data={"phase": "editor_readiness", "reason": "ping_not_answered",
                              "elapsed_ms": round((time.monotonic() - readiness_started) * 1000),
                              "execution_may_have_started": False, "editor_state": "unknown"},
                    ).model_dump()

        return await cls.send_command(session_id, command_type, params)


    @classmethod
    def _run_coroutine_sync(cls, coro: "asyncio.Future[Any]") -> Any:
        if cls._loop is None:
            raise RuntimeError("PluginHub event loop not configured")
        loop = cls._loop
        if loop.is_running():
            try:
                running_loop = asyncio.get_running_loop()
            except RuntimeError:
                running_loop = None
            else:
                if running_loop is loop:
                    raise RuntimeError(
                        "Cannot wait synchronously for PluginHub coroutine from within the event loop"
                    )
        future = asyncio.run_coroutine_threadsafe(coro, loop)
        return future.result()


    @classmethod
    def send_command_blocking(
        cls,
        unity_instance: str | None,
        command_type: str,
        params: dict[str, Any],
    ) -> dict[str, Any]:
        return cls._run_coroutine_sync(
            cls.send_command_for_instance(unity_instance, command_type, params)
        )


    @classmethod
    def list_sessions_sync(cls) -> SessionList:
        return cls._run_coroutine_sync(cls.get_sessions())
