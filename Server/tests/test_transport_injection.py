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
    async def test_middleware_injects_into_tool_context(self, mock_context):
        """
        Current behavior: on_call_tool() calls _inject_unity_instance(),
        which sets ctx.set_state("unity_instance", active_instance) when
        an instance is active.
        """
        middleware = UnityInstanceMiddleware()
        instance_id = "Project@abc123"

        await middleware.set_active_instance(mock_context, instance_id)

        # Create middleware context wrapper
        middleware_ctx = Mock()
        middleware_ctx.fastmcp_context = mock_context

        call_next_called = False
        async def mock_call_next(_ctx):
            nonlocal call_next_called
            call_next_called = True
            return {"status": "ok"}

        await middleware.on_call_tool(middleware_ctx, mock_call_next)

        assert call_next_called, "Middleware must call next handler"
        mock_context.set_state.assert_called_with("unity_instance", instance_id, serializable=False)

    @pytest.mark.asyncio
    async def test_middleware_injects_into_resource_context(self, mock_context):
        """
        Current behavior: on_read_resource() performs same injection as
        on_call_tool(), ensuring resources see the active instance.
        """
        middleware = UnityInstanceMiddleware()
        instance_id = "Project@hash123"

        await middleware.set_active_instance(mock_context, instance_id)

        middleware_ctx = Mock()
        middleware_ctx.fastmcp_context = mock_context

        async def mock_call_next(_ctx):
            return {"status": "ok"}

        await middleware.on_read_resource(middleware_ctx, mock_call_next)

        mock_context.set_state.assert_called_with("unity_instance", instance_id, serializable=False)

    @pytest.mark.asyncio
    async def test_middleware_shadows_old_injection_when_no_instance(self, mock_context):
        """An unselected request must not inherit an older persisted injection."""
        middleware = UnityInstanceMiddleware()

        # Don't set any instance (will try auto-select and fail)
        middleware_ctx = Mock()
        middleware_ctx.fastmcp_context = mock_context

        async def mock_call_next(_ctx):
            return {"status": "ok"}

        # Mock PluginHub as unavailable AND legacy connection pool to prevent fallback discovery
        with patch("transport.unity_instance_middleware.PluginHub.is_configured", return_value=False):
            with patch("transport.legacy.unity_connection.get_unity_connection_pool", return_value=None):
                await middleware.on_call_tool(middleware_ctx, mock_call_next)

        mock_context.set_state.assert_called_with("unity_instance", None, serializable=False)
