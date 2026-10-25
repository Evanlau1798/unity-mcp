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

class TestSessionResolution:
    """Test session resolution with waiting for reconnects."""

    @pytest.mark.asyncio
    async def test_resolve_session_id_waits_for_reconnect(self, plugin_registry):
        """
        Current behavior: _resolve_session_id() waits up to max_wait_s for
        a plugin to connect/reconnect before failing.
        """
        # Configure PluginHub
        loop = asyncio.get_event_loop()
        PluginHub.configure(plugin_registry, loop)

        # This simulates domain reload recovery
        target_hash = "hash-delayed"

        # Start with no sessions
        async def delayed_register():
            await asyncio.sleep(0.1)
            await plugin_registry.register(
                session_id="sess-delayed",
                project_name="Project",
                project_hash=target_hash,
                unity_version="2022.3"
            )

        # Schedule registration
        task = asyncio.create_task(delayed_register())

        # Resolve with short timeout
        session_id = await PluginHub._resolve_session_id(target_hash)

        assert session_id == "sess-delayed"

        # Ensure background task completes
        await task

        # Cleanup
        PluginHub._registry = None
        PluginHub._lock = None
        PluginHub._loop = None

    @pytest.mark.asyncio
    async def test_resolve_session_id_fails_when_no_session_appears(self, plugin_registry, monkeypatch):
        """
        Current behavior: If no session appears within max_wait_s,
        raise NoUnitySessionError.
        """
        # Configure PluginHub
        loop = asyncio.get_event_loop()
        PluginHub.configure(plugin_registry, loop)

        # Set very short timeout
        monkeypatch.setenv("UNITY_MCP_SESSION_RESOLVE_MAX_WAIT_S", "0.05")

        # Try to resolve unknown hash
        with pytest.raises(NoUnitySessionError):
            await PluginHub._resolve_session_id("nonexistent-hash")

        # Cleanup
        PluginHub._registry = None
        PluginHub._lock = None
        PluginHub._loop = None

    @pytest.mark.asyncio
    async def test_resolve_session_id_auto_selects_sole_instance(self, plugin_registry):
        """
        Current behavior: When no target_hash provided and exactly one session
        exists, auto-select it.
        """
        # Configure PluginHub
        loop = asyncio.get_event_loop()
        PluginHub.configure(plugin_registry, loop)

        await plugin_registry.register(
            session_id="sess-sole",
            project_name="Project",
            project_hash="hash-sole",
            unity_version="2022.3"
        )

        session_id = await PluginHub._resolve_session_id(None)

        assert session_id == "sess-sole"

        # Cleanup
        PluginHub._registry = None
        PluginHub._lock = None
        PluginHub._loop = None

    @pytest.mark.asyncio
    async def test_resolve_session_id_rejects_ambiguous_selection(self, plugin_registry):
        """
        Current behavior: When no target and multiple sessions exist,
        raise RuntimeError indicating ambiguity.
        """
        # Configure PluginHub
        loop = asyncio.get_event_loop()
        PluginHub.configure(plugin_registry, loop)

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

        with pytest.raises(InstanceSelectionRequiredError, match="Multiple Unity instances"):
            await PluginHub._resolve_session_id(None)

        # Cleanup
        PluginHub._registry = None
        PluginHub._lock = None
        PluginHub._loop = None

    @pytest.mark.asyncio
    async def test_resolve_session_id_ambiguity_lists_available_instances(self, plugin_registry):
        """The refusal carries the instance ids (parity with the stdio guard) so
        agents can select without a second lookup."""
        loop = asyncio.get_event_loop()
        PluginHub.configure(plugin_registry, loop)

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

        with pytest.raises(InstanceSelectionRequiredError) as excinfo:
            await PluginHub._resolve_session_id(None)

        assert excinfo.value.available_instances == [
            "Project1@hash-1", "Project2@hash-2"]
        assert "Project1@hash-1" in str(excinfo.value)
        assert "Project2@hash-2" in str(excinfo.value)

        # Cleanup
        PluginHub._registry = None
        PluginHub._lock = None
        PluginHub._loop = None

    @pytest.mark.asyncio
    async def test_http_selection_error_hints_selection_not_retry(self, monkeypatch):
        """A blind retry fails identically, so the HTTP wrapper must hint at
        selection and surface the ids structurally instead of the blanket
        retry hint."""
        from transport import unity_transport

        async def _no_user():
            return None

        async def _raise_selection(*_args, **_kwargs):
            raise InstanceSelectionRequiredError(
                InstanceSelectionRequiredError._MULTIPLE_INSTANCES,
                available_instances=["A@hash-a", "B@hash-b"])

        monkeypatch.setattr(unity_transport, "_is_http_transport", lambda: True)
        monkeypatch.setattr(
            unity_transport, "_resolve_user_id_from_request", _no_user)
        monkeypatch.setattr(
            unity_transport.PluginHub, "send_command_for_instance", _raise_selection)

        async def _send_fn(*_a, **_k):
            raise AssertionError("stdio path should not be used on HTTP transport")

        resp = await unity_transport.send_with_unity_instance(
            _send_fn, None, "manage_scene", {})

        assert resp["success"] is False
        assert resp["hint"] == "select_instance"
        assert resp["data"]["reason"] == "instance_selection_required"
        assert resp["data"]["available_instances"] == ["A@hash-a", "B@hash-b"]

    @pytest.mark.asyncio
    async def test_resolve_session_id_parses_instance_format(self, plugin_registry):
        """
        Current behavior: Accepts both "ProjectName@hash" and bare "hash"
        formats, extracting the hash portion.
        """
        # Configure PluginHub
        loop = asyncio.get_event_loop()
        PluginHub.configure(plugin_registry, loop)

        target_hash = "hash-parse"

        await plugin_registry.register(
            session_id="sess-parse",
            project_name="ProjectName",
            project_hash=target_hash,
            unity_version="2022.3"
        )

        # Resolve via "Name@hash" format
        session_id = await PluginHub._resolve_session_id("ProjectName@hash-parse")
        assert session_id == "sess-parse"

        # Resolve via bare hash format
        session_id = await PluginHub._resolve_session_id("hash-parse")
        assert session_id == "sess-parse"

        # Cleanup
        PluginHub._registry = None
        PluginHub._lock = None
        PluginHub._loop = None
