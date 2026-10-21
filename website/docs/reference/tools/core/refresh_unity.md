---
title: refresh_unity
sidebar_label: refresh_unity
description: "Request a tracked Unity refresh and optionally compilation."
---

# `refresh_unity`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.refresh_unity`

## Description

Request a tracked Unity refresh and optionally compilation. Completion requires a matching native receipt; recover with the same operation_id.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `mode` | `Literal['if_dirty', 'force']` | — | Refresh mode |
| `scope` | `Literal['assets', 'scripts', 'all']` | — | Refresh scope |
| `compile` | `Literal['none', 'request']` | — | Whether to request compilation |
| `wait_for_ready` | `bool` | — | Wait for this refresh operation; timeout keeps operation_id for recovery. |
| `operation_id` | `str \| None` | — | Query an existing refresh operation without starting another. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
### Refresh a batch of external changes

```json
{"mode": "if_dirty", "scope": "all", "compile": "none", "wait_for_ready": true}
```

A successful completed response contains `data.operation_id`, `data.status: "succeeded"` and `data.completed: true`. Unity persists the operation across domain reloads. If import triggers compilation, completion waits for that compilation and its reload even when `compile` is `"none"`. Idle editor state alone is not a completion receipt.

The server captures the current dirty generations before dispatch and acknowledges only those generations after the matching native receipt succeeds. Changes made during the operation remain dirty and need a later refresh. An unknown or incomplete file scan is never acknowledged as clean. Detection uses file metadata, not a content hash.

### Recover an interrupted observation

```json
{"operation_id": "<operation_id from the previous response>", "wait_for_ready": true}
```

Supplying `operation_id` is read-only: it queries that operation and never starts a replacement. A pending response, observation timeout or disconnected reply is not permission to submit another mutation. Keep the operation ID and continue observing it; compilation failure stays a failure until the source changes or an explicit forced retry is requested. After a server restart, a recovered native receipt cannot acknowledge a scanner snapshot that the new process never captured.

### Request compilation deliberately

```json
{"mode": "force", "scope": "scripts", "compile": "request", "wait_for_ready": true}
```

A reliable clean `if_dirty` request is a no-op, including when `compile` is `"request"`. Use `mode: "force"` when compilation itself is required. The native admission guard rejects refresh while a test job still owns Unity, including pending native cleanup.
<!-- examples:end -->

