from __future__ import annotations

import asyncio
import logging
import os
import time
from collections.abc import Awaitable, Callable
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from models import MCPResponse
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.preflight import preflight
import transport.unity_transport as unity_transport
import transport.legacy.unity_connection as _legacy_conn

from services.state.external_changes_scanner import external_changes_scanner
import services.resources.editor_state as editor_state

logger = logging.getLogger(__name__)

from services.state.refresh_tracking import RefreshAttempt, pending, run_tracked_refresh

_REFRESH_WAIT_SECONDS = 60.0
_REFRESH_POLL_SECONDS = 0.5
_refresh_attempts: dict[str, RefreshAttempt] = {}
_refresh_locks: dict[str, asyncio.Lock] = {}

# Blocking reasons that indicate Unity is actually busy (not just stale status).
# Must match activityPhase values from EditorStateCache.cs
_REAL_BLOCKING_REASONS = {"compiling", "domain_reload", "running_tests", "asset_import", "asset_refresh"}


def _in_pytest() -> bool:
    """Return True when running inside pytest to avoid polling unmocked resources."""
    return "PYTEST_CURRENT_TEST" in os.environ


async def wait_for_editor_ready(ctx: Context, timeout_s: float = 30.0) -> tuple[bool, float]:
    """Poll editor_state until Unity is ready for tool calls.

    Returns (ready, elapsed_seconds).  Treats exceptions from
    get_editor_state as "not ready yet" so the loop survives transient
    connection errors during domain reload.
    """
    if _in_pytest():
        return (True, 0.0)

    start = time.monotonic()
    while time.monotonic() - start < timeout_s:
        try:
            state_resp = await editor_state.get_editor_state(ctx)
            state = state_resp.model_dump() if hasattr(state_resp, "model_dump") else state_resp
            data = (state or {}).get("data") if isinstance(state, dict) else None
            advice = (data or {}).get("advice") if isinstance(data, dict) else None
            if isinstance(advice, dict):
                if advice.get("ready_for_tools") is True:
                    return (True, time.monotonic() - start)
                blocking = set(advice.get("blocking_reasons") or [])
                if not (blocking & _REAL_BLOCKING_REASONS):
                    return (True, time.monotonic() - start)
        except Exception:
            pass  # not ready yet — keep polling
        await asyncio.sleep(0.25)

    return (False, time.monotonic() - start)


def is_reloading_rejection(resp: Any) -> bool:
    """True when Unity rejected a command because it thinks it is reloading.

    The command was never executed, so retrying is safe.
    """
    if not isinstance(resp, dict) or resp.get("success"):
        return False
    data = resp.get("data") or {}
    return data.get("reason") == "reloading" and resp.get("hint") == "retry"


def is_connection_lost_after_send(resp: Any) -> bool:
    """True when a mutation's response indicates TCP was lost after command was sent.

    Script mutations trigger domain reload which kills the TCP connection.
    The mutation was likely executed but the response was lost.
    """
    if isinstance(resp, dict):
        if resp.get("success"):
            return False
        err = (resp.get("error") or resp.get("message") or "").lower()
    else:
        if getattr(resp, "success", None):
            return False
        err = (getattr(resp, "error", "") or "").lower()
    return "connection closed" in err or "disconnected" in err or "aborted" in err


async def send_mutation(
    ctx: Context,
    unity_instance: str | None,
    command: str,
    params: dict[str, Any],
    *,
    verify_after_disconnect: Callable[[], Awaitable[dict | None]] | None = None,
) -> dict | Any:
    """Send a non-idempotent mutation with reload recovery.

    Handles the full retry/recovery pattern for script mutations:
    1. Guard readiness without refreshing, then send with retry_on_reload=False
    2. If reloading rejection (command never executed) → wait + retry once
    3. If connection lost after send → wait + verify via callback
    4. Wait for editor readiness before returning

    Args:
        verify_after_disconnect: async callable returning a replacement response
            dict if the mutation was verified after connection loss, or None to
            keep the original error response.
    """
    gate = await preflight(ctx, wait_for_no_compile=True, refresh_if_dirty=False)
    if gate is not None:
        return gate.model_dump()

    resp = await unity_transport.send_with_unity_instance(
        _legacy_conn.async_send_command_with_retry,
        unity_instance,
        command,
        params,
        retry_on_reload=False,
    )
    if is_reloading_rejection(resp):
        ready, _elapsed = await wait_for_editor_ready(ctx)
        if not ready:
            return resp
        resp = await unity_transport.send_with_unity_instance(
            _legacy_conn.async_send_command_with_retry,
            unity_instance,
            command,
            params,
            retry_on_reload=False,
        )
    if is_connection_lost_after_send(resp) and verify_after_disconnect:
        await wait_for_editor_ready(ctx)
        verified = await verify_after_disconnect()
        if verified is not None:
            resp = verified
    await wait_for_editor_ready(ctx)
    return resp


async def verify_edit_by_sha(
    unity_instance: str | None,
    name: str,
    path: str,
    pre_sha: str | None,
) -> bool:
    """Verify a script edit was applied by comparing SHA before and after.

    Returns True if the file's SHA changed (edit likely applied).
    """
    if not pre_sha:
        return False
    try:
        verify = await unity_transport.send_with_unity_instance(
            _legacy_conn.async_send_command_with_retry,
            unity_instance,
            "manage_script",
            {"action": "get_sha", "name": name, "path": path},
        )
        if isinstance(verify, dict) and verify.get("success"):
            new_sha = (verify.get("data") or {}).get("sha256")
            return bool(new_sha and new_sha != pre_sha)
    except Exception as exc:
        logger.debug(
            "Failed to verify edit after disconnect for %s at %s: %r",
            name, path, exc,
        )
    return False


@mcp_for_unity_tool(
    description="Request a tracked Unity refresh and optionally compilation. Completion requires a matching native receipt; recover with the same operation_id.",
    annotations=ToolAnnotations(
        title="Refresh Unity",
        destructiveHint=True,
    ),
)
async def refresh_unity(
    ctx: Context,
    mode: Annotated[Literal["if_dirty", "force"], "Refresh mode"] = "if_dirty",
    scope: Annotated[Literal["assets", "scripts", "all"],
                     "Refresh scope"] = "all",
    compile: Annotated[Literal["none", "request"],
                       "Whether to request compilation"] = "none",
    wait_for_ready: Annotated[bool,
                              "Wait for this refresh operation; timeout keeps operation_id for recovery."] = True,
    operation_id: Annotated[str | None, "Query an existing refresh operation without starting another."] = None,
) -> MCPResponse | dict[str, Any]:
    deadline = time.monotonic() + _REFRESH_WAIT_SECONDS
    unity_instance = await get_unity_instance_from_context(ctx)
    instance = unity_instance or await editor_state.infer_single_instance_id(ctx)
    if not instance:
        return MCPResponse(success=False, error="unity_instance_unresolved")

    lock = _refresh_locks.setdefault(instance, asyncio.Lock())
    if lock.locked():
        return pending(_refresh_attempts.get(instance), error="refresh_observation_in_progress")

    async with lock:
        # Populate the scanner's project root using the canonical resource.
        # Failure does not fabricate a clean baseline; capture remains unknown.
        try:
            await asyncio.wait_for(
                editor_state.get_editor_state(ctx), timeout=max(0.0, deadline - time.monotonic()),
            )
        except asyncio.TimeoutError:
            return pending(_refresh_attempts.get(instance), error="refresh_observation_timeout")
        except (ConnectionError, OSError):
            pass
        snapshot = external_changes_scanner.capture(instance, scope)

        async def send(params):
            return await unity_transport.send_with_unity_instance(
                _legacy_conn.async_send_command_with_retry, unity_instance,
                "refresh_unity", params, retry_on_reload=False,
            )

        return await run_tracked_refresh(
            instance=instance, snapshot=snapshot, scanner=external_changes_scanner,
            attempts=_refresh_attempts, send=send,
            params={"mode": mode, "scope": scope, "compile": compile},
            wait_for_ready=wait_for_ready, timeout_s=max(0.0, deadline - time.monotonic()),
            poll_s=_REFRESH_POLL_SECONDS, operation_id=operation_id,
        )
