# Unity-MCP Editor Resources

Use the [resource index](resources-reference.md) for other categories. Examples are illustrative templates; decode the actual response envelope before inspecting fields.

## Editor State Resources

### mcpforunity://editor/state

**Purpose:** A read-only readiness and activity snapshot for the selected Editor. In the current v2 contract, the fields are nested under `data`; they are not flat top-level flags.

**Illustrative response (not a captured run):**

```json
{
  "success": true,
  "data": {
    "schema_version": "unity-mcp/editor_state@2",
    "observed_at_unix_ms": 1780000000000,
    "sequence": 42,
    "unity": {"instance_id": "MyProject@abc123", "unity_version": "6000.0"},
    "editor": {
      "is_focused": true,
      "play_mode": {"is_playing": false, "is_paused": false, "is_changing": false},
      "active_scene": {"path": "Assets/Scenes/Main.unity", "name": "Main"}
    },
    "compilation": {"is_compiling": false, "is_domain_reload_pending": false},
    "assets": {
      "is_updating": false,
      "external_changes_dirty": false,
      "external_changes_scan_complete": true,
      "refresh": {"operation_id": null, "status": null, "completed": null,
                  "is_refresh_in_progress": false}
    },
    "tests": {"is_running": false, "current_job_id": null},
    "settings": {"batch_execute_max_commands": 25},
    "advice": {"ready_for_tools": true, "blocking_reasons": [],
               "recommended_retry_after_ms": 0, "recommended_next_action": "none"},
    "staleness": {"age_ms": 150, "is_stale": false}
  }
}
```

| Field path | Meaning and limits |
| --- | --- |
| `data.unity` | Instance ID, Unity version, project ID, platform and batch-mode identity |
| `data.editor.play_mode` / `data.editor.active_scene` | Current play/pause/transition state and scene identity |
| `data.activity` | Phase, start timestamp and reasons for current activity |
| `data.compilation` | Compile/reload flags plus last start/finish and before/after-reload timestamps |
| `data.assets.is_updating` | Asset import activity |
| `data.assets.external_changes_*` | Server-observed dirty state, generation/acknowledged-generation values, scan completeness and timestamps |
| `data.assets.refresh` | Refresh `operation_id`, `status`, `completed`, in-progress flag and request/finish timestamps |
| `data.tests` | Running flag, mode, current job ID, start identity and `last_run` summary |
| `data.transport` | Bridge connectivity and last-message timestamp |
| `data.settings.batch_execute_max_commands` | Configured native batch limit |
| `data.advice` | `ready_for_tools`, blocking reasons, retry delay and recommended next action |
| `data.staleness` | Server-computed age and stale flag for this observation |

The current server marks snapshots older than **2,000 ms** stale. Blocking reasons can include `compiling`, `domain_reload`, `running_tests`, `asset_refresh`, `asset_import` and `stale_status`. Inspect readiness together with identity, freshness and the operation you are observing; missing/null fields are unknown. A stale status can explain an observation problem without proving the native operation failed.

For a tracked refresh, correlate `data.assets.refresh.operation_id` with the accepted operation and query `refresh_unity(operation_id=existing_id)` for its receipt. General readiness or `is_compiling=false` alone cannot establish completion of a particular request, successful compilation, native test cleanup or restoration. See [refresh contract](tools-infrastructure-editor.md#refresh_unity) and [lifecycle recovery](lifecycle-and-recovery.md).

#### Refresh operation receipts

The `refresh_unity` tool's receipt is under its own `data`, separate from the resource snapshot's `data.assets.refresh`.

| Receipt fields | Interpretation |
| --- | --- |
| `operation_id`, `completed=false`, status such as `accepted`, `importing`, `awaiting_compile_start`, `compiling` or `awaiting_reload` | Accepted/in-progress work; keep observing this identity |
| Matching `operation_id`, `completed=true`, `status="succeeded"`, top-level success true | This tracked operation completed successfully |
| `completed=false`, `status="pending"`, `timeout=true`, top-level `hint="poll_same_operation"` | Completion remains unconfirmed; inspect `last_response` and query the same operation |
| A failed response or a status such as `compilation_failed` / `dispatch_failed_pending` | Preserve the failure; inspect `completed` and ownership before interpreting it as fully cleaned up |
| `completed=true`, `status="no_op"` | Reliable scan found no external changes for the requested scope; no new operation ID is required |

When present, `dirty_snapshot_acknowledged` says whether the captured external-change snapshot was acknowledged after success. It does not erase changes made after that snapshot. A missing/unsupported receipt, identity mismatch or `refresh_operation_not_found` is not completion evidence and does not justify replaying an uncertain mutation.

### mcpforunity://editor/selection

**Purpose:** Currently selected objects.

**Illustrative payload (decode the actual envelope):**
```json
{
  "activeObject": "Player",
  "activeGameObject": "Player",
  "activeInstanceID": 12345,
  "count": 3,
  "gameObjects": ["Player", "Enemy", "Wall"],
  "assetGUIDs": []
}
```

### mcpforunity://editor/active-tool

**Purpose:** Current editor tool state.

**Illustrative payload (decode the actual envelope):**
```json
{
  "activeTool": "Move",
  "isCustom": false,
  "pivotMode": "Center",
  "pivotRotation": "Global"
}
```

### mcpforunity://editor/windows

**Purpose:** All open editor windows.

**Illustrative payload (decode the actual envelope):**
```json
{
  "windows": [
    {
      "title": "Scene",
      "typeName": "UnityEditor.SceneView",
      "isFocused": true,
      "position": {"x": 0, "y": 0, "width": 800, "height": 600}
    }
  ]
}
```

### mcpforunity://editor/prefab-stage

**Purpose:** Current prefab editing context.

**Illustrative payload (decode the actual envelope):**
```json
{
  "isOpen": true,
  "assetPath": "Assets/Prefabs/Player.prefab",
  "prefabRootName": "Player",
  "isDirty": false
}
```

---

## Best Practices

### 1. Check Editor State First

```python
# For a live operation whose readiness is uncertain:
# Read mcpforunity://editor/state
# Inspect data.advice, data.staleness and the relevant operation/job fields
```

### 2. Use Find Then Read Pattern

```python
# 1. find_gameobjects to get IDs
result = find_gameobjects(search_term="Player")

# 2. Read resource for full data
# mcpforunity://scene/gameobject/{id}
```

### 3. Paginate Large Queries

```python
# Start with include_properties=false for component lists
# mcpforunity://scene/gameobject/{id}/components?include_properties=false&page_size=25

# Then read specific components as needed
# mcpforunity://scene/gameobject/{id}/component/Rigidbody
```

### 4. URL-Encode Prefab Paths

```python
# Wrong:
# mcpforunity://prefab/Assets/Prefabs/Player.prefab

# Correct:
# mcpforunity://prefab/Assets%2FPrefabs%2FPlayer.prefab
```

### 5. Multi-Instance Awareness

```python
# Resolve the exact instance before live work, and re-resolve after reload
# or when evidence suggests the target changed. Reuse a verified target
# while its identity remains valid; offline documentation needs no Editor.
```
