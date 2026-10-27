# Camera and modeling workflows

[Workflow index](workflows.md). Read this reference only for the matching task.

> **Template:** Adapt object names, paths, component properties, and package-dependent values to the current project. Verify the template against the installed schema and project before use.

Before mutations, establish [readiness and the current target](workflow-setup.md), inspect camera/package resources, and preserve the existing camera/override state. Run only the selected procedure. Batch examples inherit the [native-only, sequential, partial-result contract](workflow-batch-recovery.md#batch-operations).

## Camera & Cinemachine Workflows

### Setting Up a Third-Person Camera

```python
# 1. Check Cinemachine availability
manage_camera(action="ping")

# 2. Ensure Brain on main camera
manage_camera(action="ensure_brain")

# 3. Create third-person camera with preset
manage_camera(action="create_camera", properties={
    "name": "FollowCam", "preset": "third_person",
    "follow": "Player", "lookAt": "Player", "priority": 20
})

# 4. Fine-tune body
manage_camera(action="set_body", target="FollowCam", properties={
    "cameraDistance": 5.0, "shoulderOffset": [0.5, 0.5, 0]
})

# 5. Add camera shake
manage_camera(action="set_noise", target="FollowCam", properties={
    "amplitudeGain": 0.3, "frequencyGain": 0.8
})

# 6. Verify with screenshot
# Choose the actual rendering Unity Camera from mcpforunity://scene/cameras.
# A Cinemachine virtual camera is not automatically a screenshot Camera target.
manage_camera(action="screenshot", camera=rendering_camera_id, include_image=True, max_resolution=512)
```

### Multi-Camera Setup with Blending

```python
# 1. Read current cameras
# Read mcpforunity://scene/cameras

# 2. Create gameplay camera (highest priority = active by default)
manage_camera(action="create_camera", properties={
    "name": "GameplayCam", "preset": "follow",
    "follow": "Player", "lookAt": "Player", "priority": 10
})

# 3. Create cinematic camera (lower priority, activated on demand)
manage_camera(action="create_camera", properties={
    "name": "CinematicCam", "preset": "dolly",
    "lookAt": "CutsceneTarget", "priority": 5
})

# 4. Set blend transition
manage_camera(action="set_blend", properties={"style": "EaseInOut", "duration": 2.0})

# 5. For a temporary preview, acquire and release this task's override.
# Confirm no pre-existing user override would be lost before starting.
manage_camera(action="force_camera", target="CinematicCam")
try:
    # Inspect the blend/result through the actual rendering camera.
    manage_camera(action="screenshot", camera=rendering_camera_id, include_image=True)
finally:
    manage_camera(action="release_override")
```

### Camera Without Cinemachine

```python
# Tier 1 actions work with plain Unity Camera
manage_camera(action="create_camera", properties={
    "name": "MainCam", "fieldOfView": 50
})

# Set lens
manage_camera(action="set_lens", target="MainCam", properties={
    "fieldOfView": 60, "nearClipPlane": 0.1, "farClipPlane": 1000
})

# Point camera at target (uses manage_gameobject look_at under the hood)
manage_camera(action="set_target", target="MainCam", properties={
    "lookAt": "Player"
})

# Screenshot from this camera
manage_camera(action="screenshot", camera="MainCam", include_image=True, max_resolution=512)
```

### Camera Inspection Workflow

```python
# 1. Read all cameras via resource
# Read mcpforunity://scene/cameras
# → Shows brain status, all Cinemachine cameras (priority, pipeline, targets),
#   all Unity cameras (FOV, depth, brain)

# 2. Get brain status for blending info
manage_camera(action="get_brain_status")

# 3. List cameras via tool (alternative to resource)
manage_camera(action="list_cameras")

# 4. Multi-view screenshot to see from different angles
manage_camera(action="screenshot_multiview", max_resolution=480)
```

### Scene View Screenshot Workflow

Use `capture_source="scene_view"` to capture the editor's Scene View viewport — useful for seeing gizmos, wireframes, grid, debug overlays, and objects without cameras.

```python
# 1. Capture the Scene View as-is
manage_camera(action="screenshot", capture_source="scene_view", include_image=True)

# 2. Frame on a specific object first, then capture
manage_camera(action="screenshot", capture_source="scene_view",
    view_target="Player", include_image=True, max_resolution=512)

# 3. Frame on UI Canvas (RectTransform bounds are supported)
manage_camera(action="screenshot", capture_source="scene_view",
    view_target="Canvas", include_image=True)

# Limitations: scene_view does not support batch, view_position, view_rotation, or camera selection.
# Use capture_source="game_view" (default) for those features.
```

---

## ProBuilder Workflows

When `com.unity.probuilder` is installed, prefer ProBuilder shapes over primitive GameObjects for any geometry that needs editing, multi-material faces, or non-trivial shapes. Check availability first with `manage_probuilder(action="ping")`.

See [ProBuilder Workflow Guide](probuilder-guide.md) for full reference with complex object examples.

### ProBuilder vs Primitives Decision

| Need | Use Primitives | Use ProBuilder |
|------|---------------|----------------|
| Simple placeholder cube | `manage_gameobject(action="create", primitive_type="Cube")` | - |
| Editable geometry | - | `manage_probuilder(action="create_shape", ...)` |
| Per-face materials | - | `set_face_material` |
| Custom shapes (L-rooms, arches) | - | `create_poly_shape` or `create_shape` |
| Mesh editing (extrude, bevel) | - | Face/edge/vertex operations |
| Batch environment building | Either | ProBuilder + `batch_execute` |

### Basic ProBuilder Scene Build

```python
# 1. Check ProBuilder availability
manage_probuilder(action="ping")

# 2. Create shapes (use batch for multiple)
batch_execute(commands=[
    {"tool": "manage_probuilder", "params": {
        "action": "create_shape",
        "properties": {"shape_type": "Cube", "name": "Floor", "width": 20, "height": 0.2, "depth": 20}
    }},
    {"tool": "manage_probuilder", "params": {
        "action": "create_shape",
        "properties": {"shape_type": "Cube", "name": "Wall1", "width": 20, "height": 3, "depth": 0.3,
                       "position": [0, 1.5, 10]}
    }},
    {"tool": "manage_probuilder", "params": {
        "action": "create_shape",
        "properties": {"shape_type": "Cylinder", "name": "Pillar1", "radius": 0.4, "height": 3,
                       "position": [5, 1.5, 5]}
    }},
])

# 3. Edit geometry (always get_mesh_info first!)
info = manage_probuilder(action="get_mesh_info", target="Wall1",
    properties={"include": "faces"})
# Find direction="front" face, subdivide it, delete center for a window

# 4. Inspect Floor faces separately; Wall1 indices do not identify Floor faces.
manage_probuilder(action="get_mesh_info", target="Floor", properties={"include": "faces"})
# Select floor_face_indices from that current result and confirm Stone.mat exists.
manage_probuilder(action="set_face_material", target="Floor",
    properties={"faceIndices": floor_face_indices, "materialPath": "Assets/Materials/Stone.mat"})

# 5. Smooth organic shapes
manage_probuilder(action="auto_smooth", target="Pillar1",
    properties={"angleThreshold": 45})

# 6. Screenshot to verify
manage_camera(action="screenshot", include_image=True, max_resolution=512)
```

### Edit-Verify Loop Pattern

Topology edits can change face indices. Re-query the affected mesh before a later indexed operation; choose faces by current geometry rather than assuming an old index remains meaningful:

```python
# face 2 was selected from the current mesh query. Re-query after this edit:
manage_probuilder(action="subdivide", target="Obj", properties={"faceIndices": [2]})
info = manage_probuilder(action="get_mesh_info", target="Obj", properties={"include": "faces"})
# Find the correct face by direction/center, then delete
manage_probuilder(action="delete_faces", target="Obj", properties={"faceIndices": [correct_index]})
```

### Known Limitations

The following behaviors depend on the installed ProBuilder integration. Check its implementation and the [ProBuilder guide](probuilder-guide.md) before relying on them:

- **`set_pivot`**: Reported vertex positions not persisting through rebuild; use a suitable `center_pivot`/Transform workflow when that limitation applies.
- **`convert_to_probuilder`**: Reported MeshImporter failure; native `create_shape`/`create_poly_shape` remains the alternative.
- **`subdivide`**: The documented behavior is `ConnectElements.Connect`, connecting face midpoints; inspect output rather than assuming traditional quad subdivision.

Read back the mesh/camera state, inspect the visual result and Console, save intended changes, and restore any temporary camera override or preview resources owned by this task.
