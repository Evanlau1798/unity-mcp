import base64
import os
from typing import Annotated, Any, Literal
from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.refresh_unity import send_mutation, verify_edit_by_sha
from transport.unity_transport import send_with_unity_instance
import transport.legacy.unity_connection
from services.tools.script_text_edits import apply_text_edits, _split_uri


@mcp_for_unity_tool(
    unity_target="manage_script",
    description="Create a new C# script at the given project path.",
    annotations=ToolAnnotations(
        title="Create Script",
        destructiveHint=True,
    ),
)
async def create_script(
    ctx: Context,
    path: Annotated[str, "Path under Assets/ to create the script at, e.g., 'Assets/Scripts/My.cs'"],
    contents: Annotated[str, "Contents of the script to create (plain text C# code). The server handles Base64 encoding."],
    script_type: Annotated[str, "Script type (e.g., 'C#')"] | None = None,
    namespace: Annotated[str, "Namespace for the script"] | None = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)
    await ctx.info(
        f"Processing create_script: {path} (unity_instance={unity_instance or 'default'})")
    name = os.path.splitext(os.path.basename(path))[0]
    directory = os.path.dirname(path)
    # Local validation to avoid round-trips on obviously bad input
    norm_path = os.path.normpath(
        (path or "").replace("\\", "/")).replace("\\", "/")
    if not directory or directory.split("/")[0].lower() != "assets":
        return {"success": False, "code": "path_outside_assets", "message": f"path must be under 'Assets/'; got '{path}'."}
    if ".." in norm_path.split("/") or norm_path.startswith("/"):
        return {"success": False, "code": "bad_path", "message": "path must not contain traversal or be absolute."}
    if not name:
        return {"success": False, "code": "bad_path", "message": "path must include a script file name."}
    if not norm_path.lower().endswith(".cs"):
        return {"success": False, "code": "bad_extension", "message": "script file must end with .cs."}
    params: dict[str, Any] = {
        "action": "create",
        "name": name,
        "path": directory,
        "namespace": namespace,
        "scriptType": script_type,
    }
    if contents:
        params["encodedContents"] = base64.b64encode(
            contents.encode("utf-8")).decode("utf-8")
        params["contentsEncoded"] = True
    params = {k: v for k, v in params.items() if v is not None}

    async def _verify_create():
        verify = await send_with_unity_instance(
            transport.legacy.unity_connection.async_send_command_with_retry,
            unity_instance, "manage_script",
            {"action": "read", "name": name, "path": directory},
        )
        if isinstance(verify, dict) and verify.get("success"):
            return {"success": True, "message": "Script created (verified after domain reload).", "data": verify.get("data")}
        return None

    resp = await send_mutation(ctx, unity_instance, "manage_script", params, verify_after_disconnect=_verify_create)
    return resp if isinstance(resp, dict) else {"success": False, "message": str(resp)}


@mcp_for_unity_tool(
    unity_target="manage_script",
    description="Delete a C# script by URI or Assets-relative path.",
    annotations=ToolAnnotations(
        title="Delete Script",
        destructiveHint=True,
    ),
)
async def delete_script(
    ctx: Context,
    uri: Annotated[str, "URI of the script to delete under Assets/ directory, mcpforunity://path/Assets/... or file://... or Assets/..."],
) -> dict[str, Any]:
    """Delete a C# script by URI."""
    unity_instance = await get_unity_instance_from_context(ctx)
    await ctx.info(
        f"Processing delete_script: {uri} (unity_instance={unity_instance or 'default'})")
    name, directory = _split_uri(uri)
    if not directory or directory.split("/")[0].lower() != "assets":
        return {"success": False, "code": "path_outside_assets", "message": "URI must resolve under 'Assets/'."}
    params = {"action": "delete", "name": name, "path": directory}

    async def _verify_delete():
        verify = await send_with_unity_instance(
            transport.legacy.unity_connection.async_send_command_with_retry,
            unity_instance, "manage_script",
            {"action": "read", "name": name, "path": directory},
        )
        if isinstance(verify, dict) and not verify.get("success"):
            return {"success": True, "message": "Script deleted (verified after domain reload)."}
        return None

    resp = await send_mutation(ctx, unity_instance, "manage_script", params, verify_after_disconnect=_verify_delete)
    return resp if isinstance(resp, dict) else {"success": False, "message": str(resp)}


@mcp_for_unity_tool(
    unity_target="manage_script",
    description="Validate a C# script and return diagnostics (basic or standard). Roslyn-based checks "
                "require a Unity package compiled with USE_ROSLYN; semantic validation is conditional.",
    annotations=ToolAnnotations(
        title="Validate Script",
        readOnlyHint=True,
        destructiveHint=False,
        idempotentHint=True,
        openWorldHint=False,
    ),
)
async def validate_script(
    ctx: Context,
    uri: Annotated[str, "URI of the script to validate under Assets/ directory, mcpforunity://path/Assets/... or file://... or Assets/..."],
    level: Annotated[Literal['basic', 'standard'],
                     "Validation level"] = "basic",
    include_diagnostics: Annotated[bool,
                                   "Include full diagnostics and summary"] = False,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)
    await ctx.info(
        f"Processing validate_script: {uri} (unity_instance={unity_instance or 'default'})")
    name, directory = _split_uri(uri)
    if not directory or directory.split("/")[0].lower() != "assets":
        return {"success": False, "code": "path_outside_assets", "message": "URI must resolve under 'Assets/'."}
    if level not in ("basic", "standard"):
        return {"success": False, "code": "bad_level", "message": "level must be 'basic' or 'standard'."}
    params = {
        "action": "validate",
        "name": name,
        "path": directory,
        "level": level,
    }
    resp = await send_with_unity_instance(
        transport.legacy.unity_connection.async_send_command_with_retry,
        unity_instance,
        "manage_script",
        params,
    )
    if isinstance(resp, dict) and resp.get("success"):
        diags = resp.get("data", {}).get("diagnostics", []) or []
        warnings = sum(1 for d in diags if str(
            d.get("severity", "")).lower() == "warning")
        errors = sum(1 for d in diags if str(
            d.get("severity", "")).lower() in ("error", "fatal"))
        if include_diagnostics:
            return {"success": True, "data": {"diagnostics": diags, "summary": {"warnings": warnings, "errors": errors}}}
        return {"success": True, "data": {"warnings": warnings, "errors": errors}}
    return resp if isinstance(resp, dict) else {"success": False, "message": str(resp)}


@mcp_for_unity_tool(
    description="Compatibility router for legacy script operations. Prefer apply_text_edits (ranges) or script_apply_edits (structured) for edits. Read-only action: read. Modifying actions: create, delete.",
    annotations=ToolAnnotations(
        title="Manage Script",
        destructiveHint=True,
    ),
)
async def manage_script(
    ctx: Context,
    action: Annotated[Literal['create', 'read', 'delete'], "Perform CRUD operations on C# scripts."],
    name: Annotated[str, "Script name (no .cs extension)", "Name of the script to create"],
    path: Annotated[str, "Asset path (default: 'Assets/')", "Path under Assets/ to create the script at, e.g., 'Assets/Scripts/My.cs'"],
    contents: Annotated[str, "Contents of the script to create",
                        "C# code for 'create' action"] | None = None,
    script_type: Annotated[str, "Script type (e.g., 'C#')",
                           "Type hint (e.g., 'MonoBehaviour')"] | None = None,
    namespace: Annotated[str, "Namespace for the script"] | None = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)
    await ctx.info(
        f"Processing manage_script: {action} (unity_instance={unity_instance or 'default'})")
    try:
        # Prepare parameters for Unity
        params = {
            "action": action,
            "name": name,
            "path": path,
            "namespace": namespace,
            "scriptType": script_type,
        }

        # Base64 encode the contents if they exist to avoid JSON escaping issues
        if contents:
            if action == 'create':
                params["encodedContents"] = base64.b64encode(
                    contents.encode('utf-8')).decode('utf-8')
                params["contentsEncoded"] = True
            else:
                params["contents"] = contents

        params = {k: v for k, v in params.items() if v is not None}

        if action == "read":
            response = await send_with_unity_instance(
                transport.legacy.unity_connection.async_send_command_with_retry,
                unity_instance,
                "manage_script",
                params,
                retry_on_reload=True,
            )
        else:
            async def _verify_mutation():
                verify = await send_with_unity_instance(
                    transport.legacy.unity_connection.async_send_command_with_retry,
                    unity_instance, "manage_script",
                    {"action": "read", "name": name, "path": path},
                )
                if action == "create" and isinstance(verify, dict) and verify.get("success"):
                    return {"success": True, "message": "Script created (verified after domain reload).", "data": verify.get("data")}
                elif action == "delete" and isinstance(verify, dict) and not verify.get("success"):
                    return {"success": True, "message": "Script deleted (verified after domain reload)."}
                return None

            response = await send_mutation(ctx, unity_instance, "manage_script", params, verify_after_disconnect=_verify_mutation)

        if isinstance(response, dict):
            if response.get("success"):
                if response.get("data", {}).get("contentsEncoded"):
                    decoded_contents = base64.b64decode(
                        response["data"]["encodedContents"]).decode('utf-8')
                    response["data"]["contents"] = decoded_contents
                    del response["data"]["encodedContents"]
                    del response["data"]["contentsEncoded"]

                return {
                    "success": True,
                    "message": response.get("message", "Operation successful."),
                    "data": response.get("data"),
                }
            return response

        return {"success": False, "message": str(response)}

    except Exception as e:
        return {
            "success": False,
            "message": f"Python error managing script: {str(e)}",
        }


@mcp_for_unity_tool(
    unity_target=None,
    group=None,
    description=(
        """Get manage_script capabilities (supported ops, limits, and guards).
    Returns:
        - ops: list of supported structured ops
        - text_ops: list of supported text ops
        - max_edit_payload_bytes: server edit payload cap
        - guards: header/using guard enabled flag"""
    ),
    annotations=ToolAnnotations(
        title="Manage Script Capabilities",
        readOnlyHint=True,
        destructiveHint=False,
        idempotentHint=True,
        openWorldHint=False,
    ),
)
async def manage_script_capabilities(ctx: Context) -> dict[str, Any]:
    await ctx.info("Processing manage_script_capabilities")
    try:
        # Keep in sync with server/Editor ManageScript implementation
        ops = [
            "replace_class", "delete_class", "replace_method", "delete_method",
            "insert_method", "anchor_insert", "anchor_delete", "anchor_replace"
        ]
        text_ops = ["replace_range", "regex_replace", "prepend", "append"]
        # Match ManageScript.MaxEditPayloadBytes if exposed; hardcode a sensible default fallback
        max_edit_payload_bytes = 256 * 1024
        guards = {"using_guard": True}
        extras = {"get_sha": True}
        return {"success": True, "data": {
            "ops": ops,
            "text_ops": text_ops,
            "max_edit_payload_bytes": max_edit_payload_bytes,
            "guards": guards,
            "extras": extras,
        }}
    except Exception as e:
        return {"success": False, "error": f"capabilities error: {e}"}


@mcp_for_unity_tool(
    unity_target="manage_script",
    description="Get SHA256 and basic metadata for a Unity C# script without returning file contents. Requires uri (script path under Assets/ or mcpforunity://path/Assets/... or file://...).",
    annotations=ToolAnnotations(
        title="Get SHA",
        readOnlyHint=True,
        destructiveHint=False,
        idempotentHint=True,
        openWorldHint=False,
    ),
)
async def get_sha(
    ctx: Context,
    uri: Annotated[str, "URI of the script to edit under Assets/ directory, mcpforunity://path/Assets/... or file://... or Assets/..."],
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)
    await ctx.info(
        f"Processing get_sha: {uri} (unity_instance={unity_instance or 'default'})")
    try:
        name, directory = _split_uri(uri)
        params = {"action": "get_sha", "name": name, "path": directory}
        resp = await send_with_unity_instance(
            transport.legacy.unity_connection.async_send_command_with_retry,
            unity_instance,
            "manage_script",
            params,
        )
        if isinstance(resp, dict) and resp.get("success"):
            data = resp.get("data", {})
            minimal = {"sha256": data.get(
                "sha256"), "lengthBytes": data.get("lengthBytes")}
            return {"success": True, "data": minimal}
        return resp if isinstance(resp, dict) else {"success": False, "message": str(resp)}
    except Exception as e:
        return {"success": False, "message": f"get_sha error: {e}"}
