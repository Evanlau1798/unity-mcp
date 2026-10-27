# Unity-MCP Testing Tools

Read this reference for discovery, test-start parameters and job/result observation. Use the [tool index](tools-reference.md) for other categories. Examples are **templates**, not evidence of a live run.

## Testing Tools

Group: `testing`. Determine the relevant validation scope before starting a run; see [validation](validation-workflow.md) and [evidence selection](evidence-and-selection.md). Tool acceptance, test execution, test outcomes and native cleanup are separate observations.

### list_tests

Read-only discovery; it invokes native `get_tests` without executing tests. Use this tool for filtering and pages beyond the first 50 tests. `run_tests` is not a discovery API.

```python
page = list_tests(
    mode="EditMode",             # "EditMode" | "PlayMode"; omit for both
    filter="Inventory",          # optional case-insensitive substring of name/full name
    page_size=50,                # default 50; native maximum 200
    cursor=0                     # 0-based offset; int or string
)
```

The decoded response contains `data.items`, `data.cursor`, `data.nextCursor`, `data.totalCount`, `data.pageSize` and `data.hasMore`. Items contain `name`, `full_name` and `mode`. Keep the same mode/filter while following `data.nextCursor` until `data.hasMore` is false; use the returned cursor rather than assuming the requested page size was applied. The legacy `mcpforunity://tests` and `mcpforunity://tests/{mode}` resources expose only the first page.

### run_tests

Start an asynchronous test run once, after discovery and selection.

```python
started = run_tests(
    mode="EditMode",             # default EditMode; also PlayMode
    test_names=["MyTests.TestA", "MyTests.TestB"],  # exact full names
    group_names=["Integration.*"],  # regex patterns
    category_names=["Unit"],     # NUnit categories
    assembly_names=["Tests"],    # assembly filter
    include_failed_tests=True,   # failed/skipped details; default false
    include_details=False,       # all test details; default false
    init_timeout=15000,          # positive milliseconds; initialization only
    clear_stuck=False
)
# After decoding and checking success:
job_id = started["data"]["job_id"]
```

Filter parameters accept a string or a list of strings; the example illustrates available filters, not a requirement to set all of them. Reconcile the actual executed leaves with the requested selection. An unfiltered call may run the whole mode, so do not use it merely to inspect available tests.

A successful start has `data.job_id`, `data.status="running"` and `data.mode`; it establishes acceptance, not test success. Preserve the exact job ID and target instance through subsequent observations. Native busy responses can identify an existing `data.job_id` or `data.active_operation_id`; inspect that owner before starting another mutation.

`init_timeout` controls initialization, not total test runtime. The current PlayMode helper temporarily disables domain reload, preserves the original scene-reload option, and restores both settings afterward. Do not infer that another operation cannot cause a reload or that restoration has finished merely because the job became terminal.

`clear_stuck=True` is a special cleanup request: it does **not** start a run. It releases an orphaned tracker only after Unity positively confirms native cleanup; active or unknown native ownership remains retained. Do not use it to overwrite uncertain execution state.

### get_test_job

Observe the same accepted job; polling does not start a test run.

```python
observed = get_test_job(
    job_id=job_id,
    wait_timeout=60,             # total observation deadline, seconds
    include_failed_tests=True,   # failed/skipped details; default false
    include_details=False,       # all details; default false
    result_offset=0,             # terminal leaf offset; 3 results per page
    detail_offset=0              # Unicode text offset; 1024 characters/field/page
)
# After decoding and checking success:
status = observed["data"]["status"]
result = observed["data"].get("result")
```

Read `data.status`, not a top-level `status`, and `data.result`, not `results`. Recognize `succeeded`, `failed` and `cancelled` as terminal when returned; `running` is nonterminal. Native implementations may emit only running/succeeded/failed while the Python observation contract also recognizes cancelled; that recognition does not guarantee the installed native implementation emits it. A successful status-query envelope does not mean the tests passed: inspect the job status, error and result counts/outcomes.

`wait_timeout` is an observation deadline covering instance resolution, requests and polling. Omitting it returns one observation; a positive timeout waits up to that deadline. Timeout does not cancel or restart native work. Query the **same** `job_id` again and retain the original error/evidence. Unknown job ID or disconnect does not authorize replaying `run_tests`.

On timeout, `data.wait_timed_out=True` and `data.observation_is_stale=True` can accompany the last successful job observation; top-level success can therefore remain true. Also inspect `observation_age_seconds` and `observation_error`. Without an earlier observation, the response has `error="observation_timeout"`, `hint="poll_same_job"` and `data.job_status_unknown=True`. The wrapper rejects an observed job ID that does not match the requested one.

Useful fields under `data` include `started_unix_ms`, `finished_unix_ms`, `last_update_unix_ms`, `observed_at_unix_ms`, `job_wall_duration_ms`, `native_run_id`, `native_run_started`, `initialization_timed_out`, `native_cleanup_pending` and `native_ownership_retained`. `progress` contains completed/total counts, current/last-finished test identity/timestamps, failure samples and focus/background observations. `stuck_suspected` or `blocked_reason` is diagnostic evidence, not proof of a dead process.

#### Result and output pagination

`data.result.summary` contains the run-level `total`, `passed`, `failed`, `skipped`, `durationSeconds` and `resultState`. When details are requested/present, leaf records are in `data.result.results`, with `name`, `fullName`, `state`, `durationSeconds` and optional text fields.

The Python wrapper projects those terminal leaves into pages of **3 results**. Keep the same job ID and detail flags, then use `data.result.result_page.next_offset` as the next `result_offset` until it is null. The page also exposes `total`, `offset`, `returned` and `leaf_duration_sum_seconds`. Its total is the available detail-list length, which can differ from the run total when only failed/skipped details were requested. Do not interpret a short response or first page as the full test run.

Each leaf's `message`, `stackTrace` and `output` is separately limited to **1,024 Unicode characters per page**. Use `detail_offset` and the returned `detail_page.lengths` to retrieve needed later chunks for that result page. Preserve original failed/skipped outcomes and retrieve relevant failure text before making a conclusion; a truncated output field is not empty output.

Each leaf's `detail_page` gives `offset`, `limit`, per-field `lengths` and `has_more_text`; keep the same `result_offset` while advancing text offsets. Running-progress `failures_so_far[].message` uses a smaller 160-character limit with `message_total_chars`, `message_truncated` and `message_next_offset`; those snippets are not full terminal failure output.

#### Terminal cleanup

A terminal status is not sufficient to start another mutation. Check that `data.native_cleanup_pending` and `data.native_ownership_retained` are explicitly false and confirm required restoration for the operation. Missing fields remain unknown. If ownership is retained or cleanup is pending, continue observation/recovery of that run. See [lifecycle and recovery](lifecycle-and-recovery.md) for uncertain outcomes and native ownership.

`job_wall_duration_ms` spans native job start to native terminal. NUnit `summary.durationSeconds` measures a different interval; report them distinctly when diagnosing slow runs.
