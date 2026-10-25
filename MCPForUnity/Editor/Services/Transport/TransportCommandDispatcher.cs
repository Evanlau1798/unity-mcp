using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Models;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Services.Transport
{
    /// <summary>
    /// Centralised command execution pipeline shared by all transport implementations.
    /// Guarantees that MCP commands are executed on the Unity main thread while preserving
    /// the legacy response format expected by the server.
    /// </summary>
    [InitializeOnLoad]
    internal static partial class TransportCommandDispatcher
    {
        private static SynchronizationContext _mainThreadContext;
        private static int _mainThreadId;
        private static int _processingFlag;

        private sealed class PendingCommand
        {
            public PendingCommand(
                string commandJson,
                TaskCompletionSource<string> completionSource,
                CancellationToken cancellationToken)
            {
                CommandJson = commandJson;
                CompletionSource = completionSource;
                CancellationToken = cancellationToken;
            }

            public string CommandJson { get; }
            public TaskCompletionSource<string> CompletionSource { get; }
            public CancellationToken CancellationToken { get; }
            public CancellationTokenRegistration CancellationRegistration { get; set; }
            public bool IsExecuting { get; set; }
            public System.Diagnostics.Stopwatch Clock { get; } = System.Diagnostics.Stopwatch.StartNew();
            public long? DispatchWaitMs { get; set; }

            public void Dispose()
            {
                CancellationRegistration.Dispose();
            }

            public void TrySetResult(string payload)
            {
                CompletionSource.TrySetResult(payload);
            }

        }

        private static readonly Dictionary<string, PendingCommand> Pending = new();
        private static readonly object PendingLock = new();
        private static bool updateHooked;
        private static bool initialised;

        static TransportCommandDispatcher()
        {
            // Ensure this runs on the Unity main thread at editor load.
            _mainThreadContext = SynchronizationContext.Current;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            EnsureInitialised();

            // Always keep the update hook installed so commands arriving from background
            // websocket tasks don't depend on a background-thread event subscription.
            if (!updateHooked)
            {
                updateHooked = true;
                EditorApplication.update += ProcessQueue;
            }
        }

        /// <summary>
        /// Schedule a command for execution on the Unity main thread and await its JSON response.
        /// </summary>
        public static Task<string> ExecuteCommandJsonAsync(string commandJson, CancellationToken cancellationToken)
        {
            if (commandJson is null)
            {
                throw new ArgumentNullException(nameof(commandJson));
            }

            EnsureInitialised();

            var id = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            var pending = new PendingCommand(commandJson, tcs, cancellationToken);

            lock (PendingLock)
            {
                Pending[id] = pending;
            }
            // Register after insertion: an already-canceled token invokes this callback synchronously.
            pending.CancellationRegistration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => CancelPending(id, cancellationToken))
                : default;
            if (tcs.Task.IsCompleted)
            {
                pending.Dispose();
                return tcs.Task;
            }

            // Proactively wake up the main thread execution loop. This improves responsiveness
            // in scenarios where EditorApplication.update is throttled or temporarily not firing
            // (e.g., Unity unfocused, compiling, or during domain reload transitions).
            RequestMainThreadPump();

            return tcs.Task;
        }

        internal static Task<T> RunOnMainThreadAsync<T>(Func<T> func, CancellationToken cancellationToken)
        {
            if (func is null)
            {
                throw new ArgumentNullException(nameof(func));
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken))
                : default;

            void Invoke()
            {
                try
                {
                    if (tcs.Task.IsCompleted)
                    {
                        return;
                    }

                    try { EditorApplication.QueuePlayerLoopUpdate(); } catch { }
                    var result = func();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    registration.Dispose();
                }
            }

            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                if (_mainThreadContext == null)
                {
                    registration.Dispose();
                    tcs.TrySetException(new InvalidOperationException("Unity main-thread context is unavailable."));
                    return tcs.Task;
                }
                _mainThreadContext.Post(_ => Invoke(), null);
                return tcs.Task;
            }

            Invoke();
            return tcs.Task;
        }

        private static void RequestMainThreadPump()
        {
            void Pump()
            {
                try
                {
                    // Hint Unity to run a loop iteration soon.
                    EditorApplication.QueuePlayerLoopUpdate();
                }
                catch
                {
                    // Best-effort only.
                }

                ProcessQueue();
            }

            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                _mainThreadContext?.Post(_ => Pump(), null);
                return;
            }

            Pump();
        }

        private static void EnsureInitialised()
        {
            if (initialised)
            {
                return;
            }

            CommandRegistry.Initialize();
            initialised = true;
        }

        private static void HookUpdate()
        {
            // Deprecated: we keep the update hook installed permanently (see static ctor).
            if (updateHooked) return;
            updateHooked = true;
            EditorApplication.update += ProcessQueue;
        }

        private static void UnhookUpdateIfIdle()
        {
            // Intentionally no-op: keep update hook installed so background commands always process.
            // This avoids "must focus Unity to re-establish contact" edge cases.
            return;
        }

        private static void ProcessQueue()
        {
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1)
            {
                return;
            }

            try
            {
            List<(string id, PendingCommand pending)> ready;

            lock (PendingLock)
            {
                // Early exit inside lock to prevent per-frame List allocations (GitHub issue #577)
                if (Pending.Count == 0)
                {
                    return;
                }

                ready = new List<(string, PendingCommand)>(Pending.Count);
                foreach (var kvp in Pending)
                {
                    if (kvp.Value.IsExecuting)
                    {
                        continue;
                    }

                    kvp.Value.IsExecuting = true;
                    ready.Add((kvp.Key, kvp.Value));
                }

                if (ready.Count == 0)
                {
                    UnhookUpdateIfIdle();
                    return;
                }
            }

            foreach (var (id, pending) in ready)
            {
                ProcessCommand(id, pending);
            }
            }
            finally
            {
                Interlocked.Exchange(ref _processingFlag, 0);
            }
        }

        private static void CancelPending(string id, CancellationToken token)
        {
            PendingCommand pending = null;
            lock (PendingLock)
            {
                if (Pending.Remove(id, out pending))
                {
                    UnhookUpdateIfIdle();
                }
            }

            if (pending != null)
            {
                string phase = pending.DispatchWaitMs.HasValue ? "editor_execution" : "editor_queue";
                string error = $"Command canceled or deadline exceeded during {phase}";
                pending.TrySetResult(JsonConvert.SerializeObject(new
                {
                    status = "error", error,
                    result = new { success = false, error,
                    hint = pending.DispatchWaitMs.HasValue ? "check_state_before_retry" : "retry", data = new
                    {
                        phase,
                        elapsed_ms = pending.Clock.ElapsedMilliseconds,
                        queue_wait_ms = pending.DispatchWaitMs ?? pending.Clock.ElapsedMilliseconds,
                        execution_may_have_started = pending.DispatchWaitMs.HasValue,
                        observed_at_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    } }
                }));
            }
            pending?.Dispose();
        }

        private static void RemovePending(string id, PendingCommand pending)
        {
            lock (PendingLock)
            {
                Pending.Remove(id);
                UnhookUpdateIfIdle();
            }

            pending.Dispose();
        }

        private static string SerializeError(string message, string commandType = null, string stackTrace = null)
        {
            var errorResponse = new
            {
                status = "error",
                error = message,
                command = commandType ?? "Unknown",
                stackTrace
            };
            return JsonConvert.SerializeObject(errorResponse);
        }

        private static bool IsValidJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim();
            if ((text.StartsWith("{") && text.EndsWith("}")) || (text.StartsWith("[") && text.EndsWith("]")))
            {
                try
                {
                    JToken.Parse(text);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }
    }
}
