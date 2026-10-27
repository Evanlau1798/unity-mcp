"""Response models for async Unity test jobs."""
from typing import Any

from pydantic import BaseModel
from models import MCPResponse


class RunTestsSummary(BaseModel):
    total: int
    passed: int
    failed: int
    skipped: int
    durationSeconds: float
    resultState: str


class RunTestsTestResult(BaseModel):
    name: str
    fullName: str
    state: str
    durationSeconds: float
    message: str | None = None
    stackTrace: str | None = None
    output: str | None = None
    detail_page: dict[str, Any] | None = None


class RunTestsResult(BaseModel):
    mode: str
    summary: RunTestsSummary
    results: list[RunTestsTestResult] | None = None
    result_page: dict[str, Any] | None = None


class RunTestsStartData(BaseModel):
    job_id: str
    status: str
    mode: str | None = None
    include_details: bool | None = None
    include_failed_tests: bool | None = None


class RunTestsStartResponse(MCPResponse):
    data: RunTestsStartData | None = None


class TestJobFailure(BaseModel):
    full_name: str | None = None
    message: str | None = None
    message_total_chars: int | None = None
    message_truncated: bool | None = None
    message_next_offset: int | None = None


class TestJobProgress(BaseModel):
    completed: int | None = None
    total: int | None = None
    current_test_full_name: str | None = None
    current_test_started_unix_ms: int | None = None
    last_finished_test_full_name: str | None = None
    last_finished_unix_ms: int | None = None
    stuck_suspected: bool | None = None
    editor_is_focused: bool | None = None
    run_in_background: bool | None = None
    focus_nudge_attempts: int | None = None
    focus_nudge_limit: int | None = None
    focus_nudge_status: str | None = None
    blocked_reason: str | None = None
    failures_so_far: list[TestJobFailure] | None = None
    failures_capped: bool | None = None


class GetTestJobData(BaseModel):
    job_id: str
    status: str
    mode: str | None = None
    started_unix_ms: int | None = None
    finished_unix_ms: int | None = None
    last_update_unix_ms: int | None = None
    # Native persisted timestamps, independent of NUnit summary.durationSeconds.
    job_wall_duration_ms: int | None = None
    observed_at_unix_ms: int | None = None
    progress: TestJobProgress | None = None
    error: str | None = None
    result: RunTestsResult | None = None
    native_run_id: str | None = None
    native_cleanup_pending: bool | None = None
    native_ownership_retained: bool | None = None
    initialization_timed_out: bool | None = None
    native_run_started: bool | None = None
    wait_timed_out: bool | None = None
    observation_is_stale: bool | None = None
    observation_age_seconds: float | None = None
    observation_error: dict[str, Any] | None = None


class GetTestJobResponse(MCPResponse):
    data: GetTestJobData | None = None
