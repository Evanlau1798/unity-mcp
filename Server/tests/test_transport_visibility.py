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

class TestUnityInstanceMiddlewareInjection:
    @pytest.mark.asyncio
    async def test_list_tools_filters_disabled_unity_tools_and_aliases(self, mock_context, monkeypatch):
        """
        Current behavior: in HTTP mode with a connected Unity session, on_list_tools()
        uses PluginHub-registered tool names to hide disabled Unity tools while keeping
        server-only tools visible. Aliases like create_script follow manage_script state.
        """
        middleware = UnityInstanceMiddleware()
        middleware_ctx = Mock()
        middleware_ctx.fastmcp_context = mock_context

        await mock_context.set_state("unity_instance", "Project@abc123")
        monkeypatch.setattr(config, "transport_mode", "http")

        available_tools = [
            SimpleNamespace(name="manage_scene"),
            SimpleNamespace(name="manage_script"),
            SimpleNamespace(name="set_active_instance"),
            SimpleNamespace(name="manage_asset"),
            SimpleNamespace(name="create_script"),
        ]

        async def call_next(_ctx):
            return available_tools

        with patch.object(middleware, "_inject_unity_instance", new=AsyncMock()):
            with patch("transport.unity_instance_middleware.PluginHub.is_configured", return_value=True):
                with patch("transport.unity_instance_middleware.get_registered_tools", return_value=_tool_registry_for_visibility_tests()):
                    with patch("transport.unity_instance_middleware.PluginHub.get_sessions", new_callable=AsyncMock) as mock_get_sessions:
                        with patch("transport.unity_instance_middleware.PluginHub.get_tools_for_project", new_callable=AsyncMock) as mock_get_tools:
                            mock_get_sessions.return_value = SessionList(
                                sessions={
                                    "session-1": SessionDetails(
                                        project="Project",
                                        hash="abc123",
                                        unity_version="2022.3",
                                        connected_at="2025-01-26T00:00:00Z",
                                    )
                                }
                            )
                            mock_get_tools.return_value = [
                                SimpleNamespace(name="manage_scene"),
                                SimpleNamespace(name="manage_script"),
                            ]

                            filtered = await middleware.on_list_tools(middleware_ctx, call_next)

        names = [tool.name for tool in filtered]
        assert "manage_scene" in names
        assert "create_script" in names
        assert "set_active_instance" in names
        assert "manage_asset" not in names

    @pytest.mark.asyncio
    async def test_list_tools_skips_filter_when_no_tools_registered_yet(self, mock_context, monkeypatch):
        """
        When a Unity session is connected but register_tools has not been sent yet
        (empty registered_tools), defer filtering to avoid hiding tools that may
        be valid once register_tools arrives. This prevents clients that cache
        early list_tools responses from getting persistently incomplete tool lists.
        """
        middleware = UnityInstanceMiddleware()
        middleware_ctx = Mock()
        middleware_ctx.fastmcp_context = mock_context

        await mock_context.set_state("unity_instance", "Project@abc123")
        monkeypatch.setattr(config, "transport_mode", "http")

        original_tools = [
            SimpleNamespace(name="manage_scene"),
            SimpleNamespace(name="manage_asset"),
            SimpleNamespace(name="create_script"),
            SimpleNamespace(name="set_active_instance"),
            SimpleNamespace(name="custom_server_tool"),
        ]

        async def call_next(_ctx):
            return original_tools

        with patch.object(middleware, "_inject_unity_instance", new=AsyncMock()):
            with patch("transport.unity_instance_middleware.PluginHub.is_configured", return_value=True):
                with patch("transport.unity_instance_middleware.get_registered_tools", return_value=_tool_registry_for_visibility_tests()):
                    with patch("transport.unity_instance_middleware.PluginHub.get_sessions", new_callable=AsyncMock) as mock_get_sessions:
                        with patch("transport.unity_instance_middleware.PluginHub.get_tools_for_project", new_callable=AsyncMock) as mock_get_tools:
                            mock_get_sessions.return_value = SessionList(
                                sessions={
                                    "session-1": SessionDetails(
                                        project="Project",
                                        hash="abc123",
                                        unity_version="2022.3",
                                        connected_at="2025-01-26T00:00:00Z",
                                    )
                                }
                            )
                            # Simulate register_tools not yet sent
                            mock_get_tools.return_value = []

                            filtered = await middleware.on_list_tools(middleware_ctx, call_next)

        names = [tool.name for tool in filtered]
        # All tools should be visible when register_tools hasn't been sent yet
        assert "manage_scene" in names
        assert "manage_asset" in names
        assert "create_script" in names
        assert "set_active_instance" in names
        assert "custom_server_tool" in names

    @pytest.mark.asyncio
    async def test_list_tools_filters_when_all_tools_disabled(self, mock_context, monkeypatch):
        """
        When register_tools has been sent with an empty tool list (all tools disabled),
        Unity-managed tools are filtered out while server-only tools remain visible.
        This differs from the "no tools registered yet" case where we defer filtering.
        """
        middleware = UnityInstanceMiddleware()
        middleware_ctx = Mock()
        middleware_ctx.fastmcp_context = mock_context

        await mock_context.set_state("unity_instance", "Project@abc123")
        monkeypatch.setattr(config, "transport_mode", "http")

        original_tools = [
            SimpleNamespace(name="manage_scene"),
            SimpleNamespace(name="manage_asset"),
            SimpleNamespace(name="create_script"),
            SimpleNamespace(name="set_active_instance"),
            SimpleNamespace(name="custom_server_tool"),
        ]

        async def call_next(_ctx):
            return original_tools

        # Simulate a registered tool that indicates all tools are disabled
        disabled_tool = SimpleNamespace(name="_marker_tool_indicates_registration_sent")
        registered_tools = [disabled_tool]

        with patch.object(middleware, "_inject_unity_instance", new=AsyncMock()):
            with patch("transport.unity_instance_middleware.PluginHub.is_configured", return_value=True):
                with patch("transport.unity_instance_middleware.get_registered_tools", return_value=_tool_registry_for_visibility_tests()):
                    with patch("transport.unity_instance_middleware.PluginHub.get_sessions", new_callable=AsyncMock) as mock_get_sessions:
                        with patch("transport.unity_instance_middleware.PluginHub.get_tools_for_project", new_callable=AsyncMock) as mock_get_tools:
                            mock_get_sessions.return_value = SessionList(
                                sessions={
                                    "session-1": SessionDetails(
                                        project="Project",
                                        hash="abc123",
                                        unity_version="2022.3",
                                        connected_at="2025-01-26T00:00:00Z",
                                    )
                                }
                            )
                            # register_tools has been sent (non-empty list), but all tools disabled
                            mock_get_tools.return_value = registered_tools

                            filtered = await middleware.on_list_tools(middleware_ctx, call_next)

        names = [tool.name for tool in filtered]
        assert "set_active_instance" in names
        assert "custom_server_tool" in names
        assert "manage_scene" not in names
        assert "manage_asset" not in names
        assert "create_script" not in names

    @pytest.mark.asyncio
    async def test_list_tools_skips_filter_when_enabled_set_lookup_fails(self, mock_context, monkeypatch):
        """
        Current behavior: if enabled-tool lookup fails unexpectedly, on_list_tools()
        leaves the FastMCP list unchanged to avoid hiding tools due to transient
        PluginHub failures.
        """
        middleware = UnityInstanceMiddleware()
        middleware_ctx = Mock()
        middleware_ctx.fastmcp_context = mock_context

        await mock_context.set_state("unity_instance", "Project@abc123")
        monkeypatch.setattr(config, "transport_mode", "http")

        original_tools = [
            SimpleNamespace(name="manage_scene"),
            SimpleNamespace(name="manage_asset"),
            SimpleNamespace(name="set_active_instance"),
        ]

        async def call_next(_ctx):
            return original_tools

        with patch.object(middleware, "_inject_unity_instance", new=AsyncMock()):
            with patch("transport.unity_instance_middleware.PluginHub.is_configured", return_value=True):
                with patch("transport.unity_instance_middleware.get_registered_tools", return_value=_tool_registry_for_visibility_tests()):
                    with patch("transport.unity_instance_middleware.PluginHub.get_sessions", new_callable=AsyncMock) as mock_get_sessions:
                        with patch("transport.unity_instance_middleware.PluginHub.get_tools_for_project", new_callable=AsyncMock) as mock_get_tools:
                            mock_get_sessions.return_value = SessionList(
                                sessions={
                                    "session-1": SessionDetails(
                                        project="Project",
                                        hash="abc123",
                                        unity_version="2022.3",
                                        connected_at="2025-01-26T00:00:00Z",
                                    )
                                }
                            )
                            mock_get_tools.side_effect = RuntimeError("hub unavailable")

                            filtered = await middleware.on_list_tools(middleware_ctx, call_next)

        assert [tool.name for tool in filtered] == [tool.name for tool in original_tools]

    @pytest.mark.asyncio
    async def test_list_tools_uses_user_scoped_tool_lookup_in_hosted_mode(self, mock_context, monkeypatch):
        """
        Current behavior: in remote-hosted HTTP mode, tool filtering fetches
        Unity-registered tools scoped to the current user.
        """
        middleware = UnityInstanceMiddleware()
        middleware_ctx = Mock()
        middleware_ctx.fastmcp_context = mock_context

        await mock_context.set_state("unity_instance", "Project@abc123")
        await mock_context.set_state("user_id", "user-123")
        monkeypatch.setattr(config, "transport_mode", "http")
        monkeypatch.setattr(config, "http_remote_hosted", True)

        async def call_next(_ctx):
            return [SimpleNamespace(name="manage_scene")]

        with patch.object(middleware, "_inject_unity_instance", new=AsyncMock()):
            with patch("transport.unity_instance_middleware.PluginHub.is_configured", return_value=True):
                with patch("transport.unity_instance_middleware.get_registered_tools", return_value=_tool_registry_for_visibility_tests()):
                    with patch("transport.unity_instance_middleware.PluginHub.get_sessions", new_callable=AsyncMock) as mock_get_sessions:
                        with patch("transport.unity_instance_middleware.PluginHub.get_tools_for_project", new_callable=AsyncMock) as mock_get_tools:
                            mock_get_sessions.return_value = SessionList(
                                sessions={
                                    "session-1": SessionDetails(
                                        project="Project",
                                        hash="abc123",
                                        unity_version="2022.3",
                                        connected_at="2025-01-26T00:00:00Z",
                                    )
                                }
                            )
                            mock_get_tools.return_value = [SimpleNamespace(name="manage_scene")]

                            filtered = await middleware.on_list_tools(middleware_ctx, call_next)

        assert [tool.name for tool in filtered] == ["manage_scene"]
        mock_get_tools.assert_awaited_once_with("abc123", user_id="user-123")

    @pytest.mark.asyncio
    async def test_list_tools_skips_filter_when_active_instance_hash_is_stale(self, mock_context, monkeypatch):
        middleware = UnityInstanceMiddleware()
        middleware_ctx = Mock()
        middleware_ctx.fastmcp_context = mock_context

        await mock_context.set_state("unity_instance", "Project@stale-hash")
        monkeypatch.setattr(config, "transport_mode", "http")

        original_tools = [
            SimpleNamespace(name="manage_scene"),
            SimpleNamespace(name="manage_asset"),
            SimpleNamespace(name="set_active_instance"),
        ]

        async def call_next(_ctx):
            return original_tools

        with patch.object(middleware, "_inject_unity_instance", new=AsyncMock()):
            with patch("transport.unity_instance_middleware.PluginHub.is_configured", return_value=True):
                with patch("transport.unity_instance_middleware.get_registered_tools", return_value=_tool_registry_for_visibility_tests()):
                    with patch("transport.unity_instance_middleware.PluginHub.get_sessions", new_callable=AsyncMock) as mock_get_sessions:
                        with patch("transport.unity_instance_middleware.PluginHub.get_tools_for_project", new_callable=AsyncMock) as mock_get_tools:
                            mock_get_sessions.return_value = SessionList(
                                sessions={
                                    "session-1": SessionDetails(
                                        project="Project",
                                        hash="abc123",
                                        unity_version="2022.3",
                                        connected_at="2025-01-26T00:00:00Z",
                                    )
                                }
                            )

                            filtered = await middleware.on_list_tools(middleware_ctx, call_next)

        assert [tool.name for tool in filtered] == [tool.name for tool in original_tools]
        mock_get_tools.assert_not_called()

    @pytest.mark.asyncio
    async def test_list_tools_hides_alias_when_target_tool_is_disabled(self, mock_context, monkeypatch):
        middleware = UnityInstanceMiddleware()
        middleware_ctx = Mock()
        middleware_ctx.fastmcp_context = mock_context

        await mock_context.set_state("unity_instance", "Project@abc123")
        monkeypatch.setattr(config, "transport_mode", "http")

        original_tools = [
            SimpleNamespace(name="manage_scene"),
            SimpleNamespace(name="manage_script"),
            SimpleNamespace(name="create_script"),
            SimpleNamespace(name="set_active_instance"),
        ]

        async def call_next(_ctx):
            return original_tools

        with patch.object(middleware, "_inject_unity_instance", new=AsyncMock()):
            with patch("transport.unity_instance_middleware.PluginHub.is_configured", return_value=True):
                with patch("transport.unity_instance_middleware.get_registered_tools", return_value=_tool_registry_for_visibility_tests()):
                    with patch("transport.unity_instance_middleware.PluginHub.get_sessions", new_callable=AsyncMock) as mock_get_sessions:
                        with patch("transport.unity_instance_middleware.PluginHub.get_tools_for_project", new_callable=AsyncMock) as mock_get_tools:
                            mock_get_sessions.return_value = SessionList(
                                sessions={
                                    "session-1": SessionDetails(
                                        project="Project",
                                        hash="abc123",
                                        unity_version="2022.3",
                                        connected_at="2025-01-26T00:00:00Z",
                                    )
                                }
                            )
                            # manage_script is disabled; alias create_script should also be hidden.
                            mock_get_tools.return_value = [SimpleNamespace(name="manage_scene")]

                            filtered = await middleware.on_list_tools(middleware_ctx, call_next)

        names = [tool.name for tool in filtered]
        assert "manage_scene" in names
        assert "set_active_instance" in names
        assert "manage_script" not in names
        assert "create_script" not in names

    @pytest.mark.asyncio
    async def test_list_tools_keeps_all_visible_when_tool_registry_is_empty(self, mock_context, monkeypatch):
        middleware = UnityInstanceMiddleware()
        middleware_ctx = Mock()
        middleware_ctx.fastmcp_context = mock_context

        await mock_context.set_state("unity_instance", "Project@abc123")
        monkeypatch.setattr(config, "transport_mode", "http")

        original_tools = [
            SimpleNamespace(name="manage_scene"),
            SimpleNamespace(name="set_active_instance"),
            SimpleNamespace(name="execute_custom_tool"),
        ]

        async def call_next(_ctx):
            return original_tools

        with patch.object(middleware, "_inject_unity_instance", new=AsyncMock()):
            with patch("transport.unity_instance_middleware.PluginHub.is_configured", return_value=True):
                with patch("transport.unity_instance_middleware.get_registered_tools", return_value=[]):
                    with patch("transport.unity_instance_middleware.PluginHub.get_sessions", new_callable=AsyncMock) as mock_get_sessions:
                        with patch("transport.unity_instance_middleware.PluginHub.get_tools_for_project", new_callable=AsyncMock) as mock_get_tools:
                            mock_get_sessions.return_value = SessionList(
                                sessions={
                                    "session-1": SessionDetails(
                                        project="Project",
                                        hash="abc123",
                                        unity_version="2022.3",
                                        connected_at="2025-01-26T00:00:00Z",
                                    )
                                }
                            )
                            mock_get_tools.return_value = []

                            filtered = await middleware.on_list_tools(middleware_ctx, call_next)

        assert [tool.name for tool in filtered] == [tool.name for tool in original_tools]

    @pytest.mark.asyncio
    async def test_list_tools_uses_union_of_enabled_tools_across_multiple_sessions(self, mock_context, monkeypatch):
        middleware = UnityInstanceMiddleware()
        middleware_ctx = Mock()
        middleware_ctx.fastmcp_context = mock_context

        monkeypatch.setattr(config, "transport_mode", "http")

        original_tools = [
            SimpleNamespace(name="manage_scene"),
            SimpleNamespace(name="manage_asset"),
            SimpleNamespace(name="manage_script"),
        ]

        async def call_next(_ctx):
            return original_tools

        async def get_tools_side_effect(project_hash, user_id=None):  # noqa: ARG001
            if project_hash == "hash-a":
                return [SimpleNamespace(name="manage_scene")]
            if project_hash == "hash-b":
                return [SimpleNamespace(name="manage_asset")]
            return []

        with patch.object(middleware, "_inject_unity_instance", new=AsyncMock()):
            with patch("transport.unity_instance_middleware.PluginHub.is_configured", return_value=True):
                with patch("transport.unity_instance_middleware.get_registered_tools", return_value=_tool_registry_for_visibility_tests()):
                    with patch("transport.unity_instance_middleware.PluginHub.get_sessions", new_callable=AsyncMock) as mock_get_sessions:
                        with patch("transport.unity_instance_middleware.PluginHub.get_tools_for_project", new_callable=AsyncMock) as mock_get_tools:
                            mock_get_sessions.return_value = SessionList(
                                sessions={
                                    "session-a": SessionDetails(
                                        project="ProjectA",
                                        hash="hash-a",
                                        unity_version="2022.3",
                                        connected_at="2025-01-26T00:00:00Z",
                                    ),
                                    "session-b": SessionDetails(
                                        project="ProjectB",
                                        hash="hash-b",
                                        unity_version="2022.3",
                                        connected_at="2025-01-26T00:00:00Z",
                                    ),
                                }
                            )
                            mock_get_tools.side_effect = get_tools_side_effect

                            filtered = await middleware.on_list_tools(middleware_ctx, call_next)

        names = [tool.name for tool in filtered]
        assert "manage_scene" in names
        assert "manage_asset" in names
        assert "manage_script" not in names
