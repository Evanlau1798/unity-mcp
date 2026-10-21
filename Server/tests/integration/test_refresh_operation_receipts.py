"""Refresh receipts, rather than generic idle, govern dirty acknowledgement."""
import asyncio
from types import SimpleNamespace

import pytest

from models import MCPResponse
import services.tools.refresh_unity as mod
from .test_helpers import DummyContext


class Scanner:
    def __init__(self):
        self.dirty = True
        self.reliable = True
        self.generation = 1
        self.acknowledged = []
        self.legacy_clears = 0

    def capture(self, instance_id, scope="all"):
        return SimpleNamespace(
            instance_id=instance_id, epoch="scanner-A", project_root="test-root",
            scope=scope, generations=(("scripts", self.generation), ("assets", self.generation)),
            reliable=self.reliable, dirty=self.dirty,
        )

    def acknowledge(self, instance_id, snapshot):
        self.acknowledged.append(snapshot)
        self.dirty = self.generation != dict(snapshot.generations)["scripts"]
        return snapshot.reliable

    def clear_dirty(self, instance_id):
        self.legacy_clears += 1
        self.dirty = False


def payload(response):
    return response.model_dump() if hasattr(response, "model_dump") else response


@pytest.fixture
def setup(monkeypatch):
    scanner = Scanner()
    monkeypatch.setattr(mod, "external_changes_scanner", scanner)
    # These constants are used by the receipt coordinator after the fix.
    monkeypatch.setattr(mod, "_REFRESH_WAIT_SECONDS", 0.035, raising=False)
    monkeypatch.setattr(mod, "_REFRESH_POLL_SECONDS", 0.001, raising=False)
    attempts = getattr(mod, "_refresh_attempts", None)
    if attempts is not None:
        attempts.clear()

    async def ready(_ctx):
        return {
            "success": True,
            "data": {
                "unity": {"instance_id": "Unity@receipt-test"},
                "advice": {"ready_for_tools": True, "blocking_reasons": []},
                "staleness": {"is_stale": False},
            },
        }
    monkeypatch.setattr(mod.editor_state, "get_editor_state", ready)
    return scanner


async def context():
    ctx = DummyContext()
    await ctx.set_state("unity_instance", "Unity@receipt-test")
    return ctx


class Bridge:
    def __init__(self):
        self.dispatches = []
        self.queries = []
        self.operation_id = None
        self.completed = False
        self.fail = False
        self.disconnect = False
        self.slow_query = False

    async def send(self, send_fn, instance, command, params, **kwargs):
        assert command == "refresh_unity"
        if params.get("query_only"):
            self.queries.append(dict(params))
            if self.slow_query:
                await asyncio.sleep(0.2)
            if self.operation_id is None:
                return {"success": False, "error": "refresh_operation_not_found",
                        "data": {"reason": "refresh_operation_not_found"}}
        else:
            self.dispatches.append(dict(params))
            self.operation_id = params.get("operation_id")
            if self.disconnect:
                return {"success": False, "error": "connection closed after send"}
        return {
            "success": not self.fail,
            "error": "compile_failed" if self.fail else None,
            "data": {
                "operation_id": self.operation_id,
                "completed": self.completed,
                "status": "failed" if self.fail else ("succeeded" if self.completed else "awaiting_compile_start"),
                "compile_started": self.completed,
                "compile_requested": True,
            },
        }


@pytest.mark.asyncio
async def test_initial_idle_does_not_complete_scheduled_compile(setup, monkeypatch):
    bridge = Bridge()
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    result = payload(await mod.refresh_unity(await context(), compile="request"))
    assert result["success"] is False
    assert result["data"]["completed"] is False
    assert result["data"]["operation_id"]
    assert setup.acknowledged == []
    assert setup.legacy_clears == 0
    assert len(bridge.dispatches) == 1


@pytest.mark.asyncio
async def test_nonwaiting_acceptance_does_not_acknowledge_dirty(setup, monkeypatch):
    bridge = Bridge()
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    result = payload(await mod.refresh_unity(await context(), compile="request", wait_for_ready=False))
    assert result["success"] is True
    assert result["data"]["completed"] is False
    assert setup.acknowledged == []
    assert setup.dirty is True


@pytest.mark.asyncio
async def test_timeout_then_completion_attaches_without_second_dispatch(setup, monkeypatch):
    bridge = Bridge()
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    ctx = await context()
    first = payload(await mod.refresh_unity(ctx, compile="request"))
    original_id = first["data"].get("operation_id")
    bridge.completed = True
    second = payload(await mod.refresh_unity(ctx, compile="request"))
    assert len(bridge.dispatches) == 1
    assert second["success"] is True
    assert second["data"]["operation_id"] == original_id
    assert second["data"]["completed"] is True
    assert len(setup.acknowledged) == 1


@pytest.mark.asyncio
async def test_disconnect_recovers_only_from_matching_receipt(setup, monkeypatch):
    bridge = Bridge()
    bridge.disconnect = True
    bridge.completed = True
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    result = payload(await mod.refresh_unity(await context(), compile="request"))
    assert result["success"] is True
    assert result["data"]["completed"] is True
    assert len(bridge.dispatches) == 1
    assert len(bridge.queries) >= 2
    assert len(setup.acknowledged) == 1


@pytest.mark.asyncio
async def test_refresh_acknowledges_captured_generation_only(setup, monkeypatch):
    bridge = Bridge()
    bridge.completed = True

    async def send(*args, **kwargs):
        result = await bridge.send(*args, **kwargs)
        if not args[3].get("query_only"):
            setup.generation = 2
        return result

    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", send)
    result = payload(await mod.refresh_unity(await context(), compile="request"))
    assert result["success"] is True
    assert len(setup.acknowledged) == 1
    assert dict(setup.acknowledged[0].generations)["scripts"] == 1
    assert setup.dirty is True


@pytest.mark.asyncio
async def test_known_clean_if_dirty_does_not_dispatch(setup, monkeypatch):
    setup.dirty = False
    bridge = Bridge()
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    result = payload(await mod.refresh_unity(await context(), mode="if_dirty", compile="request"))
    assert result["success"] is True
    assert result["data"]["completed"] is True
    assert result["data"]["status"] == "no_op"
    assert bridge.dispatches == []


@pytest.mark.asyncio
async def test_force_still_dispatches_when_scanner_is_clean(setup, monkeypatch):
    setup.dirty = False
    bridge = Bridge()
    bridge.completed = True
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    result = payload(await mod.refresh_unity(await context(), mode="force"))
    assert result["success"] is True
    assert len(bridge.dispatches) == 1
    assert bridge.dispatches[0].get("operation_id")


@pytest.mark.asyncio
async def test_compile_failure_is_retained_without_retrying_same_generation(setup, monkeypatch):
    bridge = Bridge()
    bridge.fail = True
    bridge.completed = True
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    ctx = await context()
    first = payload(await mod.refresh_unity(ctx, compile="request"))
    second = payload(await mod.refresh_unity(ctx, compile="request"))
    assert first["success"] is False and second["success"] is False
    assert first["error"] == "compile_failed"
    assert len(bridge.dispatches) == 1
    assert setup.acknowledged == []


@pytest.mark.asyncio
async def test_old_package_query_response_is_not_treated_as_completion(setup, monkeypatch):
    calls = []
    async def legacy(*args, **kwargs):
        calls.append(args[3])
        return {"success": True, "data": {"resulting_state": "idle"}}
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", legacy)
    result = payload(await mod.refresh_unity(await context(), compile="request"))
    assert result["success"] is False
    assert result["error"] == "refresh_tracking_unsupported"
    assert all(p.get("query_only") for p in calls)
    assert all(p.get("scope") == "scripts" and p.get("compile") == "none" for p in calls)
    assert setup.acknowledged == []


@pytest.mark.asyncio
async def test_readonly_probe_is_bounded_by_wait_budget(setup, monkeypatch):
    bridge = Bridge()
    bridge.slow_query = True
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    started = asyncio.get_running_loop().time()
    result = payload(await mod.refresh_unity(await context(), compile="request"))
    elapsed = asyncio.get_running_loop().time() - started
    assert result["success"] is False
    assert elapsed < 0.15
    assert setup.acknowledged == []
    assert bridge.dispatches == []

@pytest.mark.asyncio
async def test_state_read_and_receipt_share_one_deadline(setup, monkeypatch):
    bridge = Bridge()
    bridge.completed = True
    async def slow_state(ctx):
        await asyncio.sleep(0.2)
        return {"success": True, "data": {}}
    monkeypatch.setattr(mod.editor_state, "get_editor_state", slow_state)
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    result = payload(await mod.refresh_unity(await context(), compile="request"))
    assert result["success"] is False
    assert bridge.dispatches == []


@pytest.mark.asyncio
async def test_definite_admission_rejection_does_not_leave_phantom_owner(setup, monkeypatch):
    bridge = Bridge()
    async def rejected(*args, **kwargs):
        if args[3].get("query_only"):
            return await bridge.send(*args, **kwargs)
        bridge.dispatches.append(dict(args[3]))
        return {"success": False, "error": "tests_running",
                "data": {"request_accepted": False, "reason": "tests_running"}}
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", rejected)
    result = payload(await mod.refresh_unity(await context(), compile="request"))
    assert result["error"] == "tests_running"
    assert "Unity@receipt-test" not in mod._refresh_attempts


@pytest.mark.asyncio
async def test_explicit_operation_id_is_readonly_recovery(setup, monkeypatch):
    bridge = Bridge()
    bridge.operation_id = "known-refresh"
    bridge.completed = True
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    result = payload(await mod.refresh_unity(await context(), operation_id="known-refresh"))
    assert result["success"] is True
    assert result["data"]["operation_id"] == "known-refresh"
    assert bridge.dispatches == []
    assert setup.acknowledged == []

@pytest.mark.asyncio
@pytest.mark.parametrize("native_status", ["importing", "compilation_failed", "dispatch_failed_pending"])
async def test_server_restart_attaches_to_every_native_pending_phase(setup, monkeypatch, native_status):
    calls = []
    async def observe(*args, **kwargs):
        calls.append(args[3])
        return {"success": True, "data": {
            "operation_id": "native-owner", "completed": False, "status": native_status,
        }}
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", observe)
    result = payload(await mod.refresh_unity(await context(), wait_for_ready=False))
    assert result["data"]["operation_id"] == "native-owner"
    assert result["data"]["completed"] is False
    assert all(call.get("query_only") for call in calls)
    assert setup.acknowledged == []


@pytest.mark.asyncio
async def test_malformed_native_response_is_not_treated_as_a_receipt(setup, monkeypatch):
    async def malformed(*args, **kwargs):
        return {"success": False, "error": "invalid_native_response", "data": "not-an-object"}
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", malformed)
    result = payload(await mod.refresh_unity(await context()))
    assert result["success"] is False
    assert result["error"] == "invalid_native_response"
    assert setup.acknowledged == []


@pytest.mark.asyncio
async def test_explicit_recovery_rejects_another_pending_identity(setup, monkeypatch):
    bridge = Bridge()
    bridge.operation_id = "other-operation"
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    result = payload(await mod.refresh_unity(
        await context(), operation_id="requested-operation", wait_for_ready=False,
    ))
    assert result["success"] is False
    assert result["error"] == "refresh_identity_mismatch"
    assert result["data"]["operation_id"] == "requested-operation"
    assert bridge.dispatches == []
    assert setup.acknowledged == []


@pytest.mark.asyncio
@pytest.mark.parametrize("wait_for_ready", [False, True])
@pytest.mark.parametrize("rejection", [
    {"success": False, "error": "Unity is reloading; please retry", "hint": "retry",
     "data": {"reason": "reloading", "retry_after_ms": 250}},
    {"success": False, "error": "Unity is reloading; please retry", "hint": "retry"},
    MCPResponse(success=False, error="Unity is reloading; please retry", hint="retry"),
])
async def test_legacy_reload_rejection_releases_only_the_unaccepted_mutation(setup, monkeypatch, wait_for_ready, rejection):
    bridge = Bridge()
    expected = payload(rejection)

    async def send(*args, **kwargs):
        if not args[3].get("query_only") and not bridge.dispatches:
            bridge.dispatches.append(dict(args[3]))
            return rejection
        return await bridge.send(*args, **kwargs)

    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", send)
    ctx = await context()
    first = payload(await mod.refresh_unity(ctx, compile="request", wait_for_ready=wait_for_ready))
    assert first["error"] == expected["error"]
    assert first["hint"] == expected["hint"]
    assert first["data"] == expected.get("data")
    assert "Unity@receipt-test" not in mod._refresh_attempts
    assert len(bridge.dispatches) == 1
    assert setup.acknowledged == []
    assert setup.dirty is True

    bridge.completed = True
    second = payload(await mod.refresh_unity(ctx, compile="request"))
    assert second["success"] is True
    assert second["data"]["completed"] is True
    assert len(bridge.dispatches) == 2
    assert bridge.dispatches[0]["operation_id"] != bridge.dispatches[1]["operation_id"]
    assert len(setup.acknowledged) == 1


@pytest.mark.asyncio
async def test_reload_rejection_of_receipt_query_keeps_the_existing_mutation(setup, monkeypatch):
    bridge = Bridge()
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    ctx = await context()
    first = payload(await mod.refresh_unity(ctx, compile="request", wait_for_ready=False))
    operation = first["data"]["operation_id"]

    async def reject_query(*args, **kwargs):
        assert args[3].get("query_only") is True
        return {"success": False, "error": "Unity is reloading; please retry", "hint": "retry",
                "data": {"reason": "reloading"}}

    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", reject_query)
    observed = payload(await mod.refresh_unity(ctx, wait_for_ready=False))
    assert observed["data"]["operation_id"] == operation
    assert mod._refresh_attempts["Unity@receipt-test"].operation_id == operation
    assert len(bridge.dispatches) == 1
    assert setup.acknowledged == []

    bridge.completed = True
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    recovered = payload(await mod.refresh_unity(ctx))
    assert recovered["success"] is True
    assert recovered["data"]["operation_id"] == operation
    assert len(bridge.dispatches) == 1


@pytest.mark.asyncio
@pytest.mark.parametrize("mutation_response", [
    {"success": False, "error": "connection closed after send", "hint": "retry"},
    {"success": False, "error": "reload after acceptance", "hint": "retry",
     "data": {"reason": "reloading", "request_accepted": True}},
])
async def test_uncertain_mutation_and_missing_receipt_do_not_release_or_redispatch(setup, monkeypatch, mutation_response):
    bridge = Bridge()

    async def send(*args, **kwargs):
        if args[3].get("query_only"):
            return await bridge.send(*args, **kwargs)
        bridge.dispatches.append(dict(args[3]))
        return mutation_response

    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", send)
    ctx = await context()
    first = payload(await mod.refresh_unity(ctx, compile="request", wait_for_ready=False))
    operation = first["data"]["operation_id"]
    second = payload(await mod.refresh_unity(ctx, compile="request", wait_for_ready=False))
    assert first["success"] is False and second["success"] is False
    assert second["data"]["operation_id"] == operation
    assert mod._refresh_attempts["Unity@receipt-test"].operation_id == operation
    assert len(bridge.dispatches) == 1
    assert setup.acknowledged == []
