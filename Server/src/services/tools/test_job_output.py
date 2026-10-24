"""Bound public test-job result payloads without changing the native job.

A native job stores its full outcome. Each get_test_job observation is a
read-only, repeatable view; callers can retrieve later result leaves and
subsequent text chunks with the same job_id.
"""
from __future__ import annotations

from typing import Any

RESULTS_PER_PAGE = 3
DETAIL_CHARS_PER_PAGE = 1024
PROGRESS_MESSAGE_CHARS = 160


def _slice_text(value: Any, offset: int, limit: int) -> Any:
    return value[offset:offset + limit] if isinstance(value, str) else value


def project_test_job(
    response: dict[str, Any], *, result_offset: int = 0,
    detail_offset: int = 0,
) -> dict[str, Any]:
    """Limit serialized output, keeping outcome, job/native identity and timing."""
    if response.get("success") is not True:
        return response
    data = response.get("data")
    if not isinstance(data, dict):
        return response
    result_data = dict(data)
    output = dict(response)
    output["data"] = result_data

    progress = data.get("progress")
    if isinstance(progress, dict) and isinstance(progress.get("failures_so_far"), list):
        progress = dict(progress)
        failures = []
        for item in progress["failures_so_far"]:
            if not isinstance(item, dict):
                failures.append(item)
                continue
            failure = dict(item)
            message = failure.get("message")
            if isinstance(message, str):
                failure["message"] = _slice_text(
                    message, detail_offset, PROGRESS_MESSAGE_CHARS,
                )
                failure["message_total_chars"] = len(message)
                failure["message_truncated"] = (
                    detail_offset > 0 or len(message) > PROGRESS_MESSAGE_CHARS
                )
                next_offset = detail_offset + PROGRESS_MESSAGE_CHARS
                failure["message_next_offset"] = (
                    next_offset if next_offset < len(message) else None
                )
            failures.append(failure)
        progress["failures_so_far"] = failures
        result_data["progress"] = progress

    native_result = data.get("result")
    if not isinstance(native_result, dict) or not isinstance(native_result.get("results"), list):
        return output

    full_results = native_result["results"]
    selected = full_results[result_offset:result_offset + RESULTS_PER_PAGE]
    projected = []
    for native in selected:
        if not isinstance(native, dict):
            projected.append(native)
            continue
        item = dict(native)
        lengths = {}
        for name in ("message", "stackTrace", "output"):
            value = native.get(name)
            if isinstance(value, str):
                lengths[name] = len(value)
                item[name] = _slice_text(value, detail_offset, DETAIL_CHARS_PER_PAGE)
        item["detail_page"] = {
            "offset": detail_offset,
            "limit": DETAIL_CHARS_PER_PAGE,
            "lengths": lengths,
            "has_more_text": any(length > detail_offset + DETAIL_CHARS_PER_PAGE for length in lengths.values()),
        }
        projected.append(item)

    next_offset = result_offset + len(selected)
    leaf_seconds = sum(
        float(item["durationSeconds"])
        for item in selected
        if isinstance(item, dict) and isinstance(item.get("durationSeconds"), (int, float))
    )
    result_data["result"] = {
        **native_result,
        "results": projected,
        "result_page": {
            "total": len(full_results),
            "offset": result_offset,
            "returned": len(projected),
            "next_offset": next_offset if next_offset < len(full_results) else None,
            "detail_offset": detail_offset,
            "detail_limit": DETAIL_CHARS_PER_PAGE,
            "leaf_duration_sum_seconds": leaf_seconds,
        },
    }
    return output
