using System;
using System.Reflection;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using TestMode = UnityEditor.TestTools.TestRunner.Api.TestMode;

namespace MCPForUnityTests.Editor.Services
{
    public partial class TestJobManagerLifecycleTests
    {
        [Test]
        public void StartCommand_DirtyUnnamedSceneIsRejectedBeforeJobAllocation()
        {
            WithDirtyUnnamedScene(() =>
            {
                bool executeCalled = false;
                var service = StubSceneTestScheduler(() => executeCalled = true);
                try
                {
                    var reply = RunTestCommand(new JObject { ["mode"] = "EditMode" });

                    Assert.IsFalse(reply.Value<bool>("success"), reply.ToString());
                    Assert.AreEqual("unsaved_scene", reply["data"]?["reason"]?.ToString());
                    StringAssert.Contains("Save or close", reply["data"]?["message"]?.ToString());
                    Assert.IsFalse(reply["data"].Value<bool>("request_accepted"));
                    Assert.IsFalse(executeCalled, "Scene validation must precede native Execute.");
                    Assert.IsNull(TestJobManager.CurrentJobId);
                    Assert.AreEqual(0, _jobs.Count, "A rejected preflight must not leave a phantom job.");
                    Assert.IsFalse(TestRunStatus.IsRunning);
                }
                finally { FinishSyntheticSceneRun(service); }
            });
        }

        [Test]
        public void PreDispatch_DirtyUnnamedSceneFailsSafelyAfterJobRegistration()
        {
            var job = AddJob("scene-changed-after-registration");
            WithDirtyUnnamedScene(() =>
            {
                bool executeCalled = false;
                var service = StubSceneTestScheduler(() => executeCalled = true);
                try
                {
                    var pending = service.RunTestsAsync(TestMode.EditMode);
                    Assert.IsTrue(pending.IsCompleted, "The pre-dispatch scene check must fail immediately.");
                    var error = Assert.Catch<InvalidOperationException>(() => pending.GetAwaiter().GetResult());
                    StringAssert.Contains("Save or close", error.Message);
                    Assert.IsFalse(executeCalled);
                    Assert.IsNull(TestJobManager.CurrentJobId);
                    Assert.AreEqual(TestJobStatus.Failed, job.Status);
                    Assert.IsFalse(job.NativeCleanupPending);
                    Assert.IsFalse(TestRunStatus.IsRunning);
                }
                finally { FinishSyntheticSceneRun(service); }
            });
        }

        private static TestRunnerService StubSceneTestScheduler(Action onExecute)
        {
            var service = MCPServiceLocator.Tests as TestRunnerService;
            var api = typeof(TestRunnerService).GetField("_testRunnerApi", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(service);
            var scheduler = typeof(TestRunnerApi).GetField("ScheduleJob", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(scheduler);
            scheduler.SetValue(api, new Func<ExecutionSettings, string>(_ =>
            {
                onExecute();
                return "synthetic-scene-preflight-run";
            }));
            return service;
        }

        private static void FinishSyntheticSceneRun(TestRunnerService service)
        {
            // A failing baseline may have accepted the stubbed run. Complete only
            // that synthetic service; no native run was scheduled by the delegate.
            if (TestJobManager.CurrentJobId != null)
                service.RunFinished(ResultStub.Suite(new ResultStub("Synthetic.Scene.Pass", "Passed")));
        }

        private static void WithDirtyUnnamedScene(Action action)
        {
            // Unity cannot add an Editor scene while its runner has an untitled scene.
            // Reuse that empty test context without creating objects or saving it.
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var existing = SceneManager.GetSceneAt(i);
                if (!string.IsNullOrEmpty(existing.path)) continue;
                bool wasDirty = existing.isDirty;
                var clearDirty = typeof(EditorSceneManager).GetMethod("ClearSceneDirtiness",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (!wasDirty) Assert.NotNull(clearDirty, "The fixture must restore a previously clean scene.");
                try
                {
                    if (!wasDirty) EditorSceneManager.MarkSceneDirty(existing);
                    action();
                }
                finally
                {
                    if (!wasDirty) clearDirty.Invoke(null, new object[] { existing });
                }
                return;
            }

            var originalActiveScene = SceneManager.GetActiveScene();
            var ownScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                Assert.IsTrue(SceneManager.SetActiveScene(ownScene));
                // Create only in our additive scene, so the outer runner's scene is untouched.
                new GameObject("MCP scene preflight test object");
                EditorSceneManager.MarkSceneDirty(ownScene);
                Assert.IsTrue(ownScene.isDirty);
                Assert.IsTrue(string.IsNullOrEmpty(ownScene.path));
                action();
            }
            finally
            {
                if (originalActiveScene.IsValid() && originalActiveScene.isLoaded)
                    SceneManager.SetActiveScene(originalActiveScene);
                if (ownScene.IsValid() && ownScene.isLoaded)
                    EditorSceneManager.CloseScene(ownScene, true);
            }
        }
    }
}
