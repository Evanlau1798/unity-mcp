"""Configured batch limits belong to the current routed Editor and current call."""
from unittest.mock import AsyncMock

import pytest

from services.tools import batch_execute as batch
from services.resources import editor_state


@pytest.fixture
def setup_batch(monkeypatch):
    monkeypatch.setattr(batch, "get_unity_instance_from_context", AsyncMock(side_effect=lambda ctx: ctx))
    sender = AsyncMock(return_value={"success": True})
    monkeypatch.setattr(batch, "send_with_unity_instance", sender)
    return sender


def commands(count):
    return [{"tool": "get_editor_state", "params": {}} for _ in range(count)]


@pytest.mark.asyncio
async def test_two_editors_have_independent_limits(setup_batch, monkeypatch):
    read = AsyncMock(side_effect=lambda ctx: {"data": {"settings": {
        "batch_execute_max_commands": {"A@hash": 25, "B@hash": 100}[ctx]}}})
    monkeypatch.setattr(editor_state, "get_editor_state", read)
    await batch.batch_execute("A@hash", commands(1))
    await batch.batch_execute("B@hash", commands(26))
    assert setup_batch.await_args.args[1] == "B@hash"
    assert read.await_count == 2


@pytest.mark.asyncio
async def test_changed_editor_setting_is_observed(setup_batch, monkeypatch):
    read = AsyncMock(side_effect=[{"data": {"settings": {"batch_execute_max_commands": n}}}
                                 for n in [100, 25]])
    monkeypatch.setattr(editor_state, "get_editor_state", read)
    await batch.batch_execute("A@hash", commands(26))
    with pytest.raises(ValueError, match="up to 25 commands"):
        await batch.batch_execute("A@hash", commands(26))
    assert setup_batch.await_count == 1


@pytest.mark.asyncio
@pytest.mark.parametrize("limit", [0, -1, 101, "100", None])
async def test_invalid_settings_fall_back_to_25(setup_batch, monkeypatch, limit):
    monkeypatch.setattr(editor_state, "get_editor_state", AsyncMock(return_value={
        "data": {"settings": {"batch_execute_max_commands": limit}}}))
    with pytest.raises(ValueError, match="up to 25 commands"):
        await batch.batch_execute("A@hash", commands(26))
    setup_batch.assert_not_awaited()


@pytest.mark.asyncio
async def test_failed_settings_read_falls_back_and_preserves_nested_keys(setup_batch, monkeypatch):
    monkeypatch.setattr(editor_state, "get_editor_state", AsyncMock(side_effect=ConnectionError("offline")))
    payload = [{"tool": "manage_components", "params": {"properties": {"some_Key": 1}}}]
    await batch.batch_execute("A@hash", payload, parallel=True, fail_fast=False)
    assert setup_batch.await_args.args[3] == {"commands": payload, "parallel": True, "failFast": False}
    with pytest.raises(ValueError, match="up to 25 commands"):
        await batch.batch_execute("A@hash", commands(101))
