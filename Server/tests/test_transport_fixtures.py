"""Shared fixtures for transport characterization contracts."""
import asyncio
import pytest
import pytest_asyncio
from unittest.mock import AsyncMock, Mock, MagicMock, patch, call
from datetime import datetime, timezone
import uuid
from types import SimpleNamespace

from transport.unity_instance_middleware import UnityInstanceMiddleware, get_unity_instance_middleware, set_unity_instance_middleware
from transport.plugin_registry import PluginRegistry, PluginSession
from transport.plugin_hub import PluginHub, NoUnitySessionError, InstanceSelectionRequiredError, PluginDisconnectedError
from transport.models import (
    RegisterMessage,
    RegisterToolsMessage,
    CommandResultMessage,
    PongMessage,
    SessionList,
    SessionDetails,
)
from models.models import ToolDefinitionModel
from core.config import config



def _tool_registry_for_visibility_tests() -> list[dict]:
    return [
        {"name": "manage_scene", "unity_target": "manage_scene"},
        {"name": "manage_script", "unity_target": "manage_script"},
        {"name": "manage_asset", "unity_target": "manage_asset"},
        {"name": "create_script", "unity_target": "manage_script"},
        {"name": "find_in_file", "unity_target": "manage_script"},
        {"name": "script_apply_edits", "unity_target": "manage_script"},
        {"name": "set_active_instance", "unity_target": None},
        {"name": "execute_custom_tool", "unity_target": None},
    ]

@pytest.fixture
def mock_context():
    """Create a mock FastMCP context."""
    ctx = Mock()
    ctx.session_id = "test-session-123"
    ctx.client_id = "test-client-456"

    state_storage = {}
    ctx.set_state = AsyncMock(side_effect=lambda k, v, **kw: state_storage.__setitem__(k, v))
    ctx.get_state = AsyncMock(side_effect=lambda k: state_storage.get(k))
    ctx.info = AsyncMock()

    return ctx


@pytest.fixture
def mock_websocket():
    """Create a mock WebSocket."""
    ws = AsyncMock()
    ws.send_json = AsyncMock()
    ws.receive_json = AsyncMock()
    ws.accept = AsyncMock()
    ws.close = AsyncMock()
    return ws


@pytest.fixture
def plugin_registry():
    """Create an in-memory plugin registry."""
    return PluginRegistry()


@pytest_asyncio.fixture
async def configured_plugin_hub(plugin_registry):
    """Configure PluginHub with a registry and event loop."""
    loop = asyncio.get_running_loop()
    PluginHub.configure(plugin_registry, loop)
    yield
    # Cleanup
    PluginHub._registry = None
    PluginHub._lock = None
    PluginHub._loop = None
    PluginHub._connections.clear()
    PluginHub._pending.clear()


# ============================================================================
# SESSION MANAGEMENT & ROUTING TESTS
# ============================================================================

def _make_ctx(session_id: str | None = None) -> Mock:
    """Build a minimal Context shim with FastMCP-compatible session state.

    Each ctx has its own private ``state`` dict, so isolation tests can prove
    that two ctxs cannot read each other's writes — which is the FastMCP
    invariant we now rely on (state keyed by ``ctx.session_id`` in production).
    """
    state: dict[str, object] = {}
    ctx = Mock()
    ctx.session_id = session_id or "test-session"
    ctx.set_state = AsyncMock(side_effect=lambda k, v, **kw: state.__setitem__(k, v))
    ctx.get_state = AsyncMock(side_effect=lambda k: state.get(k))
    ctx.delete_state = AsyncMock(side_effect=lambda k: state.pop(k, None))
    return ctx
