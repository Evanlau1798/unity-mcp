"""Public tools retain request-scoped targeting through the MCP middleware."""
from types import SimpleNamespace
from unittest.mock import AsyncMock

from fastmcp import Client, FastMCP
import pytest

from core.config import config
from services.resources import editor_state
from services.tools import get_unity_instance_from_context
from services.tools.batch_execute import batch_execute
from services.tools.list_tests import list_tests
from transport.unity_instance_middleware import UnityInstanceMiddleware


@pytest.mark.asyncio
async def test_public_discovery_and_batch_do_not_share_target_or_limit(monkeypatch):
    monkeypatch.setattr(config, "transport_mode", "http")
    monkeypatch.setattr(config, "http_remote_hosted", False)
    mcp = FastMCP("discovery-batch")
    mw = UnityInstanceMiddleware()
    mcp.add_middleware(mw)
    instances = [SimpleNamespace(id=name, hash=name.split("@")[1]) for name in ("A@aaa", "B@bbb")]
    monkeypatch.setattr(mw, "_discover_instances", AsyncMock(return_value=instances))
    monkeypatch.setattr(mw, "_maybe_autoselect_instance", AsyncMock(return_value=None))
    mcp.tool(list_tests)
    mcp.tool(batch_execute)
    targets = []
    limits = {"A@aaa": 25, "B@bbb": 100}

    async def read_limit(ctx):
        target = await get_unity_instance_from_context(ctx)
        return {"data": {"settings": {"batch_execute_max_commands": limits[target]}}}

    async def native(_sender, target, command, params):
        targets.append((target, command, params))
        return {"success": True, "data": {"target": target, "params": params}}

    monkeypatch.setattr(editor_state, "get_editor_state", read_limit)
    monkeypatch.setattr("services.tools.list_tests.send_with_unity_instance", native)
    monkeypatch.setattr("services.tools.batch_execute.send_with_unity_instance", native)
    commands = [{"tool": "get_editor_state", "params": {}}] * 26
    async with Client(mcp) as client:
        a = await client.call_tool("list_tests", {"unity_instance": "A@aaa", "cursor": 50, "filter": "Player"})
        assert a.data["data"]["target"] == "A@aaa"
        assert a.data["data"]["params"] == {"page_size": 50, "cursor": 50, "filter": "Player"}
        rejected = await client.call_tool("batch_execute", {"unity_instance": "A@aaa", "commands": commands}, raise_on_error=False)
        assert rejected.is_error
        b = await client.call_tool("batch_execute", {"unity_instance": "B@bbb", "commands": commands})
        assert b.data["data"]["target"] == "B@bbb"
        limits["B@bbb"] = 25
        changed = await client.call_tool("batch_execute", {"unity_instance": "B@bbb", "commands": commands}, raise_on_error=False)
        assert changed.is_error
        assert [(t, c) for t, c, _ in targets] == [("A@aaa", "get_tests"), ("B@bbb", "batch_execute")]
