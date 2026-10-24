using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Services;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine.TestTools;
using TestMode = UnityEditor.TestTools.TestRunner.Api.TestMode;

namespace MCPForUnityTests.Editor.Services
{
    public partial class TestJobManagerLifecycleTests
    {
        [UnityTest]
        public IEnumerator InitializationTimeout_CleansUpNativeRunBeforeStartingNextJob()
        {
            var service = MCPServiceLocator.Tests as TestRunnerService;
            var api = typeof(TestRunnerService).GetField("_testRunnerApi", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
            var scheduler = typeof(TestRunnerApi).GetField("ScheduleJob", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(scheduler);
            int scheduled = 0;
            scheduler.SetValue(api, new Func<ExecutionSettings, string>(_ => "synthetic-timeout-run-" + ++scheduled));
            SessionState.SetBool("TestRunnerNoThrottle_SettingsCaptured", false);
            EditorPrefs.SetInt("ApplicationIdleTime", 9);
            EditorPrefs.SetInt("InteractionMode", 0);
            var jobId = TestJobManager.StartJob(TestMode.EditMode);
            var job = TestJobManager.GetJob(jobId);
            Assert.AreEqual("synthetic-timeout-run-1", job.UnityRunId);
            job.StartedUnixMs -= 20_000;

            Assert.AreEqual(TestJobStatus.Failed, TestJobManager.GetJob(jobId).Status);
            Assert.IsTrue(TestJobManager.HasRunningJob, "The timed-out run still owns its native cleanup.");
            Assert.IsTrue(TestRunStatus.IsRunning);
            Assert.Throws<InvalidOperationException>(() => TestJobManager.StartJob(TestMode.EditMode));
            service.RunStarted(null);
            Assert.AreEqual(TestJobStatus.Failed, job.Status, "Late startup cannot undo the timeout.");

            // The synthetic GUID is not active in Unity. Polling must release ownership
            // even when cancellation before RunStarted produces no terminal callback.
            double deadline = EditorApplication.timeSinceStartup + 5;
            while (TestJobManager.HasRunningJob && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.IsFalse(TestJobManager.HasRunningJob);
            Assert.IsFalse(TestRunStatus.IsRunning);
            Assert.AreEqual(9, EditorPrefs.GetInt("ApplicationIdleTime"));
            Assert.AreEqual(0, EditorPrefs.GetInt("InteractionMode"));
            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            Assert.AreEqual(TestJobManager.InitializationTimeoutError, job.Error);

            var nextId = TestJobManager.StartJob(TestMode.EditMode);
            deadline = EditorApplication.timeSinceStartup + 5;
            while (scheduled < 2 && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.AreEqual(2, scheduled, "Cleanup must release the operation lock for the next run.");
            service.RunFinished(ResultStub.Suite(new ResultStub("Next.Pass", "Passed")));
            Assert.AreEqual(TestJobStatus.Succeeded, TestJobManager.GetJob(nextId).Status);
            yield return null;
        }

        [UnityTest]
        public IEnumerator InitializationTimeout_PendingCancellationSurvivesReload()
        {
            var job = AddJob();
            job.StartedUnixMs -= 20_000;
            TestJobManager.OnRunScheduled(job.JobId, "synthetic-recovered-timeout-run");
            var service = RestoreCallbacks();
            Assert.AreEqual(TestJobStatus.Failed, TestJobManager.GetJob(job.JobId).Status);
            InvokeManager("BeforeAssemblyReload");
            service.Dispose();
            ServiceField.SetValue(null, null);
            _jobs.Clear();
            ManagerField("_currentJobId").SetValue(null, null);
            InvokeManager("TryRestoreFromSessionState");

            var recovered = _jobs[job.JobId];
            Assert.AreEqual("synthetic-recovered-timeout-run", recovered.UnityRunId);
            Assert.IsTrue(recovered.InitializationTimedOut);
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId);
            Assert.NotNull(RestoreCallbacks(), "A failed job must retain callbacks while cancellation is pending.");
            double deadline = EditorApplication.timeSinceStartup + 5;
            while (TestJobManager.HasRunningJob && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.IsFalse(TestJobManager.HasRunningJob);
            Assert.IsFalse(TestRunStatus.IsRunning);
            Assert.AreEqual(TestJobStatus.Failed, recovered.Status);
            Assert.AreEqual(TestJobManager.InitializationTimeoutError, recovered.Error);
        }

        [Test]
        public void InitializationTimeout_LateRunFinishedPreservesFailure()
        {
            var job = AddJob();
            job.Status = TestJobStatus.Failed;
            job.Error = TestJobManager.InitializationTimeoutError;
            job.InitializationTimedOut = true;
            job.UnityRunId = "synthetic-late-timeout-run";
            var service = RestoreCallbacks();

            service.RunFinished(ResultStub.Suite(new ResultStub("Late.Pass", "Passed")));

            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            Assert.AreEqual(TestJobManager.InitializationTimeoutError, job.Error);
            Assert.IsNull(TestJobManager.CurrentJobId);
            Assert.IsTrue(job.InitializationTimedOut, "Keep the original timeout in the job history.");
        }

        [Test]
        public void NativeCleanup_PreservesResultAndPreferencesAcrossReload()
        {
            // Use the outer test run's GUID only for a read-only activity query. Never
            // cancel it or schedule a nested run. Its native teardown is still pending.
            var nativeId = GetActiveNativeRunId();
            var job = AddJob();
            TestJobManager.OnRunScheduled(job.JobId, nativeId);
            var service = RestoreCallbacks();
            SessionState.SetBool("TestRunnerNoThrottle_SettingsCaptured", false);
            EditorPrefs.SetInt("ApplicationIdleTime", 9);
            EditorPrefs.SetInt("InteractionMode", 0);
            TestRunnerNoThrottle.ApplyNoThrottlingPreemptive();

            service.RunFinished(ResultStub.Suite(new ResultStub("Fixture.Pass", "Passed")));
            Assert.AreEqual(TestJobStatus.Succeeded, job.Status);
            Assert.IsTrue(job.NativeCleanupPending);
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId);
            Assert.IsTrue(TestRunStatus.IsRunning);
            var callbackType = typeof(TestRunnerNoThrottle).GetNestedType("TestCallbacks", BindingFlags.NonPublic);
            var callback = (IErrorCallbacks)Activator.CreateInstance(callbackType, true);
            callback.RunFinished(null);
            Assert.IsTrue(SessionState.GetBool("TestRunnerNoThrottle_SettingsCaptured", false), "Native cleanup must retain the original preference backup.");
            InvokeManager("BeforeAssemblyReload");
            service.Dispose();
            ServiceField.SetValue(null, null);
            _jobs.Clear();
            ManagerField("_currentJobId").SetValue(null, null);
            InvokeManager("TryRestoreFromSessionState");
            var recovered = _jobs[job.JobId];
            Assert.AreEqual(1, recovered.Result.Passed);
            Assert.AreEqual("Fixture.Pass", recovered.Result.Results.Single().FullName);
            service = RestoreCallbacks();
            Assert.NotNull(service);

            // Emulate native teardown becoming idle after resetting its saved 0/1 values.
            EditorPrefs.SetInt("ApplicationIdleTime", 0);
            EditorPrefs.SetInt("InteractionMode", 1);
            typeof(TestRunnerService).GetField("_unityRunId", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(service, "synthetic-finished-cleanup");
            typeof(TestRunnerService).GetMethod("CompleteRunWhenIdle", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(service, null);

            Assert.IsFalse(TestJobManager.HasRunningJob);
            Assert.IsFalse(TestRunStatus.IsRunning);
            Assert.IsFalse(recovered.NativeCleanupPending);
            Assert.AreEqual(1, recovered.Result.Passed);
            Assert.AreEqual(9, EditorPrefs.GetInt("ApplicationIdleTime"));
            Assert.AreEqual(0, EditorPrefs.GetInt("InteractionMode"));
            Assert.IsFalse(SessionState.GetBool("TestRunnerNoThrottle_SettingsCaptured", true));
        }

        [Test]
        public void UnconfirmedNativeCleanup_FailsJobButKeepsOwnershipUntilStateIsKnown()
        {
            var job = AddJob();
            TestJobManager.OnRunScheduled(job.JobId, "synthetic-unconfirmed-run");
            var service = RestoreCallbacks();
            // The native activity query cannot be made to throw here, so end the recovery window directly.
            typeof(TestRunnerService).GetMethod("StopMonitoringUnconfirmedRun", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(service, null);

            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            StringAssert.Contains("run_tests(clear_stuck=true)", job.Error);
            Assert.IsTrue(job.NativeCleanupPending, "A domain reload must resume the cleanup check.");
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId, "An unconfirmed run must keep ownership.");
            Assert.IsTrue(TestRunStatus.IsRunning);

            // A later callback whose activity query works (the synthetic GUID is idle) releases ownership.
            service.RunFinished(ResultStub.Suite(new ResultStub("Fixture.Pass", "Passed")));
            Assert.IsFalse(TestJobManager.HasRunningJob);
            Assert.IsFalse(TestRunStatus.IsRunning);
            Assert.AreEqual(TestJobStatus.Succeeded, job.Status);
            Assert.IsFalse(job.NativeCleanupPending);
        }

        [Test]
        public void InitializationTimeout_UnconfirmedCleanupKeepsItsMessage()
        {
            var job = AddJob();
            job.InitializationTimedOut = true;
            string message = TestRunnerService.NativeStateUnconfirmedError + TestRunnerService.RunErrorMarker + TestJobManager.InitializationTimeoutError;

            TestJobManager.FinalizeCurrentJobFromRunError(message, keepOwnership: true);

            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            Assert.AreEqual(message, job.Error);
            Assert.IsTrue(job.InitializationTimedOut, "A domain reload must still resume the timeout cleanup.");
            Assert.IsTrue(job.NativeCleanupPending);
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId);
        }

        [Test]
        public void InitializationTimeout_OtherRunErrorsAreStillReportedAsTheTimeout()
        {
            var job = AddJob();
            job.InitializationTimedOut = true;

            TestJobManager.FinalizeCurrentJobFromRunError("Unity test run failed.", keepOwnership: true);

            Assert.AreEqual(TestJobManager.InitializationTimeoutError, job.Error);
        }

        private static string GetActiveNativeRunId()
        {
            var holderProperty = typeof(TestRunnerApi).GetProperty("m_testJobDataHolder", PrivateStatic);
            if (holderProperty != null)
            {
                var holder = holderProperty.GetValue(null);
                var runners = (IEnumerable)holder.GetType().GetMethod("GetAllRunners").Invoke(holder, null);
                var runner = runners.Cast<object>().First(r => (bool)r.GetType().GetMethod("IsRunningJob").Invoke(r, null));
                var data = runner.GetType().GetMethod("GetData").Invoke(runner, null);
                return (string)data.GetType().GetField("guid").GetValue(data);
            }
            // The installed 1.1.33 stores GUID/isRunning directly in the serialized list.
            var type = typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder");
            Assert.NotNull(type);
            var instance = type.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy).GetValue(null);
            var runs = (IEnumerable)type.GetField("TestRuns").GetValue(instance);
            var active = runs.Cast<object>().First(r => (bool)r.GetType().GetField("isRunning").GetValue(r));
            return (string)active.GetType().GetField("guid").GetValue(active);
        }

        [Test]
        public void NativeQueryFailure_ActualUnknownPathRetainsOwnershipAndCanRecover()
        {
            var job = AddJob("query-failure-owner");
            var service = RestoreCallbacks();
            var runId = typeof(TestRunnerService).GetField("_unityRunId", BindingFlags.Instance | BindingFlags.NonPublic);
            runId.SetValue(service, null); // Missing identity makes the real query throw.
            service.RunFinished(ResultStub.Suite(new ResultStub("Fixture.Pass", "Passed")));
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId);
            Assert.IsTrue(job.NativeCleanupPending);
            var failedSince = typeof(TestRunnerService).GetField("_nativeQueryFailedSince", BindingFlags.Instance | BindingFlags.NonPublic);
            failedSince.SetValue(service, EditorApplication.timeSinceStartup - 31.0);
            var monitor = typeof(TestRunnerService).GetMethod("CompleteRunWhenIdle", BindingFlags.Instance | BindingFlags.NonPublic);
            monitor.Invoke(service, null);
            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId);
            StringAssert.Contains("could not be confirmed", job.Error);
            runId.SetValue(service, "synthetic-native-cleanup-finished");
            monitor.Invoke(service, null);
            Assert.IsNull(TestJobManager.CurrentJobId);
            Assert.AreEqual(TestJobStatus.Succeeded, job.Status);
            Assert.AreEqual(1, job.Result.Passed);
        }

    }
}
