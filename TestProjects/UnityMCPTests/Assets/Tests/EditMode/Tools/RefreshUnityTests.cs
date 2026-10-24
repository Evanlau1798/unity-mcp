using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class RefreshUnityTests
    {
        [Test]
        public void HandleCommand_Query_NoWait_CompletesSynchronously()
        {
            // Status reads must remain usable while tests run and must not
            // schedule a refresh merely to determine whether a receipt exists.
            var operationId = "missing-" + Guid.NewGuid().ToString("N");
            var task = RefreshUnity.HandleCommand(new JObject
            {
                ["operation_id"] = operationId,
                ["query_only"] = true,
                ["mode"] = "if_dirty",
                ["scope"] = "scripts",
                ["compile"] = "none",
                ["wait_for_ready"] = false,
            });
            Assert.IsTrue(task.IsCompleted, "Queries must not wait through a reload.");
            var result = ToJObject(task.Result);
            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("refresh_operation_not_found", result["data"]?["reason"]?.ToString());
            Assert.AreEqual(operationId, result["data"]?["operation_id"]?.ToString());
        }

        [Test]
        public void WaitForCompilationToStart_CounterAlreadyMoved_CompletesSynchronouslyTrue()
        {
            // A compile that began and ended inside AssetDatabase.Refresh leaves only
            // the counter behind. Presenting a stale "before" value reproduces that
            // state without triggering a compile.
            var task = RefreshUnity.WaitForCompilationToStartAsync(
                EditorStateCache.CompileCount - 1,
                TimeSpan.FromSeconds(10));

            Assert.IsTrue(task.IsCompleted, "counter already moved must resolve without a tick");
            Assert.IsTrue(task.Result, "a moved counter is a start, not a grace expiry");
        }

        [UnityTest]
        public IEnumerator WaitForCompilationToStart_GraceElapsed_ResolvesFalse()
        {
            // No compile is requested here, so with the counter current the only way
            // out is the grace. A zero grace expires on the first update tick.
            var task = RefreshUnity.WaitForCompilationToStartAsync(
                EditorStateCache.CompileCount,
                TimeSpan.Zero);

            Assert.IsFalse(task.IsCompleted, "nothing has started, so the wait must actually wait");

            double deadline = EditorApplication.timeSinceStartup + 5.0;
            while (!task.IsCompleted)
            {
                if (EditorApplication.timeSinceStartup > deadline)
                {
                    Assert.Fail("grace expiry never resolved the wait");
                }
                yield return null;
            }

            Assert.IsFalse(task.Result, "grace expiry must report that no compile started");
        }
    }
}
