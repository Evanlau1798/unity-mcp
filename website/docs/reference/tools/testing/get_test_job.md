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
| `result_offset` | `int` | — | Zero-based offset of terminal test-result leaves (3 results per page). Use result.result_page.next_offset on the same job_id to read more. |
| `detail_offset` | `int` | — | Zero-based Unicode text offset for each leaf's message/stackTrace/output (1024 characters per field per page). Use detail_page.lengths to retrieve all chunks. |

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

`include_details` selects all tests, while `include_failed_tests` selects failed and skipped ones. To avoid oversized and sometimes truncated MCP client envelopes, **only three leaf results appear in each reply**, and each `message`, `stackTrace` and `output` field is windowed to 1,024 Unicode characters. Full original native results remain available under the same job ID; a short reply does not mean the underlying evidence was discarded.

### Retrieve additional result leaves and error text

```json
{
  "job_id": "<the SAME job_id>",
  "include_details": true,
  "result_offset": 3
}
```

Read `data.result.result_page.total`, `offset`, `returned` and `next_offset`. Continue at `next_offset` until null. Within one result leaf, if `detail_page.has_more_text` is true, use the **same** `result_offset` with `detail_offset: 1024`, `2048`, etc., until each of the `detail_page.lengths` is recovered. Text pagination is independent of result pagination.

```json
{
  "job_id": "<the SAME job_id>",
  "include_failed_tests": true,
  "result_offset": 0,
  "detail_offset": 1024
}
```

`result_page.leaf_duration_sum_seconds` sums **only the returned page's selected leaf durations**. It is neither the overall native `job_wall_duration_ms` nor the NUnit root `summary.durationSeconds`. These figures can represent overlapping and different intervals and must not be added together. Progress `failures_so_far` keeps original test names with 160-character message windows; `message_truncated`, `message_total_chars` and `message_next_offset` expose the full length and next available 160-character slice. Requery the same job with `detail_offset` set to the indicated `message_next_offset` even for running jobs without terminal `data.result`.
<!-- examples:end -->

