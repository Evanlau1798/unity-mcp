"""A lost mutation reply must not launch again or retarget a newer owner."""
import pytest

from models import MCPResponse
import services.tools.run_tests as mod
from .test_helpers import DummyContext


LOST_REPLY = {
    "success": False,
    "error": "ConnectionResetError: native mutation finished before reply was lost",
    "message": "The original mutation outcome is unknown.",
    "hint": "observe_original_operation",
    "data": {"reason": "response_lost", "mutation_outcome_unknown": True},
}


@pytest.fixture
def target(monkeypatch):
    async def resolve(_ctx):
        return "Unity@no-replay"

    async def admitted(*_args, **_kwargs):
        return None

    monkeypatch.setattr(mod, "get_unity_instance_from_context", resolve)
    monkeypatch.setattr(mod, "preflight", admitted)


class LostReplyBridge:
    def __init__(self, *, clear, as_model, retry_available=True):
        self.clear = clear
        self.as_model = as_model
        self.retry_available = retry_available
        self.effects = []
        self.owner = "original-owner" if clear else None

    async def send(self, _sender, _instance, command, params, **kwargs):
        assert command == "run_tests"
        assert params.get("clear_stuck", False) is self.clear
        self.apply_native_mutation()
        # The original native run/clear already finished when the reply is lost.
        # A transport retry can now start again or clear an unrelated new owner.
        if self.clear:
            self.owner = "new-owner"
        if self.retry_available and kwargs.get("retry_on_reload", True):
            self.apply_native_mutation()
            if self.clear:
                return {"success": True, "data": {"cleared": True}}
            return {"success": True, "data": {"job_id": "job-2", "status": "running"}}
        return MCPResponse(**LOST_REPLY) if self.as_model else dict(LOST_REPLY)

    def apply_native_mutation(self):
        if self.clear:
            self.effects.append(self.owner)
            self.owner = None
        else:
            self.effects.append(f"job-{len(self.effects) + 1}")
            self.owner = None  # A short suite completes before its response arrives.


@pytest.mark.asyncio
@pytest.mark.parametrize("clear", [False, True])
@pytest.mark.parametrize("as_model", [False, True])
async def test_launch_and_clear_do_not_replay_a_mutation_after_lost_reply(target, monkeypatch, clear, as_model):
    bridge = LostReplyBridge(clear=clear, as_model=as_model)
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    result = await mod.run_tests(DummyContext(), clear_stuck=clear)
    assert bridge.effects == (["original-owner"] if clear else ["job-1"])
    if clear:
        assert bridge.owner == "new-owner"
    returned = result.model_dump()
    assert {key: returned[key] for key in LOST_REPLY} == LOST_REPLY


@pytest.mark.asyncio
@pytest.mark.parametrize("clear", [False, True])
async def test_model_mutation_error_keeps_unknown_outcome_data_and_hint(target, monkeypatch, clear):
    bridge = LostReplyBridge(clear=clear, as_model=True, retry_available=False)
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", bridge.send)
    result = await mod.run_tests(DummyContext(), clear_stuck=clear)
    returned = result.model_dump()
    assert returned["error"] == LOST_REPLY["error"]
    assert returned["hint"] == LOST_REPLY["hint"]
    assert returned["data"] == LOST_REPLY["data"]
    assert len(bridge.effects) == 1


@pytest.mark.asyncio
async def test_test_job_observation_still_allows_safe_transport_retry(target, monkeypatch):
    observations = []

    async def send(_sender, _instance, command, params, **kwargs):
        assert command == "get_test_job"
        observations.append(params["job_id"])
        if kwargs.get("retry_on_reload", True):
            observations.append(params["job_id"])
            return {"success": True, "data": {"job_id": params["job_id"], "status": "succeeded"}}
        return dict(LOST_REPLY)

    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", send)
    result = await mod.get_test_job(DummyContext(), job_id="original-job")
    assert result.success is True
    assert result.data.job_id == "original-job"
    assert observations == ["original-job", "original-job"]
