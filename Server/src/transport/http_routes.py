"""Local CLI and health HTTP routes."""
import logging
import time
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.exceptions import HTTPException
from core.config import config
from services.custom_tool_service import CustomToolService, resolve_project_id_for_unity_instance
from transport.plugin_hub import PluginHub
from transport.plugin_errors import CommandDeadlineError
logger = logging.getLogger("mcp-for-unity-server")

def _normalize_instance_token(instance_token: str | None) -> tuple[str | None, str | None]:
    if not instance_token:
        return None, None
    if "@" in instance_token:
        name_part, _, hash_part = instance_token.partition("@")
        return (name_part or None), (hash_part or None)
    return None, instance_token



def _select_cli_session(sessions, token):
    if token is not None and not isinstance(token, str):
        raise HTTPException(400, "unity_instance must be a string")
    if not token:
        matches = list(sessions.items())
    elif "@" in token:
        matches = [(sid, d) for sid, d in sessions.items() if token == f"{d.project}@{d.hash}"]
    else:
        # Keep existing CLI shorthand only when it identifies one Editor.
        matches = [(sid, d) for sid, d in sessions.items() if token in (d.hash, d.project)]
    if not matches:
        raise HTTPException(404, f"Unity instance '{token}' not found")
    if len(matches) != 1:
        raise HTTPException(409, "instance_selection_required: use exact Name@hash from /api/instances")
    return matches[0]


def register_http_routes(mcp, version_getter):
    @mcp.custom_route("/health", methods=["GET"])
    async def health_http(_: Request) -> JSONResponse:
        return JSONResponse({
            "status": "healthy",
            "timestamp": time.time(),
            "version": version_getter() or "unknown",
            "message": "MCP for Unity server is running"
        })

    @mcp.custom_route("/api/auth/login-url", methods=["GET"])
    async def auth_login_url(_: Request) -> JSONResponse:
        """Return the login URL for users to obtain/manage API keys."""
        if not config.api_key_login_url:
            return JSONResponse(
                {
                    "success": False,
                    "error": "API key management not configured. Contact your server administrator.",
                },
                status_code=404,
            )
        return JSONResponse({
            "success": True,
            "login_url": config.api_key_login_url,
        })

    # Only expose CLI routes if running locally (not in remote hosted mode)
    if not config.http_remote_hosted:
        @mcp.custom_route("/api/command", methods=["POST"])
        async def cli_command_route(request: Request) -> JSONResponse:
            """REST endpoint for CLI commands to Unity."""
            try:
                body = await request.json()

                command_type = body.get("type")
                params = body.get("params", {})
                unity_instance = body.get("unity_instance")

                if not command_type:
                    return JSONResponse({"success": False, "error": "Missing 'type' field"}, status_code=400)

                # Get available sessions
                sessions = await PluginHub.get_sessions()
                if not sessions.sessions:
                    return JSONResponse({
                        "success": False,
                        "error": "No Unity instances connected. Make sure Unity is running with MCP plugin."
                    }, status_code=503)

                session_id, session_details = _select_cli_session(sessions.sessions, unity_instance)

                # Custom tool execution - must be checked BEFORE the final PluginHub.send_command call
                # This applies to both cases: with or without explicit unity_instance
                if command_type == "execute_custom_tool":
                    # session_id and session_details are already set above
                    if not session_id or not session_details:
                        return JSONResponse(
                            {"success": False,
                                "error": "No valid Unity session available for custom tool execution"},
                            status_code=503,
                        )
                    tool_name = None
                    tool_params = {}
                    if isinstance(params, dict):
                        tool_name = params.get(
                            "tool_name") or params.get("name")
                        tool_params = params.get(
                            "parameters") or params.get("params") or {}

                    if not tool_name:
                        return JSONResponse(
                            {"success": False,
                                "error": "Missing 'tool_name' for execute_custom_tool"},
                            status_code=400,
                        )
                    if tool_params is None:
                        tool_params = {}
                    if not isinstance(tool_params, dict):
                        return JSONResponse(
                            {"success": False,
                                "error": "Tool parameters must be an object/dict"},
                            status_code=400,
                        )

                    # Prefer a concrete hash for project-scoped tools.
                    unity_instance_hint = unity_instance
                    if session_details and session_details.hash:
                        unity_instance_hint = session_details.hash

                    project_id = resolve_project_id_for_unity_instance(
                        unity_instance_hint)
                    if not project_id:
                        return JSONResponse(
                            {"success": False,
                                "error": "Could not resolve project id for custom tool"},
                            status_code=400,
                        )

                    service = CustomToolService.get_instance()
                    result = await service.execute_tool(
                        project_id, tool_name, unity_instance_hint, tool_params
                    )
                    return JSONResponse(result.model_dump())

                # Send command to Unity
                result = await PluginHub.send_command(session_id, command_type, params)
                return JSONResponse(result)

            except HTTPException as exc:
                return JSONResponse({"success": False, "error": exc.detail}, status_code=exc.status_code)
            except CommandDeadlineError as exc:
                return JSONResponse({"success": False, "error": str(exc),
                                     "hint": "check_state_before_retry", "data": exc.diagnostics}, status_code=504)
            except Exception as e:
                logger.exception("CLI command error: %s", e)
                return JSONResponse({"success": False, "error": str(e)}, status_code=500)

        @mcp.custom_route("/api/instances", methods=["GET"])
        async def cli_instances_route(_: Request) -> JSONResponse:
            """REST endpoint to list connected Unity instances."""
            try:
                sessions = await PluginHub.get_sessions()
                instances = []
                for session_id, details in sessions.sessions.items():
                    instances.append({
                        "session_id": session_id,
                        "project": details.project,
                        "hash": details.hash,
                        "unity_version": details.unity_version,
                        "connected_at": details.connected_at,
                    })
                return JSONResponse({"success": True, "instances": instances})
            except HTTPException as exc:
                return JSONResponse({"success": False, "error": exc.detail}, status_code=exc.status_code)
            except Exception as e:
                return JSONResponse({"success": False, "error": str(e)}, status_code=500)

        @mcp.custom_route("/api/custom-tools", methods=["GET"])
        async def cli_custom_tools_route(request: Request) -> JSONResponse:
            """REST endpoint to list custom tools for the active Unity project."""
            try:
                unity_instance = request.query_params.get("instance")
                sessions = await PluginHub.get_sessions()
                if not sessions.sessions:
                    return JSONResponse({
                        "success": False,
                        "error": "No Unity instances connected. Make sure Unity is running with MCP plugin."
                    }, status_code=503)

                _, session_details = _select_cli_session(sessions.sessions, unity_instance)

                unity_instance_hint = unity_instance
                if session_details and session_details.hash:
                    unity_instance_hint = session_details.hash

                project_id = resolve_project_id_for_unity_instance(
                    unity_instance_hint)
                if not project_id:
                    return JSONResponse(
                        {"success": False,
                            "error": "Could not resolve project id for custom tools"},
                        status_code=400,
                    )

                service = CustomToolService.get_instance()
                tools = await service.list_registered_tools(project_id)
                tools_payload = [
                    tool.model_dump() if hasattr(tool, "model_dump") else tool for tool in tools
                ]

                return JSONResponse({
                    "success": True,
                    "project_id": project_id,
                    "tool_count": len(tools_payload),
                    "tools": tools_payload,
                })
            except HTTPException as exc:
                return JSONResponse({"success": False, "error": exc.detail}, status_code=exc.status_code)
            except Exception as e:
                logger.exception("CLI custom tools error: %s", e)
                return JSONResponse({"success": False, "error": str(e)}, status_code=500)
