"""Public wrapper keeps before anchors and rejects invalid requests before mutation."""
import importlib
from unittest.mock import AsyncMock

import pytest

from .test_helpers import DummyContext


@pytest.fixture
def script_tool(monkeypatch):
    module = importlib.import_module("services.tools.script_apply_edits")
    monkeypatch.setattr(module, "get_unity_instance_from_context", AsyncMock(return_value="B@bbb"))
    monkeypatch.setattr(module, "async_send_command_with_retry", AsyncMock(return_value={
        "success": True, "data": {"sha256": "original"}}))
    mutation = AsyncMock(return_value={"success": True, "data": {"editsApplied": 1}})
    monkeypatch.setattr(module, "send_mutation", mutation)
    return module, mutation


@pytest.mark.asyncio
@pytest.mark.parametrize("anchor_field", ["beforeMethodName", "before_method"])
async def test_before_anchor_qualifiers_and_target_are_forwarded_once(script_tool, anchor_field):
    module, mutation = script_tool
    edit = {"op": "insert_method", "className": "C", "position": "before",
            anchor_field: "Target", "beforeParametersSignature": "int value",
            "beforeReturnType": "int", "replacement": "void Added() {}"}
    result = await module.script_apply_edits(DummyContext(), "C", "Assets/Scripts", [edit])
    assert result["success"]
    mutation.assert_awaited_once()
    args = mutation.await_args.args
    assert args[1:3] == ("B@bbb", "manage_script")
    payload = args[3]
    assert payload["action"] == "edit"
    assert payload["edits"] == [{**{k: v for k, v in edit.items() if k != anchor_field}, "beforeMethodName": "Target"}]


@pytest.mark.asyncio
@pytest.mark.parametrize("incorrect_anchor", [{}, {"methodName": "Target"}, {"afterMethodName": "Target"}])
async def test_missing_before_anchor_never_sends_mutation(script_tool, incorrect_anchor):
    module, mutation = script_tool
    result = await module.script_apply_edits(DummyContext(), "C", "Assets/Scripts", [{
        "op": "insert_method", "className": "C", "position": "before",
        "replacement": "void Added() {}", **incorrect_anchor}])
    assert result["success"] is False
    assert "beforeMethodName" in str(result)
    mutation.assert_not_awaited()
    module.async_send_command_with_retry.assert_not_awaited()
