using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace MCPForUnity.Editor.Services
{
    /// <summary>One active refresh and a bounded replay history, surviving domain reload.</summary>
    [InitializeOnLoad]
    internal static class RefreshOperationTracker
    {
        private const string StateKey = "MCPForUnity.RefreshOperations.v1";
        private const int HistoryLimit = 8;
        private static readonly object Guard = new object();
        private static List<Receipt> _receipts = new List<Receipt>();
        private static string _restoreError;

        internal sealed class Receipt
        {
            public string operation_id;
            public string mode;
            public string scope;
            public string status = "accepted";
            public bool request_accepted = true;
            public bool completed;
            public bool dispatch_started;
            public bool refresh_triggered;
            public bool refresh_returned;
            public bool compile_requested;
            public bool? compile_started;
            public bool compile_finished;
            public bool compile_pending;
            public bool compilation_failed;
            public bool reload_started;
            public bool reload_observed;
            public string error;
            public long? requested_at_unix_ms;
            public long? completed_at_unix_ms;
        }

        static RefreshOperationTracker()
        {
            RestoreFromSession(true);
            CompilationPipeline.compilationStarted += _ => MarkCompilationStarted();
            CompilationPipeline.compilationFinished += _ => MarkCompilationFinished();
            CompilationPipeline.assemblyCompilationFinished += (_, messages) =>
            {
                foreach (var message in messages ?? Array.Empty<CompilerMessage>())
                    if (message.type == CompilerMessageType.Error)
                        MarkCompilationError(message.message);
            };
            AssemblyReloadEvents.beforeAssemblyReload += MarkBeforeReload;
            AssemblyReloadEvents.afterAssemblyReload += MarkReloadObserved;
            EditorApplication.update += OnUpdate;
        }

        private static Receipt Active => _receipts.LastOrDefault(r => !r.completed);

        internal static bool IsInProgress
        {
            get { lock (Guard) return _restoreError != null || Active != null; }
        }

        internal static string ActiveOperationId
        {
            get { lock (Guard) return Active?.operation_id; }
        }

        // Dispatch and all lifecycle mutations run on Unity's main thread.
        internal static Receipt ActiveReceipt
        {
            get { lock (Guard) return Active; }
        }

        internal static void RestoreFromSession(bool afterDomainReload)
        {
            lock (Guard)
            {
                _restoreError = null;
                try
                {
                    var raw = SessionState.GetString(StateKey, string.Empty);
                    _receipts = string.IsNullOrEmpty(raw) ? new List<Receipt>()
                        : JsonConvert.DeserializeObject<List<Receipt>>(raw) ?? new List<Receipt>();
                    if (_receipts.Any(r => r == null || string.IsNullOrEmpty(r.operation_id))
                        || _receipts.Count(r => !r.completed) > 1)
                        throw new InvalidOperationException("Invalid persisted refresh receipts.");
                    var active = Active;
                    if (afterDomainReload && active != null && active.reload_started)
                    {
                        active.reload_observed = true;
                        Persist();
                    }
                }
                catch (Exception ex)
                {
                    _receipts = new List<Receipt>();
                    _restoreError = "refresh_state_unavailable: " + ex.Message;
                }
            }
        }

        internal static bool TryReplay(string operationId, out object response)
        {
            lock (Guard)
            {
                var receipt = _receipts.LastOrDefault(r => r.operation_id == operationId);
                response = receipt == null ? null : Reply(receipt);
                return receipt != null;
            }
        }

        internal static object Query(string operationId)
        {
            lock (Guard)
            {
                if (_restoreError != null)
                    return Rejected("refresh_state_unavailable", operationId, _restoreError);
                var receipt = string.IsNullOrEmpty(operationId) ? Active ?? _receipts.LastOrDefault()
                    : _receipts.LastOrDefault(r => r.operation_id == operationId);
                if (receipt != null) return Reply(receipt);
                // Absence is not evidence that an unobserved mutation was never accepted.
                return new ErrorResponse("refresh_operation_not_found", new
                {
                    reason = "refresh_operation_not_found", operation_id = operationId,
                    status = "not_found", completed = false,
                    active_operation_id = Active?.operation_id
                });
            }
        }

        internal static object Begin(string operationId, string mode, string scope, bool compileRequested)
        {
            lock (Guard)
            {
                var existing = _receipts.LastOrDefault(r => r.operation_id == operationId);
                if (existing != null) return Reply(existing);
                if (_restoreError != null)
                    return Rejected("refresh_state_unavailable", operationId, _restoreError);
                if (Active != null) return Rejected("refresh_in_progress", operationId);
                var receipt = new Receipt
                {
                    operation_id = operationId, mode = mode, scope = scope,
                    compile_requested = compileRequested,
                    requested_at_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                _receipts.Add(receipt);
                while (_receipts.Count > HistoryLimit) _receipts.RemoveAt(0);
                Persist();
                return Reply(receipt);
            }
        }

        internal static object Rejected(string reason, string operationId, string error = null)
        {
            return new ErrorResponse(error ?? reason, new
            {
                reason, operation_id = operationId, active_operation_id = ActiveOperationId,
                request_accepted = false, status = "rejected", completed = false
            });
        }

        private static object Reply(Receipt receipt)
        {
            var data = JObject.FromObject(receipt);
            data["resulting_state"] = receipt.completed ? "idle" : receipt.status;
            data["hint"] = receipt.completed ? "Refresh operation finished; read editor state for readiness."
                : "Query this operation_id until completed; do not resend the refresh.";
            return receipt.error != null || receipt.status == "failed"
                ? (object)new ErrorResponse(receipt.error ?? "refresh_failed", data)
                : new SuccessResponse(receipt.completed ? "Refresh completed." : "Refresh accepted.", data);
        }

        internal static JObject GetRefreshSnapshot()
        {
            lock (Guard)
            {
                var receipt = Active ?? _receipts.LastOrDefault();
                return new JObject
                {
                    ["is_refresh_in_progress"] = _restoreError != null || Active != null,
                    ["last_refresh_requested_unix_ms"] = receipt?.requested_at_unix_ms,
                    ["last_refresh_finished_unix_ms"] = receipt?.completed_at_unix_ms,
                    ["operation_id"] = receipt?.operation_id,
                    ["status"] = _restoreError != null ? "recovery_required" : receipt?.status,
                    ["completed"] = receipt?.completed
                };
            }
        }

        internal static void MarkDispatchStarted()
        {
            lock (Guard)
            {
                var receipt = Active;
                if (receipt == null || receipt.dispatch_started) return;
                receipt.dispatch_started = true;
                receipt.refresh_triggered = true;
                receipt.status = "importing";
                Persist(); // Must precede the first native mutation.
            }
        }

        internal static void MarkRefreshReturned(string operationId)
        {
            lock (Guard)
            {
                var receipt = Active;
                if (receipt == null || receipt.operation_id != operationId) return;
                receipt.refresh_returned = true;
                if (receipt.compile_started != true && receipt.compile_requested)
                    receipt.status = "awaiting_compile_start";
                Persist();
            }
        }

        internal static void MarkDispatchError(string operationId, string error)
        {
            lock (Guard)
            {
                var receipt = Active;
                if (receipt == null || receipt.operation_id != operationId) return;
                // An exception after a native call is not proof that queued work stopped.
                receipt.error = string.IsNullOrEmpty(receipt.error) ? error : receipt.error + "\n" + error;
                receipt.status = "dispatch_failed_pending";
                Persist();
            }
        }

        internal static void MarkCompilationStarted()
        {
            lock (Guard)
            {
                var receipt = Active;
                if (receipt == null || !receipt.dispatch_started) return;
                receipt.compile_started = true;
                receipt.compile_finished = false;
                receipt.reload_started = false;
                receipt.reload_observed = false;
                receipt.status = "compiling";
                Persist();
            }
        }

        internal static void MarkCompilationError(string error)
        {
            lock (Guard)
            {
                var receipt = Active;
                if (receipt == null || receipt.compile_started != true || receipt.compile_finished) return;
                receipt.compilation_failed = true;
                if ((receipt.error?.Length ?? 0) < 2000)
                    receipt.error = string.IsNullOrEmpty(receipt.error) ? error ?? "script_compilation_failed"
                        : receipt.error + "\n" + error;
                Persist();
            }
        }

        internal static void MarkCompilationFinished()
        {
            lock (Guard)
            {
                var receipt = Active;
                if (receipt == null || receipt.compile_started != true) return;
                receipt.compile_finished = true;
                receipt.compile_pending = false;
                receipt.status = receipt.compilation_failed ? "compilation_failed" : "awaiting_reload";
                Persist();
            }
        }

        internal static void MarkBeforeReload()
        {
            lock (Guard)
            {
                var receipt = Active;
                if (receipt == null || !receipt.dispatch_started) return;
                receipt.reload_started = true;
                Persist();
            }
        }

        internal static void MarkReloadObserved()
        {
            lock (Guard)
            {
                var receipt = Active;
                if (receipt == null || !receipt.dispatch_started) return;
                receipt.reload_observed = true;
                Persist();
            }
        }

        internal static void ObserveEditor(bool isCompiling, bool isUpdating)
        {
            lock (Guard)
            {
                var receipt = Active;
                if (receipt == null || !receipt.dispatch_started) return;
                if (isCompiling)
                {
                    if (!receipt.compile_pending) { receipt.compile_pending = true; Persist(); }
                    return;
                }
                if (isUpdating) return;
                if ((receipt.compile_requested || receipt.compile_started == true || receipt.compile_pending)
                    && !receipt.compile_finished) return;
                if (receipt.compile_finished && !receipt.compilation_failed && !receipt.reload_observed) return;
                if (!receipt.refresh_returned && !receipt.reload_observed) return;
                if (receipt.status == "dispatch_failed_pending" && !receipt.compile_finished) return;
                Finish(receipt, receipt.error == null && !receipt.compilation_failed);
            }
        }

        private static void OnUpdate()
        {
            // A never-dispatched receipt can resume after reload. A dispatched
            // receipt is observed only: replaying its mutation would be unsafe.
            Tools.RefreshUnity.DispatchPendingOperation();
            ObserveEditor(EditorApplication.isCompiling, EditorApplication.isUpdating);
        }

        private static void Finish(Receipt receipt, bool succeeded)
        {
            receipt.completed = true;
            receipt.status = succeeded ? "succeeded" : "failed";
            receipt.completed_at_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Persist();
        }

        private static void Persist()
            => SessionState.SetString(StateKey, JsonConvert.SerializeObject(_receipts));
    }
}
