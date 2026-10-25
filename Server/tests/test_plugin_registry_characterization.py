"""Transport behavior contracts using isolated registries and contexts."""
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



from tests.test_transport_fixtures import (
    mock_context, mock_websocket, plugin_registry, configured_plugin_hub,
    _make_ctx, _tool_registry_for_visibility_tests,
)

class TestPluginRegistryFunctionality:
    """Test plugin session registration and lookup."""

    @pytest.mark.asyncio
    async def test_registry_registers_session(self, plugin_registry):
        """
        Current behavior: register() creates a new PluginSession and stores
        it by session_id and project_hash.
        """
        session, _ = await plugin_registry.register(
            session_id="sess-abc",
            project_name="TestProject",
            project_hash="hash123",
            unity_version="2022.3"
        )

        assert session.session_id == "sess-abc"
        assert session.project_name == "TestProject"
        assert session.project_hash == "hash123"
        assert session.unity_version == "2022.3"

    @pytest.mark.asyncio
    async def test_registry_lookup_by_hash(self, plugin_registry):
        """
        Current behavior: get_session_id_by_hash() maps project_hash to
        the active session_id.
        """
        await plugin_registry.register(
            session_id="sess-1",
            project_name="Project1",
            project_hash="hash-aaa",
            unity_version="2022.3"
        )

        found_id = await plugin_registry.get_session_id_by_hash("hash-aaa")
        assert found_id == "sess-1"

    @pytest.mark.asyncio
    async def test_registry_reconnect_updates_mapping(self, plugin_registry):
        """
        Current behavior: When a new session registers with same project_hash,
        it replaces the old mapping (supporting reconnect scenarios).
        """
        # Register first session
        await plugin_registry.register(
            session_id="sess-1",
            project_name="Project",
            project_hash="hash-same",
            unity_version="2022.3"
        )

        # Reconnect with new session_id, same hash
        await plugin_registry.register(
            session_id="sess-2",
            project_name="Project",
            project_hash="hash-same",
            unity_version="2022.3"
        )

        # Hash should map to new session
        found_id = await plugin_registry.get_session_id_by_hash("hash-same")
        assert found_id == "sess-2"

        # Old session should be removed
        old_session = await plugin_registry.get_session("sess-1")
        assert old_session is None

    @pytest.mark.asyncio
    async def test_registry_register_tools_for_session(self, plugin_registry):
        """
        Current behavior: register_tools_for_session() stores tool definitions
        keyed by tool name on the session.
        """
        await plugin_registry.register(
            session_id="sess-x",
            project_name="Project",
            project_hash="hash-x",
            unity_version="2022.3"
        )

        tools = [
            ToolDefinitionModel(name="tool1", description="Test tool 1"),
            ToolDefinitionModel(name="tool2", description="Test tool 2"),
        ]

        await plugin_registry.register_tools_for_session("sess-x", tools)

        updated_session = await plugin_registry.get_session("sess-x")
        assert len(updated_session.tools) == 2
        assert "tool1" in updated_session.tools
        assert "tool2" in updated_session.tools

    @pytest.mark.asyncio
    async def test_registry_touch_updates_connected_at(self, plugin_registry):
        """
        Current behavior: touch() updates the connected_at timestamp on heartbeat.
        """
        session, _ = await plugin_registry.register(
            session_id="sess-y",
            project_name="Project",
            project_hash="hash-y",
            unity_version="2022.3"
        )

        original_timestamp = session.connected_at

        # Wait a tiny bit
        await asyncio.sleep(0.01)

        # Touch should update timestamp
        await plugin_registry.touch("sess-y")

        updated = await plugin_registry.get_session("sess-y")
        assert updated.connected_at > original_timestamp

    @pytest.mark.asyncio
    async def test_registry_unregister_removes_session(self, plugin_registry):
        """
        Current behavior: unregister() removes session and its hash mapping.
        """
        await plugin_registry.register(
            session_id="sess-z",
            project_name="Project",
            project_hash="hash-z",
            unity_version="2022.3"
        )

        await plugin_registry.unregister("sess-z")

        session = await plugin_registry.get_session("sess-z")
        assert session is None

        hash_id = await plugin_registry.get_session_id_by_hash("hash-z")
        assert hash_id is None

    @pytest.mark.asyncio
    async def test_registry_list_sessions(self, plugin_registry):
        """
        Current behavior: list_sessions() returns shallow copy of all sessions.
        """
        await plugin_registry.register(
            session_id="sess-1",
            project_name="Project1",
            project_hash="hash-1",
            unity_version="2022.3"
        )
        await plugin_registry.register(
            session_id="sess-2",
            project_name="Project2",
            project_hash="hash-2",
            unity_version="2023.2"
        )

        sessions = await plugin_registry.list_sessions()

        assert len(sessions) == 2
        assert "sess-1" in sessions
        assert "sess-2" in sessions
