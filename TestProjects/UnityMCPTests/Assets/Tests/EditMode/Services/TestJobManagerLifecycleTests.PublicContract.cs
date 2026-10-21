using System;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using TestMode = UnityEditor.TestTools.TestRunner.Api.TestMode;

namespace MCPForUnityTests.Editor.Services
{
    public partial class TestJobManagerLifecycleTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void ClearCommand_ReportsRetainedNativeOwnership(bool knownActive)
        {
            var job = AddJob("public-native-owner");
            job.UnityRunId = knownActive ? GetActiveNativeRunId() : null;
            RestoreCallbacks();
            var reply = RunTestCommand(new JObject { ["clear_stuck"] = true });

            Assert.IsFalse(reply.Value<bool>("success"), reply.ToString());
            Assert.AreEqual("tests_running", reply["data"]?["reason"]?.ToString());
            Assert.AreEqual(job.JobId, reply["data"]?["job_id"]?.ToString());
            Assert.IsTrue(reply["data"].Value<bool>("native_ownership_retained"));
            Assert.IsFalse(reply["data"].Value<bool>("cleared"));
            Assert.AreEqual(job.JobId, TestJobManager.CurrentJobId);
        }

        [Test]
        public void ClearCommand_OrphanOwnerDoesNotClaimNothingIsRunning()
        {
            ManagerField("_currentJobId").SetValue(null, "public-orphan-owner");
            var reply = RunTestCommand(new JObject { ["clear_stuck"] = true });
            Assert.IsFalse(reply.Value<bool>("success"), reply.ToString());
            Assert.AreEqual("public-orphan-owner", reply["data"]?["job_id"]?.ToString());
            Assert.IsTrue(reply["data"].Value<bool>("native_ownership_retained"));
            Assert.AreEqual("public-orphan-owner", TestJobManager.CurrentJobId);
        }

        [Test]
        public void ClearCommand_WithoutOwnerRemainsSuccessfulNoOp()
        {
            var reply = RunTestCommand(new JObject { ["clear_stuck"] = true });
            Assert.IsTrue(reply.Value<bool>("success"), reply.ToString());
            Assert.IsFalse(reply["data"].Value<bool>("cleared"));
            Assert.IsFalse(reply["data"].Value<bool>("native_ownership_retained"));
        }

        [Test]
        public void ClearCommand_ConfirmedInactiveOwnerCanBeCleared()
        {
            var job = AddJob("public-inactive-owner");
            RestoreCallbacks();
            var reply = RunTestCommand(new JObject { ["clear_stuck"] = true });
            Assert.IsTrue(reply.Value<bool>("success"), reply.ToString());
            Assert.IsTrue(reply["data"].Value<bool>("cleared"));
            Assert.IsFalse(reply["data"].Value<bool>("native_ownership_retained"));
            Assert.IsNull(TestJobManager.CurrentJobId);
        }

        [Test]
        public void StartCommand_ExistingOwnerReturnsOwnerIdentity()
        {
            var job = AddJob("public-start-owner");
            var reply = RunTestCommand(new JObject { ["mode"] = "EditMode" });
            Assert.IsFalse(reply.Value<bool>("success"), reply.ToString());
            Assert.AreEqual("tests_running", reply["data"]?["reason"]?.ToString());
            Assert.AreEqual(job.JobId, reply["data"]?["job_id"]?.ToString());
            Assert.IsTrue(reply["data"].Value<bool>("native_ownership_retained"));
        }

        [Test]
        public void StartCommand_RefreshOwnershipHasDistinctBusyReason()
        {
            const string key = "MCPForUnity.RefreshOperations.v1";
            string original = SessionState.GetString(key, string.Empty);
            try
            {
                SessionState.SetString(key, new JArray(new JObject
                {
                    ["operation_id"] = "public-pending-refresh", ["completed"] = false,
                    ["dispatch_started"] = true, ["compile_requested"] = true,
                    ["status"] = "awaiting_compile_start"
                }).ToString());
                RefreshOperationTracker.RestoreFromSession(false);
                var reply = RunTestCommand(new JObject { ["mode"] = "EditMode" });
                Assert.IsFalse(reply.Value<bool>("success"), reply.ToString());
                Assert.AreEqual("refresh_in_progress", reply["data"]?["reason"]?.ToString());
                Assert.AreEqual("public-pending-refresh", reply["data"]?["active_operation_id"]?.ToString());
                Assert.IsNull(TestJobManager.CurrentJobId);
            }
            finally
            {
                SessionState.SetString(key, original);
                RefreshOperationTracker.RestoreFromSession(false);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EditorSnapshot_OrphanOwnerIsBusyInWarmAndFreshCache(bool rebuild)
        {
            WithEditorSnapshotState(() =>
            {
                ForceEditorSnapshot();
                Assert.IsFalse(EditorStateCache.GetSnapshot()["tests"].Value<bool>("is_running"));
                ManagerField("_currentJobId").SetValue(null, "snapshot-orphan-owner");
                if (rebuild) ForceEditorSnapshot();
                var snapshot = EditorStateCache.GetSnapshot();
                Assert.IsTrue(snapshot["tests"].Value<bool>("is_running"), snapshot.ToString());
                Assert.AreEqual("snapshot-orphan-owner", snapshot["tests"]?["current_job_id"]?.ToString());
                Assert.AreEqual("running_tests", snapshot["activity"]?["phase"]?.ToString());
                Assert.AreEqual(JTokenType.Null, snapshot["tests"]?["mode"]?.Type);
            });
        }

        [Test]
        public void EditorSnapshot_ReleasedOwnerDoesNotLeaveCachedRunningPhase()
        {
            WithEditorSnapshotState(() =>
            {
                AddJob("snapshot-released-owner");
                TestRunStatus.MarkStarted(TestMode.EditMode);
                ForceEditorSnapshot();
                ManagerField("_currentJobId").SetValue(null, null);
                TestRunStatus.MarkFinished();
                var snapshot = EditorStateCache.GetSnapshot();
                Assert.IsFalse(snapshot["tests"].Value<bool>("is_running"), snapshot.ToString());
                Assert.AreEqual(JTokenType.Null, snapshot["tests"]?["current_job_id"]?.Type);
                Assert.AreNotEqual("running_tests", snapshot["activity"]?["phase"]?.ToString());
            });
        }

        private static JObject RunTestCommand(JObject parameters)
        {
            var task = RunTests.HandleCommand(parameters);
            Assert.IsTrue(task.IsCompleted, "Busy/clear responses must not dispatch a native test.");
            return JObject.FromObject(task.Result);
        }

        private static void ForceEditorSnapshot() => typeof(EditorStateCache)
            .GetMethod("ForceUpdate", PrivateStatic).Invoke(null, new object[] { "ownership_contract_test" });

        private static void WithEditorSnapshotState(Action action)
        {
            var fields = new[] { "_cached", "_sequence", "_observedUnixMs" }
                .Select(name => typeof(EditorStateCache).GetField(name, PrivateStatic)).ToArray();
            var saved = fields.Select(field => field.GetValue(null)).ToArray();
            try { action(); }
            finally
            {
                for (int i = 0; i < fields.Length; i++) fields[i].SetValue(null, saved[i]);
            }
        }
    }
}
