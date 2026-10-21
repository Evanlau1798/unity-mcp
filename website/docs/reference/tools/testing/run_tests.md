---
title: run_tests
sidebar_label: run_tests
description: "Starts a Unity test run asynchronously and returns a job_id immediately."
---

# `run_tests`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `testing` &nbsp;·&nbsp; **Module:** `services.tools.run_tests`

## Description

Starts a Unity test run asynchronously and returns a job_id immediately. Poll with get_test_job for progress.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `mode` | `Literal['EditMode', 'PlayMode']` | — | Unity test mode to run |
| `test_names` | `list[str] \| str \| None` | — | Full names of specific tests to run |
| `group_names` | `list[str] \| str \| None` | — | Same as test_names, except it allows for Regex |
| `category_names` | `list[str] \| str \| None` | — | NUnit category names to filter by |
| `assembly_names` | `list[str] \| str \| None` | — | Assembly names to filter tests by |
| `include_failed_tests` | `bool` | — | Include details for failed/skipped tests only (default: false) |
| `include_details` | `bool` | — | Include details for all tests (default: false) |
| `init_timeout` | `int \| None` | — | Initialization timeout in milliseconds. PlayMode tests may need longer due to domain reload (default: 15000). Recommended: 120000 for PlayMode. |
| `clear_stuck` | `bool` | — | Release an orphaned job only after Unity positively confirms native cleanup. Active or unknown native ownership is retained; this does not start a run. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
### Run every EditMode test

> Run all EditMode tests and tell me what failed.

```json
{
  "mode": "EditMode",
  "include_failed_tests": true
}
```

Returns immediately with a `job_id` and `status: "running"`. Poll it with [`get_test_job`](./get_test_job.md) — the results are not in this response.

### Run specific tests by full name

> Re-run only `InventoryTests.AddItem_IncreasesCount`.

```json
{
  "mode": "EditMode",
  "test_names": ["MyGame.Tests.InventoryTests.AddItem_IncreasesCount"],
  "include_failed_tests": true
}
```

`test_names` must be full names (namespace, class, method). A single string is accepted as well as a list.

### Run a whole namespace with a regex

> Run every test under `MyGame.Tests.Inventory`.

```json
{
  "mode": "EditMode",
  "group_names": ["^MyGame\\.Tests\\.Inventory"]
}
```

Each `group_names` entry is a regular expression matched against the full test name, not a name that has to match exactly. Filters can be combined with `category_names` and `assembly_names`.

### Run PlayMode tests from one assembly

> Run the PlayMode tests in `MyGame.PlayModeTests`.

```json
{
  "mode": "PlayMode",
  "assembly_names": ["MyGame.PlayModeTests"],
  "init_timeout": 120000
}
```

PlayMode runs start with a domain reload, so the default 15 s `init_timeout` is often too short. 120000 ms is the recommended value.

### Unblock a run lost to a domain reload

> Every `run_tests` call fails because an old job is still marked as running.

```json
{
  "clear_stuck": true
}
```

`clear_stuck` does not start a run. It releases an owned job only when the native runner positively confirms that this run is no longer active. Active or unknown native ownership remains held; continue observing the same `job_id` instead of submitting another run. A recorded result or runner error survives cleanup. Initialization timeout is a failed job outcome, but it is not evidence that Unity has stopped executing or restoring scenes. Both refresh and the next test launch remain blocked while `native_ownership_retained` or `native_cleanup_pending` is true.
<!-- examples:end -->

