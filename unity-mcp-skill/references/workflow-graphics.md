# Graphics and rendering workflows

[Workflow index](workflows.md). Read this reference only for the matching task.

> **Template:** Adapt object names, paths, component properties, and package-dependent values to the current project. Verify the template against the installed schema and project before use.

Before mutations, use [setup/readiness](workflow-setup.md). Confirm the installed render pipeline and available effects/features. Numeric enum values and settings are templates: inspect them through the live API/current components before reuse. Record existing shared settings and only restore temporary changes that this task owns.

## Graphics & Rendering Workflows

### Setting Up Post-Processing

Add post-processing effects to a URP/HDRP scene using Volumes.

```python
# 1. Check pipeline status and available effects
manage_graphics(action="ping")

# 2. List available volume effects for the active pipeline
manage_graphics(action="volume_list_effects")

# 3. Create a global post-processing volume with common effects
manage_graphics(action="volume_create", name="GlobalPostProcess", is_global=True,
    effects=[
        {"type": "Bloom", "parameters": {"intensity": 1.0, "threshold": 0.9, "scatter": 0.7}},
        {"type": "Vignette", "parameters": {"intensity": 0.35}},
        {"type": "Tonemapping", "parameters": {"mode": 1}},
        {"type": "ColorAdjustments", "parameters": {"postExposure": 0.2, "contrast": 10}}
    ])

# 4. Verify the volume was created
# Read mcpforunity://scene/volumes

# 5. Fine-tune an effect parameter
manage_graphics(action="volume_set_effect", target="GlobalPostProcess",
    effect="Bloom", parameters={"intensity": 1.5})

# 6. Screenshot to verify visual result
manage_camera(action="screenshot", include_image=True, max_resolution=512)
```

**Tips:**
- Always `ping` first to confirm URP/HDRP is active. Volumes do nothing on Built-in RP.
- Use `volume_list_effects` to discover available effect types for the active pipeline (URP and HDRP have different sets).
- Use `volume_get_info` to inspect current effect parameters before modifying.
- Create a reusable VolumeProfile asset with `volume_create_profile` and reference it via `profile_path` on multiple volumes.

### Adding a Full-Screen Effect via Renderer Features (URP)

Add a custom full-screen shader pass using URP Renderer Features.

```python
# 1. Check pipeline and confirm URP
manage_graphics(action="ping")

# 2. Create a material for the full-screen effect
manage_material(action="create",
    material_path="Assets/Materials/GrayscaleEffect.mat",
    shader="Shader Graphs/GrayscaleFullScreen")

# 3. List current renderer features
manage_graphics(action="feature_list")

# 4. Add a FullScreenPassRendererFeature with the material
manage_graphics(action="feature_add",
    feature_type="FullScreenPassRendererFeature",
    name="GrayscalePass",
    material="Assets/Materials/GrayscaleEffect.mat")

# 5. Verify it was added; select effect_index from this actual list.
manage_graphics(action="feature_list")

# 6. Toggle it on/off to compare
manage_graphics(action="feature_toggle", index=effect_index, active=False)  # observed new feature index
manage_camera(action="screenshot", include_image=True, max_resolution=512)

manage_graphics(action="feature_toggle", index=effect_index, active=True)   # re-enable
manage_camera(action="screenshot", include_image=True, max_resolution=512)

# 7. Reorder features if needed (execution order matters)
# desired_current_index_order is a permutation of the actual current features.
manage_graphics(action="feature_reorder", order=desired_current_index_order)
```

**Tips:**
- Renderer Features are URP-only. `feature_*` actions return an error on HDRP or Built-in RP.
- Read `mcpforunity://pipeline/renderer-features` to inspect features without modifying.
- Feature execution order affects the final image. Use `feature_reorder` to control pass ordering.

### Configuring Light Baking

Set up lightmaps, light probes, and reflection probes for baked GI.

```python
# 1. Set lights to Baked or Mixed mode
manage_components(action="set_property", target="Directional Light",
    component_type="Light", properties={"lightmapBakeType": 1})  # 1 = Mixed

# 2. Inspect the geometry's static flags and set ContributeGI through a verified
# Editor API if needed. StaticFlags is not a component-properties payload.
# manage_gameobject(is_static=True) sets ALL static flags, so it is not a
# selective ContributeGI setter; preserve unrelated flags when using an API edit.

# 3. Configure lightmap settings
manage_graphics(action="bake_get_settings")
manage_graphics(action="bake_set_settings", settings={
    "lightmapper": observed_gpu_lightmapper_enum,  # verify the installed enum value
    "directSamples": 32,
    "indirectSamples": 128,
    "maxBounces": 4,
    "lightmapResolution": 40
})

# 4. Place light probes for dynamic objects
manage_graphics(action="bake_create_light_probe_group", name="MainProbeGrid",
    position=[0, 1.5, 0], grid_size=[5, 3, 5], spacing=3.0)

# 5. Place a reflection probe for an interior room
manage_graphics(action="bake_create_reflection_probe", name="RoomReflection",
    position=[0, 2, 0], size=[8, 4, 8], resolution=256,
    hdr=True, box_projection=True)

# 6. Start async bake
manage_graphics(action="bake_start", async_bake=True)

# 7. Poll bake status
manage_graphics(action="bake_status")
# Continue bounded observation of this bake until it actually finishes.
# A wait timeout is not a reason to launch a second bake.

# 8. Bake the reflection probe separately if needed
manage_graphics(action="bake_reflection_probe", target="RoomReflection")

# 9. Check rendering stats after bake
manage_graphics(action="stats_get")
```

**Tips:**
- Baking only works in Edit mode. If the editor is in Play mode, `bake_start` will fail.
- Use `bake_cancel` to abort a long bake.
- `bake_clear` removes baked data (lightmaps/probes); use only when the requested rebake workflow requires that removal.
- For large scenes, use `async_bake=True` (default) and poll `bake_status` periodically.

## Verify and finish

Read back configured volumes/features/bake settings, inspect Console and the intended visual result, and save intentional assets/scene changes. If a bake is cancelled, observe its exit before dependent mutations. Restore temporary comparison settings and remove only task-owned preview assets; do not clear unrelated baked data as cleanup.
