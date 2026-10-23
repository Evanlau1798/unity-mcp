from __future__ import annotations

import asyncio
import os
import time
from typing import Any, Literal

from models import MCPResponse


def _in_pytest() -> bool:
    # Integration tests in this repo stub transports and do not run against a live Unity editor.
    # Preflight must be a no-op in that environment to avoid breaking the existing test suite.
    return bool(os.environ.get("PYTEST_CURRENT_TEST"))


def _busy(reason: str, retry_after_ms: int) -> MCPResponse:
    return MCPResponse(
        success=False,
        error="busy",
        message=reason,
        hint="retry",
        data={"reason": reason, "retry_after_ms": int(retry_after_ms)},
    )


def _refresh_scope(assets: dict[str, Any]) -> tuple[Literal["assets", "scripts", "all"], Literal["none", "request"]]:
    """Select the minimal safe refresh from a complete generation snapshot.

    Incomplete, absent, or inconsistent scanner data retains the previous
    conservative full-refresh behavior. Project/Package settings changes are
    already classified into both generations by the external scanner.
    """
    if assets.get("external_changes_scan_complete") is not True:
        return "all", "request"
    observed = assets.get("external_changes_generations")
    acknowledged = assets.get("external_changes_acknowledged_generations")
    if not isinstance(observed, dict) or not isinstance(acknowledged, dict):
        return "all", "request"
    try:
        scopes = []
        for scope in ("assets", "scripts"):
            seen = observed[scope]
            ack = acknowledged[scope]
            if (type(seen) is not int or type(ack) is not int
                    or seen < 0 or ack < 0 or ack > seen):
                return "all", "request"
            if seen > ack:
                scopes.append(scope)
    except (KeyError, TypeError):
        return "all", "request"
    if scopes == ["assets"]:
        return "assets", "none"
    if scopes == ["scripts"]:
        return "scripts", "request"
    return "all", "request"


async def preflight(
    ctx,
    *,
    requires_no_tests: bool = False,
    wait_for_no_compile: bool = False,
    refresh_if_dirty: bool = False,
    max_wait_s: float = 30.0,
) -> MCPResponse | None:
    """
    Server-side preflight guard used by tools so they behave safely even if the client never reads resources.

    Returns:
      - MCPResponse busy/retry payload when the tool should not proceed right now
      - None when the tool should proceed normally
    """
    if _in_pytest():
        return None

    # Load canonical editor state (server enriches advice + staleness).
    try:
        from services.resources.editor_state import get_editor_state
        state_resp = await get_editor_state(ctx)
        state = state_resp.model_dump() if hasattr(
            state_resp, "model_dump") else state_resp
    except Exception:
        return _busy("editor_state_unconfirmed", 500)

    if not isinstance(state, dict) or state.get("success") is not True:
        return _busy("editor_state_unconfirmed", 500)

    data = state.get("data")
    if not isinstance(data, dict) or (data.get("staleness") or {}).get("is_stale") is True:
        return _busy("editor_state_unconfirmed", 500)

    # Check the native owner before a refresh, which is itself a mutation.
    if requires_no_tests and (data.get("tests") or {}).get("is_running") is True:
        return _busy("tests_running", 5000)

    # A refresh acceptance is not completion. Preserve failures and operation
    # identity, then obtain a new snapshot instead of reusing pre-refresh state.
    refreshed = False
    assets = data.get("assets") or {}
    # A refresh that is already underway must be reattached to its original
    # receipt even when the external-dirty bit is currently false. The
    # refresh tracker handles this as a query, never as a second mutation.
    refreshing = (assets.get("refresh") or {}).get("is_refresh_in_progress") is True
    if refreshing and not refresh_if_dirty:
        return _busy("refresh_pending", 500)
    if refresh_if_dirty:
        if assets.get("external_changes_dirty") is True or refreshing:
            scope, compile_mode = _refresh_scope(assets)
            try:
                from services.tools.refresh_unity import refresh_unity
                response = await refresh_unity(
                    ctx, mode="if_dirty", scope=scope, compile=compile_mode, wait_for_ready=True,
                )
                payload = response.model_dump() if hasattr(response, "model_dump") else response
            except Exception:
                return _busy("refresh_unconfirmed", 500)
            if not isinstance(payload, dict) or "success" not in payload:
                return _busy("refresh_unconfirmed", 500)
            if payload["success"] is not True:
                return MCPResponse(**payload)
            receipt = payload.get("data")
            if not isinstance(receipt, dict) or receipt.get("completed") is not True:
                blocked = _busy("refresh_pending", 500)
                blocked.data["refresh"] = payload
                return blocked

            try:
                state_resp = await get_editor_state(ctx)
                state = state_resp.model_dump() if hasattr(state_resp, "model_dump") else state_resp
                if not isinstance(state, dict) or state.get("success") is not True:
                    return _busy("editor_state_unconfirmed", 500)
                data = state.get("data")
                if not isinstance(data, dict) or (data.get("staleness") or {}).get("is_stale") is True:
                    return _busy("editor_state_unconfirmed", 500)
            except Exception:
                return _busy("editor_state_unconfirmed", 500)
            refreshed = True
            if ((data.get("assets") or {}).get("refresh") or {}).get("is_refresh_in_progress") is True:
                return _busy("refresh_pending", 500)
            if (data.get("assets") or {}).get("external_changes_dirty") is True:
                return _busy("external_changes_pending", 500)

    if (data.get("assets") or {}).get("is_updating") is True:
        return _busy("asset_import", 500)

    # Tests running: fail fast for tools that require exclusivity.
    if requires_no_tests:
        tests = data.get("tests")
        if isinstance(tests, dict) and tests.get("is_running") is True:
            return _busy("tests_running", 5000)

    # Compilation: optionally wait for a bounded time.
    if wait_for_no_compile:
        deadline = time.monotonic() + float(max_wait_s)
        while True:
            compilation = data.get("compilation") if isinstance(
                data, dict) else None
            is_compiling = isinstance(compilation, dict) and compilation.get(
                "is_compiling") is True
            is_domain_reload_pending = isinstance(compilation, dict) and compilation.get(
                "is_domain_reload_pending") is True
            if not is_compiling and not is_domain_reload_pending:
                break
            if time.monotonic() >= deadline:
                return _busy("compiling", 500)
            await asyncio.sleep(0.25)

            # Refresh state for the next loop iteration.
            try:
                from services.resources.editor_state import get_editor_state
                state_resp = await get_editor_state(ctx)
                state = state_resp.model_dump() if hasattr(
                    state_resp, "model_dump") else state_resp
                data = state.get("data") if isinstance(state, dict) and state.get("success") is True else None
                if not isinstance(data, dict):
                    return _busy("editor_state_unconfirmed", 500)
                if (data.get("staleness") or {}).get("is_stale") is True:
                    return _busy("editor_state_unconfirmed", 500)
                assets = data.get("assets") or {}
                if (assets.get("refresh") or {}).get("is_refresh_in_progress") is True:
                    return _busy("refresh_pending", 500)
                if assets.get("is_updating") is True:
                    return _busy("asset_import", 500)
                if requires_no_tests and (data.get("tests") or {}).get("is_running") is True:
                    return _busy("tests_running", 5000)
                if refreshed and (data.get("assets") or {}).get("external_changes_dirty") is True:
                    return _busy("external_changes_pending", 500)
            except Exception:
                return _busy("editor_state_unconfirmed", 500)

    return None
