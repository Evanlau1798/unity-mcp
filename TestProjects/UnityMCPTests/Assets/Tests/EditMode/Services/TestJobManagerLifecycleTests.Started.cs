using System;
using MCPForUnity.Editor.Services;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public partial class TestJobManagerLifecycleTests
    {
        [Test]
        public void RunStartedWithUnknownCount_DoesNotTriggerInitializationTimeout()
        {
            var job = AddJob("started-with-unknown-count");
            job.InitTimeoutMs = 1;
            job.StartedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000;
            var service = RestoreCallbacks();
            service.RunStarted(null);

            var observed = TestJobManager.GetJob(job.JobId);

            Assert.AreEqual(TestJobStatus.Running, observed.Status,
                "RunStarted proves initialization completed even when no count is available.");
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId);
        }
    }
}
