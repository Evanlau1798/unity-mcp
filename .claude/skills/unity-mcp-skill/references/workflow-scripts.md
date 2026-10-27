# Script development workflows

[Workflow index](workflows.md). Use the matching procedure for C# creation or editing.

> **Template:** Adapt names, paths, input APIs, and class bodies to the inspected project. Verify the template against the installed schema and project before use. Native fallback behavior depends on the installed implementation.

Before writing, use [setup and readiness](workflow-setup.md), inspect the current source and component references, and confirm API availability when needed through [API verification](workflow-packages-api.md#api-verification-workflows). Public script paths must resolve under `Assets/`; the examples use Assets-relative paths or `mcpforunity://path/Assets/...`.

## After a script mutation

1. Inspect the actual response envelope, including its inner `success`, `error`, and `data`. Save any identity the tool really returns; a script edit does not universally return a tracked refresh `operation_id`.
2. Read back the intended source change. A changed SHA or a reconnect verifies only the corresponding evidence; it does not by itself prove the intended code compiled or loaded.
3. If the tool already initiated import/compilation, observe that work instead of calling `refresh_unity` again. Inspect v2 `data.compilation`, `data.assets.refresh`, `data.tests`, `data.advice`, and `data.staleness`; a lone `is_compiling=False` is insufficient. Use [the readiness procedure](workflow-setup.md#before-any-operation).
4. Inspect current Console errors after the relevant import/compile has completed. Only then attach/use the changed component and verify its serialized fields or affected behavior.
5. After a timeout or disconnect, inspect the same file, Editor, and any returned operation/job receipt before deciding whether a new mutation is needed. For an actual tracked refresh, use its original `operation_id`; see [recovery](workflow-batch-recovery.md#compilation-block-recovery).

The native structured/text-edit receipt can include `data.path`, `data.uri`, `data.sha256`, `data.editsApplied`, and `data.scheduledRefresh`. `scheduledRefresh=False` can mean import was requested immediately, not that import was omitted. An explicit `data.no_op=True` reports unchanged contents; do not wait for a new compile that this no-op did not request. These fields are different from a tracked refresh operation receipt.

## Script Overwrites with `replace_class`

When a generated script needs to be rewritten, retain the existing asset and GUID. Deleting and recreating the file can break components referencing it. `create_script` refuses to overwrite an existing file; public `manage_script` supports `create`, `read`, and `delete`, with no `update` action.

```python
current = manage_script(action="read", name="MyScript", path="Assets/Scripts")
# Inspect current["data"]["contents"] and construct a complete replacement class.
# Preserve required members/serialized fields; reviewed_complete_class is that text.
script_apply_edits(
    name="MyScript", path="Assets/Scripts",
    edits=[{
        "op": "replace_class", "className": "MyScript",
        "replacement": reviewed_complete_class
    }]
)
```

Finish the script-mutation procedure above, then inspect existing components for retained script references and field values. Keeping the asset GUID does not make arbitrary changes to serialized members harmless.

## Script Development Workflows

### Create New Script and Attach

Prerequisites: the file does not already exist, and the intended `Enemy` GameObject has been resolved. This example assumes `UnityEngine` APIs and a compatible Transform target.

```python
creation = create_script(
    path="Assets/Scripts/EnemyAI.cs",
    contents='''using UnityEngine;

public class EnemyAI : MonoBehaviour
{
    public float speed = 5f;
    public Transform target;

    void Update()
    {
        if (target != null)
        {
            Vector3 direction = (target.position - transform.position).normalized;
            transform.position += direction * speed * Time.deltaTime;
        }
    }
}'''
)
```

Check `creation` and complete [After a script mutation](#after-a-script-mutation). Once the intended script is imported, compiled, and usable, attach it to the resolved object:

```python
manage_gameobject(action="modify", target=enemy_id, components_to_add=["EnemyAI"])
manage_components(
    action="set_property", target=enemy_id,
    component_type="EnemyAI", properties={"speed": 10.0}
)
# Wire target to the intended Transform, then read the component back.
```

`enemy_id` is selected from current discovery. Verify the expected movement only in the project's permitted runtime flow; successful attachment is not behavioral evidence.

### Edit Existing Script Safely

```python
script_uri = "mcpforunity://path/Assets/Scripts/PlayerController.cs"
current = manage_script(action="read", name="PlayerController", path="Assets/Scripts")
sha_info = get_sha(uri=script_uri)
baseline_sha = sha_info["data"]["sha256"]
find_in_file(uri=script_uri, pattern=r"void Update\(\)")
```

Inspect the method/class boundary and existing members. The SHA is a baseline here; this public structured-edit call has no `precondition_sha256` argument. Use [SHA-guarded text edits](workflow-batch-recovery.md#stale-file-recovery) when a range edit and optimistic concurrency are needed.

The following replacement assumes a `speed` member and the old Input Manager or `Both` setting. For a New-only project, adapt [the input-system template](workflow-input.md) instead.

```python
script_apply_edits(
    name="PlayerController", path="Assets/Scripts",
    edits=[{
        "op": "replace_method", "className": "PlayerController",
        "methodName": "Update",
        "replacement": '''void Update()
    {
        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        transform.Translate(new Vector3(h, 0, v) * speed * Time.deltaTime);
    }'''
    }]
)
validate_script(uri=script_uri, level="standard", include_diagnostics=True)
```

Public validation levels are `basic` and `standard`; Roslyn checks depend on the installed package build. Validation diagnostics do not replace the actual import/compile, Console check, source readback, and relevant behavior verification from [After a script mutation](#after-a-script-mutation).

### Add Method to Existing Class

Inspect `GameManager` and ensure the intended `Start` anchor is unambiguous and `ResetGame` is absent. Use canonical `replacement`, `position`, and `afterMethodName`; `anchor_insert` uses `text` and a regex anchor. Preserve the file's newline convention and avoid duplicating an existing using directive.

```python
source = manage_script(action="read", name="GameManager", path="Assets/Scripts")
script_text = source["data"]["contents"]
newline = "\r\n" if "\r\n" in script_text else "\n"
edits = []
if "using UnityEngine.SceneManagement;" not in script_text:
    edits.append({
        "op": "anchor_insert",
        "anchor": r"(?m)^using UnityEngine;[ \t]*\r?\n",
        "position": "after",
        "text": "using UnityEngine.SceneManagement;" + newline
    })
edits.append({
    "op": "insert_method", "className": "GameManager",
    "position": "after", "afterMethodName": "Start",
    "replacement": newline.join([
        "    public void ResetGame()",
        "    {",
        "        SceneManager.LoadScene(SceneManager.GetActiveScene().name);",
        "    }"
    ])
})
script_apply_edits(name="GameManager", path="Assets/Scripts", edits=edits)
```

The using anchor includes the original line break, so inserted `text` is a complete following line. Adapt the anchor if the inspected source uses a different header layout. Read back method placement and header contents, then complete the import/compile and Console checks.

The public before variant is `position="before"` with `beforeMethodName`. Parameter availability does **not** establish correct native fallback placement. Confirm the installed implementation and read back the exact method location; use a verified implementation or an inspected, SHA-guarded range edit where appropriate.
