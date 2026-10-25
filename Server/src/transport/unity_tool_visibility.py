"""Unity tool-list visibility, shared with instance routing middleware."""
import logging
import time
from fastmcp.server.middleware import MiddlewareContext
from core.config import config

logger = logging.getLogger("mcp-for-unity-server")
_diag = logging.getLogger("transport.unity_instance_middleware")


class UnityToolVisibilityMixin:
    async def on_list_tools(self, context: MiddlewareContext, call_next):
        """Filter MCP tool listing to the Unity-enabled set when session data is available."""
        try:
            await self._inject_unity_instance(context)
        except Exception as exc:
            # Re-raise authentication errors so callers get a proper auth failure
            if isinstance(exc, RuntimeError) and "authentication" in str(exc).lower():
                raise
            _diag.warning(
                "on_list_tools: _inject_unity_instance failed (%s: %s), continuing without instance",
                type(exc).__name__, exc,
            )

        tools = await call_next(context)

        tool_names_from_fastmcp = sorted(getattr(t, "name", "?") for t in tools)
        _diag.debug(
            "on_list_tools: FastMCP returned %d tools: %s",
            len(tools), tool_names_from_fastmcp,
        )

        if not self._should_filter_tool_listing():
            _diag.debug("on_list_tools: skipping middleware filter (not HTTP or PluginHub not configured)")
            return tools

        self._refresh_tool_visibility_metadata_from_registry()
        enabled_tool_names = await self._resolve_enabled_tool_names_for_context(context)
        if enabled_tool_names is None:
            _diag.debug("on_list_tools: no Unity session data, returning %d tools from FastMCP as-is", len(tools))
            return tools

        filtered = []
        for tool in tools:
            tool_name = getattr(tool, "name", None)
            if self._is_tool_visible(tool_name, enabled_tool_names):
                filtered.append(tool)

        _diag.debug(
            "on_list_tools: filtered %d/%d tools visible (Unity register_tools). "
            "enabled_names=%s",
            len(filtered), len(tools), sorted(enabled_tool_names),
        )
        return filtered

    def _should_filter_tool_listing(self) -> bool:
        from transport.unity_instance_middleware import PluginHub
        transport = (config.transport_mode or "stdio").lower()
        return transport == "http" and PluginHub.is_configured()

    async def _resolve_enabled_tool_names_for_context(
        self,
        context: MiddlewareContext,
    ) -> set[str] | None:
        from transport.unity_instance_middleware import PluginHub
        ctx = context.fastmcp_context
        user_id = (await ctx.get_state("user_id")) if config.http_remote_hosted else None
        active_instance = await ctx.get_state("unity_instance")
        project_hashes = self._resolve_candidate_project_hashes(active_instance)
        try:
            sessions_data = await PluginHub.get_sessions(user_id=user_id)
            sessions = sessions_data.sessions if sessions_data else {}
        except Exception as exc:
            logger.debug(
                "Failed to fetch sessions for tool filtering (user_id=%s, %s)",
                user_id,
                type(exc).__name__,
                exc_info=True,
            )
            return None

        session_hashes = {
            getattr(session, "hash", None)
            for session in sessions.values()
            if getattr(session, "hash", None)
        }

        if project_hashes:
            active_hash = project_hashes[0]
            # Stale active_instance should not hide all Unity-managed tools.
            if active_hash not in session_hashes:
                return None
        else:
            if not sessions:
                return None

            if len(sessions) == 1:
                only_session = next(iter(sessions.values()))
                only_hash = getattr(only_session, "hash", None)
                if only_hash:
                    project_hashes = [only_hash]
            else:
                # Multiple sessions without explicit selection: use a union so we don't
                # hide tools that are valid in at least one visible Unity instance.
                project_hashes = [hash_value for hash_value in session_hashes if hash_value]

        if not project_hashes:
            return None

        enabled_tool_names: set[str] = set()
        resolved_any_project = False
        for project_hash in project_hashes:
            try:
                registered_tools = await PluginHub.get_tools_for_project(project_hash, user_id=user_id)
                # Only mark as resolved if tools are actually registered.
                # An empty list means register_tools hasn't been sent yet.
                if registered_tools:
                    resolved_any_project = True
            except Exception as exc:
                logger.debug(
                    "Failed to fetch tools for project hash %s (user_id=%s, %s)",
                    project_hash,
                    user_id,
                    type(exc).__name__,
                    exc_info=True,
                )
                continue

            for tool in registered_tools:
                tool_name = getattr(tool, "name", None)
                if isinstance(tool_name, str) and tool_name:
                    enabled_tool_names.add(tool_name)

        if not resolved_any_project:
            return None

        return enabled_tool_names

    def _refresh_tool_visibility_metadata_from_registry(self) -> None:
        from transport.unity_instance_middleware import get_registered_tools
        now = time.monotonic()
        if now - self._last_tool_visibility_refresh < self._tool_visibility_refresh_interval_seconds:
            return

        with self._metadata_lock:
            now = time.monotonic()
            if now - self._last_tool_visibility_refresh < self._tool_visibility_refresh_interval_seconds:
                return

            try:
                registry_tools = get_registered_tools()
            except Exception:
                logger.warning(
                    "Failed to refresh tool visibility metadata from registry; keeping previous metadata.",
                    exc_info=True,
                )
                self._last_tool_visibility_refresh = now
                return

            if not registry_tools and not self._has_logged_empty_registry_warning:
                logger.warning(
                    "Tool registry is empty during tool-list filtering; treating tools as unknown/visible."
                )
                self._has_logged_empty_registry_warning = True
            elif registry_tools:
                self._has_logged_empty_registry_warning = False

            unity_managed_tool_names: set[str] = set()
            tool_alias_to_unity_target: dict[str, str] = {}
            server_only_tool_names: set[str] = set()
            signature_entries: list[tuple[str, str]] = []

            for tool_info in registry_tools:
                tool_name = tool_info.get("name")
                if not isinstance(tool_name, str) or not tool_name:
                    continue

                unity_target = tool_info.get("unity_target", tool_name)
                if unity_target is None:
                    server_only_tool_names.add(tool_name)
                    signature_entries.append((tool_name, "<server-only>"))
                    continue

                if not isinstance(unity_target, str) or not unity_target:
                    logger.debug(
                        "Skipping tool visibility metadata with invalid unity_target: %s",
                        tool_info,
                    )
                    continue

                if unity_target == tool_name:
                    unity_managed_tool_names.add(tool_name)
                    signature_entries.append((tool_name, unity_target))
                    continue

                tool_alias_to_unity_target[tool_name] = unity_target
                unity_managed_tool_names.add(unity_target)
                signature_entries.append((tool_name, unity_target))

            signature = tuple(sorted(signature_entries, key=lambda item: item[0]))
            if signature == self._tool_visibility_signature:
                self._last_tool_visibility_refresh = now
                return

            self._unity_managed_tool_names = unity_managed_tool_names
            self._tool_alias_to_unity_target = tool_alias_to_unity_target
            self._server_only_tool_names = server_only_tool_names
            self._tool_visibility_signature = signature
            self._last_tool_visibility_refresh = now

    @staticmethod
    def _resolve_candidate_project_hashes(active_instance: str | None) -> list[str]:
        if not active_instance:
            return []

        if "@" in active_instance:
            _, _, suffix = active_instance.rpartition("@")
            return [suffix] if suffix else []

        return [active_instance]

    def _is_tool_visible(self, tool_name: str | None, enabled_tool_names: set[str]) -> bool:
        if not isinstance(tool_name, str) or not tool_name:
            return True

        if tool_name in self._server_only_tool_names:
            return True

        if tool_name in enabled_tool_names:
            return True

        unity_target = self._tool_alias_to_unity_target.get(tool_name)
        if unity_target:
            return unity_target in enabled_tool_names

        # Keep unknown tools visible for forward compatibility.
        if tool_name not in self._unity_managed_tool_names:
            return True

        return False
