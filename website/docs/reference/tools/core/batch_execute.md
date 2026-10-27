---
title: batch_execute
sidebar_label: batch_execute
description: "Executes native Unity commands sequentially on the Editor main thread in one request."
---

# `batch_execute`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.batch_execute`

## Description

Executes native Unity commands sequentially on the Editor main thread in one request. Only native command handlers are dispatched; Python tool wrapper encoding, preflight and recovery do not run per command. Non-transactional: no rollback; inspect partial results before retrying. Only top-level parameter keys are normalized; nested property keys are preserved. The current Editor limit is read each call: default 25, configurable 1–100, hard maximum 100. fail_fast defaults to false. parallel is accepted for compatibility but parallelApplied is false.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `commands` | `list[dict[str, Any]]` | yes | List of commands with 'tool' and 'params' keys. |
| `parallel` | `bool \| None` | — | Compatibility hint only; native commands run sequentially (parallelApplied=false) |
| `fail_fast` | `bool \| None` | — | Stop processing after the first failure (default false); earlier effects remain |
| `max_parallelism` | `int \| None` | — | Compatibility hint; no parallel workers are used |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
### Why batch?

A batch groups multiple native Unity commands into one dispatch, reducing per-call overhead. Use it when the next command doesn't need a previous command's return value; actual performance depends on the work Unity performs.

### Spawn three colored cubes in one round trip

> Create a red, blue, and yellow cube at x = -1, 0, 1.

```json
{
  "commands": [
    { "tool": "manage_gameobject", "params": {
        "action": "create", "name": "RedCube",
        "primitive_type": "Cube", "position": [-1, 0, 0]
    }},
    { "tool": "manage_gameobject", "params": {
        "action": "create", "name": "BlueCube",
        "primitive_type": "Cube", "position": [0, 0, 0]
    }},
    { "tool": "manage_gameobject", "params": {
        "action": "create", "name": "YellowCube",
        "primitive_type": "Cube", "position": [1, 0, 0]
    }},
    { "tool": "manage_material", "params": {
        "action": "create", "material_path": "Materials/Red.mat",
        "shader": "Standard", "properties": { "_Color": [1, 0, 0, 1] }
    }},
    { "tool": "manage_material", "params": {
        "action": "create", "material_path": "Materials/Blue.mat",
        "shader": "Standard", "properties": { "_Color": [0, 0, 1, 1] }
    }},
    { "tool": "manage_material", "params": {
        "action": "create", "material_path": "Materials/Yellow.mat",
        "shader": "Standard", "properties": { "_Color": [1, 1, 0, 1] }
    }},
    { "tool": "manage_material", "params": {
        "action": "assign_material_to_renderer", "target": "RedCube",
        "search_method": "by_name", "material_path": "Materials/Red.mat"
    }},
    { "tool": "manage_material", "params": {
        "action": "assign_material_to_renderer", "target": "BlueCube",
        "search_method": "by_name", "material_path": "Materials/Blue.mat"
    }},
    { "tool": "manage_material", "params": {
        "action": "assign_material_to_renderer", "target": "YellowCube",
        "search_method": "by_name", "material_path": "Materials/Yellow.mat"
    }}
  ]
}
```

### Mix native commands

A batch can mix registered native commands. It does not call the individual Python tool wrappers, so their parameter encoding, preflight and recovery logic do not run. Only top-level parameter keys are normalized; nested property keys are preserved. Keep script mutations and other operations that require wrapper handling in their dedicated tools. If step N's response must feed step N+1, split the calls.

### Stop on first failure vs continue

`fail_fast` defaults to `false`: every command is attempted and per-command results are collected. Set `fail_fast: true` to stop after the first failure. A batch is not a transaction and has no rollback: earlier successful changes remain applied. Inspect `data.results` and each `callSucceeded` before deciding what to do next, including when the overall response reports failure.

### Execution order

All commands run sequentially on Unity's main thread, including reads. `parallel` and `max_parallelism` are accepted compatibility hints; `data.parallelApplied` is always `false`.

### Limits

Batch size is configurable from 1–100 in the Unity MCP Tools window (default 25, hard max 100). Past the current limit, split into multiple batches. Route the whole batch with the outer `unity_instance`; per-command instance routing is rejected.
<!-- examples:end -->

