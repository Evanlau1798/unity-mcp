"""One outstanding refresh per Editor, with a receipt that survives observation loss.

The native SessionState receipt is authoritative. This process-local record only
holds the scanner snapshot that may be acknowledged when that receipt completes.
"""
from __future__ import annotations

import asyncio
from dataclasses import dataclass
import time
from typing import Any, Awaitable, Callable
from uuid import uuid4

from models import MCPResponse


@dataclass
class RefreshAttempt:
    operation_id: str
    snapshot: Any = None
    terminal_response: dict | None = None


def _dict(value: Any) -> dict:
    if isinstance(value, dict):
        return value
    if hasattr(value, "model_dump"):
        return value.model_dump()
    return {"success": False, "error": str(value)}


def _receipt(response: dict) -> dict | None:
    data = response.get("data")
    if (isinstance(data, dict) and isinstance(data.get("operation_id"), str)
            and isinstance(data.get("completed"), bool)
            and data.get("status") in {"accepted", "importing", "awaiting_compile_start", "compiling",
                                      "awaiting_reload", "compilation_failed", "dispatch_failed_pending",
                                      "succeeded", "failed"}):
        return data
    return None


def _not_found(response: dict) -> bool:
    data = response.get("data")
    return isinstance(data, dict) and data.get("reason") == "refresh_operation_not_found"


def _definite_mutation_rejection(response: dict) -> bool:
    if response.get("success") is not False:
        return False
    data = response.get("data")
    if isinstance(data, dict):
        accepted = data.get("request_accepted")
        if accepted is not None:
            return accepted is False
        # Structured pre-admission contract used by is_reloading_rejection.
        return data.get("reason") == "reloading" and response.get("hint") == "retry"
    # Legacy UnityConnection.send_command rejects before writing to the socket.
    # Match that exact shape; generic reload/disconnect text proves nothing.
    return (data is None and response.get("hint") == "retry"
            and response.get("error") == "Unity is reloading; please retry")


def _same_snapshot(left: Any, right: Any) -> bool:
    return left is not None and right is not None and all(
        getattr(left, key, None) == getattr(right, key, None)
        for key in ("epoch", "project_root", "scope", "generations")
    )


def pending(attempt: RefreshAttempt | None, *, error: str = "refresh_pending",
            last_response: dict | None = None) -> MCPResponse:
    return MCPResponse(
        success=False, error=error,
        message="Refresh completion is unconfirmed; query the same operation before retrying.",
        hint="poll_same_operation",
        data={
            "operation_id": attempt.operation_id if attempt else None,
            "completed": False, "status": "pending", "timeout": True,
            "last_response": last_response,
        },
    )


async def run_tracked_refresh(
    *, instance: str, snapshot: Any, scanner: Any, attempts: dict[str, RefreshAttempt],
    send: Callable[[dict], Awaitable[Any]], params: dict, wait_for_ready: bool,
    timeout_s: float, poll_s: float, operation_id: str | None = None,
) -> MCPResponse:
    deadline = time.monotonic() + timeout_s
    attempt = attempts.get(instance)
    response: dict | None = None

    async def request(values: dict) -> dict:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise asyncio.TimeoutError
        return _dict(await asyncio.wait_for(send(values), timeout=remaining))

    async def query(request_id: str | None) -> dict:
        # Safe on older packages too: their unrecognised query flag must not
        # accidentally request a full refresh or compilation.
        values = {
            "query_only": True, "mode": "if_dirty", "scope": "scripts",
            "compile": "none", "wait_for_ready": False,
        }
        if request_id:
            values["operation_id"] = request_id
        return await request(values)

    if attempt and attempt.terminal_response:
        if operation_id == attempt.operation_id or (
            params["mode"] != "force" and _same_snapshot(attempt.snapshot, snapshot)
        ):
            return MCPResponse(**attempt.terminal_response)
        attempts.pop(instance, None)
        attempt = None

    if operation_id and attempt and operation_id != attempt.operation_id:
        return MCPResponse(
            success=False, error="refresh_busy", hint="poll_same_operation",
            data={"operation_id": operation_id, "active_operation_id": attempt.operation_id,
                  "completed": False},
        )

    try:
        if attempt is None:
            response = await query(operation_id)
            existing = _receipt(response)
            if operation_id and existing and existing["operation_id"] != operation_id:
                return pending(RefreshAttempt(operation_id), error="refresh_identity_mismatch",
                               last_response=response)
            if existing and existing.get("completed") is not True:
                attempt = RefreshAttempt(existing["operation_id"])
                attempts[instance] = attempt
            elif operation_id:
                # An explicit recovery request is read-only even when absent.
                if existing and existing["operation_id"] == operation_id:
                    return MCPResponse(**response)
                return MCPResponse(
                    success=False, error="refresh_operation_not_found",
                    data={"operation_id": operation_id, "completed": False},
                )
            elif not existing and not _not_found(response):
                if response.get("success") is True:
                    return MCPResponse(
                        success=False, error="refresh_tracking_unsupported",
                        message="Update the Unity package before using tracked refresh.",
                        data={"completed": False, "last_response": response},
                    )
                return MCPResponse(**response)
            else:
                if params["mode"] == "if_dirty" and snapshot.reliable and not snapshot.dirty:
                    return MCPResponse(success=True, message="No external changes to refresh.",
                                       data={"completed": True, "status": "no_op"})
                attempt = RefreshAttempt(uuid4().hex, snapshot=snapshot)
                # Save before dispatch: cancellation or a lost response must
                # never turn the next invocation into a second mutation.
                attempts[instance] = attempt
                mutation = dict(params, operation_id=attempt.operation_id, wait_for_ready=False)
                response = await request(mutation)
                # Only a mutation's own admission reply can release its owner.
                # Rejection of a later receipt query says nothing about dispatch.
                if _definite_mutation_rejection(response):
                    attempts.pop(instance, None)
                    return MCPResponse(**response)
        else:
            response = await query(attempt.operation_id)
    except (asyncio.TimeoutError, ConnectionError, OSError) as exc:
        response = {"success": False, "error": type(exc).__name__}
        if attempt is None:
            return pending(None, error="refresh_observation_timeout", last_response=response)

    while attempt is not None:
        receipt = _receipt(response or {})
        if receipt and receipt["operation_id"] != attempt.operation_id:
            return pending(attempt, error="refresh_identity_mismatch", last_response=response)
        if receipt and receipt.get("completed") is True:
            if response.get("success") is True and receipt.get("status") == "succeeded":
                acknowledged = bool(attempt.snapshot and scanner.acknowledge(instance, attempt.snapshot))
                data = dict(receipt, dirty_snapshot_acknowledged=acknowledged)
                attempts.pop(instance, None)
                return MCPResponse(**dict(response, data=data))
            # Keep the original terminal failure for this exact generation. A
            # later idle snapshot must not relabel it as success or retry it.
            failed = dict(response, success=False)
            failed.setdefault("error", "refresh_failed")
            attempt.terminal_response = failed
            return MCPResponse(**failed)
        if not wait_for_ready:
            if receipt and response.get("success") is True:
                return MCPResponse(**response)
            return pending(attempt, last_response=response)
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return pending(attempt, last_response=response)
        await asyncio.sleep(min(poll_s, remaining))
        try:
            response = await query(attempt.operation_id)
        except (asyncio.TimeoutError, ConnectionError, OSError) as exc:
            response = {"success": False, "error": type(exc).__name__}
    return pending(attempt, last_response=response)
