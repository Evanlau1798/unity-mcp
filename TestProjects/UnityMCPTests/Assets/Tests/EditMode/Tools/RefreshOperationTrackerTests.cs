using System.Reflection;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class RefreshOperationTrackerTests
    {
        private const string StateKey = "MCPForUnity.RefreshOperations.v1";
        private string _savedState;

        [SetUp]
        public void SetUp()
        {
            _savedState = SessionState.GetString(StateKey, string.Empty);
            SetReceipts();
        }

        [TearDown]
        public void TearDown()
        {
            SessionState.SetString(StateKey, _savedState);
            Signal("RestoreFromSession", false);
        }

        [Test]
        public void Query_UnknownId_IsReadOnlyAndReturnsNotFound()
        {
            var result = Command("missing", true);
            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("refresh_operation_not_found",
                result["data"]?["reason"]?.ToString(), result.ToString());
            Assert.AreEqual("missing", result["data"]?["operation_id"]?.ToString());
            Assert.AreEqual("[]", SessionState.GetString(StateKey, string.Empty),
                "A status query must not create an operation.");
        }

        [Test]
        public void Query_PendingCompile_InitialIdleDoesNotComplete()
        {
            SetReceipts(Pending("pending"));
            var result = Command("pending", true);
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("pending", result["data"]?["operation_id"]?.ToString());
            Assert.AreEqual("awaiting_compile_start", result["data"]?["status"]?.ToString());
            Assert.IsFalse(result["data"].Value<bool>("completed"));
            Assert.IsTrue(result["data"].Value<bool>("compile_requested"));
        }

        [Test]
        public void Query_WithoutId_ReturnsActiveOperation()
        {
            var finished = Pending("finished");
            finished["status"] = "succeeded";
            finished["completed"] = true;
            SetReceipts(finished, Pending("active"));
            var result = Command(null, true);
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("active", result["data"]?["operation_id"]?.ToString());
            Assert.IsFalse(result["data"].Value<bool>("completed"));
        }

        [Test]
        public void Replay_SameId_ReturnsOriginalReceipt()
        {
            var receipt = Pending("same");
            receipt["status"] = "succeeded";
            receipt["completed"] = true;
            receipt["compile_started"] = true;
            receipt["compile_finished"] = true;
            receipt["reload_observed"] = true;
            SetReceipts(receipt);
            var result = Command("same", false);
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("same", result["data"]?["operation_id"]?.ToString());
            Assert.AreEqual("succeeded", result["data"]?["status"]?.ToString());
            Assert.IsTrue(result["data"].Value<bool>("compile_requested"),
                "Replay must return the original action, not execute the new parameters.");
        }

        [Test]
        public void NewId_WhileAnotherOperationPending_ReturnsOwnerWithoutReplacingRequestId()
        {
            SetReceipts(Pending("owner"));
            var result = Command("new-request", false);
            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("refresh_in_progress", result["data"]?["reason"]?.ToString());
            Assert.AreEqual("new-request", result["data"]?["operation_id"]?.ToString());
            Assert.AreEqual("owner", result["data"]?["active_operation_id"]?.ToString());
            Assert.IsFalse(result["data"].Value<bool>("request_accepted"));
        }

        [Test]
        public void CompilationFinished_WithoutReload_RemainsPending()
        {
            SetReceipts(Pending("compile"));
            Signal("MarkCompilationStarted");
            Signal("MarkCompilationFinished");
            Signal("ObserveEditor", false, false);
            var result = Command("compile", true);
            Assert.AreEqual("awaiting_reload", result["data"]?["status"]?.ToString(), result.ToString());
            Assert.IsTrue(result["data"].Value<bool>("compile_started"));
            Assert.IsTrue(result["data"].Value<bool>("compile_finished"));
            Assert.IsFalse(result["data"].Value<bool>("completed"));
        }

        [Test]
        public void Reload_PersistedAfterCompile_CompletesTheSameOperation()
        {
            SetReceipts(Pending("reload"));
            Signal("MarkCompilationStarted");
            Signal("MarkCompilationFinished");
            Signal("MarkBeforeReload");
            Signal("RestoreFromSession", true);
            Signal("ObserveEditor", false, false);
            var result = Command("reload", true);
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("reload", result["data"]?["operation_id"]?.ToString());
            Assert.AreEqual("succeeded", result["data"]?["status"]?.ToString());
            Assert.IsTrue(result["data"].Value<bool>("reload_observed"));
            Assert.IsTrue(result["data"].Value<bool>("completed"));
        }

        [Test]
        public void CompilationError_WaitsForPipelineAndImportThenPersistsFailure()
        {
            SetReceipts(Pending("error"));
            Signal("MarkCompilationStarted");
            Signal("MarkCompilationError", "CS1002: simulated compile error");
            Signal("ObserveEditor", false, false);
            Assert.IsFalse(Command("error", true)["data"].Value<bool>("completed"),
                "An assembly error does not prove that the entire compilation stopped.");
            Signal("MarkCompilationFinished");
            Signal("ObserveEditor", false, true);
            Assert.IsFalse(Command("error", true)["data"].Value<bool>("completed"),
                "Import work still owns the refresh.");
            Signal("ObserveEditor", false, false);
            Signal("RestoreFromSession", false);
            var result = Command("error", true);
            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("failed", result["data"]?["status"]?.ToString());
            Assert.IsTrue(result["data"].Value<bool>("completed"));
            Assert.IsTrue(result["data"].Value<bool>("compilation_failed"));
            StringAssert.Contains("CS1002", result["data"]?["error"]?.ToString());
        }

        [Test]
        public void SynchronousImport_ImplicitCompileStillRequiresReload()
        {
            var receipt = Pending("implicit");
            receipt["compile_requested"] = false;
            SetReceipts(receipt);
            Signal("MarkCompilationStarted");
            Signal("MarkCompilationFinished");
            Signal("ObserveEditor", false, false);
            var result = Command("implicit", true);
            Assert.AreEqual("awaiting_reload", result["data"]?["status"]?.ToString(), result.ToString());
            Assert.IsFalse(result["data"].Value<bool>("completed"));
            Assert.IsFalse(result["data"].Value<bool>("compile_requested"));
            Assert.IsTrue(result["data"].Value<bool>("compile_started"));
        }

        [Test]
        public void SynchronousImport_WithoutCompilation_CompletesAfterImportReturns()
        {
            var receipt = Pending("assets");
            receipt["compile_requested"] = false;
            receipt["status"] = "importing";
            receipt["refresh_returned"] = false;
            SetReceipts(receipt);
            Signal("ObserveEditor", false, false);
            Assert.IsFalse(Command("assets", true)["data"].Value<bool>("completed"));
            Signal("MarkRefreshReturned", "assets");
            Signal("ObserveEditor", false, false);
            var result = Command("assets", true);
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("succeeded", result["data"]?["status"]?.ToString());
            Assert.IsTrue(result["data"].Value<bool>("completed"));
            Assert.AreEqual(JTokenType.Null, result["data"]?["compile_started"]?.Type);
        }

        [Test]
        public void CompilationBeforeDispatch_IsNotAttributedToQueuedRefresh()
        {
            var receipt = Pending("queued");
            receipt["dispatch_started"] = false;
            receipt["refresh_returned"] = false;
            receipt["status"] = "accepted";
            SetReceipts(receipt);
            Signal("MarkCompilationStarted");
            Signal("MarkCompilationFinished");
            Signal("MarkBeforeReload");
            Signal("MarkReloadObserved");
            Signal("ObserveEditor", false, false);
            var result = Command("queued", true);
            Assert.AreEqual("accepted", result["data"]?["status"]?.ToString(), result.ToString());
            Assert.AreEqual(JTokenType.Null, result["data"]?["compile_started"]?.Type);
            Assert.IsFalse(result["data"].Value<bool>("completed"));
            Assert.IsFalse(result["data"].Value<bool>("reload_observed"));
        }

        [Test]
        public void PendingImplicitCompile_DoesNotBecomeSuccessfulFromIdleSample()
        {
            var receipt = Pending("pending-implicit");
            receipt["compile_requested"] = false;
            SetReceipts(receipt);
            Signal("ObserveEditor", true, false);
            Signal("ObserveEditor", false, false);
            var result = Command("pending-implicit", true);
            Assert.IsFalse(result["data"].Value<bool>("completed"), result.ToString());
            Assert.IsTrue(result["data"].Value<bool>("compile_pending"));
        }

        [Test]
        public void Snapshot_PendingRefreshAppearsBeforeCompilationStarts()
        {
            // Warm the cache before acceptance: subsequent reads must not wait
            // for a compilation or scene-change edge to notice the receipt.
            EditorStateCache.GetSnapshot();
            SetReceipts(Pending("snapshot"));
            var refresh = EditorStateCache.GetSnapshot()["assets"]?["refresh"];
            Assert.IsNotNull(refresh);
            Assert.IsTrue(refresh.Value<bool>("is_refresh_in_progress"));
            Assert.AreEqual("snapshot", refresh["operation_id"]?.ToString());
            Assert.IsFalse(refresh.Value<bool>("completed"));
        }

        private static JObject Pending(string operationId) => new JObject
        {
            ["operation_id"] = operationId,
            ["status"] = "awaiting_compile_start",
            ["completed"] = false,
            ["dispatch_started"] = true,
            ["refresh_triggered"] = true,
            ["refresh_returned"] = true,
            ["compile_requested"] = true,
            ["compile_started"] = null,
            ["compile_finished"] = false,
            ["compilation_failed"] = false,
            ["reload_started"] = false,
            ["reload_observed"] = false,
        };

        private static JObject Command(string operationId, bool queryOnly)
        {
            // The baseline ignores query_only. These parameters also keep its
            // path side-effect free: scope=scripts and compile=none do no work.
            var parameters = new JObject
            {
                ["mode"] = "if_dirty",
                ["scope"] = "scripts",
                ["compile"] = "none",
                ["wait_for_ready"] = false,
                ["query_only"] = queryOnly,
            };
            if (operationId != null) parameters["operation_id"] = operationId;
            var task = RefreshUnity.HandleCommand(parameters);
            Assert.IsTrue(task.IsCompleted, "Receipt commands must not wait through a reload.");
            return ToJObject(task.Result);
        }

        private static void SetReceipts(params JObject[] receipts)
        {
            SessionState.SetString(StateKey,
                new JArray(receipts).ToString(Newtonsoft.Json.Formatting.None));
            Signal("RestoreFromSession", false);
        }

        private static void Signal(string method, params object[] args)
        {
            // The original code must compile and fail on public behavior,
            // rather than on the implementation type being absent.
            var tracker = typeof(RefreshUnity).Assembly.GetType(
                "MCPForUnity.Editor.Services.RefreshOperationTracker");
            if (tracker == null) return;
            var member = tracker.GetMethod(method,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(member, "Missing lifecycle signal: " + method);
            member.Invoke(null, args);
        }
    }
}
