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

class TestUnityInstanceMiddlewareSessionManagement:
    """Test instance routing and per-session state management.

    The middleware now delegates persistence to FastMCP's session-scoped
    state store (``ctx.set_state`` / ``ctx.get_state``), which is keyed by
    ``ctx.session_id`` (the MCP-Session-Id header on HTTP, a per-subprocess
    UUID on stdio). The tests below validate that contract from the
    middleware's perspective.
    """

    @pytest.mark.asyncio
    async def test_middleware_stores_instance_per_session(self, mock_context):
        """A single ctx round-trips set/get correctly via session state."""
        middleware = UnityInstanceMiddleware()
        instance_id = "TestProject@abc123def456"

        await middleware.set_active_instance(mock_context, instance_id)
        retrieved = await middleware.get_active_instance(mock_context)

        assert retrieved == instance_id, \
            "Middleware must store and retrieve instance per session"

    @pytest.mark.asyncio
    async def test_middleware_isolates_multiple_sessions(self):
        """
        Two independent ctxs must not see each other's selection.

        This is the regression test for #1023: previously the middleware keyed
        on the peer-supplied client_id and collapsed multiple clients onto the
        same record. The new implementation defers to FastMCP session state,
        which is isolated per ``ctx.session_id`` — modelled here as two ctxs
        each holding their own private state dict.
        """
        middleware = UnityInstanceMiddleware()

        ctx1 = _make_ctx("session-1")
        ctx2 = _make_ctx("session-2")

        await middleware.set_active_instance(ctx1, "Project1@hash1")
        await middleware.set_active_instance(ctx2, "Project2@hash2")

        assert await middleware.get_active_instance(ctx1) == "Project1@hash1"
        assert await middleware.get_active_instance(ctx2) == "Project2@hash2"

    @pytest.mark.asyncio
    async def test_middleware_clear_instance(self, mock_context):
        """clear_active_instance() resets the per-session selection to None."""
        middleware = UnityInstanceMiddleware()
        instance_id = "TestProject@xyz"

        await middleware.set_active_instance(mock_context, instance_id)
        assert await middleware.get_active_instance(mock_context) == instance_id

        await middleware.clear_active_instance(mock_context)
        assert await middleware.get_active_instance(mock_context) is None

    @pytest.mark.asyncio
    async def test_middleware_repeated_updates_settle_to_latest(self):
        """Sequential writes within one session leave the latest value in place."""
        middleware = UnityInstanceMiddleware()
        ctx = _make_ctx("session-123")

        for i in range(10):
            await middleware.set_active_instance(ctx, f"Project{i}@hash{i}")

        assert await middleware.get_active_instance(ctx) == "Project9@hash9"

class TestAutoSelectInstance:
    """Test auto-selection of sole Unity instance when none is explicitly set."""

    @pytest.mark.asyncio
    async def test_autoselect_via_plugin_hub_single_instance(self, mock_context):
        """
        Current behavior: When single instance is available via PluginHub,
        auto-select it and store in middleware state.
        """
        middleware = UnityInstanceMiddleware()

        # Mock PluginHub to return single session
        fake_sessions = SessionList(
            sessions={
                "session-1": SessionDetails(
                    project="TestProject",
                    hash="abc123",
                    unity_version="2022.3",
                    connected_at="2025-01-26T00:00:00Z"
                )
            }
        )

        with patch("transport.unity_instance_middleware.PluginHub.is_configured", return_value=True):
            with patch("transport.unity_instance_middleware.PluginHub.get_sessions", new_callable=AsyncMock) as mock_get:
                mock_get.return_value = fake_sessions

                instance = await middleware._maybe_autoselect_instance(mock_context)

        assert instance == "TestProject@abc123"
        assert await middleware.get_active_instance(mock_context) == "TestProject@abc123"

    @pytest.mark.asyncio
    async def test_autoselect_fails_with_multiple_instances(self, mock_context):
        """
        Current behavior: When multiple instances available, auto-select
        returns None (ambiguous), allowing caller to decide.
        """
        middleware = UnityInstanceMiddleware()

        fake_sessions = SessionList(
            sessions={
                "session-1": SessionDetails(
                    project="Project1",
                    hash="aaa111",
                    unity_version="2022.3",
                    connected_at="2025-01-26T00:00:00Z"
                ),
                "session-2": SessionDetails(
                    project="Project2",
                    hash="bbb222",
                    unity_version="2023.2",
                    connected_at="2025-01-26T00:00:00Z"
                )
            }
        )

        with patch("transport.unity_instance_middleware.PluginHub.is_configured", return_value=True):
            with patch("transport.unity_instance_middleware.PluginHub.get_sessions", new_callable=AsyncMock) as mock_get:
                with patch("transport.legacy.unity_connection.get_unity_connection_pool", return_value=None):
                    mock_get.return_value = fake_sessions

                    instance = await middleware._maybe_autoselect_instance(mock_context)

        assert instance is None

    @pytest.mark.asyncio
    async def test_autoselect_handles_plugin_hub_connection_error(self, mock_context):
        """
        Current behavior: If PluginHub probe fails with ConnectionError,
        gracefully falls back and returns None (no instance selected).
        """
        middleware = UnityInstanceMiddleware()

        with patch("transport.unity_instance_middleware.PluginHub.is_configured", return_value=True):
            with patch("transport.unity_instance_middleware.PluginHub.get_sessions", new_callable=AsyncMock) as mock_get:
                with patch("transport.legacy.unity_connection.get_unity_connection_pool", return_value=None):
                    mock_get.side_effect = ConnectionError("Plugin hub unavailable")

                    # When PluginHub fails, auto-select returns None (graceful fallback)
                    instance = await middleware._maybe_autoselect_instance(mock_context)

        # Should return None since both PluginHub failed
        assert instance is None

class TestMiddlewareSingleton:
    """Test global middleware singleton pattern."""

    def test_get_unity_instance_middleware_lazy_initializes(self):
        """
        Current behavior: get_unity_instance_middleware() lazily creates
        a singleton if not already set.
        """
        # Reset global state
        import transport.unity_instance_middleware as mw_module
        mw_module._unity_instance_middleware = None

        middleware1 = get_unity_instance_middleware()
        middleware2 = get_unity_instance_middleware()

        assert middleware1 is middleware2

    def test_set_unity_instance_middleware_replaces_singleton(self):
        """
        Current behavior: set_unity_instance_middleware() allows replacing
        the global singleton (used during server initialization).
        """
        import transport.unity_instance_middleware as mw_module
        mw_module._unity_instance_middleware = None

        middleware1 = UnityInstanceMiddleware()
        set_unity_instance_middleware(middleware1)

        retrieved = get_unity_instance_middleware()
        assert retrieved is middleware1

class TestTransportEdgeCases:
    """Test edge cases and error scenarios."""

    @pytest.mark.asyncio
    async def test_middleware_handles_exception_during_autoselect(self, mock_context):
        """
        Current behavior: If autoselect raises an unexpected exception,
        it's caught and logged, allowing the middleware to continue.
        """
        middleware = UnityInstanceMiddleware()

        with patch("transport.unity_instance_middleware.PluginHub.is_configured", return_value=True):
            with patch("transport.unity_instance_middleware.PluginHub.get_sessions", new_callable=AsyncMock) as mock_get:
                with patch("transport.legacy.unity_connection.get_unity_connection_pool", return_value=None):
                    mock_get.side_effect = RuntimeError("Unexpected error")

                    # Should not raise, just return None
                    instance = await middleware._maybe_autoselect_instance(mock_context)

        assert instance is None

    def test_plugin_hub_encoding_is_json(self):
        """
        Current behavior: PluginHub WebSocketEndpoint uses JSON encoding.
        """
        assert PluginHub.encoding == "json"

    def test_plugin_hub_timeout_constants(self):
        """
        Current behavior: PluginHub defines standard timeout constants.
        """
        assert PluginHub.KEEP_ALIVE_INTERVAL == 15
        assert PluginHub.SERVER_TIMEOUT == 30
        assert PluginHub.COMMAND_TIMEOUT == 30
        assert PluginHub.FAST_FAIL_TIMEOUT == 2.0

class TestTransportIntegration:
    """Test realistic integration scenarios."""

    @pytest.mark.asyncio
    async def test_middleware_and_registry_interaction(self, mock_context, plugin_registry):
        """
        Current behavior: Middleware stores instance selection, which
        can be used to route commands via registry lookup.
        """
        middleware = UnityInstanceMiddleware()

        # Register a session in the registry
        await plugin_registry.register(
            session_id="sess-interact",
            project_name="Project",
            project_hash="hash-interact",
            unity_version="2022.3"
        )

        # Middleware stores the instance
        await middleware.set_active_instance(mock_context, "Project@hash-interact")

        # Application can use middleware to route
        instance = await middleware.get_active_instance(mock_context)
        assert instance == "Project@hash-interact"

        # And registry to find session
        resolved_id = await plugin_registry.get_session_id_by_hash("hash-interact")
        assert resolved_id == "sess-interact"

    @pytest.mark.asyncio
    async def test_registry_and_middleware_complete_flow(self, mock_context, plugin_registry):
        """
        Current behavior: Integrated flow - register session in registry,
        select it in middleware, then route by hash lookup.
        """
        # Setup
        middleware = UnityInstanceMiddleware()

        # 1. Plugin connects and registers in registry
        await plugin_registry.register(
            session_id="sess-complete",
            project_name="CompleteProject",
            project_hash="hash-complete",
            unity_version="2022.3"
        )

        # 2. User selects instance via middleware
        await middleware.set_active_instance(mock_context, "CompleteProject@hash-complete")

        # 3. Tools route using both middleware + registry
        selected_instance = await middleware.get_active_instance(mock_context)
        assert selected_instance == "CompleteProject@hash-complete"

        # Extract hash and resolve back to session
        hash_part = selected_instance.split("@")[1]
        resolved_session = await plugin_registry.get_session_id_by_hash(hash_part)
        assert resolved_session == "sess-complete"

        # 4. Verify session has the correct data
        session = await plugin_registry.get_session(resolved_session)
        assert session.project_name == "CompleteProject"
        assert session.unity_version == "2022.3"
