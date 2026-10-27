# Batch operations and error recovery

[Workflow index](workflows.md). Load the matching procedure for bulk native commands, partial results, stale edits, or interrupted observations.

> **Template:** Names, IDs, paths, and selected commands must come from the inspected project. Confirm the installed native batch, refresh, and script wrapper contracts before use.

## Batch Operations

`batch_execute` dispatches **native Unity command handlers sequentially on the Editor main thread**. It does not invoke each Python tool wrapper, so wrapper encoding, preflight, and recovery do not run per command. Do not put arbitrary public Python tools, script convenience wrappers, or API-doc wrappers into a batch merely because they are visible tools. Use a verified native command and its native parameter contract.

| Contract | Operational consequence |
| --- | --- |
| Native sequential dispatch | Order dependencies can be explicit; there are no parallel Unity workers. |
| `parallel` / `max_parallelism` compatibility hints | `data.parallelApplied` remains `false`, even if parallel was requested. |
| Default `fail_fast=False` | Independent later commands continue after a failure. Use `True` for a dependency chain. |
| Default cap 25; configurable 1–100; hard maximum 100 | Read `data.settings.batch_execute_max_commands` from Editor state; split according to the current cap. |
| Only top-level parameter keys normalized | Preserve exact nested component/property keys. |
| One outer instance target | Per-command `unity_instance` is rejected; route the whole batch to one verified Editor. |
| Non-transactional execution | Earlier effects remain after failure; no rollback is provided. |

A batch can reduce request overhead; it does not establish a fixed speedup or batch AssetDatabase imports. After any batch inspect `data.results`: each entry contains `tool`, `callSucceeded`, and its nested `result` or `error`. A successful invocation can still represent accepted asynchronous work, not completed work. With `fail_fast=True`, later commands may have no result because they were not dispatched. Reconcile the actual changed state before retrying only the still-needed commands.

### Batch Discovery (Multi-Search)

```python
report = batch_execute(commands=[
    {"tool": "find_gameobjects", "params": {"search_term": "Camera", "search_method": "by_component"}},
    {"tool": "find_gameobjects", "params": {"search_term": "Rigidbody", "search_method": "by_component"}},
    {"tool": "find_gameobjects", "params": {"search_term": "Player", "search_method": "by_tag"}},
    {"tool": "find_gameobjects", "params": {"search_term": "GameManager", "search_method": "by_name"}}
])
for entry in (report.get("data") or {}).get("results", []):
    print(entry["tool"], entry.get("callSucceeded"), entry.get("result"), entry.get("error"))
```

Inspect each discovery result and follow its pagination where present. Do not treat the outer response as an unwrapped array or assume each query returned a unique object.

### Mass Property Update

```python
enemies = find_gameobjects(search_term="Enemy", search_method="by_tag")
# Resolve the intended objects across the actual returned pages into enemy_ids.
# Inspect EnemyHealth and maxHealth before applying the template.
commands = [
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": enemy_id,
        "component_type": "EnemyHealth", "property": "maxHealth", "value": 100
    }}
    for enemy_id in enemy_ids
]
# batch_limit is the observed Editor setting (default 25, range 1–100).
for i in range(0, len(commands), batch_limit):
    report = batch_execute(commands=commands[i:i + batch_limit], fail_fast=False)
    # Inspect every per-command result before deciding whether to continue.
```

Read back representative or required target properties according to the task's acceptance condition. Handle failed objects without repeating successful non-idempotent work.

### Mass Object Creation with Variations

```python
import random

commands = []
for i in range(20):
    commands.append({
        "tool": "manage_gameobject", "params": {
            "action": "create", "name": f"Tree_{i}", "primitive_type": "Capsule",
            "position": [random.uniform(-50, 50), 0, random.uniform(-50, 50)],
            "scale": [1, random.uniform(2, 5), 1]
        }
    })
for i in range(0, len(commands), batch_limit):
    report = batch_execute(commands=commands[i:i + batch_limit])
    # Record the actual successful creations and their owner/identity.
```

Inspect for name collisions before launch. Preserve the generated command values and actual creation receipts so a retry does not silently choose a different layout. Verify object counts/transforms and the visual result, then save intentional scene changes.

### Cleanup Pattern

Name discovery can help locate temporary objects, but a prefix does not establish ownership. Delete only objects this task created, still owns, and no active native job needs. Re-resolve ownership and current IDs after reload; do not reuse old numeric IDs blindly.

```python
# owned_temp_ids comes from this task's creation records plus current verification.
commands = [
    {"tool": "manage_gameobject", "params": {"action": "delete", "target": object_id}}
    for object_id in owned_temp_ids
]
for i in range(0, len(commands), batch_limit):
    report = batch_execute(commands=commands[i:i + batch_limit], fail_fast=False)
    # Inspect each deletion result and confirm the owned objects are gone.
```

An empty list produces no batch call. Native deletion is immediate and has no Undo guarantee. If an inactive object cannot be resolved by the installed mutation handler, inspect that limitation rather than falling back to a broad name deletion. Preserve primary errors and any cleanup failures separately. Completion also includes any required scene/settings restoration; see [lifecycle cleanup](lifecycle-and-recovery.md#cleanup-擁有權).

## Error Recovery Patterns

### Stale File Recovery

A `stale_file` rejection means the old SHA does not match current contents. Re-read the source and determine whether the original change is still needed. Rebuild ranges/anchors against that source; changing only the SHA and replaying old offsets can overwrite somebody else's change.

```python
script_uri = "mcpforunity://path/Assets/Scripts/PlayerController.cs"
current = manage_script(action="read", name="PlayerController", path="Assets/Scripts")
sha_info = get_sha(uri=script_uri)
# Inspect current["data"]["contents"] and build reviewed_range_edits from it.
# Confirm that the read content still corresponds to the SHA before submitting.
edit_result = apply_text_edits(
    uri=script_uri, edits=reviewed_range_edits,
    precondition_sha256=sha_info["data"]["sha256"]
)
```

Inspect returned errors as well as transport exceptions. After a successful write, read back the intended edit and use [script completion](workflow-scripts.md#after-a-script-mutation). If the result is unknown after a disconnect, verify the existing file before sending another mutation.

### Domain Reload Recovery

1. Retain the original Editor, operation/job ID if provided, and mutation receipt. A lost connection does not prove the mutation was rejected.
2. Reobserve `mcpforunity://editor/state` with bounded waits, using returned retry guidance and backing off when no new information appears. Validate the nested v2 fields and staleness through [setup](workflow-setup.md#before-any-operation).
3. Continue an existing test with `get_test_job(job_id=original_job_id)` or an existing tracked refresh with `refresh_unity(operation_id=original_operation_id)`. These are observations of the existing identity.
4. Re-read affected source/assets and re-resolve current object IDs. If the wait budget ends, report the unresolved observation and retained identity; do not replay, cancel, clear, or restart merely because the connection was lost.

### Compilation Block Recovery

Read current Console errors, identify the relevant source, and apply only the needed correction. Script editing tools normally initiate import/compilation themselves; observe that work and check the current v2 readiness and Console result without a duplicate refresh.

For **external changes that still need import**, and only after any previous native owner is released:

```python
refresh_result = refresh_unity(
    mode="if_dirty", scope="scripts", compile="request", wait_for_ready=True
)
# Inspect success/error, the actual operation receipt, and dirty-generation outcome.
```

When this refresh returns an unresolved receipt with `data.operation_id`, preserve it. Continue that exact operation rather than starting another:

```python
# original_operation_id is copied from the actual unresolved refresh receipt.
observed = refresh_unity(operation_id=original_operation_id, wait_for_ready=True)
```

Check the matching native completion receipt, v2 state freshness/readiness, and Console. `if_dirty` is conditional on the installed tracked implementation and observed generation; do not turn recovery into a routine force refresh or clear changes added after the captured generation. When receipt fields or cleanup are unknown, follow [lifecycle and recovery](lifecycle-and-recovery.md) and preserve that uncertainty.
