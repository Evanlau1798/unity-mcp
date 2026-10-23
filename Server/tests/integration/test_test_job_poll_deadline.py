import asyncio

import pytest

from models import MCPResponse

from .test_helpers import DummyContext


@pytest.fixture
def poll_module(monkeypatch):
    import services.tools.run_tests as mod

    async def resolve(_ctx):
        return "Project@instance"

    async def no_nudge(*_args, **_kwargs):
        return None

    monkeypatch.setattr(mod, "get_unity_instance_from_context", resolve)
    monkeypatch.setattr(mod, "_update_job_nudge", no_nudge)
    monkeypatch.setattr(mod.config, "http_remote_hosted", False)
    return mod


def running(job_id="job-1"):
    return {"success": True, "data": {
        "job_id": job_id, "status": "running", "progress": {"completed": 2, "total": 5},
    }}


def accelerate_poll_sleeps(monkeypatch):
    original = asyncio.sleep

    async def brief(delay, *args, **kwargs):
        return await original(min(delay, 0.001), *args, **kwargs)

    monkeypatch.setattr(asyncio, "sleep", brief)
    return original


@pytest.mark.asyncio
async def test_wait_timeout_bounds_the_inner_status_fetch(poll_module, monkeypatch):
    mod = poll_module

    async def slow_fetch(*_args, **_kwargs):
        await asyncio.sleep(0.3)
        return running()

    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", slow_fetch)
    start = asyncio.get_running_loop().time()
    result = await mod.get_test_job(DummyContext(), job_id="job-1", wait_timeout=0.025)
    assert asyncio.get_running_loop().time() - start < 0.2
    assert result.success is False
    assert result.error == "observation_timeout"
    assert result.data["job_id"] == "job-1"
    assert result.data["job_status_unknown"] is True


@pytest.mark.asyncio
async def test_wait_budget_includes_instance_resolution(poll_module, monkeypatch):
    mod = poll_module
    sent = []

    async def slow_resolve(_ctx):
        await asyncio.sleep(0.3)
        return "Project@instance"

    async def fetch(*_args, **_kwargs):
        sent.append(True)
        return running()

    monkeypatch.setattr(mod, "get_unity_instance_from_context", slow_resolve)
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fetch)
    start = asyncio.get_running_loop().time()
    result = await mod.get_test_job(DummyContext(), job_id="job-1", wait_timeout=0.025)
    assert asyncio.get_running_loop().time() - start < 0.2
    assert sent == []
    assert result.error == "observation_timeout"


@pytest.mark.asyncio
async def test_transient_unavailable_retries_only_the_same_job(poll_module, monkeypatch):
    mod = poll_module
    accelerate_poll_sleeps(monkeypatch)
    sent = []

    async def fetch(_send, _instance, command, params, **_kwargs):
        sent.append((command, params["job_id"]))
        if len(sent) == 1:
            return {"success": False, "error": "Unity instance is unavailable", "hint": "retry"}
        return {"success": True, "data": {"job_id": "job-1", "status": "succeeded"}}

    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fetch)
    result = await mod.get_test_job(DummyContext(), job_id="job-1", wait_timeout=0.1)
    assert result.success is True
    assert result.data.status == "succeeded"
    assert sent == [("get_test_job", "job-1"), ("get_test_job", "job-1")]


@pytest.mark.asyncio
@pytest.mark.parametrize("reason", ["no_unity_session", "stale_connection"])
async def test_structured_session_unavailable_resumes_same_job(poll_module, monkeypatch, reason):
    accelerate_poll_sleeps(monkeypatch)
    observed = []

    async def fetch(_send, _instance, command, params, **_kwargs):
        observed.append((command, params["job_id"]))
        if len(observed) == 1:
            return {"success": False, "error": "Unity session not available; please retry",
                    "hint": "retry", "data": {"reason": reason, "retry_after_ms": 250}}
        return running("job-1") if len(observed) == 2 else {
            "success": True, "data": {"job_id": "job-1", "status": "succeeded"}
        }

    monkeypatch.setattr(poll_module.unity_transport, "send_with_unity_instance", fetch)
    result = await poll_module.get_test_job(
        DummyContext(), job_id="job-1", wait_timeout=0.1,
    )
    assert result.success is True
    assert result.data.status == "succeeded"
    assert observed == [("get_test_job", "job-1")] * 3


@pytest.mark.asyncio
async def test_unavailable_after_running_preserves_stale_observation(poll_module, monkeypatch):
    accelerate_poll_sleeps(monkeypatch)
    calls = []

    async def fetch(_send, _instance, command, params, **_kwargs):
        calls.append(params["job_id"])
        if len(calls) == 1:
            return running("job-1")
        return {"success": False, "error": "Unity session not available; please retry",
                "hint": "retry", "data": {"reason": "no_unity_session", "retry_after_ms": 250}}

    monkeypatch.setattr(poll_module.unity_transport, "send_with_unity_instance", fetch)
    result = await poll_module.get_test_job(DummyContext(), job_id="job-1", wait_timeout=0.02)
    assert result.success is True
    assert result.data.status == "running"
    assert result.data.observation_is_stale is True
    assert result.data.wait_timed_out is True
    assert len(calls) >= 2
    assert set(calls) == {"job-1"}


@pytest.mark.asyncio
async def test_timeout_preserves_last_observed_running_status(poll_module, monkeypatch):
    mod = poll_module
    original_sleep = accelerate_poll_sleeps(monkeypatch)
    sent = []

    async def fetch(_send, _instance, command, params, **_kwargs):
        sent.append(command)
        if len(sent) > 1:
            await original_sleep(0.3)
        return running(params["job_id"])

    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fetch)
    start = asyncio.get_running_loop().time()
    result = await mod.get_test_job(DummyContext(), job_id="job-1", wait_timeout=0.025)
    assert asyncio.get_running_loop().time() - start < 0.2
    assert result.success is True
    assert result.data.status == "running"
    assert result.data.progress.completed == 2
    assert result.data.wait_timed_out is True
    assert result.data.observation_is_stale is True
    assert result.data.observation_error["error"] == "TimeoutError"
    assert set(sent) == {"get_test_job"}


@pytest.mark.asyncio
async def test_wait_budget_also_bounds_observer_work(poll_module, monkeypatch):
    mod = poll_module

    async def fetch(*_args, **_kwargs):
        return running()

    async def slow_nudge(*_args, **_kwargs):
        await asyncio.sleep(0.3)

    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fetch)
    monkeypatch.setattr(mod, "_update_job_nudge", slow_nudge)
    start = asyncio.get_running_loop().time()
    result = await mod.get_test_job(DummyContext(), job_id="job-1", wait_timeout=0.025)
    assert asyncio.get_running_loop().time() - start < 0.2
    assert result.data.status == "running"
    assert result.data.wait_timed_out is True


@pytest.mark.asyncio
async def test_permanent_error_is_returned_without_retry(poll_module, monkeypatch):
    mod = poll_module
    sent = []

    async def fetch(*_args, **_kwargs):
        sent.append(True)
        return {"success": False, "error": "job_not_found"}

    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fetch)
    result = await mod.get_test_job(DummyContext(), job_id="missing", wait_timeout=0.1)
    assert result.success is False
    assert result.error == "job_not_found"
    assert sent == [True]


@pytest.mark.asyncio
async def test_caller_cancellation_still_propagates(poll_module, monkeypatch):
    async def fetch(*_args, **_kwargs):
        raise asyncio.CancelledError()

    monkeypatch.setattr(poll_module.unity_transport, "send_with_unity_instance", fetch)
    with pytest.raises(asyncio.CancelledError):
        await poll_module.get_test_job(DummyContext(), job_id="job-1", wait_timeout=0.1)


@pytest.mark.asyncio
@pytest.mark.parametrize("wait_timeout", [None, 0.025])
async def test_mismatched_job_identity_is_not_observed_as_the_requested_run(poll_module, monkeypatch, wait_timeout):
    async def fetch(*_args, **_kwargs):
        return running("different-job")

    monkeypatch.setattr(poll_module.unity_transport, "send_with_unity_instance", fetch)
    result = await poll_module.get_test_job(DummyContext(), job_id="job-1", wait_timeout=wait_timeout)
    assert result.success is False
    assert result.error == "test_job_identity_mismatch"


@pytest.mark.asyncio
async def test_native_ownership_metadata_survives_poll_response(poll_module, monkeypatch):
    ownership = {
        "native_run_id": "native-run-1",
        "native_cleanup_pending": True,
        "native_ownership_retained": True,
        "initialization_timed_out": True,
        "native_run_started": False,
    }

    async def fetch(*_args, **_kwargs):
        response = running()
        response["data"].update(ownership)
        return response

    monkeypatch.setattr(poll_module.unity_transport, "send_with_unity_instance", fetch)
    result = await poll_module.get_test_job(DummyContext(), job_id="job-1")
    observed = result.data.model_dump()
    assert {key: observed.get(key) for key in ownership} == ownership


@pytest.mark.asyncio
async def test_native_job_wall_time_and_observation_timestamp_are_retained(poll_module, monkeypatch):
    async def fetch(*_args, **_kwargs):
        return {
            "success": True, "data": {
                "job_id": "job-1", "status": "succeeded",
                "started_unix_ms": 1000, "finished_unix_ms": 1642,
                "job_wall_duration_ms": 642, "observed_at_unix_ms": 2000,
            },
        }

    monkeypatch.setattr(poll_module.unity_transport, "send_with_unity_instance", fetch)
    result = await poll_module.get_test_job(DummyContext(), job_id="job-1")
    assert result.data.job_wall_duration_ms == 642
    assert result.data.observed_at_unix_ms == 2000


def reload_observation(kind):
    response = {"success": False, "error": "Unity is reloading; please retry", "hint": "retry"}
    if kind == "structured_model":
        response["data"] = {"reason": "reloading", "retry_after_ms": 250}
    return response if kind == "legacy_dict" else MCPResponse(**response)


@pytest.mark.asyncio
@pytest.mark.parametrize("kind", ["structured_model", "legacy_model", "legacy_dict"])
async def test_reload_observation_recovers_the_same_job_without_losing_model_data(poll_module, monkeypatch, kind):
    accelerate_poll_sleeps(monkeypatch)
    sent = []
    observations = [running(), reload_observation(kind),
                    MCPResponse(success=True, data={"job_id": "job-1", "status": "succeeded"})]

    async def fetch(_send, _instance, command, params, **_kwargs):
        sent.append((command, params["job_id"]))
        return observations.pop(0)

    monkeypatch.setattr(poll_module.unity_transport, "send_with_unity_instance", fetch)
    result = await poll_module.get_test_job(DummyContext(), job_id="job-1", wait_timeout=0.1)
    assert result.success is True
    assert result.data.status == "succeeded"
    assert sent == [("get_test_job", "job-1")] * 3


@pytest.mark.asyncio
@pytest.mark.parametrize("kind", ["structured_model", "legacy_model", "legacy_dict"])
async def test_repeated_reload_observation_keeps_last_running_data_until_deadline(poll_module, monkeypatch, kind):
    accelerate_poll_sleeps(monkeypatch)
    sent = []
    reloading = reload_observation(kind)
    expected = reloading.model_dump() if hasattr(reloading, "model_dump") else reloading

    async def fetch(_send, _instance, command, params, **_kwargs):
        sent.append((command, params["job_id"]))
        return running() if len(sent) == 1 else reloading

    monkeypatch.setattr(poll_module.unity_transport, "send_with_unity_instance", fetch)
    start = asyncio.get_running_loop().time()
    result = await poll_module.get_test_job(DummyContext(), job_id="job-1", wait_timeout=0.025)
    assert asyncio.get_running_loop().time() - start < 0.2
    assert result.success is True
    assert result.data.status == "running"
    assert result.data.progress.completed == 2
    assert result.data.wait_timed_out is True
    assert result.data.observation_is_stale is True
    for key in ("error", "hint", "data"):
        assert result.data.observation_error.get(key) == expected.get(key)
    assert len(sent) > 1 and set(sent) == {("get_test_job", "job-1")}


@pytest.mark.asyncio
@pytest.mark.parametrize("wait_timeout", [None, 0.1])
async def test_permanent_model_error_retains_its_contract_without_retry(poll_module, monkeypatch, wait_timeout):
    sent = []

    async def fetch(*_args, **_kwargs):
        sent.append(True)
        return MCPResponse(success=False, error="job_not_found", data={"reason": "job_not_found"})

    monkeypatch.setattr(poll_module.unity_transport, "send_with_unity_instance", fetch)
    result = await poll_module.get_test_job(DummyContext(), job_id="job-1", wait_timeout=wait_timeout)
    assert result.success is False
    assert result.error == "job_not_found"
    assert result.data == {"reason": "job_not_found"}
    assert sent == [True]
