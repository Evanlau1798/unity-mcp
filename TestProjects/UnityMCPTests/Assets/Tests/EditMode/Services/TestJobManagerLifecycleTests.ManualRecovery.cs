using System.Reflection;
using MCPForUnity.Editor.Services;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public partial class TestJobManagerLifecycleTests
    {
        [Test]
        public void ClearStuckJob_AfterNativeCleanupPreservesPendingSuccessfulResult()
        {
            var job = AddJob("completed-native-success");
            TestJobManager.OnRunScheduled(job.JobId, GetActiveNativeRunId());
            var service = RestoreCallbacks();
            service.RunFinished(ResultStub.Suite(new ResultStub("Fixture.Completed", "Passed")));
            var completedResult = job.Result;

            Assert.AreEqual(TestJobStatus.Succeeded, job.Status);
            Assert.AreEqual(1, completedResult.Passed);
            Assert.IsTrue(job.NativeCleanupPending);
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId);
            Assert.IsFalse(TestJobManager.ClearStuckJob(), "An active native run still owns cleanup.");
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId);

            // Only the native activity observation changes. Preserve the actual result
            // already delivered by RunFinished while releasing its completed cleanup.
            typeof(TestRunnerService).GetField("_unityRunId", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(service, "synthetic-manually-observed-inactive");
            Assert.IsTrue(TestJobManager.ClearStuckJob());

            Assert.IsNull(TestJobManager.CurrentJobId);
            Assert.IsFalse(job.NativeCleanupPending);
            Assert.AreEqual(TestJobStatus.Succeeded, job.Status);
            Assert.IsNull(job.Error);
            Assert.AreSame(completedResult, job.Result);
            Assert.AreEqual(1, job.Result.Passed);
        }
    }
}
