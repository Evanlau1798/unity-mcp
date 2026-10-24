"""Compact, losslessly pageable public get_test_job responses.

The Unity job remains untouched; pagination/field windows are just projections
of the same underlying native result at a fixed job_id.
"""
import json
from unittest.mock import AsyncMock

import pytest

from .test_helpers import DummyContext


def big_job(job_id="job-1", result_count=6, msg_size=40_000):
    cases = [
        {"name": f"test-{i}", "fullName": f"Suite.Test{i}", "state": "Failed",
         "durationSeconds": float(i + 1),
         "message": "M" * msg_size + str(i),
         "stackTrace": "S" * msg_size,
         "output": "O" * msg_size}
        for i in range(result_count)
    ]
    return {"success": True, "message": "Test job status retrieved.",
        "data": {
            "job_id": job_id, "status": "failed", "mode": "EditMode",
            "native_cleanup_pending": False, "native_ownership_retained": False,
            "job_wall_duration_ms": 219_000,
            "result": {"mode": "EditMode", "summary": {
                "total": result_count, "passed": 0, "failed": result_count,
                "skipped": 0, "durationSeconds": 99.0, "resultState": "Failed",
            }, "results": cases},
            "progress": {"completed": result_count, "total": result_count,
                         "failures_so_far": [{"full_name": f"Suite.Test{i}",
                                             "message": "E" * 25_000}
                                            for i in range(20)]},
        }}


@pytest.fixture
def install_poll_stub(monkeypatch):
    import services.tools.run_tests as mod

    monkeypatch.setattr(mod, "get_unity_instance_from_context", AsyncMock(return_value="Project@instance"))
    monkeypatch.setattr(mod, "_update_job_nudge", AsyncMock())
    monkeypatch.setattr(mod.config, "http_remote_hosted", False)
    return mod


@pytest.mark.asyncio
async def test_terminal_job_is_compact_and_preserves_identity_and_outcome(install_poll_stub, monkeypatch):
    mod = install_poll_stub
    original = big_job()
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", AsyncMock(return_value=original))
    result = await mod.get_test_job(
        DummyContext(), job_id="job-1", include_failed_tests=True, include_details=True)
    data = result.model_dump()
    encoded = json.dumps(data, ensure_ascii=True)
    assert len(encoded) < 18_000
    assert data["data"]["job_id"] == "job-1"
    assert data["data"]["status"] == "failed"
    assert data["data"]["job_wall_duration_ms"] == 219_000
    assert data["data"]["native_ownership_retained"] is False
    assert data["data"]["result"]["summary"]["durationSeconds"] == 99.0
    assert len(data["data"]["result"]["results"]) <= 3
    meta = data["data"]["result"]["result_page"]
    assert meta["total"] == 6 and meta["next_offset"] == 3
    assert meta["leaf_duration_sum_seconds"] == 6.0
    assert data["data"]["result"]["results"][0]["detail_page"]["has_more_text"] is True
    assert len(data["data"]["progress"]["failures_so_far"]) == 20
    assert data["data"]["progress"]["failures_so_far"][0]["message_truncated"] is True
    assert len(original["data"]["result"]["results"][0]["message"]) == 40_001


@pytest.mark.asyncio
async def test_job_detail_pages_recover_all_message_chunks_without_restarting_job(install_poll_stub, monkeypatch):
    mod = install_poll_stub
    original = big_job(result_count=4, msg_size=2100)
    send = AsyncMock(return_value=original)
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", send)
    first = await mod.get_test_job(DummyContext(), job_id="job-1", include_details=True)
    page = first.data.result.result_page
    assert page["next_offset"] == 3
    second = await mod.get_test_job(DummyContext(), job_id="job-1", include_details=True, result_offset=3)
    assert second.data.result.result_page["next_offset"] is None
    assert second.data.result.results[0].name == "test-3"

    fragments = []
    offset = 0
    while True:
        reply = await mod.get_test_job(
            DummyContext(), job_id="job-1", include_details=True,
            result_offset=0, detail_offset=offset,
        )
        case = reply.data.result.results[0]
        fragments.append(case.message)
        meta = case.detail_page
        if not meta["has_more_text"]:
            break
        offset += meta["limit"]
    assert "".join(fragments) == original["data"]["result"]["results"][0]["message"]
    assert send.await_count >= 5
    for call in send.await_args_list:
        assert call.args[2] == "get_test_job" and call.args[3]["job_id"] == "job-1"


@pytest.mark.asyncio
async def test_bounded_wait_uses_same_compact_projection(install_poll_stub, monkeypatch):
    mod = install_poll_stub
    send = AsyncMock(return_value=big_job(msg_size=30_000))
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", send)
    result = await mod.get_test_job(
        DummyContext(), job_id="job-1", include_details=True,
        include_failed_tests=True, wait_timeout=5,
    )
    assert result.success is True
    assert len(json.dumps(result.model_dump())) < 18_000


@pytest.mark.asyncio
async def test_running_failure_message_can_be_recovered_without_terminal_result(install_poll_stub, monkeypatch):
    mod = install_poll_stub
    original = {"success": True, "data": {
        "job_id": "job-1", "status": "running",
        "progress": {"completed": 0, "total": 1, "failures_so_far": [
            {"full_name": "Suite.Test0", "message": "A" * 160 + "B" * 160 + "C" * 5}
        ]},
    }}
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", AsyncMock(return_value=original))
    first = await mod.get_test_job(DummyContext(), job_id="job-1")
    failure0 = first.data.progress.failures_so_far[0]
    assert failure0.message == "A" * 160
    assert failure0.message_next_offset == 160
    second = await mod.get_test_job(DummyContext(), job_id="job-1", detail_offset=160)
    failure1 = second.data.progress.failures_so_far[0]
    assert failure1.message == "B" * 160
    assert failure1.message_next_offset == 320
    third = await mod.get_test_job(DummyContext(), job_id="job-1", detail_offset=320)
    failure2 = third.data.progress.failures_so_far[0]
    assert failure2.message == "C" * 5
    assert failure2.message_next_offset is None
    assert failure0.message + failure1.message + failure2.message == original["data"]["progress"]["failures_so_far"][0]["message"]


@pytest.mark.asyncio
async def test_negative_offsets_reject_without_contacting_unity(install_poll_stub, monkeypatch):
    mod = install_poll_stub
    send = AsyncMock()
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", send)
    reply = await mod.get_test_job(DummyContext(), job_id="job-1", include_details=True, result_offset=-1)
    assert reply.success is False
    send.assert_not_awaited()
