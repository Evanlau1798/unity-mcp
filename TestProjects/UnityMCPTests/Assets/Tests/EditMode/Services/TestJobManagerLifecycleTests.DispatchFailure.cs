using System;
using System.Collections;
using System.Reflection;
using MCPForUnity.Editor.Services;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using TestMode = UnityEditor.TestTools.TestRunner.Api.TestMode;
using UnityEngine.TestTools;

namespace MCPForUnityTests.Editor.Services
{
    public partial class TestJobManagerLifecycleTests
    {
        [UnityTest]
        public IEnumerator NativeDispatchException_RetainsOwnershipAndSettingsUntilCleanupIsKnown()
        {
            var job = AddJob("dispatch-unknown-owner");
            job.UnityRunId = null;
            var service = MCPServiceLocator.Tests as TestRunnerService;
            var api = typeof(TestRunnerService).GetField("_testRunnerApi", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
            var scheduler = typeof(TestRunnerApi).GetField("ScheduleJob", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(scheduler);
            scheduler.SetValue(api, new Func<ExecutionSettings, string>(_ => throw new InvalidOperationException("Native dispatch failed after entry")));
            SessionState.SetBool("TestRunnerNoThrottle_SettingsCaptured", false);
            EditorPrefs.SetInt("ApplicationIdleTime", 9);
            EditorPrefs.SetInt("InteractionMode", 0);

            var pending = service.RunTestsAsync(TestMode.EditMode);
            if (pending.IsFaulted) _ = pending.Exception;
            Assert.IsFalse(pending.IsCompleted, "The owning task must wait for positive cleanup evidence.");
            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            StringAssert.Contains("Native dispatch failed", job.Error);
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId);
            Assert.IsTrue(job.NativeCleanupPending);
            Assert.IsTrue(TestRunStatus.IsRunning);
            Assert.AreEqual(0, EditorPrefs.GetInt("ApplicationIdleTime"));
            Assert.AreEqual(1, EditorPrefs.GetInt("InteractionMode"));
            Assert.IsTrue(SessionState.GetBool("TestRunnerNoThrottle_SettingsCaptured", false));
            Assert.IsFalse(TestJobManager.ClearStuckJob());

            // A recovered per-run identity now positively establishes inactivity.
            typeof(TestRunnerService).GetField("_unityRunId", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(service, "synthetic-recovered-dispatch-cleanup");
            typeof(TestRunnerService).GetMethod("CompleteRunWhenIdle", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(service, null);
            double deadline = EditorApplication.timeSinceStartup + 5;
            while (!pending.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.IsTrue(pending.IsFaulted);
            StringAssert.Contains("Native dispatch failed", pending.Exception.GetBaseException().Message);
            Assert.IsNull(TestJobManager.CurrentJobId);
            Assert.IsFalse(TestRunStatus.IsRunning);
            Assert.AreEqual(9, EditorPrefs.GetInt("ApplicationIdleTime"));
            Assert.AreEqual(0, EditorPrefs.GetInt("InteractionMode"));
            Assert.IsFalse(SessionState.GetBool("TestRunnerNoThrottle_SettingsCaptured", true));
        }
    }
}
