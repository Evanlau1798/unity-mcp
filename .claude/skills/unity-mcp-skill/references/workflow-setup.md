# Setup and readiness

[Workflow index](workflows.md). Use this procedure before an authorized Editor operation; reading or editing documentation does not require runtime preflight.

> **Template:** Resource-reading syntax below is client pseudocode. `read_resource` means the client's resource reader, not a Unity tool. Inspect the installed resource schema before applying these field names.

## Setup & Verification

### Initial Connection Verification

1. Read `mcpforunity://instances` when instance selection is needed. Resolve the exact intended project/Editor; use `set_active_instance` with its observed identifier when the client has no exact target selected.
2. Read `mcpforunity://editor/state`. Require a successful resource response and associate `data.unity.instance_id` with that target.
3. Inspect the current scene at `data.editor.active_scene`, mode at `data.editor.play_mode`, and observation identity/time at `data.schema_version`, `data.sequence`, and `data.observed_at_unix_ms`.
4. Read only the scene/component/project resources needed for the task. Re-resolve object references after reload or instance changes; old instance IDs remain evidence, not current targets.

### Before Any Operation

The v2 payload is nested under `data`; the old root-level `ready_for_tools` and `is_compiling` examples are not the v2 contract.

| Field beneath `data` | Decision it informs |
| --- | --- |
| `compilation.is_compiling` | Compilation is still active. |
| `compilation.is_domain_reload_pending` | The loaded scripting domain is not yet stable. |
| `assets.is_updating` | Asset import is active. |
| `assets.refresh` | Tracked refresh `operation_id`, `status`, `completed`, and `is_refresh_in_progress`. |
| `assets.external_changes_dirty` and generation fields | External changes may still need import; relate them to the operation that handled them. |
| `tests.is_running`, `tests.current_job_id` | Observe an existing run before starting another mutation. |
| `advice.ready_for_tools`, `advice.blocking_reasons` | Current readiness advice and blockers. |
| `advice.recommended_retry_after_ms` | Retry guidance for a bounded read-only wait. |
| `staleness.is_stale`, `staleness.age_ms` | Whether the snapshot can support a current decision. |

```python
response = read_resource("mcpforunity://editor/state")
state = response.get("data") or {}
compilation = state.get("compilation") or {}
assets = state.get("assets") or {}
refresh = assets.get("refresh") or {}
tests = state.get("tests") or {}
advice = state.get("advice") or {}
staleness = state.get("staleness") or {}

snapshot_ready = (
    response.get("success") is True
    and state.get("schema_version") == "unity-mcp/editor_state@2"
    and advice.get("ready_for_tools") is True
    and staleness.get("is_stale") is False
    and compilation.get("is_compiling") is False
    and compilation.get("is_domain_reload_pending") is False
    and assets.get("is_updating") is False
    and refresh.get("is_refresh_in_progress") is False
    and tests.get("is_running") is False
)
# If false/unknown: inspect blockers and reobserve within a bounded wait.
# Do not infer completion merely from compilation.is_compiling == False.
```

Snapshot readiness alone does not prove that a particular script edit or refresh completed, or that a previous test job released ownership. Correlate the actual operation/job receipt with the intended change. For tests, confirm both cleanup and ownership are released before a new mutation; see [testing](workflow-testing.md) and [lifecycle and recovery](lifecycle-and-recovery.md).

If an editing tool already initiated import, observe that work and check Console instead of issuing another refresh. For external file edits that still require import, use the tracked refresh procedure in [batch and recovery](workflow-batch-recovery.md#compilation-block-recovery). A stale snapshot, timeout, or connection loss is an observation limitation, not proof that native work stopped.

## Completion evidence

Read back the changed objects/assets and check the relevant Console output. Use screenshots for visual changes and the project's required tests only when they address the changed behavior. Keep the original scene, dirty-state ownership, and temporary resources within the task's scope. The shared [validation procedure](validation-workflow.md) defines evidence selection and completion boundaries.
