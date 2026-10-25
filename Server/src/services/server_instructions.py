"""Agent-facing server instructions."""

def _build_instructions(project_scoped_tools: bool) -> str:
    if project_scoped_tools:
        custom_tools_note = (
            "I have a dynamic tool system. Always check the mcpforunity://custom-tools resource first "
            "to see what special capabilities are available for the current project."
        )
    else:
        custom_tools_note = (
            "Custom tools are registered as standard tools when Unity connects. "
            "No project-scoped custom tools resource is available."
        )

    return f"""
This server provides tools to interact with the Unity Game Engine Editor.

{custom_tools_note}

Targeting Unity instances:
- Use the resource mcpforunity://instances to list active Unity sessions (Name@hash).
- For HTTP agents that must keep the same Editor across MCP session replacement, connect to /mcp/instance/{{Name@hash}}. Tools and resources use that exact target; conflicting unity_instance or set_active_instance is refused.
- When multiple instances are connected, call set_active_instance with the exact Name@hash before using tools/resources to pin routing for the whole session. The server will error if multiple are connected and no active instance is set.
- Alternatively, pass unity_instance as a parameter on any individual tool call to route just that call (e.g. unity_instance="MyGame@abc123", unity_instance="abc" for a hash prefix, or unity_instance="6401" for a port number in stdio mode). This does not change the session default.

Important Workflows:

Resources vs Tools:
- Use RESOURCES to read editor state (mcpforunity://editor/state, mcpforunity://project/info, mcpforunity://project/tags, mcpforunity://tests, etc)
- Use TOOLS to perform actions and mutations (manage_editor for play mode control, tag/layer management, etc)
- Always check related resources before modifying the engine state with tools

Reading resources (read this before using ANY resource named below):
- Resources are addressed by URI, never by name. A resource's name and URI are NOT interchangeable: names use underscores (e.g. editor_state) while URIs use slashes (e.g. mcpforunity://editor/state). Do NOT build a URI by swapping separators in the name — you will 404.
- These instructions always spell resources as full mcpforunity:// URIs — read one exactly as written. If you only have a name (from resources/list or another tool's output), look its URI up in resources/list rather than guessing it.
- Resource payloads are wrapped: the content lives under a top-level `data` object, so field paths are `data.<section>.<field>` (e.g. `data.advice.ready_for_tools`), not bare top-level fields.
- The mcpforunity:// URI names the resource, not the server. Some clients take a separate server key on a resource read — in Codex, tools are exposed as mcp__unityMCP__* but resources/read wants server: "unityMCP". If a read fails with an unknown-server error, list resources first and use the key exactly as returned.

Script Management:
- After creating or modifying scripts (by your own tools or the `manage_script` tool) use `read_console` to check for compilation errors before proceeding
- Only after successful compilation can new components/types be used
- You can poll mcpforunity://editor/state and read `data.compilation.is_compiling` to check if the domain reload is complete, or `data.advice.ready_for_tools` for overall readiness

Scene Setup:
- Always include a Camera and main Light (Directional Light) in new scenes
- Create prefabs with `manage_asset` for reusable GameObjects
- Use `manage_scene` to load, save, and query scene information

Path Conventions:
- Unless specified otherwise, all paths are relative to the project's `Assets/` folder
- Use forward slashes (/) in paths for cross-platform compatibility

Console Monitoring:
- Check `read_console` regularly to catch errors, warnings, and compilation status
- Filter by log type (Error, Warning, Log) to focus on specific issues

Menu Items:
- Use `execute_menu_item` when you have read the mcpforunity://menu-items resource
- This lets you interact with Unity's menu system and third-party tools

Unity API Verification (requires 'docs' tool group):
- When the 'docs' tool group is active, use `unity_reflect` and `unity_docs` to verify Unity API details before answering questions or writing C# code. LLM training data frequently contains incorrect, outdated, or hallucinated Unity APIs.
- BEFORE answering Unity API questions: search the project's assets (`manage_asset`) and reflect the API (`unity_reflect`) to verify. Do NOT rely on training data alone.
- Common hallucination areas: shaders and materials (always search assets for actual shader names), package-specific APIs (Input System, Cinemachine, ProBuilder, NavMesh, URP/HDRP), and APIs that changed between Unity versions.
- Workflow: `unity_reflect search` → `unity_reflect get_type` → `unity_reflect get_member` → `unity_docs get_doc` (if you need examples/caveats).
- For shader/material questions: use `manage_asset(action="search", filter_type="Shader")` to find actual shaders in the project before recommending one.

Payload sizing & paging (important):
- Many Unity queries can return very large JSON. Prefer **paged + summary-first** calls.
- `manage_scene(action="get_hierarchy")`:
  - Use `page_size` + `cursor` and follow `next_cursor` until null.
  - `page_size` is **items per page**; recommended starting point: **50**.
- `manage_gameobject(action="get_components")`:
  - Start with `include_properties=false` (metadata-only) and small `page_size` (e.g. **10-25**).
  - Only request `include_properties=true` when needed; keep `page_size` small (e.g. **3-10**) to bound payloads.
- `manage_asset(action="search")`:
  - Use paging (`page_size`, `page_number`) and keep `page_size` modest (e.g. **25-50**) to avoid token-heavy responses.
  - Keep `generate_preview=false` unless you explicitly need thumbnails (previews may include large base64 payloads).
"""
