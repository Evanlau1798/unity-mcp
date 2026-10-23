---
title: get_test_job
sidebar_label: get_test_job
description: "Polls an async Unity test job by job_id."
---

# `get_test_job`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `testing` &nbsp;·&nbsp; **Module:** `services.tools.run_tests`

## Description

Polls an async Unity test job by job_id.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `job_id` | `str` | yes | Job id returned by run_tests |
| `include_failed_tests` | `bool` | — | Include details for failed/skipped tests only (default: false) |
| `include_details` | `bool` | — | Include details for all tests (default: false) |
| `wait_timeout` | `int \| None` | — | Total observation deadline in seconds, including routing, requests and polling. A timeout does not cancel or restart the job; poll the same job_id again. Recommended: 30-60 seconds. Inspect native ownership before starting another run. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
### Wait for a run to finish

> Wait for the test job I just started and show the failures.

```json
{
  "job_id": "<job_id from run_tests>",
  "wait_timeout": 60,
  "include_failed_tests": true
}
```

`wait_timeout` is one total observation deadline covering instance routing, requests, focus observation and polling sleeps. It returns a terminal job result as soon as it is observed, or the last real progress when the deadline expires. A timeout never cancels or relaunches the job: call `get_test_job` again with the same `job_id`. If Unity becomes unreachable, the last observation carries `wait_timed_out`, `observation_is_stale`, `observation_age_seconds` and `observation_error`; if no valid observation exists, the response reports `job_status_unknown` rather than inventing a running or failed result.

### Check progress without waiting

> How far along is the test run?

```json
{
  "job_id": "<job_id from run_tests>"
}
```

Returns the next available observation. `data.progress` includes `completed` / `total`, the current test and `failures_so_far` (capped at 25). `data.result` retains the runner summary and requested test details for completed runs, including runs with failing tests. `data.error` describes a runner or initialization failure. A terminal `status` describes the outcome, while `native_ownership_retained` and `native_cleanup_pending` describe whether Unity still owns cleanup. Keep observing that same job until both are false before starting another run. `native_run_id`, `native_run_started` and `initialization_timed_out` provide the corresponding native lifecycle evidence. `data.job_wall_duration_ms` is populated once a native job has a valid start and terminal timestamp; `data.observed_at_unix_ms` records when Unity produced this status observation. These wall-clock values are independent of NUnit's original `result.summary.durationSeconds`, which can cover a different interval.

If Unity is in the background and the job has not moved for 3 s, both this call and the `wait_timeout` form start a focus nudge that brings the Unity window to the front for a few seconds and then switches back. This form runs it in the background, so the response is not delayed.

### Get details for every test

> Show me the result of every test, not just the failures.

```json
{
  "job_id": "<job_id from run_tests>",
  "include_details": true
}
```

`include_details` returns all tests; `include_failed_tests` returns only failed and skipped ones, which keeps the response small on large suites.
<!-- examples:end -->

