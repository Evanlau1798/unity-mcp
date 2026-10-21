using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    // The existing lifecycle fixture saves and restores the outer runner's state.
    // No nested Unity test execution is scheduled by these cases.
    public partial class TestJobManagerLifecycleTests
    {
        [Test]
        public void InitializationTimeout_RetainsOwnershipUntilNativeCleanupIsConfirmed()
        {
            var job = AddJob("initialization-timeout-owner");
            job.UnityRunId = null; // The missing scheduling receipt is unknown, not idle.
            job.InitTimeoutMs = 1;
            job.StartedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000;
            RestoreCallbacks();

            var observed = TestJobManager.GetJob(job.JobId);

            Assert.AreEqual(TestJobStatus.Failed, observed.Status);
            StringAssert.Contains("initialize", observed.Error);
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId,
                "A polling deadline does not prove that the native run has stopped.");
            Assert.IsTrue(TestRunStatus.IsRunning);
            Assert.Throws<InvalidOperationException>(() => TestJobManager.StartJob(
                UnityEditor.TestTools.TestRunner.Api.TestMode.EditMode));
        }

        [Test]
        public void ClearStuckJob_DoesNotDiscardUnconfirmedNativeOwnership()
        {
            var job = AddJob("unconfirmed-native-owner");
            job.UnityRunId = null;
            RestoreCallbacks();

            bool cleared = TestJobManager.ClearStuckJob();

            Assert.IsFalse(cleared, "Clearing a tracker is not native cancellation or cleanup.");
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId);
            Assert.AreEqual(TestJobStatus.Running, job.Status);
            Assert.IsTrue(TestRunStatus.IsRunning);
        }

        [Test]
        public void ReloadRecovery_DoesNotAbandonOwnershipBecauseProgressIsOld()
        {
            var job = AddJob("old-progress-owner");
            job.LastUpdateUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 600_000;
            InvokeManager("PersistToSessionState", true);
            _jobs.Clear();
            ManagerField("_currentJobId").SetValue(null, null);

            InvokeManager("TryRestoreFromSessionState");

            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId,
                "An old heartbeat cannot establish that a native runner is inactive.");
            Assert.AreEqual(TestJobStatus.Running, _jobs[job.JobId].Status);
            Assert.NotNull(RestoreCallbacks());
        }

        [Test]
        public void TaskFallback_DoesNotReleaseFailureStillOwnedByNativeRunner()
        {
            var job = AddJob("failed-native-owner");
            RestoreCallbacks();
            job.Status = TestJobStatus.Failed;
            job.Error = "Initialization deadline elapsed; native cleanup remains unknown";

            InvokeManager("FinalizeFromTask", job.JobId,
                Task.FromResult<TestRunResult>(null));

            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId);
            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            StringAssert.Contains("native cleanup remains unknown", job.Error);
            Assert.IsTrue(TestRunStatus.IsRunning);
        }
    }
}
