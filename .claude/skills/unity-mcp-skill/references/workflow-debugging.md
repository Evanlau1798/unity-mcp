# Debugging workflows

[Workflow index](workflows.md). Read only the procedure relevant to the observed failure.

> **Template:** Names and paths are placeholders. Verify the template against the installed schema and project before use. Establish the [target and readiness](workflow-setup.md) before mutations; observation can continue while a job is busy.

## Debugging Workflows

### Diagnose Compilation Errors

```python
errors = read_console(
    types=["error"],
    count=20,
    include_stacktrace=True,
    format="detailed"
)
```

Inspect the actual returned error entries, source locations, and relevant scripts. Separate a compilation/tool failure from a product test failure. Read the current source before applying a targeted fix, then follow [script completion](workflow-scripts.md#after-a-script-mutation).

When `create_script`, `script_apply_edits`, or another editing tool already initiated import, wait for that work; do not force another refresh. External edits needing import use [tracked refresh](workflow-batch-recovery.md#compilation-block-recovery). Preserve the original diagnostics and distinguish new errors from older Console entries.

### Investigate Missing References

```python
# 1. Discover and inspect the exact affected object.
player_candidates = find_gameobjects(search_term="Player", search_method="by_name")
# Select player_id from the actual result after resolving ambiguity.
# Read mcpforunity://scene/gameobject/{player_id}/components.

# 2. Inspect the missing field's declared type and locate its intended target.
target_candidates = find_gameobjects(search_term="Target", search_method="by_name")
# Select target_id from the actual result and inspect the target component.

# 3. Set the observed reference using current instance IDs.
manage_components(
    action="set_property", target=player_id,
    component_type="PlayerController", property="target",
    value={"instanceID": target_id}
)
# 4. Read the component again and confirm the intended reference resolved.
```

`player_id` and `target_id` are values selected from discovery, not guessed identifiers. Re-resolve them after reload. If a recreated script lost its GUID, restore the correct reference and use [in-place class editing](workflow-scripts.md#script-overwrites-with-replace_class) for future changes.

### Check Scene State

```python
hierarchy = manage_scene(action="get_hierarchy", page_size=100, include_transform=True)
# Inspect hierarchy["data"]["items"] and follow next_cursor when present.
# Evaluate returned transform values using their actual serialized shape.
# Flag positions that violate this scene's expected bounds, not a universal threshold.
manage_camera(action="screenshot", include_image=True, max_resolution=512)
```

A screenshot establishes appearance at one observation. Verify the relevant object/component state and behavior separately. For long-running or disconnected operations, preserve the original job/operation identity and use [lifecycle and recovery](lifecycle-and-recovery.md) before retrying a mutation.
