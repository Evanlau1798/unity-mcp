# Scene and asset workflows

[Workflow index](workflows.md). Read this reference only for the matching task.

> **Template:** Adapt object names, paths, component properties, and package-dependent values to the current project. Verify the template against the installed schema and project before use.

Before mutation, use [setup/readiness](workflow-setup.md) and inspect the intended scene, object names, asset paths, and installed render pipeline. Each example is a separate task template, not a sequence to run wholesale. For every batch, use the current configured command cap, inspect per-command results, and continue only after its prerequisites succeeded; see [batch contracts](workflow-batch-recovery.md#batch-operations).

## Scene Generator Build Workflow

### Fresh Scene Before Building

For an explicitly requested new generated scene, create a fresh scene after resolving the current scene's unsaved changes within the task's authorization. When modifying an existing scene, inspect and reuse that scene instead. A fresh scene avoids collisions with existing Camera/Light objects; do not assume it contains either object.

```python
# Step 0: For an authorized new scene, after handling current unsaved changes
manage_scene(action="create", name="MyGeneratedScene", path="Assets/Scenes/")

# Now proceed with the phased execution plan...
# Phase 1: Environment (camera, lights) — no conflicts
# Phase 2: Objects (GameObjects)
# Phase 3: Materials
# etc.
```

### Wiring Object References Between Components

After successful script import/compilation and component attachment, inspect the serialized field type and wire cross-references with `set_property`. The named-reference format below requires unique names; use observed IDs or full paths for ambiguous targets and read back the resolved references:

```python
# Wire a list of target GameObjects into a script's serialized field
manage_components(
    action="set_property",
    target="BeeManager",
    component_type="BeeManagerScript",
    property="targetObjects",
    value=[{"name": "Flower_1"}, {"name": "Flower_2"}, {"name": "Flower_3"}]
)
```

### Physics Requirements for Trigger-Based Interactions

For a 3D trigger interaction, inspect both colliders, the trigger flag, layer collision settings, and Rigidbody setup. The Rigidbody example below supplies one part of that configuration; it does not establish that callbacks fire. The 2D physics path uses different components/APIs.

```python
# Moving objects (bees, players) need Rigidbody for triggers to fire
batch_execute(fail_fast=True, commands=[
    {"tool": "manage_components", "params": {
        "action": "add", "target": "Bee_1", "component_type": "Rigidbody"
    }},
    {"tool": "manage_components", "params": {
        "action": "set_property", "target": "Bee_1",
        "component_type": "Rigidbody",
        "properties": {"useGravity": False, "isKinematic": True}
    }}
])
```

## Scene Creation Workflows

### Create Complete Scene from Scratch

```python
# 1. Create new scene
manage_scene(action="create", name="GameLevel", path="Assets/Scenes/")

# 2. Batch create environment objects
batch_execute(commands=[
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "Ground", "primitive_type": "Plane",
        "position": [0, 0, 0], "scale": [10, 1, 10]
    }},
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "Light", "primitive_type": "Cube"
    }},
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": "Player", "primitive_type": "Capsule",
        "position": [0, 1, 0]
    }}
])

# 3. Add light component (delete cube mesh, add light)
manage_components(action="remove", target="Light", component_type="MeshRenderer")
manage_components(action="remove", target="Light", component_type="MeshFilter")
manage_components(action="remove", target="Light", component_type="BoxCollider")
manage_components(action="add", target="Light", component_type="Light")
manage_components(action="set_property", target="Light", component_type="Light",
    property="type", value="Directional")

# 4. Create the camera in the new scene, then position it
manage_camera(action="create_camera", properties={"name": "Main Camera"})
manage_gameobject(action="modify", target="Main Camera", position=[0, 5, -10],
    rotation=[30, 0, 0])

# 5. Verify with screenshot
manage_camera(action="screenshot")

# 6. Save scene
manage_scene(action="save")
```

### Populate Scene with Grid of Objects

```python
# Create 5x5 grid of cubes using batch
commands = []
for x in range(5):
    for z in range(5):
        commands.append({
            "tool": "manage_gameobject",
            "params": {
                "action": "create",
                "name": f"Cube_{x}_{z}",
                "primitive_type": "Cube",
                "position": [x * 2, 0, z * 2]
            }
        })

# batch_limit is the observed configured cap; default 25, range 1–100.
for i in range(0, len(commands), batch_limit):
    report = batch_execute(commands=commands[i:i + batch_limit])
    # Inspect actual successes before retrying or continuing.
```

### Clone and Arrange Objects

```python
# Find template object
result = find_gameobjects(search_term="Template", search_method="by_name")
# Select template_id from the actual response after resolving ambiguity.
# Inspect the source object before cloning it.

# Duplicate in a line
for i in range(10):
    manage_gameobject(
        action="duplicate",
        target=str(template_id),
        new_name=f"Instance_{i}",
        offset=[i * 2, 0, 0]
    )
```

## Asset Management Workflows

### Create and Apply Material

This `Standard` shader/property example targets the Built-in pipeline. For URP/HDRP, inspect the actual shader and its property names first; do not substitute pipeline-specific names by guesswork.

```python
# 1. Create material
manage_material(
    action="create",
    material_path="Assets/Materials/PlayerMaterial.mat",
    shader="Standard",
    properties={
        "_Color": [0.2, 0.5, 1.0, 1.0],
        "_Metallic": 0.5,
        "_Glossiness": 0.8
    }
)

# 2. Assign to renderer
manage_material(
    action="assign_material_to_renderer",
    target="Player",
    material_path="Assets/Materials/PlayerMaterial.mat",
    slot=0
)

# 3. Verify visually
manage_camera(action="screenshot")
```

### Create Procedural Texture

```python
# 1. Create base texture
manage_texture(
    action="create",
    path="Assets/Textures/Checkerboard.png",
    width=256,
    height=256,
    fill_color=[255, 255, 255, 255]
)

# 2. Apply checkerboard pattern
manage_texture(
    action="apply_pattern",
    path="Assets/Textures/Checkerboard.png",
    pattern="checkerboard",
    palette=[[0, 0, 0, 255], [255, 255, 255, 255]],
    pattern_size=32
)

# 3. Create material with texture
manage_material(
    action="create",
    material_path="Assets/Materials/CheckerMaterial.mat",
    shader="Standard"
)

# 4. Inspect the material's actual texture property, then assign the generated
# texture using manage_material(action="set_material_shader_property", ...).
# Use its installed schema for the texture-reference payload, then assign the
# material to the intended renderer and verify the visible result.
```

### Organize Assets into Folders

```python
# 1. Create folder structure
batch_execute(commands=[
    {"tool": "manage_asset", "params": {"action": "create_folder", "path": "Assets/Prefabs"}},
    {"tool": "manage_asset", "params": {"action": "create_folder", "path": "Assets/Materials"}},
    {"tool": "manage_asset", "params": {"action": "create_folder", "path": "Assets/Scripts"}},
    {"tool": "manage_asset", "params": {"action": "create_folder", "path": "Assets/Textures"}}
])

# 2. Move existing assets
manage_asset(action="move", path="Assets/MyMaterial.mat", destination="Assets/Materials/MyMaterial.mat")
manage_asset(action="move", path="Assets/MyScript.cs", destination="Assets/Scripts/MyScript.cs")
```

### Search and Process Assets

```python
# Find all prefabs
result = manage_asset(
    action="search",
    path="Assets",
    search_pattern="*.prefab",
    page_size=50,
    generate_preview=False
)

# Inspect the actual asset response and follow its pagination.
# selected_prefab_paths contains the intended paths from those results.
for prefab_path in selected_prefab_paths:
    info = manage_prefabs(action="get_info", prefab_path=prefab_path)
    # Inspect the actual hierarchy/details before deciding on any modification.
```

### Instantiate Prefab in Scene

Use `manage_gameobject` to place prefab instances in the scene. Confirm the prefab asset and intended parent exist. Prefer the full asset path; a short-name lookup is useful only when it resolves unambiguously. `manage_prefabs` handles prefab inspection/content editing instead.

```python
# Full path
manage_gameobject(
    action="create",
    name="Enemy_1",
    prefab_path="Assets/Prefabs/Enemy.prefab",
    position=[5, 0, 3],
    parent="Enemies"
)

# Smart lookup — just the prefab name works too
manage_gameobject(action="create", name="Enemy_2", prefab_path="Enemy", position=[10, 0, 3])

# Batch-spawn multiple instances
batch_execute(commands=[
    {"tool": "manage_gameobject", "params": {
        "action": "create", "name": f"Enemy_{i}",
        "prefab_path": "Enemy", "position": [i * 3, 0, 0], "parent": "Enemies"
    }}
    for i in range(5)
])
```

> **Note:** `manage_prefabs` is for headless prefab editing (inspect, modify contents, create from GameObject). To *instantiate* a prefab into the scene, always use `manage_gameobject(action="create", prefab_path="...")`.

## Verify and finish

Read back object/component references, transforms, asset paths, and material assignments changed by the selected procedure. Check relevant Console output and capture visuals when appearance matters. Save intentional scene/asset changes. Clean up only temporary resources created and still owned by this task, after native work releases them; retain user objects and unrelated dirty changes.
