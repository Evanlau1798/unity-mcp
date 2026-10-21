import pytest

from services.state.external_changes_scanner import ExternalChangesScanner
import services.tools.refresh_unity as refresh_mod
from .test_helpers import DummyContext


@pytest.mark.asyncio
async def test_refresh_unity_recovers_from_retry_disconnect(monkeypatch, tmp_path):
    """A lost mutation reply is recovered only from the same native receipt."""
    instance = "UnityMCPTests@receipt-recovery"
    root = tmp_path / "Project"
    (root / "Assets").mkdir(parents=True)
    (root / "Assets" / "Source.cs").write_text("// source", encoding="utf-8")
    scanner = ExternalChangesScanner(scan_interval_ms=0)
    scanner.set_project_root(instance, str(root))
    monkeypatch.setattr(refresh_mod, "external_changes_scanner", scanner)
    monkeypatch.setattr(refresh_mod, "_refresh_attempts", {})
    monkeypatch.setattr(refresh_mod, "_refresh_locks", {})
    monkeypatch.setattr(refresh_mod, "_REFRESH_POLL_SECONDS", 0.001)
    monkeypatch.setattr(refresh_mod, "_REFRESH_WAIT_SECONDS", 0.2)
    operation = None
    mutations = []

    async def state(_ctx):
        return {"success": True, "data": {"advice": {"ready_for_tools": True}}}

    async def send(_fn, target, command, params, **kwargs):
        nonlocal operation
        assert target == instance and command == "refresh_unity"
        if not params.get("query_only"):
            mutations.append(params)
            operation = params["operation_id"]
            return {"success": False, "error": "disconnected", "hint": "retry"}
        if operation is None:
            return {"success": False, "error": "refresh_operation_not_found",
                    "data": {"reason": "refresh_operation_not_found"}}
        assert params["operation_id"] == operation
        return {"success": True, "data": {
            "operation_id": operation, "completed": True, "status": "succeeded",
        }}

    monkeypatch.setattr(refresh_mod.editor_state, "get_editor_state", state)
    monkeypatch.setattr(refresh_mod.unity_transport, "send_with_unity_instance", send)
    ctx = DummyContext()
    await ctx.set_state("unity_instance", instance)
    response = await refresh_mod.refresh_unity(ctx, wait_for_ready=True)
    assert response.success is True
    assert response.data["operation_id"] == operation
    assert response.data["dirty_snapshot_acknowledged"] is True
    assert len(mutations) == 1
    assert scanner.capture(instance).dirty is False
