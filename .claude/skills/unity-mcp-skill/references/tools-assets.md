# Unity-MCP Asset / Material & Shader Tools

Read only the sections needed for the current task. Return to the [tool index](tools-reference.md) for other categories.

All examples are **templates**, not evidence of a live run. Use the installed tool schema and the target project/package versions as authority; native batch commands can differ from their Python wrappers.

## Asset Tools

### manage_asset

Asset operations: search, import, create, modify, delete.

```python
# Search assets (paginated)
manage_asset(
    action="search",
    path="Assets",               # search scope
    search_pattern="*.prefab",   # glob or "t:MonoScript" filter
    filter_type="Prefab",        # optional type filter
    page_size=25,                # keep small to avoid large payloads
    page_number=1,               # 1-based
    generate_preview=False       # avoid base64 bloat
)

# Get asset info
manage_asset(action="get_info", path="Assets/Prefabs/Player.prefab")

# Create asset
manage_asset(
    action="create",
    path="Assets/Materials/NewMaterial.mat",
    asset_type="Material",
    properties={"color": [1, 0, 0, 1]}
)

# Duplicate/move/rename
manage_asset(action="duplicate", path="Assets/A.prefab", destination="Assets/B.prefab")
manage_asset(action="move", path="Assets/A.prefab", destination="Assets/Prefabs/A.prefab")
manage_asset(action="rename", path="Assets/A.prefab", destination="Assets/B.prefab")

# Create folder
manage_asset(action="create_folder", path="Assets/NewFolder")

# Delete
manage_asset(action="delete", path="Assets/OldAsset.asset")
```

### manage_prefabs

Prefab assets: headless edits with `modify_contents`, or interactive editing in a prefab stage.

```python
# Get prefab info
manage_prefabs(action="get_info", prefab_path="Assets/Prefabs/Player.prefab")

# Get prefab hierarchy
manage_prefabs(action="get_hierarchy", prefab_path="Assets/Prefabs/Player.prefab")

# Create prefab from scene GameObject
manage_prefabs(
    action="create_from_gameobject",
    target="Player",             # GameObject in scene
    prefab_path="Assets/Prefabs/Player.prefab",
    allow_overwrite=False
)

# Modify prefab contents (headless)
manage_prefabs(
    action="modify_contents",
    prefab_path="Assets/Prefabs/Player.prefab",
    target="ChildObject",        # object within prefab
    position=[0, 1, 0],
    components_to_add=["AudioSource"]
)

# Delete child GameObjects from prefab
manage_prefabs(
    action="modify_contents",
    prefab_path="Assets/Prefabs/Player.prefab",
    delete_child=["OldChild", "Turret/Barrel"]  # single string or list
)

# Create child GameObject in prefab
manage_prefabs(
    action="modify_contents",
    prefab_path="Assets/Prefabs/Player.prefab",
    create_child={"name": "SpawnPoint", "primitive_type": "Sphere", "position": [0, 2, 0]}
)
# A list creates several children in one save; "source_prefab_path" instead of
# "primitive_type" nests a prefab instance: {"name": "Gun", "source_prefab_path": "Assets/Prefabs/Gun.prefab"}

# Set component properties on prefab contents
manage_prefabs(
    action="modify_contents",
    prefab_path="Assets/Prefabs/Player.prefab",
    target="ChildObject",
    component_properties={"Rigidbody": {"mass": 5.0}, "MyScript": {"health": 100}}
)

# Interactive: open a prefab stage, edit with manage_gameobject/manage_components, save, close
manage_prefabs(action="open_prefab_stage", prefab_path="Assets/Prefabs/Enemy.prefab")
manage_prefabs(action="save_prefab_stage")
manage_prefabs(action="close_prefab_stage")  # back to the main scene
```

---

## Material & Shader Tools

### manage_material

Create and modify materials.

```python
# Create material
manage_material(
    action="create",
    material_path="Assets/Materials/Red.mat",
    shader="Standard",
    properties={"_Color": [1, 0, 0, 1]}
)

# Get material info
manage_material(action="get_material_info", material_path="Assets/Materials/Red.mat")

# Set shader property
manage_material(
    action="set_material_shader_property",
    material_path="Assets/Materials/Red.mat",
    property="_Metallic",
    value=0.8
)

# Set color
manage_material(
    action="set_material_color",
    material_path="Assets/Materials/Red.mat",
    property="_BaseColor",
    color=[0, 1, 0, 1]           # RGBA
)

# Assign to renderer
manage_material(
    action="assign_material_to_renderer",
    target="MyCube",
    material_path="Assets/Materials/Red.mat",
    slot=0                       # material slot index
)

# Set renderer color directly
manage_material(
    action="set_renderer_color",
    target="MyCube",
    color=[1, 0, 0, 1],
    mode="create_unique"          # Creates a unique .mat asset per object (persistent)
    # Other modes: "property_block" (default, not persistent),
    #              "shared" (mutates shared material — avoid for primitives),
    #              "instance" (runtime only, not persistent)
)
```

### manage_texture

Create procedural textures. Group: `vfx`.

```python
manage_texture(
    action="create",
    path="Assets/Textures/Checker.png",
    width=64,
    height=64,
    fill_color=[255, 255, 255, 255]  # or [1.0, 1.0, 1.0, 1.0]
)

# Apply pattern
manage_texture(
    action="apply_pattern",
    path="Assets/Textures/Checker.png",
    pattern="checkerboard",      # "checkerboard"|"stripes"|"dots"|"grid"|"brick"
    palette=[[0,0,0,255], [255,255,255,255]],
    pattern_size=8
)

# Apply gradient
manage_texture(
    action="apply_gradient",
    path="Assets/Textures/Gradient.png",
    gradient_type="linear",      # "linear"|"radial"
    gradient_angle=45,
    palette=[[255,0,0,255], [0,0,255,255]]
)
```

### manage_shader

Create, read, update, or delete `.shader` files. Group: `vfx`. `name` is the file name without `.shader`; required `path` is its **directory** under `Assets/`, such as `Assets/Shaders`, rather than the full file path. Supply complete shader source in `contents` for create/update; choosing a shader name does not generate source. The Python wrapper handles Base64 transport encoding. Read decoded source from `data.contents`. To use the shader, pass the name declared in its `Shader "..."` line to `manage_material`.

```python
manage_shader(action="create", name="UnlitTint", path="Assets/Shaders",
              contents=shader_source)  # complete source for the target render pipeline
```

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/vfx/manage_shader)
