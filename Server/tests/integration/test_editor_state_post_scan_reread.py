"""Editor-state freshness when external scanning outlives the native snapshot."""
from unittest.mock import AsyncMock

import pytest

from models import MCPResponse
from services.resources import editor_state as state_mod
from services.resources import project_info as project_mod
from .test_helpers import DummyContext


@pytest.mark.asyncio
async def test_post_scan_stale_snapshot_gets_one_bounded_native_reobservation(monkeypatch, tmp_path):
    now = [1100]
    snapshots = [
        {"success": True, "data": {
            "schema_version": "unity-mcp/editor_state@2", "observed_at_unix_ms": 1000,
            "sequence": 1, "unity": {"instance_id": "Demo@123"},
            "compilation": {"is_compiling": False}, "assets": {"is_updating": False},
        }},
        {"success": True, "data": {
            "schema_version": "unity-mcp/editor_state@2", "observed_at_unix_ms": 4100,
            "sequence": 2, "unity": {"instance_id": "Demo@123"},
            "compilation": {"is_compiling": False}, "assets": {"is_updating": False},
        }},
    ]
    async def native(*_args, **_kwargs):
        return snapshots.pop(0)
    def scan(_instance):
        now[0] = 4150
        return {"external_changes_dirty": False, "external_changes_scan_complete": True,
                "external_changes_generation": 1, "external_changes_generations": {"assets": 1, "scripts": 0},
                "external_changes_acknowledged_generations": {"assets": 1, "scripts": 0}}

    send = AsyncMock(side_effect=native)
    monkeypatch.setattr(state_mod.unity_transport, "send_with_unity_instance", send)
    monkeypatch.setattr(state_mod, "_now_unix_ms", lambda: now[0])
    monkeypatch.setattr(state_mod, "get_unity_instance_from_context", AsyncMock(return_value="Demo@123"))
    monkeypatch.setattr(project_mod, "get_project_info", AsyncMock(return_value=MCPResponse(
        success=True, data={"projectRoot": str(tmp_path)},
    )))
    monkeypatch.setattr(state_mod.external_changes_scanner, "set_project_root", lambda *_: None)
    monkeypatch.setattr(state_mod.external_changes_scanner, "update_and_get", scan)

    result = await state_mod.get_editor_state(DummyContext())
    assert result.success is True
    assert result.data["sequence"] == 2
    assert result.data["staleness"]["is_stale"] is False
    assert result.data["assets"]["external_changes_dirty"] is False
    assert send.await_count == 2


@pytest.mark.asyncio
async def test_preexisting_native_staleness_does_not_trigger_reobservation(monkeypatch):
    send = AsyncMock(return_value={"success": True, "data": {
        "schema_version": "unity-mcp/editor_state@2",
        "observed_at_unix_ms": 1000, "sequence": 1,
        "unity": {"instance_id": "Demo@123"},
    }})
    monkeypatch.setattr(state_mod.unity_transport, "send_with_unity_instance", send)
    monkeypatch.setattr(state_mod, "_now_unix_ms", lambda: 4200)
    monkeypatch.setattr(state_mod, "get_unity_instance_from_context", AsyncMock(return_value="Demo@123"))
    result = await state_mod.get_editor_state(DummyContext())
    assert result.success is True
    assert result.data["staleness"]["is_stale"] is True
    assert send.await_count == 1
