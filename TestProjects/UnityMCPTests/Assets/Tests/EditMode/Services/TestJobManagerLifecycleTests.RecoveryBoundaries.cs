using System;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using TestMode = UnityEditor.TestTools.TestRunner.Api.TestMode;

namespace MCPForUnityTests.Editor.Services
{
    public partial class TestJobManagerLifecycleTests
    {
        [TestCase("{\"current_job_id\":\"orphan-owner\",\"jobs\":[]}", "", "orphan-owner")]
        [TestCase("{\"current_job_id\":\"missing-list-owner\",\"jobs\":null}", "", "missing-list-owner")]
        [TestCase("{broken", "legacy-owner", "legacy-owner")]
        [TestCase("null", "legacy-null-owner", "legacy-null-owner")]
        [TestCase("{\"current_job_id\":null,\"jobs\":[]}", "newer-legacy-owner", "newer-legacy-owner")]
        [TestCase("{\"current_job_id\":\"header-owner\",\"jobs\":\"broken-list\"}", "", "header-owner")]
        public void RestoreIncompleteState_RetainsKnownOwnerWithoutInventingAJob(
            string state, string legacyOwner, string expectedOwner)
        {
            SessionState.SetString(JobsKey, state);
            SessionState.SetString(CurrentKey, legacyOwner);
            InvokeManager("TryRestoreFromSessionState");

            Assert.AreEqual(expectedOwner, TestJobManager.CurrentJobId);
            Assert.IsTrue(TestJobManager.HasRunningJob);
            Assert.IsNull(TestJobManager.GetJob(expectedOwner), "Incomplete history must not fabricate a job or mode.");
            Assert.IsNull(RestoreCallbacks(), "No native identity can be recovered from an absent job.");
            Assert.IsFalse(TestJobManager.ClearStuckJob());
            Assert.AreEqual(expectedOwner, TestJobManager.CurrentJobId);
            Assert.Throws<InvalidOperationException>(() => TestJobManager.StartJob(TestMode.EditMode));
        }

        [Test]
        public void PersistenceHistoryLimit_RetainsOldOwnedJobAndNativeIdentity()
        {
            var owned = AddJob("old-owned-history");
            owned.LastUpdateUnixMs = 1;
            for (int i = 0; i < 12; i++)
            {
                var history = new TestJob
                {
                    JobId = "history-" + i, Mode = "EditMode", Status = TestJobStatus.Succeeded,
                    LastUpdateUnixMs = 1000 + i
                };
                _jobs.Add(history.JobId, history);
            }
            InvokeManager("PersistToSessionState", true);
            var saved = JObject.Parse(SessionState.GetString(JobsKey, ""));
            Assert.AreEqual(10, saved["jobs"].Count());
            _jobs.Clear();
            ManagerField("_currentJobId").SetValue(null, null);
            InvokeManager("TryRestoreFromSessionState");

            Assert.AreEqual(owned.JobId, TestJobManager.CurrentJobId);
            Assert.IsTrue(_jobs.ContainsKey(owned.JobId));
            Assert.AreEqual(owned.UnityRunId, _jobs[owned.JobId].UnityRunId);
        }

        [TestCase("success")]
        [TestCase("failure")]
        [TestCase("canceled")]
        public void TaskFallback_WithUnknownNativeIdentityRetainsOwnership(string outcome)
        {
            var job = AddJob("task-unknown-" + outcome);
            job.UnityRunId = null;
            RestoreCallbacks();
            Task<TestRunResult> task;
            if (outcome == "failure") task = Task.FromException<TestRunResult>(new InvalidOperationException("Task failed"));
            else if (outcome == "canceled")
            {
                var canceled = new TaskCompletionSource<TestRunResult>();
                canceled.SetCanceled();
                task = canceled.Task;
            }
            else task = Task.FromResult(TestRunResult.Create(ResultStub.Suite(new ResultStub("Fixture.Pass", "Passed")), Array.Empty<ITestResultAdaptor>()));

            InvokeManager("FinalizeFromTask", job.JobId, task);

            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId, "Task completion cannot prove native cleanup.");
            Assert.IsTrue(job.NativeCleanupPending);
            Assert.IsTrue(TestRunStatus.IsRunning);
            Assert.AreEqual(outcome == "success" ? TestJobStatus.Succeeded : TestJobStatus.Failed, job.Status);
            Assert.IsFalse(TestJobManager.ClearStuckJob());
        }

        [Test]
        public void TaskFallback_WithConfirmedInactiveNativeGuidCanReleaseOwnership()
        {
            var job = AddJob("task-known-inactive");
            RestoreCallbacks();
            var result = TestRunResult.Create(ResultStub.Suite(new ResultStub("Fixture.Pass", "Passed")), Array.Empty<ITestResultAdaptor>());

            InvokeManager("FinalizeFromTask", job.JobId, Task.FromResult(result));

            Assert.IsNull(TestJobManager.CurrentJobId);
            Assert.IsFalse(TestRunStatus.IsRunning);
            Assert.AreEqual(TestJobStatus.Succeeded, job.Status);
            Assert.AreSame(result, job.Result);
        }

        [Test]
        public void TaskFallback_WithMissingJobRecordRetainsOriginalOwner()
        {
            ManagerField("_currentJobId").SetValue(null, "missing-task-owner");
            InvokeManager("FinalizeFromTask", "missing-task-owner", Task.FromResult<TestRunResult>(null));
            Assert.AreEqual("missing-task-owner", TestJobManager.CurrentJobId);
            Assert.IsTrue(TestJobManager.HasRunningJob);
        }

        [Test]
        public void TaskFallback_ForHistoricalJobDoesNotChangeNewOwner()
        {
            var old = AddJob("historical-task");
            var current = AddJob("current-task-owner");
            RestoreCallbacks();
            InvokeManager("FinalizeFromTask", old.JobId, Task.FromResult<TestRunResult>(null));
            Assert.AreEqual(current.JobId, TestJobManager.CurrentJobId);
            Assert.AreEqual(TestJobStatus.Running, current.Status);
            Assert.IsTrue(TestRunStatus.IsRunning);
        }
    }
}
