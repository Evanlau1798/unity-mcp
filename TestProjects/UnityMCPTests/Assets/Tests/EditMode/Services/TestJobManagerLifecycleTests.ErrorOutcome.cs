using System.Reflection;
using MCPForUnity.Editor.Services;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public partial class TestJobManagerLifecycleTests
    {
        [Test]
        public void NativeRunError_LateSuccessfulCallbackPreservesOriginalFailure()
        {
            var job = AddJob("native-error-owner");
            job.UnityRunId = null;
            var service = RestoreCallbacks();
            service.OnError("Native dispatch failed after entry");
            service.RunFinished(ResultStub.Suite(new ResultStub("Late.Pass", "Passed")));

            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            Assert.AreEqual("Native dispatch failed after entry", job.Error);
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId);
            Assert.IsTrue(job.NativeCleanupPending);

            typeof(TestRunnerService).GetField("_unityRunId", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(service, "synthetic-native-error-cleanup-finished");
            typeof(TestRunnerService).GetMethod("CompleteRunWhenIdle", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(service, null);
            Assert.IsNull(TestJobManager.CurrentJobId);
            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            Assert.AreEqual("Native dispatch failed after entry", job.Error);
        }

        [Test]
        public void NativeRunError_LaterErrorPreservesFirstFailureDuringCleanup()
        {
            var job = AddJob("native-first-error-owner");
            job.UnityRunId = null;
            var service = RestoreCallbacks();
            service.OnError("Primary native dispatch failure");
            service.OnError("Secondary teardown failure");

            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            Assert.AreEqual("Primary native dispatch failure", job.Error);
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId);
            Assert.IsTrue(job.NativeCleanupPending);

            typeof(TestRunnerService).GetField("_unityRunId", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(service, "synthetic-primary-error-cleanup-finished");
            typeof(TestRunnerService).GetMethod("CompleteRunWhenIdle", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(service, null);
            Assert.IsNull(TestJobManager.CurrentJobId);
            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            Assert.AreEqual("Primary native dispatch failure", job.Error);
        }
    }
}
