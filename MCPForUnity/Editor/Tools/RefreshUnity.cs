using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>Refresh mutations have persistent receipts; status queries never dispatch work.</summary>
    [McpForUnityTool("refresh_unity", AutoRegister = false)]
    public static class RefreshUnity
    {
        public static Task<object> HandleCommand(JObject @params)
        {
            string operationId = @params?["operation_id"]?.ToString();
            if (string.IsNullOrWhiteSpace(operationId)) operationId = null;
            bool queryOnly = ParamCoercion.CoerceBool(@params?["query_only"], false);
            if (queryOnly) return Task.FromResult(RefreshOperationTracker.Query(operationId));

            // Replays are reads, even when tests are running or the replay supplies
            // different action parameters. Never execute the same receipt twice.
            if (operationId != null && RefreshOperationTracker.TryReplay(operationId, out var replay))
                return Task.FromResult(replay);
            operationId = operationId ?? Guid.NewGuid().ToString("N");

            string mode = (@params?["mode"]?.ToString() ?? "if_dirty").ToLowerInvariant();
            string scope = (@params?["scope"]?.ToString() ?? "all").ToLowerInvariant();
            string compile = (@params?["compile"]?.ToString() ?? "none").ToLowerInvariant();
            if (operationId.Length > 128 || (mode != "force" && mode != "if_dirty")
                || (scope != "all" && scope != "assets" && scope != "scripts")
                || (compile != "none" && compile != "request"))
                return Task.FromResult(RefreshOperationTracker.Rejected("invalid_refresh_parameters", operationId));
            if (RefreshOperationTracker.IsInProgress)
                return Task.FromResult(RefreshOperationTracker.Rejected("refresh_in_progress", operationId));
            if (TestRunStatus.IsRunning || !string.IsNullOrEmpty(TestJobManager.CurrentJobId))
                return Task.FromResult(RefreshOperationTracker.Rejected("tests_running", operationId));

            // Persist acceptance before the editor update can start native work.
            // Waiting through a reload belongs to the server, which can reconnect
            // and query this receipt; an in-domain Task cannot survive that reload.
            object response = RefreshOperationTracker.Begin(operationId, mode, scope, compile == "request");
            try { EditorApplication.QueuePlayerLoopUpdate(); } catch { }
            return Task.FromResult(response);
        }

        internal static void DispatchPendingOperation()
        {
            var receipt = RefreshOperationTracker.ActiveReceipt;
            if (receipt == null || receipt.dispatch_started) return;
            if (TestRunStatus.IsRunning || !string.IsNullOrEmpty(TestJobManager.CurrentJobId)
                || EditorApplication.isCompiling || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode) return;

            RefreshOperationTracker.MarkDispatchStarted();
            try
            {
                // Script additions/deletions also need import. The server only
                // acknowledges the requested scope, even if this imports more.
                var options = ImportAssetOptions.ForceSynchronousImport;
                if (receipt.mode == "force") options |= ImportAssetOptions.ForceUpdate;
                AssetDatabase.Refresh(options);
                RefreshOperationTracker.MarkRefreshReturned(receipt.operation_id);

                // Synchronous refresh can itself run compilation. Do not schedule
                // a second cycle when this operation already observed that work.
                if (!receipt.completed && receipt.compile_requested && receipt.compile_started != true)
                    CompilationPipeline.RequestScriptCompilation();
                RefreshOperationTracker.ObserveEditor(EditorApplication.isCompiling, EditorApplication.isUpdating);
            }
            catch (Exception ex)
            {
                // The native call may already have queued work. Preserve ownership
                // rather than retrying the mutation after an uncertain exception.
                RefreshOperationTracker.MarkDispatchError(receipt.operation_id, "refresh_failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Observes only the compilation start edge. A grace expiry is not a
        /// completion receipt and is never used to acknowledge refresh work.
        /// </summary>
        internal static Task<bool> WaitForCompilationToStartAsync(int compileCountBefore, TimeSpan grace)
        {
            if (EditorStateCache.CompileCount != compileCountBefore || EditorStateCache.GetActualIsCompiling())
                return Task.FromResult(true);

            var tcs = new TaskCompletionSource<bool>();
            var start = DateTime.UtcNow;
            Action<object> onStarted = null;
            EditorApplication.CallbackFunction tick = null;
            onStarted = _ =>
            {
                CompilationPipeline.compilationStarted -= onStarted;
                EditorApplication.update -= tick;
                tcs.TrySetResult(true);
            };
            tick = () =>
            {
                if ((DateTime.UtcNow - start) <= grace) return;
                CompilationPipeline.compilationStarted -= onStarted;
                EditorApplication.update -= tick;
                tcs.TrySetResult(false);
            };
            CompilationPipeline.compilationStarted += onStarted;
            EditorApplication.update += tick;
            try { EditorApplication.QueuePlayerLoopUpdate(); } catch { }
            return tcs.Task;
        }
    }
}
