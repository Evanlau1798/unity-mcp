# Unity-MCP Scene, Camera and Graphics Resources

Use the [resource index](resources-reference.md) for other categories. Examples are illustrative templates; decode the actual response envelope before inspecting fields.

## Camera Resources

### mcpforunity://scene/cameras

**Purpose:** List all cameras in the scene (Unity Camera + CinemachineCamera) with full status. Read this before using `manage_camera` to understand the current camera setup.

**Illustrative payload (decode the actual envelope):**
```json
{
  "brain": {
    "exists": true,
    "gameObject": "Main Camera",
    "instanceID": 55504,
    "activeCameraName": "Cam_Cinematic",
    "activeCameraID": -39420,
    "isBlending": false
  },
  "cinemachineCameras": [
    {
      "instanceID": -39420,
      "name": "Cam_Cinematic",
      "isLive": true,
      "priority": 50,
      "follow": {"name": "CameraTarget", "instanceID": -26766},
      "lookAt": {"name": "CameraTarget", "instanceID": -26766},
      "body": "CinemachineThirdPersonFollow",
      "aim": "CinemachineRotationComposer",
      "noise": "CinemachineBasicMultiChannelPerlin",
      "extensions": []
    }
  ],
  "unityCameras": [
    {
      "instanceID": 55504,
      "name": "Main Camera",
      "depth": 0.0,
      "fieldOfView": 50.0,
      "hasBrain": true
    }
  ],
  "cinemachineInstalled": true
}
```

**Key Fields:**
- `brain`: CinemachineBrain status — which camera is active, blend state
- `cinemachineCameras`: All CinemachineCamera components with pipeline info (body, aim, noise, extensions)
- `unityCameras`: All Unity Camera components with depth and FOV
- `cinemachineInstalled`: Whether Cinemachine package is available

**Use with:** `manage_camera` tool for creating/configuring cameras

---

## Graphics Resources

### mcpforunity://scene/volumes

**Purpose:** List all Volume components in the scene with effects and parameters. Read this before using `manage_graphics` volume actions.

**Illustrative payload (decode the actual envelope):**
```json
{
  "pipeline": "Universal (URP)",
  "volumes": [
    {
      "name": "PostProcessVolume",
      "instance_id": -24600,
      "is_global": true,
      "weight": 1.0,
      "priority": 0,
      "blend_distance": 0,
      "profile": "MyProfile",
      "profile_path": "Assets/Settings/MyProfile.asset",
      "effects": [
        {
          "type": "Bloom",
          "active": true,
          "overridden_params": ["intensity", "threshold", "scatter"]
        },
        {
          "type": "Vignette",
          "active": true,
          "overridden_params": ["intensity", "smoothness"]
        }
      ]
    }
  ]
}
```

**Key Fields:**
- `is_global`: Whether the volume applies everywhere or only within its collider bounds
- `effects[].overridden_params`: Which parameters are actively overridden (not using defaults)
- `profile_path`: Empty string for embedded profiles, asset path for shared profiles

**Use with:** `manage_graphics` volume actions (volume_create, volume_add_effect, volume_set_effect, etc.)

### mcpforunity://rendering/stats

**Purpose:** Current rendering performance counters (draw calls, batches, triangles, memory).

**Illustrative payload (decode the actual envelope):**
```json
{
  "draw_calls": 42,
  "batches": 35,
  "set_pass_calls": 12,
  "triangles": 15234,
  "vertices": 8456,
  "dynamic_batches": 5,
  "static_batches": 20,
  "shadow_casters": 3,
  "render_textures": 8,
  "render_textures_bytes": 16777216,
  "visible_skinned_meshes": 2
}
```

**Use with:** `manage_graphics` stats actions (stats_get, stats_list_counters, stats_get_memory)

### mcpforunity://pipeline/renderer-features

**Purpose:** URP renderer features on the active renderer (SSAO, Decals, etc.).

**Illustrative payload (decode the actual envelope):**
```json
{
  "rendererDataName": "PC_Renderer",
  "features": [
    {
      "index": 0,
      "name": "ScreenSpaceAmbientOcclusion",
      "type": "ScreenSpaceAmbientOcclusion",
      "isActive": true,
      "properties": { "m_Settings": "Generic" }
    }
  ]
}
```

**Key Fields:**
- `index`: Position in the feature list (use for feature_toggle, feature_remove, feature_configure)
- `isActive`: Whether the feature is enabled
- `rendererDataName`: Which URP renderer data asset is active

**Use with:** `manage_graphics` feature actions (feature_list, feature_add, feature_remove, feature_toggle, etc.)

---

## Scene & GameObject Resources

### mcpforunity://scene/gameobject-api

**Purpose:** Documentation for GameObject resources (read this first).

### mcpforunity://scene/gameobject/{instance_id}

**Purpose:** Basic GameObject data (metadata, no component properties).

**Parameters:**
- `instance_id` (int): GameObject instance ID from `find_gameobjects`

**Illustrative payload (decode the actual envelope):**
```json
{
  "instanceID": 12345,
  "name": "Player",
  "tag": "Player",
  "layer": 8,
  "layerName": "Player",
  "active": true,
  "activeInHierarchy": true,
  "isStatic": false,
  "transform": {
    "position": [0, 1, 0],
    "rotation": [0, 0, 0],
    "scale": [1, 1, 1]
  },
  "parent": {"instanceID": 0},
  "children": [{"instanceID": 67890}],
  "componentTypes": ["Transform", "Rigidbody", "PlayerController"],
  "path": "/Player"
}
```

### mcpforunity://scene/gameobject/{instance_id}/components

**Purpose:** All components with full property serialization (paginated).

**Parameters:**
- `instance_id` (int): GameObject instance ID
- `page_size` (int): Default 25, max 100
- `cursor` (int): Pagination cursor
- `include_properties` (bool): Default true, set false for just types

**Illustrative payload (decode the actual envelope):**
```json
{
  "gameObjectID": 12345,
  "gameObjectName": "Player",
  "components": [
    {
      "type": "Transform",
      "properties": {
        "position": {"x": 0, "y": 1, "z": 0},
        "rotation": {"x": 0, "y": 0, "z": 0, "w": 1}
      }
    },
    {
      "type": "Rigidbody",
      "properties": {
        "mass": 1.0,
        "useGravity": true
      }
    }
  ],
  "cursor": 0,
  "pageSize": 25,
  "nextCursor": null,
  "hasMore": false
}
```

### mcpforunity://scene/gameobject/{instance_id}/component/{component_name}

**Purpose:** Single component with full properties.

**Parameters:**
- `instance_id` (int): GameObject instance ID
- `component_name` (string): e.g., "Rigidbody", "Camera", "Transform"

**Illustrative payload (decode the actual envelope):**
```json
{
  "gameObjectID": 12345,
  "gameObjectName": "Player",
  "component": {
    "type": "Rigidbody",
    "properties": {
      "mass": 1.0,
      "drag": 0,
      "angularDrag": 0.05,
      "useGravity": true,
      "isKinematic": false
    }
  }
}
```

---

## Prefab Resources

### mcpforunity://prefab-api

**Purpose:** Documentation for prefab resources.

### mcpforunity://prefab/{encoded_path}

**Purpose:** Prefab asset information.

**Parameters:**
- `encoded_path` (string): URL-encoded path, e.g., `Assets%2FPrefabs%2FPlayer.prefab`

**Path Encoding:**
```
Assets/Prefabs/Player.prefab → Assets%2FPrefabs%2FPlayer.prefab
```

**Illustrative payload (decode the actual envelope):**
```json
{
  "assetPath": "Assets/Prefabs/Player.prefab",
  "guid": "abc123...",
  "prefabType": "Regular",
  "rootObjectName": "Player",
  "rootComponentTypes": ["Transform", "PlayerController"],
  "childCount": 5,
  "isVariant": false,
  "parentPrefab": null
}
```

### mcpforunity://prefab/{encoded_path}/hierarchy

**Purpose:** Full prefab hierarchy with nested prefab info.

**Illustrative payload (decode the actual envelope):**
```json
{
  "prefabPath": "Assets/Prefabs/Player.prefab",
  "total": 6,
  "items": [
    {
      "name": "Player",
      "instanceId": 12345,
      "path": "/Player",
      "activeSelf": true,
      "childCount": 2,
      "componentTypes": ["Transform", "PlayerController"]
    },
    {
      "name": "Model",
      "path": "/Player/Model",
      "isNestedPrefab": true,
      "nestedPrefabPath": "Assets/Prefabs/PlayerModel.prefab"
    }
  ]
}
```
