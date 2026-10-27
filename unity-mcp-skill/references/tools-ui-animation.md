# Unity-MCP UI / Animation / VFX Tools

Read only the sections needed for the current task. Return to the [tool index](tools-reference.md) for other categories.

All examples are **templates**, not evidence of a live run. Use the installed tool schema and the target project/package versions as authority; native batch commands can differ from their Python wrappers.

## UI Tools

### manage_ui

Manage Unity UI Toolkit elements: UXML documents, USS stylesheets, UIDocument components, and visual tree inspection. Group: `ui`.

```python
# Create a UXML file
manage_ui(
    action="create",
    path="Assets/UI/MainMenu.uxml",
    contents='<ui:UXML xmlns:ui="UnityEngine.UIElements"><ui:Label text="Hello" /></ui:UXML>'
)

# Create a USS stylesheet
manage_ui(
    action="create",
    path="Assets/UI/Styles.uss",
    contents=".title { font-size: 32px; color: white; }"
)

# Read a UXML/USS file
manage_ui(
    action="read",
    path="Assets/UI/MainMenu.uxml"
)
# Returns: {"success": true, "data": {"contents": "...", "path": "..."}}

# Update an existing file
manage_ui(
    action="update",
    path="Assets/UI/Styles.uss",
    contents=".title { font-size: 48px; color: yellow; -unity-font-style: bold; }"
)

# Attach UIDocument to a GameObject
manage_ui(
    action="attach_ui_document",
    target="UICanvas",                    # GameObject name or path
    source_asset="Assets/UI/MainMenu.uxml",
    panel_settings="Assets/UI/Panel.asset",  # optional, auto-creates if omitted
    sort_order=0                          # optional, default 0
)

# Create PanelSettings asset
manage_ui(
    action="create_panel_settings",
    path="Assets/UI/Panel.asset",
    scale_mode="ScaleWithScreenSize",     # optional: "ConstantPixelSize"|"ConstantPhysicalSize"|"ScaleWithScreenSize"
    reference_resolution={"width": 1920, "height": 1080}  # optional, for ScaleWithScreenSize
)

# Inspect the visual tree of a UIDocument
manage_ui(
    action="get_visual_tree",
    target="UICanvas",                    # GameObject with UIDocument
    max_depth=10                          # optional, default 10
)
# Returns: hierarchy of VisualElements with type, name, classes, styles, text, children
```

**UI Toolkit workflow:**

1. Create UXML (structure, like HTML) and USS (styling, like CSS) files
2. Create a PanelSettings asset (or let `attach_ui_document` auto-create one)
3. Create an empty GameObject and attach UIDocument with the UXML source
4. Use `get_visual_tree` to inspect the result

**Important:** Always use `<ui:Style>` (with the `ui:` namespace prefix) in UXML files, not bare `<Style>`. UI Builder will fail to open files that use `<Style>` without the prefix.

---

## Animation Tools

Group: `animation`.

### manage_animation

Animator control, AnimatorController authoring, and AnimationClip creation. Actions by prefix:

- `animator_*` (a scene object's Animator): `get_info`, `get_parameter`, `play`, `crossfade`, `set_parameter`, `set_speed`, `set_enabled`
- `controller_*` (`.controller` assets): `create`, `get_info`, `assign`, `add_state`, `add_transition`, `add_parameter`, `add_layer`, `remove_layer`, `set_layer_weight`, `create_blend_tree_1d`, `create_blend_tree_2d`, `add_blend_tree_child`
- `clip_*` (`.anim` assets): `create`, `get_info`, `add_curve`, `set_curve`, `set_vector_curve`, `create_preset`, `assign`, `add_event`, `remove_event`

Top-level parameters are `target`, `search_method`, `clip_path` and `controller_path`; everything else goes in `properties` (camelCase or snake_case keys).

```python
manage_animation(action="controller_add_transition", controller_path="Assets/Animators/Player.controller",
                 properties={"fromState": "Idle", "toState": "Walk", "hasExitTime": False,
                             "conditions": [{"parameter": "Speed", "mode": "greater", "threshold": 0.1}]})
```

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/animation/manage_animation)

### manage_sprite

Turns a 2D sprite sheet into sliced sprites, AnimationClips and an AnimatorController. Actions: `get_info`, `slice_sheet`, `setup_clips`, `setup_controller`, `full_setup`.

- `get_info` returns the sheet's import settings and slices and, for PNG/JPEG, the sheet as an image: count the grid from it before slicing.
- `slice_sheet` cuts a grid (`cols`/`rows` or `frame_width`/`frame_height`) and replaces the sheet's existing slices; `filter_mode` defaults to `point`.
- `full_setup` runs slice, clips, controller. Clip names decide the controller: `idle` is the default state, `walk`/`run` share a `Speed` blend tree, `jump`/`attack`/`hurt`-type names become trigger states.
- Without `overwrite=True`, an existing clip is skipped and an existing controller fails the call.

```python
manage_sprite(action="full_setup", path="Assets/Sprites/hero.png", cols=6, rows=2,
              clips=[{"name": "idle", "start_frame": 0, "end_frame": 5},
                     {"name": "walk", "start_frame": 6, "end_frame": 11}],
              add_to_scene=True, scene_target="Hero")
```

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/animation/manage_sprite)

---

## VFX Tools

### manage_vfx

ParticleSystem, VisualEffect (VFX Graph), LineRenderer and TrailRenderer components. Group: `vfx`. Action prefixes: `particle_*` (create, modules, bursts, playback), `vfx_*` (VFX Graph assets, exposed properties, events; needs `com.unity.visualeffectgraph`), `line_*` (positions, width, color, and shapes such as `line_create_circle`), `trail_*`; plus `ping`. Settings go in `properties` (camelCase or snake_case keys); `component_index` picks one of several components of the same type.

```python
manage_vfx(action="particle_create", target="Sparks", properties={"position": [0, 1, 0]})  # creates the GameObject if missing
manage_vfx(action="particle_set_main", target="Sparks",
           properties={"startLifetime": 0.8, "startSpeed": 6, "startSize": 0.1, "maxParticles": 200})
```

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/vfx/manage_vfx)
