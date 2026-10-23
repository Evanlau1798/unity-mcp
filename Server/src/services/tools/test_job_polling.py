"""Bounded observation of one already accepted Unity test job."""
from __future__ import annotations

import asyncio
from collections.abc import Awaitable, Callable
from typing import Any


_TERMINAL = {"succeeded", "failed", "cancelled"}
_TRANSIENT_REASONS = {
    "reloading", "domain_reload", "unity_unavailable", "unavailable",
    "no_unity_session", "stale_connection", "timeout",
}
_TRANSIENT_TEXT = (
    "unavailable", "not available", "connection closed", "disconnected",
    "connection aborted", "connection reset", "could not connect",
    "timeout", "timed out",
)


def normalize_response(response: Any) -> dict[str, Any]:
    """Preserve transport error metadata for mutation and observation callers."""
    if hasattr(response, "model_dump"):
        response = response.model_dump()
    if isinstance(response, dict):
        return response
    return {"success": False, "error": str(response)}


async def resolve_test_job_target(ctx, resolve_instance, *, remote_hosted: bool, deadline: float | None):
    """Keep routing and user-scope resolution inside the same observation budget."""
    async def resolve():
        instance = await resolve_instance(ctx)
        user = await ctx.get_state("user_id") if remote_hosted else None
        return instance, user

    if deadline is None:
        return await resolve()
    return await asyncio.wait_for(resolve(), max(0.0, deadline - asyncio.get_running_loop().time()))


def _is_transient(response: dict[str, Any]) -> bool:
    # Legacy preflight can omit data.reason; retain its exact reload contract.
    if (response.get("error") == "Unity is reloading; please retry"
            and response.get("hint") == "retry"):
        return True
    data = response.get("data")
    reason = data.get("reason") if isinstance(data, dict) else None
    if reason in _TRANSIENT_REASONS:
        return True
    message = " ".join(str(response.get(key) or "") for key in ("error", "message")).lower()
    return any(part in message for part in _TRANSIENT_TEXT)


def validate_job_response(response: Any, job_id: str) -> dict[str, Any] | None:
    """Use the same identity guard for single observations and bounded polling."""
    if not isinstance(response, dict):
        return {"success": False, "error": str(response)}
    if not response.get("success", True):
        return response
    data = response.get("data")
    if not isinstance(data, dict) or not isinstance(data.get("status"), str):
        return {"success": False, "error": "invalid_test_job_response", "data": response}
    if data.get("job_id") != job_id:
        return {
            "success": False, "error": "test_job_identity_mismatch",
            "data": {"job_id": job_id, "observed_job_id": data.get("job_id")},
        }
    return None


def observation_timeout(
    job_id: str, *, last_response: dict[str, Any] | None = None,
    last_error: dict[str, Any] | None = None, observation_age_seconds: float | None = None,
) -> dict[str, Any]:
    """An observer deadline does not change the native job's outcome or ownership."""
    metadata = {
        "wait_timed_out": True,
        "observation_is_stale": True,
        "observation_age_seconds": observation_age_seconds,
        "observation_error": last_error,
    }
    if last_response is not None:
        return {**last_response, "data": {**last_response["data"], **metadata}}
    return {
        "success": False,
        "error": "observation_timeout",
        "message": "Observation wait expired. Continue polling the same job_id to determine its outcome.",
        "hint": "poll_same_job",
        "data": {"job_id": job_id, "job_status_unknown": True, **metadata},
    }


async def poll_test_job(
    fetch: Callable[[], Awaitable[tuple[Any, int]]],
    observe: Callable[[dict[str, Any], int], Awaitable[None]],
    *, job_id: str, deadline: float, poll_interval: float = 2.0,
) -> dict[str, Any]:
    loop = asyncio.get_running_loop()
    last_response = None
    last_error = None
    observed_at = None

    def timed_out():
        age = max(0.0, loop.time() - observed_at) if observed_at is not None else None
        return observation_timeout(
            job_id, last_response=last_response, last_error=last_error,
            observation_age_seconds=age,
        )

    while True:
        remaining = deadline - loop.time()
        if remaining <= 0:
            return timed_out()
        try:
            response, order = await asyncio.wait_for(fetch(), timeout=remaining)
        except (asyncio.TimeoutError, ConnectionError, OSError) as exc:
            last_error = {"success": False, "error": type(exc).__name__, "message": str(exc)}
        else:
            failure = validate_job_response(response, job_id)
            if failure is not None:
                if not _is_transient(failure):
                    return failure
                last_error = failure
            else:
                data = response["data"]
                last_response = response
                observed_at = loop.time()
                last_error = None
                remaining = deadline - loop.time()
                if remaining <= 0:
                    return response if data["status"] in _TERMINAL else timed_out()
                try:
                    await asyncio.wait_for(observe(data, order), timeout=remaining)
                except asyncio.TimeoutError as exc:
                    last_error = {
                        "success": False, "error": type(exc).__name__,
                        "message": "Job observation processing exceeded the remaining wait budget.",
                    }
                    return timed_out()
                if data["status"] in _TERMINAL:
                    return response
        remaining = deadline - loop.time()
        if remaining <= 0:
            return timed_out()
        await asyncio.sleep(min(poll_interval, remaining))
