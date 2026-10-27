# Unity-MCP Resources Reference

Resources expose read-only observations. Use only the resources relevant to the current task; offline documentation work does not require a live Editor. Read the actual response envelope and freshness fields. The shapes below are templates, not captured results.

## Table of Contents

- [URI Scheme](#uri-scheme)
- [Editor State Resources](#editor-state-resources)
- [Camera Resources](#camera-resources)
- [Graphics Resources](#graphics-resources)
- [Scene & GameObject Resources](#scene--gameobject-resources)
- [Prefab Resources](#prefab-resources)
- [Project Resources](#project-resources)
- [Instance Resources](#instance-resources)
- [Test Resources](#test-resources)
- [Best Practices](#best-practices)

## URI Scheme

All resources use `mcpforunity://` scheme:

```
mcpforunity://{category}/{resource_path}[?query_params]
```

**Categories:** `editor`, `scene`, `prefab`, `project`, `pipeline`, `rendering`, `menu-items`, `custom-tools`, `tool-groups`, `tests`, `instances`

`mcpforunity://path/Assets/...` is accepted by specific script tools as an input URI; this fork does not register a generic file resource for reading arbitrary paths. Read C# content with `manage_script(action="read", ...)`, or another actually advertised content tool.

## Editor State Resources

Read [editor state resources](resources-editor.md#editor-state-resources) for fields, limits and usage.

### mcpforunity://editor/state

[Details](resources-editor.md#mcpforunityeditorstate)

### mcpforunity://editor/selection

[Details](resources-editor.md#mcpforunityeditorselection)

### mcpforunity://editor/active-tool

[Details](resources-editor.md#mcpforunityeditoractive-tool)

### mcpforunity://editor/windows

[Details](resources-editor.md#mcpforunityeditorwindows)

### mcpforunity://editor/prefab-stage

[Details](resources-editor.md#mcpforunityeditorprefab-stage)

## Camera Resources

Read [camera resources](resources-scene.md#camera-resources) for fields, limits and usage.

### mcpforunity://scene/cameras

[Details](resources-scene.md#mcpforunityscenecameras)

## Graphics Resources

Read [graphics resources](resources-scene.md#graphics-resources) for fields, limits and usage.

### mcpforunity://scene/volumes

[Details](resources-scene.md#mcpforunityscenevolumes)

### mcpforunity://rendering/stats

[Details](resources-scene.md#mcpforunityrenderingstats)

### mcpforunity://pipeline/renderer-features

[Details](resources-scene.md#mcpforunitypipelinerenderer-features)

## Scene & GameObject Resources

Read [scene & gameobject resources](resources-scene.md#scene--gameobject-resources) for fields, limits and usage.

### mcpforunity://scene/gameobject-api

[Details](resources-scene.md#mcpforunityscenegameobject-api)

### mcpforunity://scene/gameobject/{instance_id}

[Details](resources-scene.md#mcpforunityscenegameobjectinstance_id)

### mcpforunity://scene/gameobject/{instance_id}/components

[Details](resources-scene.md#mcpforunityscenegameobjectinstance_idcomponents)

### mcpforunity://scene/gameobject/{instance_id}/component/{component_name}

[Details](resources-scene.md#mcpforunityscenegameobjectinstance_idcomponentcomponent_name)

## Prefab Resources

Read [prefab resources](resources-scene.md#prefab-resources) for fields, limits and usage.

### mcpforunity://prefab-api

[Details](resources-scene.md#mcpforunityprefab-api)

### mcpforunity://prefab/{encoded_path}

[Details](resources-scene.md#mcpforunityprefabencoded_path)

### mcpforunity://prefab/{encoded_path}/hierarchy

[Details](resources-scene.md#mcpforunityprefabencoded_pathhierarchy)

## Project Resources

Read [project resources](resources-project-tests.md#project-resources) for fields, limits and usage.

### mcpforunity://project/info

[Details](resources-project-tests.md#mcpforunityprojectinfo)

### mcpforunity://project/tags

[Details](resources-project-tests.md#mcpforunityprojecttags)

### mcpforunity://project/layers

[Details](resources-project-tests.md#mcpforunityprojectlayers)

### mcpforunity://menu-items

[Details](resources-project-tests.md#mcpforunitymenu-items)

### mcpforunity://custom-tools

[Details](resources-project-tests.md#mcpforunitycustom-tools)

### mcpforunity://tool-groups

[Details](resources-project-tests.md#mcpforunitytool-groups)

## Instance Resources

Read [instance resources](resources-project-tests.md#instance-resources) for fields, limits and usage.

### mcpforunity://instances

[Details](resources-project-tests.md#mcpforunityinstances)

## Test Resources

Read [test resources](resources-project-tests.md#test-resources) for fields, limits and usage.

### mcpforunity://tests

[Details](resources-project-tests.md#mcpforunitytests)

### mcpforunity://tests/{mode}

[Details](resources-project-tests.md#mcpforunitytestsmode)

## Best Practices

Read [best practices](resources-editor.md#best-practices) for fields, limits and usage.

### 1. Check Editor State First

[Details](resources-editor.md#1-check-editor-state-first)

### 2. Use Find Then Read Pattern

[Details](resources-editor.md#2-use-find-then-read-pattern)

### 3. Paginate Large Queries

[Details](resources-editor.md#3-paginate-large-queries)

### 4. URL-Encode Prefab Paths

[Details](resources-editor.md#4-url-encode-prefab-paths)

### 5. Multi-Instance Awareness

[Details](resources-editor.md#5-multi-instance-awareness)
