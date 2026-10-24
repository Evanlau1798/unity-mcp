from services.resources.editor_state import EditorStateData, _enrich_advice_and_staleness
import time


def envelope(assets):
    return {
        "schema_version": "unity-mcp/editor_state@2",
        "observed_at_unix_ms": int(time.time() * 1000),
        "sequence": 1,
        "assets": assets,
    }


def test_native_refresh_identity_survives_state_validation():
    receipt = {
        "is_refresh_in_progress": True, "operation_id": "refresh-A",
        "status": "awaiting_compile_start", "completed": False,
    }
    result = EditorStateData.model_validate(envelope({"refresh": receipt})).model_dump()
    assert result["assets"]["refresh"]["operation_id"] == "refresh-A"
    assert result["assets"]["refresh"]["status"] == "awaiting_compile_start"
    assert result["assets"]["refresh"]["completed"] is False


def test_scanner_generations_survive_state_validation():
    assets = {
        "external_changes_dirty": True, "external_changes_generation": 3,
        "external_changes_acknowledged_generation": 2,
        "external_changes_scan_complete": False,
    }
    result = EditorStateData.model_validate(envelope(assets)).model_dump()["assets"]
    assert result["external_changes_generation"] == 3
    assert result["external_changes_acknowledged_generation"] == 2
    assert result["external_changes_scan_complete"] is False


def test_asset_import_is_a_readiness_blocker():
    result = _enrich_advice_and_staleness(envelope({"is_updating": True}))
    assert result["advice"]["ready_for_tools"] is False
    assert "asset_import" in result["advice"]["blocking_reasons"]
