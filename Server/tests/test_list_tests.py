"""Public discovery must expose native pagination without starting a test run."""
import importlib
from unittest.mock import AsyncMock

import pytest


@pytest.fixture
def discovery(monkeypatch):
    spec = importlib.util.find_spec("services.tools.list_tests")
    if spec is None:
        return None
    module = importlib.import_module(spec.name)
    monkeypatch.setattr(module, "get_unity_instance_from_context", AsyncMock(return_value="B@hash"))
    return module


def test_registered_read_only_discovery(discovery):
    assert discovery is not None, "No public read-only list_tests pagination tool is registered"
    from services.registry import get_registered_tools
    info = next(t for t in get_registered_tools() if t["name"] == "list_tests")
    assert info["group"] == "testing"
    # get_tests is a resource, not a toggleable native tool.
    assert info["unity_target"] is None
    annotations = info["kwargs"]["annotations"]
    assert annotations.readOnlyHint and annotations.idempotentHint
    assert annotations.destructiveHint is False


@pytest.mark.asyncio
async def test_discovery_forwards_page_filter_and_target(discovery, monkeypatch):
    assert discovery is not None, "No public read-only test discovery API"
    response = {"success": True, "data": {"items": [], "cursor": 50, "nextCursor": None,
                "totalCount": 50, "pageSize": 50, "hasMore": False}}
    send = AsyncMock(return_value=response)
    monkeypatch.setattr(discovery, "send_with_unity_instance", send)
    actual = await discovery.list_tests(object(), mode="EditMode", filter="Player", page_size=50, cursor=50)
    assert actual == response
    assert send.await_args.args[1:] == ("B@hash", "get_tests", {
        "mode": "EditMode", "filter": "Player", "page_size": 50, "cursor": 50})


@pytest.mark.asyncio
async def test_discovery_walks_beyond_first_fifty(discovery, monkeypatch):
    assert discovery is not None, "No public read-only test discovery API"
    items = [{"name": f"Test{i:03}", "full_name": f"Suite.Test{i:03}", "mode": "EditMode"}
             for i in range(63)]
    async def native(_sender, target, command, params):
        assert target == "B@hash" and command == "get_tests"
        start, size = params["cursor"], params["page_size"]
        end = min(start + size, len(items))
        return {"success": True, "data": {"items": items[start:end], "cursor": start,
                "nextCursor": end if end < len(items) else None, "totalCount": len(items),
                "pageSize": size, "hasMore": end < len(items)}}
    monkeypatch.setattr(discovery, "send_with_unity_instance", native)
    first = await discovery.list_tests(object())
    second = await discovery.list_tests(object(), cursor=first["data"]["nextCursor"])
    assert first["data"]["items"] + second["data"]["items"] == items
    assert not second["data"]["hasMore"] and second["data"]["nextCursor"] is None
    empty = await discovery.list_tests(object(), cursor=63)
    assert empty["data"]["items"] == []


@pytest.mark.asyncio
async def test_native_failure_is_preserved(discovery, monkeypatch):
    assert discovery is not None, "No public read-only test discovery API"
    response = {"success": False, "error": "Failed to retrieve tests", "data": {"cursor": "bad"}}
    send = AsyncMock(return_value=response)
    monkeypatch.setattr(discovery, "send_with_unity_instance", send)
    assert await discovery.list_tests(object(), cursor="bad") == response
    assert send.await_args.args[3]["cursor"] == "bad"
