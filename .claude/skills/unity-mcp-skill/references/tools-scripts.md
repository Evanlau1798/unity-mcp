# Unity-MCP Script / Search / Scripting Extension Tools

Read only the sections needed for the current task. Return to the [tool index](tools-reference.md) for other categories.

All examples are **templates**, not evidence of a live run. Use the installed tool schema and the target project/package versions as authority; native batch commands can differ from their Python wrappers.

## Script Tools

### create_script

Create a new C# script.

```python
create_script(
    path="Assets/Scripts/MyScript.cs",  # str, required
    contents='''using UnityEngine;

public class MyScript : MonoBehaviour
{
    void Start() { }
    void Update() { }
}''',
    script_type="MonoBehaviour",  # optional hint
    namespace="MyGame"            # optional namespace
)
```

### script_apply_edits

Apply structured edits to C# scripts. `className` defaults to `name` on method/class operations; supply it explicitly when the file contains a different target class. `options`, `script_type` (default `MonoBehaviour`) and `namespace` are also exposed.

```python
script_apply_edits(
    name="MyScript",             # script name (no .cs)
    path="Assets/Scripts",       # folder path
    edits=[
        # Replace entire method
        {
            "op": "replace_method",
            "methodName": "Update",
            "replacement": "void Update() { transform.Rotate(Vector3.up); }"
        },
        # Insert new method
        {
            "op": "insert_method",
            "position": "after",
            "afterMethodName": "Start",
            "replacement": "void OnEnable() { Debug.Log(\"Enabled\"); }"
        },
        # Delete method
        {
            "op": "delete_method",
            "methodName": "OldMethod"
        },
        # Anchor-based insert
        {
            "op": "anchor_insert",
            "anchor": r"(?m)^[ \t]*void Start\(\)",
            "position": "before",  # "before" | "after"
            "text": "// Called before Start\n"
        },
        # Regex replace
        {
            "op": "regex_replace",
            "pattern": "Debug\\.Log\\(",
            "replacement": "Debug.LogWarning("
        },
        # Prepend/append to file
        {"op": "prepend", "text": "// File header\n"},
        {"op": "append", "text": "\n// File footer"}
    ]
)
```

`insert_method` uses `replacement` plus `position="start"|"end"|"after"|"before"`. Relative placement uses the exact keys `afterMethodName` or `beforeMethodName`. Native fallback placement depends on the installed implementation: accepting `beforeMethodName` in the Python wrapper is not proof that the insertion point is correct; inspect the resulting method boundary before relying on it.

`anchor` is a regular expression. `anchor_insert` inserts the provided text at the match boundary; include the required newline/indentation explicitly so a `//` comment cannot consume the following declaration. Inspect `data.normalizedEdits` and `data.routing` where returned. Mixed text/structured edits can be applied in separate stages; read failures and partial results rather than assuming an all-or-nothing operation.

Other advertised operations include `replace_class`, `delete_class`, `anchor_delete`, `anchor_replace` and `replace_range`; read `manage_script_capabilities` for the installed list/guards. Capabilities advertise accepted operations, not package-specific proof of every native path.

### apply_text_edits

Apply precise character-position edits (1-indexed lines/columns).

```python
apply_text_edits(
    uri="mcpforunity://path/Assets/Scripts/MyScript.cs",
    edits=[
        {
            "startLine": 10,
            "startCol": 5,
            "endLine": 10,
            "endCol": 20,
            "newText": "replacement text"
        }
    ],
    precondition_sha256="abc123...",  # optional, prevents stale edits
    strict=True                        # reject explicit line/column values below 1
)
```

The public `strict` flag controls the Python wrapper's explicit-coordinate normalization: with it, zero-based explicit fields are rejected instead of clamped. It is not a Roslyn/semantic-validation switch. `options` is a separate dictionary; multi-span calls default `options.applyMode` to `atomic` when omitted. Supply a hash obtained from the same file/target when using `precondition_sha256`; the wrapper does not silently fetch a missing precondition.

### validate_script

Check a script at the requested `basic` or `standard` level (default `basic`; diagnostics default false). Roslyn-based checks require the Unity package to be compiled with `USE_ROSLYN`; semantic validation is conditional. A successful basic/standard check is not a substitute for required compilation or behavioral evidence.

```python
validate_script(
    uri="mcpforunity://path/Assets/Scripts/MyScript.cs",
    level="standard",            # "basic" | "standard"
    include_diagnostics=True     # include full error details
)
```

### get_sha

Get file hash without content (for preconditions).

```python
get_sha(uri="mcpforunity://path/Assets/Scripts/MyScript.cs")
# Decoded response: {"success": True, "data": {"sha256": "...", "lengthBytes": 1234}}
```

### delete_script

Delete a script file.

```python
delete_script(uri="mcpforunity://path/Assets/Scripts/OldScript.cs")
```

### manage_script

Legacy whole-file router with three actions: `create`, `read`, `delete`. `name` is the class name without `.cs`; `path` is its folder under `Assets/`. There is no update action: edit with `script_apply_edits` or `apply_text_edits`.

```python
manage_script(action="read", name="PlayerController", path="Assets/Scripts")  # returns the file contents
```

Read decoded text from `data.contents`. Script path URIs accepted by `get_sha`, `apply_text_edits`, `validate_script` and similar tools are **tool arguments**; they do not establish a generic file-read resource at `mcpforunity://path/...`. Use `manage_script(action="read", ...)` for C# content.

Script writes use the wrapper's mutation/reload recovery and may already refresh/import. Native text/structured edit replies expose fields such as `data.path`, `data.uri`, `data.editsApplied`, `data.sha256` and `data.scheduledRefresh`; a no-op can return `data.no_op=true` and zero edits. These are not a tracked refresh-completion receipt. After a disconnect, keep the original result and verify file content/hash and relevant compilation evidence; a changed hash alone does not prove the intended code or compilation succeeded. Do not assume every script response contains a tracked `refresh_unity` operation ID.

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/core/manage_script)

### manage_script_capabilities

Lists the edit ops `script_apply_edits` supports (structured and text), the max edit payload size, and the active guards. No parameters; always visible (no group).

```python
manage_script_capabilities()
```

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/core/manage_script_capabilities)

---

## Search Tools

### find_in_file

Search file contents with regex.

```python
find_in_file(
    uri="mcpforunity://path/Assets/Scripts/MyScript.cs",
    pattern="public void \\w+",  # regex pattern
    max_results=200,
    ignore_case=True
)
# Returns: line numbers, content excerpts, match positions
```

---

## Scripting Extension Tools

Group: `scripting_ext`.

### execute_code

Runs a C# method body inside the editor, compiled in memory (no script file). `System`, `System.Collections.Generic`, `System.Linq`, `System.Reflection`, `UnityEngine` and `UnityEditor` are imported; `return` a value to get it back. Actions: `execute`, `get_history`, `replay` (`index`), `clear_history`. `safety_checks` (default on) blocks known dangerous calls but is not a sandbox. `compiler`: `auto` (Roslyn if installed, else CodeDom with C# 6), `roslyn`, `codedom`.

```python
execute_code(action="execute", code="return Selection.gameObjects.Select(g => g.name).ToArray();")
```

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/scripting_ext/execute_code)

### manage_scriptable_object

Creates ScriptableObject assets and edits their serialized fields by property path. `create` takes `type_name` (namespace-qualified), `folder_path`, `asset_name` and optional `overwrite`; `modify` takes `target` (`{"path": ...}` or `{"guid": ...}`) and optional `dry_run`. Both apply `patches`: `{"propertyPath": ..., "op": "set" (default) or "array_resize", "value": ...}`; an object reference uses `"ref": {"guid": ...}` in place of `"value"`.

```python
manage_scriptable_object(action="modify", target={"path": "Assets/Data/Goblin.asset"},
                         patches=[{"propertyPath": "maxHealth", "value": 50},
                                  {"propertyPath": "drops", "op": "array_resize", "value": 2}])
```

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/scripting_ext/manage_scriptable_object)
