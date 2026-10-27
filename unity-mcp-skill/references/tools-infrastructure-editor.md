# Unity-MCP Infrastructure / Editor Control / Build / Custom Tools

Read only the sections needed for the current task. Return to the [tool index](tools-reference.md) for other categories.

All examples are **templates**, not evidence of a live run. Use the installed tool schema and the target project/package versions as authority; native batch commands can differ from their Python wrappers.

## Infrastructure Tools

### batch_execute

Execute compatible **native Unity command handlers** in one request, reducing RPC round trips. This is not an executor for arbitrary Python MCP wrappers: per-child wrapper encoding, preflight and recovery do not run.

```python
batch_execute(
    commands=[                    # non-empty list of native commands
        {"tool": "manage_gameobject", "params": {
            "action": "create", "name": "CubeA", "primitive_type": "Cube"
        }},
        {"tool": "manage_gameobject", "params": {
            "action": "create", "name": "CubeB", "primitive_type": "Cube"
        }}
    ],
    parallel=False,              # compatibility hint; parallelApplied is false
    fail_fast=False,             # default false; true stops after the first failure
    max_parallelism=None         # compatibility hint; no parallel workers
)
```

Commands run **sequentially on Unity's main thread**. The configured limit is read each call: default/fallback 25, configurable from 1 to 100, hard maximum 100. `data.settings.batch_execute_max_commands` in Editor state exposes the current setting when available.

Only top-level parameter keys are normalized; nested component/property keys are preserved. Route the entire batch with the outer `unity_instance` argument where supported; per-child `params.unity_instance` is rejected. A Python alias such as `create_script` is not automatically a native command with equivalent payload semantics.

Batches are **not transactional** and have no rollback. Inspect top-level success plus `data.results[]`, each child's `callSucceeded` and nested `result`/`error`, and the success/failure counts. With `fail_fast=True`, later commands may not have run; with the default false, later commands continue. Inspect partial changes before retrying. RPC batching neither guarantees a speed multiplier nor implies AssetDatabase import batching.

### set_active_instance

Set the legacy session selection for a specific Unity instance. Current targeting also supports per-call `unity_instance` and an instance-bound endpoint; session selection is not the only route.

```python
set_active_instance(
    instance="ProjectName@abc123"  # str, required - Name@hash or hash prefix
)
```

Resolution considers an explicit per-call target, then an exact `Name@hash` bound endpoint, then legacy session selection/automatic selection. An explicit target conflicting with a bound endpoint is rejected rather than switching projects. Resource reads have no tool arguments, so use the bound endpoint or established session target. See [instance resources](resources-project-tests.md#instance-resources).

### refresh_unity

Request a tracked asset refresh and optionally compilation. Preserve the returned operation identity; a timeout or reconnect is an observation problem until the matching receipt establishes the outcome.

```python
refresh_unity(
    mode="if_dirty",             # "if_dirty" | "force"
    scope="all",                 # "assets" | "scripts" | "all"
    compile="none",              # "none" | "request"
    wait_for_ready=True,         # wait for this operation; timeout preserves its identity
    operation_id=None            # supply an existing ID to query without starting another
)
```

Completion requires the matching native operation receipt, not merely `is_compiling=False` or general readiness. Query an existing request with `refresh_unity(operation_id=existing_id)`. For script tools that already import/refresh, observe their own result and readback instead of adding a redundant refresh. See [Editor state and receipt fields](resources-editor.md#mcpforunityeditorstate).

### manage_tools

Show, enable, or hide tool groups for the current session. Always visible (no group).

```python
manage_tools(action="list_groups")                  # every group, whether it is on, and its tools
manage_tools(action="activate", group="animation")  # "deactivate" hides a group again
manage_tools(action="sync")                         # re-read the tool toggles in the Unity editor
manage_tools(action="reset")                        # back to the defaults
```

Groups: `core`, `animation`, `asset_gen`, `docs`, `probuilder`, `profiling`, `scripting_ext`, `testing`, `ui`, `vfx`. The `mcpforunity://tool-groups` resource lists the same catalog read-only. [Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/core/manage_tools)

### debug_request_context

Diagnostics: returns the request's `client_id`, `session_id` and meta, the session's active Unity instance, and the server version. No parameters; always visible (no group).

```python
debug_request_context()
```

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/core/debug_request_context)

---

## Editor Control Tools

### manage_editor

Control Unity Editor state, undo/redo. Prefab stages are `manage_prefabs` actions.

```python
manage_editor(action="play")               # Enter play mode
manage_editor(action="pause")              # Pause play mode
manage_editor(action="stop")               # Exit play mode

manage_editor(action="set_active_tool", tool_name="Move")  # Move/Rotate/Scale/etc.

manage_editor(action="add_tag", tag_name="Enemy")
manage_editor(action="remove_tag", tag_name="OldTag")

manage_editor(action="add_layer", layer_name="Projectiles")
manage_editor(action="remove_layer", layer_name="OldLayer")

manage_editor(action="undo")               # Returns the name of the undone step
manage_editor(action="redo")

# Package deployment (no confirmation dialog — designed for LLM-driven iteration)
manage_editor(action="deploy_package")     # Copy configured MCPForUnity source into installed package
manage_editor(action="restore_package")    # Revert to pre-deployment backup
```

**Deploy workflow:** Set the source path in MCP for Unity Advanced Settings first. `deploy_package` copies the source into the project's package location, creates a backup, and triggers `AssetDatabase.Refresh`. Observe that reload/compilation and relevant console evidence before continuing; do not request another refresh solely to wait for the first one.

### execute_menu_item

Execute any Unity menu item.

```python
execute_menu_item(menu_path="File/Save Project")
execute_menu_item(menu_path="GameObject/3D Object/Cube")
execute_menu_item(menu_path="Window/General/Console")
```

### read_console

Read or clear Unity console messages.

```python
# Get recent messages
read_console(
    action="get",
    types=["error", "warning", "log"],  # or ["all"]
    count=10,                    # max messages (ignored with paging)
    filter_text="NullReference", # optional text filter
    page_size=50,
    cursor=0,
    format="detailed",           # "plain"|"detailed"|"json"
    include_stacktrace=True
)

# Clear console
read_console(action="clear")
```

---

## Build Tools

### manage_build

Player builds, platform switching, player settings, build-settings scenes, build profiles, and batch builds. Actions: `build`, `status`, `platform`, `settings`, `scenes`, `profiles`, `batch`, `cancel`. `build` and `batch` return a `job_id`; poll it with `status`, stop it with `cancel`. `platform`, `settings` (with `property`) and `scenes` read when the value is omitted and write when it is given. `profiles` and `profile` need Unity 6+.

```python
manage_build(action="scenes", scenes='["Assets/Scenes/Menu.unity", "Assets/Scenes/Level1.unity"]')  # replaces the list
manage_build(action="build", target="windows64", output_path="Builds/Win/Game.exe", development="true")
manage_build(action="status", job_id="<job_id>")
```

Targets: `windows64`, `osx`, `linux64`, `android`, `ios`, `webgl`, `uwp`, `tvos`, `visionos`. Switching `platform` reimports assets. [Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/core/manage_build)

---

## Custom Tools

### execute_custom_tool

Execute project-specific custom tools.

```python
execute_custom_tool(
    tool_name="my_custom_tool",
    parameters={"param1": "value", "param2": 42}
)
```

Discover available custom tools via `mcpforunity://custom-tools` resource.
