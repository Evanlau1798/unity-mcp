# Unity-MCP Scene / GameObject Tools

Read only the sections needed for the current task. Return to the [tool index](tools-reference.md) for other categories.

All examples are **templates**, not evidence of a live run. Use the installed tool schema and the target project/package versions as authority; native batch commands can differ from their Python wrappers.

## Scene Tools

### manage_scene

Scene CRUD operations, hierarchy queries, multi-scene editing, validation, and scene view control. Screenshots are `manage_camera` actions (see [Camera Tools](tools-camera-graphics.md#camera-tools)); build-settings scenes are `manage_build(action="scenes")`.

`get_hierarchy` queries the active scene, or the current Prefab Stage when one is open. It returns roots when `parent` is omitted, or **direct children** of the given parent. It does not recursively return the entire scene or every loaded scene. `max_depth` maps to the native `maxDepth` compatibility field; the current implementation still pages one level. Traverse selected parents explicitly and follow each page's continuation.

Its response is under `data`: `scope` (`roots`/`children`), `items`, `cursor`, `pageSize`, `next_cursor`, `truncated` and `total`. Follow `next_cursor` (a string offset or null), keeping the same parent. `page_size` defaults to 50 and clamps to 1–500; `max_nodes` defaults to 1,000 and clamps to 1–5,000, limiting the effective page size. `max_children_per_node` defaults to 200 and clamps to 0–2,000 for child summaries; it does not enable recursion.

```python
# Get hierarchy (paginated)
manage_scene(
    action="get_hierarchy",
    page_size=50,                # int, default 50, max 500
    cursor=0,                    # int, pagination cursor
    parent=None,                 # omitted: roots; otherwise: this parent's direct children
    include_transform=False      # bool - include local transforms
)

# Frame scene view on target
manage_scene(
    action="scene_view_frame",
    scene_view_target="Player"   # str|int - GO name, path, or instance ID to frame
)

# Other actions
manage_scene(action="get_active")        # Current scene info
manage_scene(action="get_build_settings") # Build settings
manage_scene(action="create", name="NewScene", path="Assets/Scenes/")
manage_scene(action="load", path="Assets/Scenes/Main.unity")
manage_scene(action="save")

# Scene templates: "empty" (no objects), "default", "3d_basic", "2d_basic"
manage_scene(action="create", name="Level1", path="Assets/Scenes/", template="3d_basic")

# Multi-scene editing
manage_scene(action="load", path="Assets/Scenes/Level2.unity", additive=True)  # keep current scene
manage_scene(action="get_loaded_scenes")
manage_scene(action="set_active_scene", scene_name="Level2")
manage_scene(action="move_to_scene", target="Player", scene_name="Level2")     # moves a root GameObject
manage_scene(action="close_scene", scene_name="Level2", remove_scene=True)     # remove_scene=False only unloads

# Validation: missing scripts, broken prefabs
manage_scene(action="validate")
manage_scene(action="validate", auto_repair=True)  # also removes missing scripts (undoable)
```

### find_gameobjects

Search for GameObjects (returns instance IDs only).

```python
find_gameobjects(
    search_term="Player",        # str, required
    search_method="by_name",     # "by_name"|"by_tag"|"by_layer"|"by_component"|"by_path"|"by_id"
    include_inactive=False,      # bool|str
    page_size=50,                # int, default 50, max 500
    cursor=0                     # int, pagination cursor
)
# Inspect the actual success/data envelope and continuation fields.
```

---

## GameObject Tools

### manage_gameobject

Create, modify, delete, duplicate GameObjects.

**Deletion and target scope:** The current delete handler uses `Object.DestroyImmediate`; it is **not undoable** through Editor Undo. It resolves all matches, so a broad name/tag/layer/component selector can delete more than one object. Inspect exact targets before deletion and use an explicit instance ID/search method when a single active object is intended.

The public `manage_gameobject` schema does not expose `include_inactive`/`searchInactive`. Its standard native resolver defaults `searchInactive` to false, including the normal by-ID path. Finding an inactive object with `find_gameobjects(include_inactive=True)` does not establish that a subsequent modify/delete call will resolve it. Internal helper paths differ; do not invent a public selector or activate an object solely to work around the boundary without considering the intended change.

```python
# Create
manage_gameobject(
    action="create",
    name="MyCube",               # str, required
    primitive_type="Cube",       # "Cube"|"Sphere"|"Capsule"|"Cylinder"|"Plane"|"Quad"
    position=[0, 1, 0],          # list[float] or JSON string "[0,1,0]"
    rotation=[0, 45, 0],         # euler angles
    scale=[1, 1, 1],
    components_to_add=["Rigidbody", "BoxCollider"],
    save_as_prefab=False,
    prefab_path="Assets/Prefabs/MyCube.prefab"
)

# Prefab instantiation — place a prefab instance in the scene
manage_gameobject(
    action="create",
    name="Enemy_1",
    prefab_path="Assets/Prefabs/Enemy.prefab",
    position=[5, 0, 3],
    parent="Enemies"                # optional parent GameObject
)
# Smart lookup — just the prefab name works too:
manage_gameobject(action="create", name="Enemy_2", prefab_path="Enemy", position=[10, 0, 3])

# Modify
manage_gameobject(
    action="modify",
    target="Player",             # name, path, or instance ID
    search_method="by_name",     # how to find target
    position=[10, 0, 0],
    rotation=[0, 90, 0],
    scale=[2, 2, 2],
    set_active=True,
    layer="Player",
    components_to_add=["AudioSource"],
    components_to_remove=["OldComponent"],
    component_properties={       # nested dict for property setting
        "Rigidbody": {
            "mass": 10.0,
            "useGravity": True
        }
    }
)

# Delete an inspected active object; this is not undoable
manage_gameobject(action="delete", target="12345", search_method="by_id")

# Duplicate
manage_gameobject(
    action="duplicate",
    target="Player",
    new_name="Player2",
    offset=[5, 0, 0]             # position offset from original
)

# Move relative
manage_gameobject(
    action="move_relative",
    target="Player",
    reference_object="Enemy",    # reference object for relative movement
    direction="left",            # "left"|"right"|"up"|"down"|"forward"|"back"
    distance=5.0,
    world_space=True
)

# Look at target (rotates GO to face a point or another GO)
manage_gameobject(
    action="look_at",
    target="MainCamera",         # the GO to rotate
    look_at_target="Player",     # str (GO name/path) or list[float] world position
    look_at_up=[0, 1, 0]        # optional up vector, default [0,1,0]
)
```

### manage_components

Add, remove, or set properties on components.

```python
# Add component
manage_components(
    action="add",
    target=12345,                # instance ID (preferred) or name
    component_type="Rigidbody",
    search_method="by_id"
)

# Remove component
manage_components(
    action="remove",
    target="Player",
    component_type="OldScript"
)

# Set single property
manage_components(
    action="set_property",
    target=12345,
    component_type="Rigidbody",
    property="mass",
    value=5.0
)

# Set multiple properties
manage_components(
    action="set_property",
    target=12345,
    component_type="Transform",
    properties={
        "position": [1, 2, 3],
        "localScale": [2, 2, 2]
    }
)

# Set object reference property (reference another GameObject by name)
manage_components(
    action="set_property",
    target="GameManager",
    component_type="GameManagerScript",
    property="targetObjects",
    value=[{"name": "Flower_1"}, {"name": "Flower_2"}, {"name": "Bee_1"}]
)

# Object reference formats supported:
# - {"name": "ObjectName"}     → Find GameObject in scene by name
# - {"instanceID": 12345}      → Direct instance ID reference
# - {"guid": "abc123..."}      → Asset GUID reference
# - {"path": "Assets/..."}     → Asset path reference
# - "Assets/Prefabs/My.prefab" → String shorthand for asset paths
# - "ObjectName"               → String shorthand for scene name lookup
# - 12345                      → Integer shorthand for instanceID
# - {"guid": "...", "spriteName": "Hero_0"} or {"guid": "...", "fileID": 12345}
#                              → Sprite sub-asset of a sliced sheet (single-sprite textures resolve from guid/path alone)
```
