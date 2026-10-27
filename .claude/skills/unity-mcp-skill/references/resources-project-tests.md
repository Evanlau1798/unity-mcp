# Unity-MCP Project, Instance and Test Resources

Use the [resource index](resources-reference.md) for other categories. Examples are illustrative templates; decode the actual response envelope before inspecting fields.

## Project Resources

### mcpforunity://project/info

**Purpose:** Project identity and capabilities for choosing operations compatible with this project.

**Illustrative payload (decode the actual envelope):**
```json
{
  "projectRoot": "/Users/dev/MyProject",
  "projectName": "MyProject",
  "unityVersion": "2022.3.10f1",
  "platform": "StandaloneWindows64",
  "assetsPath": "/Users/dev/MyProject/Assets"
}
```

**Capability fields:**

| Field | Type | Meaning |
| --- | --- | --- |
| `projectRoot`, `projectName`, `assetsPath` | string | Exact project identity and paths |
| `unityVersion`, `platform` | string | Unity version and active build target |
| `renderPipeline` | string | `BuiltIn`, `Universal`, `HighDefinition` or `Custom` |
| `activeInputHandler` | string | `Old`, `New` or `Both` |
| `packages.ugui` | bool | Canvas/uGUI package availability |
| `packages.textmeshpro` | bool | TextMeshPro package availability |
| `packages.inputsystem` | bool | New Input System package availability |
| `packages.uiToolkit` | bool | UI Toolkit availability (Unity 2021.3+ in this contract) |
| `packages.screenCapture` | bool | ScreenCapture module availability |

Use the project's requested/existing UI system and these package fields together; capability detection does not mandate migrating UI. For new UI Toolkit work, use `manage_ui`; uGUI uses Canvas/components. Choose TextMeshPro when the project uses it and the package is available. Match EventSystem modules to `activeInputHandler` (`StandaloneInputModule` for Old, `InputSystemUIInputModule` for New); see [input workflow](workflows.md#input-system-old-vs-new). Match material shaders to `renderPipeline`, such as `Standard`, `Universal Render Pipeline/Lit` or `HDRP/Lit`, and inspect package-specific alternatives.

### mcpforunity://project/tags

**Purpose:** All tags defined in TagManager.

**Illustrative payload (decode the actual envelope):**
```json
["Untagged", "Respawn", "Finish", "EditorOnly", "MainCamera", "Player", "GameController", "Enemy"]
```

### mcpforunity://project/layers

**Purpose:** All layers with indices (0-31).

**Illustrative payload (decode the actual envelope):**
```json
{
  "0": "Default",
  "1": "TransparentFX",
  "2": "Ignore Raycast",
  "4": "Water",
  "5": "UI",
  "8": "Player",
  "9": "Enemy"
}
```

### mcpforunity://menu-items

**Purpose:** All available Unity menu items.

**Illustrative payload (decode the actual envelope):**
```json
[
  "File/New Scene",
  "File/Open Scene",
  "File/Save",
  "Edit/Undo",
  "Edit/Redo",
  "GameObject/Create Empty",
  "GameObject/3D Object/Cube",
  "Window/General/Console"
]
```

### mcpforunity://custom-tools

**Purpose:** Custom tools available in the active Unity project.

**Returns** (abbreviated: one of the 3 tools is shown):
```json
{
  "project_id": "MyProject",
  "tool_count": 3,
  "tools": [
    {
      "name": "capture_screenshot",
      "description": "Capture screenshots in Unity",
      "parameters": [
        {"name": "filename", "type": "string", "required": true},
        {"name": "width", "type": "int", "required": false},
        {"name": "height", "type": "int", "required": false}
      ]
    }
  ]
}
```

### mcpforunity://tool-groups

**Purpose:** Every tool group, whether it is on by default, and its tools. Enable or hide a group for the session with `manage_tools`.

**Returns** (abbreviated: `groups` has one entry per group, in name order; one is shown):
```json
{
  "groups": [
    {"name": "animation", "description": "Animator control, AnimationClip creation & 2D sprite-sheet animation",
     "default_enabled": false, "tools": ["manage_animation", "manage_sprite"], "tool_count": 2}
  ],
  "total_groups": 10,
  "default_enabled": ["core"],
  "usage": "Call manage_tools(action='activate', group='<name>') to enable a group."
}
```

---

## Instance Resources

### mcpforunity://instances

**Purpose:** All running Unity Editor instances (for multi-instance workflows).

**Illustrative payload (decode the actual envelope):**
```json
{
  "success": true,
  "transport": "http",
  "instance_count": 2,
  "instances": [
    {
      "id": "MyProject@abc123",
      "name": "MyProject",
      "hash": "abc123",
      "unity_version": "2022.3.10f1",
      "connected_at": "2024-01-15T10:30:00Z"
    },
    {
      "id": "TestProject@def456",
      "name": "TestProject",
      "hash": "def456",
      "unity_version": "2022.3.10f1",
      "connected_at": "2024-01-15T11:00:00Z"
    }
  ]
}
```

**Routing:** An explicit per-call `unity_instance` is considered first, an instance-bound endpoint next, then legacy `set_active_instance` session selection or automatic selection. A per-call target that conflicts with a bound endpoint is an error. Bound endpoints require an exact `Name@hash`; tool calls may accept the selectors described by the active schema. Resource reads have no per-call tool argument, so establish their endpoint/session target explicitly.

The instances resource returns top-level `success`, `transport`, `instance_count` and `instances`; it is not the same envelope as Editor state. HTTP entries can include `session_id` and `connected_at`; stdio entries can include path, port, status and heartbeat fields. Duplicate names produce a singular `warning` string when present. Keep exact IDs, not names alone.

---

## Test Resources

### mcpforunity://tests

**Purpose:** The first discovery page across both modes. It does not execute tests and is not an exhaustive list when `data.hasMore` is true.

**Illustrative response:**

```json
{
  "success": true,
  "data": {
    "items": [{"name": "TestSomething", "full_name": "MyTests.TestSomething", "mode": "EditMode"}],
    "cursor": 0,
    "nextCursor": null,
    "totalCount": 1,
    "pageSize": 50,
    "hasMore": false
  }
}
```

The resource requests native default pagination (50 items) and exposes no cursor/filter parameters. Use the read-only [list_tests tool](tools-testing.md#list_tests) for mode/name filtering and complete pagination: keep the same filter, follow `data.nextCursor`, and stop when `data.hasMore` is false. Do not call `run_tests` to enumerate tests or treat a first page as the full inventory.

### mcpforunity://tests/{mode}

**Purpose:** The first discovery page for `EditMode` or `PlayMode`, with the same `data.items` and pagination envelope above.

**Example:** `mcpforunity://tests/EditMode`. For later pages use `list_tests(mode="EditMode", cursor=returned_next_cursor, ...)`.
