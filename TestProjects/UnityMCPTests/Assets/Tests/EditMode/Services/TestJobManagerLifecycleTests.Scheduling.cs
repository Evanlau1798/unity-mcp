using System;
using System.Collections;
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
        public IEnumerator EditMode_ExecuteDisablesThrottlingBeforeRunStarted()
        {
            AddJob();
            var service = MCPServiceLocator.Tests as TestRunnerService;
            var api = typeof(TestRunnerService).GetField("_testRunnerApi", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
            var scheduler = typeof(TestRunnerApi).GetField("ScheduleJob", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(scheduler);
            int idleAtExecute = -1;
            int modeAtExecute = -1;
            scheduler.SetValue(api, new Func<ExecutionSettings, string>(_ =>
            {
                idleAtExecute = EditorPrefs.GetInt("ApplicationIdleTime", 4);
                modeAtExecute = EditorPrefs.GetInt("InteractionMode", 0);
                return "synthetic-background-run";
            }));
            SessionState.SetBool("TestRunnerNoThrottle_SettingsCaptured", false);
            EditorPrefs.SetInt("ApplicationIdleTime", 9);
            EditorPrefs.SetInt("InteractionMode", 0);

            var pending = service.RunTestsAsync(TestMode.EditMode);
            service.RunFinished(ResultStub.Suite(new ResultStub("Fixture.Pass", "Passed")));
            double deadline = EditorApplication.timeSinceStartup + 5;
            while (!pending.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.IsTrue(pending.IsCompleted);
            pending.GetAwaiter().GetResult();
            Assert.AreEqual(0, idleAtExecute, "Setup must advance without background throttling before RunStarted.");
            Assert.AreEqual(1, modeAtExecute);
            Assert.AreEqual(9, EditorPrefs.GetInt("ApplicationIdleTime"));
            Assert.AreEqual(0, EditorPrefs.GetInt("InteractionMode"));
        }

        [Test]
        public void PreDispatchFailure_RestoresPreemptiveThrottlingSettings()
        {
            var job = AddJob();
            var service = MCPServiceLocator.Tests as TestRunnerService;
            var api = typeof(TestRunnerService).GetField("_testRunnerApi", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
            var scheduler = typeof(TestRunnerApi).GetField("ScheduleJob", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(scheduler);
            bool executeCalled = false;
            scheduler.SetValue(api, new Func<ExecutionSettings, string>(_ =>
            {
                executeCalled = true;
                throw new InvalidOperationException("Unexpected entry into native Execute");
            }));
            // A destroyed Unity API object is a genuine pre-dispatch validation failure.
            // The injected delegate prevents a baseline failure from starting a nested run.
            UnityEngine.Object.DestroyImmediate((TestRunnerApi)api);
            SessionState.SetBool("TestRunnerNoThrottle_SettingsCaptured", false);
            EditorPrefs.SetInt("ApplicationIdleTime", 9);
            EditorPrefs.SetInt("InteractionMode", 0);

            Assert.Throws<InvalidOperationException>(() => service.RunTestsAsync(TestMode.EditMode).GetAwaiter().GetResult());

            Assert.IsFalse(executeCalled, "An invalid API object must be rejected before native dispatch.");
            Assert.IsNull(TestJobManager.CurrentJobId);
            Assert.AreEqual(TestJobStatus.Failed, job.Status);
            Assert.AreEqual(9, EditorPrefs.GetInt("ApplicationIdleTime"));
            Assert.AreEqual(0, EditorPrefs.GetInt("InteractionMode"));
            Assert.IsFalse(SessionState.GetBool("TestRunnerNoThrottle_SettingsCaptured", true));
            Assert.IsFalse(TestRunStatus.IsRunning);
        }

    }
}
