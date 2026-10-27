# Testing workflows

[Workflow index](workflows.md). Use when a requested behavior change or an existing project gate needs Unity test evidence.

> **Template:** Check the installed start/poll schema before applying these examples. Documentation work alone does not start tests.

Before starting, read [validation-workflow.md](validation-workflow.md) and only the relevant [evidence/selection](evidence-and-selection.md) or [lifecycle](lifecycle-and-recovery.md) section. Resolve the exact project/Editor, candidate and selected tests. Confirm compilation/import readiness and that any previous native job has released ownership. Handle an `unsaved_scene` blocker through the authorized save/close workflow; do not discard user content to satisfy preflight.

## Testing Workflows

### Run Specific Tests

Use discovery to verify the requested full names, assembly, and mode before launch. Read `mcpforunity://tests/EditMode` or use the installed `list_tests` discovery schema; follow its pagination. Discovery counts are not execution counts.

```python
started = run_tests(
    mode="EditMode",
    test_names=["MyTests.TestPlayerMovement", "MyTests.TestEnemySpawn"],
    include_failed_tests=True
)
# Only extract an accepted handle after inspecting success and any preflight error.
if started.get("success") is not True:
    raise RuntimeError(started)
job_id = started["data"]["job_id"]

# This observes the same job; it does not start a second run.
observed = get_test_job(job_id=job_id, wait_timeout=30)
```

Interpret the **actual response envelope**, preserving errors and stale/timeout observations:

| Field | Meaning |
| --- | --- |
| `data.job_id` | Must match the launched job. |
| `data.status` | `running` while active; terminal values accepted by the public contract are `succeeded`, `failed`, or `cancelled`. Do not wait for `complete`. |
| `data.progress` | Current progress and available failure summaries. |
| `data.result` | Terminal test result when available; inspect its summary and actual cases. |
| `data.native_cleanup_pending` | `true` means cleanup is still pending. |
| `data.native_ownership_retained` | `true` means the native owner still blocks the next mutation. |
| `data.observed_at_unix_ms`, `data.observation_is_stale`, `data.observation_age_seconds` | Distinguish current observation from a cached/last-known state. |
| `data.wait_timed_out`, `data.observation_error` | Observation deadline/error metadata; does not change the native outcome. |

If the observation deadline expires or transport disconnects, continue bounded read-only observation using **the same `job_id`**. Keep the last observed status and the uncertainty; a polling timeout does not cancel, restart, or prove failure of the native run. Do not automatically call `run_tests` or `clear_stuck` to recover.

A terminal status alone does not release the Editor. Confirm both cleanup/ownership fields are explicitly `false`, plus the required environment restoration, before allowing the next mutation. Missing fields remain unknown; inspect the available native evidence under [lifecycle and recovery](lifecycle-and-recovery.md). If the waiting budget ends, retain the job and report the unresolved state instead of launching a replacement.

Once terminal evidence is available, fetch the details needed for diagnosis or completion:

```python
details = get_test_job(
    job_id=job_id, wait_timeout=30,
    include_failed_tests=True, result_offset=0, detail_offset=0
)
result = (details.get("data") or {}).get("result")
# Inspect the envelope/freshness, then the summary and returned test leaves.
```

Inspect `data.result.summary` and `data.result.results`; the latter holds the selected leaf page, not an unwrapped `failed_tests` list. The public wrapper pages terminal leaves. Follow `data.result.result_page.next_offset` with `result_offset` on the same job; use the returned detail-page metadata and `detail_offset` for truncated message/stack/output fields. Request `include_details=True` only when all case details are needed. Preserve skipped/missing cases and compare requested tests with actual executed leaves; `succeeded` alone does not establish the intended coverage.

### Run Tests by Category

Confirm the category's discovered cases and relevant assembly scope, then launch once:

```python
started = run_tests(
    mode="EditMode", category_names=["Unit"],
    include_failed_tests=True
)
if started.get("success") is not True:
    raise RuntimeError(started)
job_id = started["data"]["job_id"]
observed = get_test_job(job_id=job_id, wait_timeout=30)
```

Apply the same bounded observation, terminal/result inspection, and cleanup/ownership procedure above. Use `assembly_names` when the requested scope needs that filter; do not broaden to all assemblies merely to discover names.

### Test-Driven Development Pattern

For an actual behavior change, first establish a valid failing behavioral case, then the smallest fix and affected regression evidence under the project's TDD rules. A failing fixture, compile, or transport is not automatically a product RED.

The retained example below is a **smoke fixture**, expected to pass on an unmodified Unity project; it does not constitute a product RED. Use it only when this smoke check is relevant. Create it inside the project's configured test assembly, with NUnit/Unity dependencies available.

```python
create_script(
    path="Assets/Tests/Editor/PlayerTests.cs",
    contents='''using NUnit.Framework;
using UnityEngine;

public class PlayerTests
{
    [Test]
    public void TestPlayerStartsAtOrigin()
    {
        var player = new GameObject("TestPlayer");
        try
        {
            Assert.AreEqual(Vector3.zero, player.transform.position);
        }
        finally
        {
            Object.DestroyImmediate(player);
        }
    }
}'''
)
```

Complete [script import/compilation and Console verification](workflow-scripts.md#after-a-script-mutation), then discover the new case before launching it:

```python
started = run_tests(mode="EditMode", test_names=["PlayerTests.TestPlayerStartsAtOrigin"])
if started.get("success") is not True:
    raise RuntimeError(started)
job_id = started["data"]["job_id"]
observed = get_test_job(job_id=job_id, wait_timeout=30)
```

Use the same completion/cleanup procedure, preserving original failures and run identity. Remove temporary fixture assets only if they were created solely for this task and cleanup is authorized; retain intentional regression tests. Do not rerun unrelated suites solely because documentation or a reviewer changed.
