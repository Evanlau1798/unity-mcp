# Unity-MCP Asset Generation Tools

Read only the sections needed for the current task. Return to the [tool index](tools-reference.md) for other categories.

All examples are **templates**, not evidence of a live run. Use the installed tool schema and the target project/package versions as authority; native batch commands can differ from their Python wrappers.

## Asset Generation Tools

Group: `asset_gen`. Bring your own key: provider keys are entered in the Unity editor (MCP for Unity window, Generative tab) and never cross the bridge; `list_providers` shows which are configured. Generation and Sketchfab imports are async: `generate`/`import` return a `job_id`, `status` reports progress and then the imported `assetPath`, and `cancel` stops the job.

### generate_image

Text-to-image or image-to-image with fal.ai or OpenRouter, imported as a texture or sprite. Actions: `generate`, `status`, `cancel`, `list_providers`, `list_models`, `refresh_models` (`remove_background` is not supported yet). `generate` takes `provider` (`fal`, `openrouter`), `mode` (`text` or `image`), `prompt` or `image_path`/`image_url`, and optional `model` (from `list_models`), `width`, `height`, `transparent`, `name`, `output_folder`.

```python
generate_image(action="generate", provider="fal", mode="text", prompt="mossy stone wall, hand-painted",
               width=1024, height=1024, output_folder="Assets/Generated")
```

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/asset_gen/generate_image)

### generate_model

Text-to-3D or image-to-3D with Tripo, Meshy or fal, imported as a model. Actions as `generate_image`, without `remove_background`. `generate` takes `provider` (`tripo`, `meshy`, `fal`; fal outputs GLB), `mode`, `prompt` or an image, and optional `format` (`glb`, `fbx`, `obj`, `usdz`), `target_size` (meters, largest side), `texture`, `tier`, `model`.

```python
generate_model(action="generate", provider="tripo", mode="text", prompt="low-poly treasure chest",
               format="glb", target_size=1.0)
```

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/asset_gen/generate_model)

### generate_audio

Sound effects and music from a text prompt with fal.ai, imported as an AudioClip. Actions as `generate_model`. `generate` takes `prompt` and optional `model` and `duration` (seconds, clamped per model).

```python
generate_audio(action="generate", provider="fal", prompt="short bright coin pickup chime", duration=2)
```

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/asset_gen/generate_audio)

### import_model

Search Sketchfab and import a downloadable model. Actions: `search` (`query`, `categories`, `downloadable`, `count`, `cursor`), `preview` (`uid`: license, face counts, thumbnails), `import` (`uid`, `target_size`, `name`, `output_folder`), `status`, `cancel`, `list_providers`.

```python
import_model(action="search", query="wooden barrel", downloadable=True, count=5)
import_model(action="import", uid="<uid from search>", target_size=1.0)
```

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/asset_gen/import_model)

### import_model_file

Imports a model file already on disk (`.fbx`, `.obj`, `.glb`, `.gltf`, or a `.zip` holding a model and its sidecar files) through the import pipeline the generators use. No `action` parameter and no key. A rigged FBX/OBJ needs `animation_type="generic"` or `"humanoid"`, or it imports without clips; glTF needs the glTFast package.

```python
import_model_file(source_path="C:/Exports/robot.fbx", animation_type="humanoid", target_size=1.8)
```

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/asset_gen/import_model_file)

### blender_bridge

Drives a running Blender that has the BlenderMCP addon connected (default `127.0.0.1:9876`) directly from Unity. Actions: `status`, `scene_info`, `object_info`, `screenshot`, `run_python`, `import_model`, `compare_screenshot`, `setup_bloom`, `check_updates`, `sync_addon`. `import_model` exports from Blender (glb by default), imports the file, places it at `position`, and scales its largest side to `target_size` meters.

```python
blender_bridge(action="import_model", object_names=["Robot"], target_size=2.0, position=[0, 0, 0])
```

[Full parameters](https://coplaydev.github.io/unity-mcp/reference/tools/asset_gen/blender_bridge)
