using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditorInternal;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Tracks async test jobs started via MCP tools. This is not intended to capture manual Test Runner UI runs.
    /// </summary>
    [InitializeOnLoad]
    internal static partial class TestJobManager
    {
        // Keep this small to avoid ballooning payloads during polling.
        private const int FailureCap = 25;
        internal const string InitializationTimeoutError = "Test job failed to initialize (tests did not start within timeout)";
        private const long StuckThresholdMs = 60_000;
        private const long DefaultInitializationTimeoutMs = 15_000; // 15 seconds default; override per-job via run_tests init_timeout param
        private const long MaxInitializationTimeoutMs = 600_000; // 10 minutes hard cap
        private const int MaxJobsToKeep = 10;
        private const long MinPersistIntervalMs = 1000; // Throttle persistence to reduce overhead

        // SessionState survives domain reloads within the same Unity Editor session.
        private const string SessionKeyJobs = "MCPForUnity.TestJobsV1";
        private const string SessionKeyCurrentJobId = "MCPForUnity.CurrentTestJobIdV1";

        private static readonly object LockObj = new();
        private static readonly Dictionary<string, TestJob> Jobs = new();
        private static string _currentJobId;
        private static long _lastPersistUnixMs;

        static TestJobManager()
        {
            // Restore after domain reloads (e.g., compilation while a job is running).
            TryRestoreFromSessionState();
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            RestoreRunningJobCallbacks();
        }

        private static void BeforeAssemblyReload()
        {
            // Progress callbacks are normally throttled. Flush the last update before the
            // managed domain (and the original RunTestsAsync task) is discarded.
            PersistToSessionState(force: true);
        }

        private static void RestoreRunningJobCallbacks()
        {
            TestJob job;
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) ||
                    !Jobs.TryGetValue(_currentJobId, out job))
                {
                    return;
                }
            }

            try
            {
                // Polling a restored job never otherwise touches the lazy test service.
                // Re-register callbacks now, before the Test Runner resumes after reload.
                if (MCPServiceLocator.Tests is TestRunnerService service)
                {
                    service.ResumeJobAfterReload(job.JobId, job.Mode, job.UnityRunId,
                        job.InitializationTimedOut, job.NativeRunStarted, job.NativeCleanupPending, job.Result, job.Error);
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[TestJobManager] Failed to restore test callbacks: {ex.Message}");
            }
        }

        public static string CurrentJobId
        {
            get { lock (LockObj) return _currentJobId; }
        }

        public static bool HasRunningJob
        {
            get
            {
                lock (LockObj)
                {
                    return !string.IsNullOrEmpty(_currentJobId);
                }
            }
        }

        /// <summary>
        /// Clear an owned job only after its native GUID is confirmed inactive.
        /// A tracker reset is never a substitute for cancellation or native cleanup.
        /// </summary>
        public static bool ClearStuckJob()
        {
            string jobId = CurrentJobId;
            if (string.IsNullOrEmpty(jobId)) return false;
            RestoreRunningJobCallbacks();
            return MCPServiceLocator.Tests is TestRunnerService service
                && service.TryClearCompletedNativeRun(jobId);
        }

        public static string StartJob(TestMode mode, TestFilterOptions filterOptions = null, long initTimeoutMs = 0)
        {
            // Clamp to valid range: non-positive values mean "use default", cap at 10 minutes
            if (initTimeoutMs < 0) initTimeoutMs = 0;
            if (initTimeoutMs > MaxInitializationTimeoutMs) initTimeoutMs = MaxInitializationTimeoutMs;

            string jobId;
            // Check preconditions before allocating a job, while preserving ownership priority.
            lock (LockObj)
            {
                if (!string.IsNullOrEmpty(_currentJobId))
                    throw new InvalidOperationException("A Unity test run is already in progress.");
                if (RefreshOperationTracker.IsInProgress)
                    throw new InvalidOperationException("A Unity refresh is still in progress; query its operation before starting tests.");
                TestRunnerService.ValidateScenesBeforeTestRun();

                jobId = Guid.NewGuid().ToString("N");
                long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string modeStr = mode.ToString();

                var job = new TestJob
                {
                    JobId = jobId,
                    Status = TestJobStatus.Running,
                    Mode = modeStr,
                    StartedUnixMs = started,
                    FinishedUnixMs = null,
                    LastUpdateUnixMs = started,
                    TotalTests = null,
                    CompletedTests = 0,
                    CurrentTestFullName = null,
                    CurrentTestStartedUnixMs = null,
                    LastFinishedTestFullName = null,
                    LastFinishedUnixMs = null,
                    FailuresSoFar = new List<TestJobFailure>(),
                    Error = null,
                    Result = null,
                    InitTimeoutMs = initTimeoutMs
                };

                Jobs[jobId] = job;
                _currentJobId = jobId;
            }
            PersistToSessionState(force: true);

            // Kick the run (must be called on main thread; our command handlers already run there).
            Task<TestRunResult> task = MCPServiceLocator.Tests.RunTestsAsync(mode, filterOptions);

            void FinalizeJob(Action finalize)
            {
                // Ensure state mutation happens on main thread to avoid Unity API surprises.
                EditorApplication.delayCall += () =>
                {
                    try { finalize(); }
                    catch (Exception ex) { McpLog.Error($"[TestJobManager] Finalize failed: {ex.Message}\n{ex.StackTrace}"); }
                };
            }

            task.ContinueWith(t =>
            {
                // NOTE: We now finalize jobs deterministically from the TestRunnerService RunFinished callback.
                // This continuation is retained as a safety net in case RunFinished is not delivered.
                FinalizeJob(() => FinalizeFromTask(jobId, t));
            }, TaskScheduler.Default);

            return jobId;
        }

        internal static void OnRunScheduled(string jobId, string unityRunId)
        {
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(jobId) || _currentJobId != jobId || !Jobs.TryGetValue(jobId, out var job)) return;
                job.UnityRunId = unityRunId;
            }
            PersistToSessionState(force: true);
        }

        public static void FinalizeCurrentJobFromRunFinished(TestRunResult resultPayload, bool keepOwnership = false)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
                {
                    return;
                }

                job.LastUpdateUnixMs = now;
                job.FinishedUnixMs = now;
                job.Status = job.InitializationTimedOut || (resultPayload != null && resultPayload.Failed > 0)
                    ? TestJobStatus.Failed
                    : TestJobStatus.Succeeded;
                job.Error = job.InitializationTimedOut ? InitializationTimeoutError : null;
                job.NativeCleanupPending = keepOwnership;
                job.Result = resultPayload;
                if (resultPayload != null)
                {
                    job.TotalTests = resultPayload.Total;
                    job.CompletedTests = resultPayload.Total;
                }
                job.CurrentTestFullName = null;
                job.CurrentTestStartedUnixMs = null;
                if (!keepOwnership) _currentJobId = null;
            }
            PersistToSessionState(force: true);
        }

        internal static void FinalizeCurrentJobFromRunError(string message, bool keepOwnership = false)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
                {
                    return;
                }
                job.Status = TestJobStatus.Failed;
                // A message that already carries the timeout text (for example one that also says how to
                // clear an unconfirmed native cleanup) is kept; any other error is reported as the timeout.
                job.Error = job.InitializationTimedOut
                    && (message == null || message.IndexOf(InitializationTimeoutError, StringComparison.Ordinal) < 0)
                    ? InitializationTimeoutError : message;
                job.NativeCleanupPending = keepOwnership;
                job.LastUpdateUnixMs = now;
                job.FinishedUnixMs = now;
                job.CurrentTestFullName = null;
                job.CurrentTestStartedUnixMs = null;
                if (!keepOwnership) _currentJobId = null;
            }
            PersistToSessionState(force: true);
        }

        public static void OnRunStarted(int? totalTests)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
                {
                    return;
                }

                job.NativeRunStarted = true;
                job.LastUpdateUnixMs = now;
                job.TotalTests = totalTests;
                job.CompletedTests = 0;
                job.CurrentTestFullName = null;
                job.CurrentTestStartedUnixMs = null;
                job.LastFinishedTestFullName = null;
                job.LastFinishedUnixMs = null;
                job.FailuresSoFar ??= new List<TestJobFailure>();
                job.FailuresSoFar.Clear();
            }
            PersistToSessionState(force: true);
        }

        public static void OnTestStarted(string testFullName)
        {
            if (string.IsNullOrWhiteSpace(testFullName))
            {
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job) || job.Status != TestJobStatus.Running)
                {
                    return;
                }

                job.LastUpdateUnixMs = now;
                job.CurrentTestFullName = testFullName;
                job.CurrentTestStartedUnixMs = now;
            }
            PersistToSessionState();
        }

        public static void OnLeafTestFinished(string testFullName, bool isFailure, string message)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job) || job.Status != TestJobStatus.Running)
                {
                    return;
                }

                job.LastUpdateUnixMs = now;
                job.CompletedTests = Math.Max(0, job.CompletedTests + 1);
                job.LastFinishedTestFullName = testFullName;
                job.LastFinishedUnixMs = now;

                if (isFailure)
                {
                    job.FailuresSoFar ??= new List<TestJobFailure>();
                    if (job.FailuresSoFar.Count < FailureCap)
                    {
                        job.FailuresSoFar.Add(new TestJobFailure
                        {
                            FullName = testFullName,
                            Message = string.IsNullOrWhiteSpace(message) ? "Test failed" : message
                        });
                    }
                }
            }
            PersistToSessionState();
        }

        public static void OnRunFinished()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
                {
                    return;
                }

                job.LastUpdateUnixMs = now;
                job.CurrentTestFullName = null;
            }
            PersistToSessionState(force: true);
        }

        internal static TestJob GetJob(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return null;
            }

            TestJob jobToReturn = null;
            bool shouldPersist = false;
            bool cancelRun = false;
            lock (LockObj)
            {
                if (!Jobs.TryGetValue(jobId, out var job))
                {
                    return null;
                }

                // Check if job is stuck in "running" state without having called OnRunStarted (TotalTests still null).
                // This happens when tests fail to initialize (e.g., unsaved scene, compilation issues).
                // After 15 seconds without initialization, auto-fail the job to prevent hanging.
                if (job.Status == TestJobStatus.Running && !job.NativeRunStarted && job.TotalTests == null)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    long initTimeout = job.InitTimeoutMs > 0 ? job.InitTimeoutMs : DefaultInitializationTimeoutMs;
                    if (!EditorStateCache.GetActualIsCompiling() && !EditorApplication.isUpdating && now - job.StartedUnixMs > initTimeout)
                    {
                        McpLog.Warn($"[TestJobManager] Job {jobId} failed to initialize within {initTimeout}ms, auto-failing");
                        job.Status = TestJobStatus.Failed;
                        job.Error = InitializationTimeoutError;
                        job.FinishedUnixMs = now;
                        job.LastUpdateUnixMs = now;
                        if (_currentJobId == jobId)
                        {
                            cancelRun = true;
                            job.InitializationTimedOut = true;
                            job.NativeCleanupPending = true;
                            // A scheduled Unity run still owns its callbacks and operation lock.
                            // Reject a new job until cancellation has finished cleaning up.
                        }
                        shouldPersist = true;
                    }
                }

                jobToReturn = job;
            }

            if (cancelRun && MCPServiceLocator.Tests is TestRunnerService service)
            {
                service.CancelForInitializationTimeout(jobId);
            }
            if (shouldPersist)
            {
                PersistToSessionState(force: true);
            }
            return jobToReturn;
        }

        private static void FinalizeFromTask(string jobId, Task<TestRunResult> task)
        {
            bool ownsCurrent;
            lock (LockObj)
            {
                if (!Jobs.TryGetValue(jobId, out var existing) || existing.Status != TestJobStatus.Running) return;
                ownsCurrent = _currentJobId == jobId;
            }

            TestRunResult result = null;
            Exception error = null;
            if (task.IsFaulted) error = task.Exception?.GetBaseException()
                ?? new InvalidOperationException("Unknown test job failure");
            else if (task.IsCanceled) error = new InvalidOperationException("Test job canceled");
            else result = task.Result;

            if (ownsCurrent)
            {
                // A Task outcome says nothing about native teardown. Reuse the same
                // per-GUID completion monitor as callbacks; unknown identity stays owned.
                RestoreRunningJobCallbacks();
                if (MCPServiceLocator.Tests is TestRunnerService service
                    && service.TryCompleteFromTask(jobId, result, error)) return;
            }

            lock (LockObj)
            {
                if (!Jobs.TryGetValue(jobId, out var existing) || existing.Status != TestJobStatus.Running) return;
                existing.LastUpdateUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                existing.FinishedUnixMs = existing.LastUpdateUnixMs;
                existing.Status = error != null || (result != null && result.Failed > 0)
                    ? TestJobStatus.Failed : TestJobStatus.Succeeded;
                existing.Error = error?.Message;
                existing.Result = result;
                // If callback restoration was unavailable, persist the cleanup hold.
                // A historical task may update its result but cannot release a newer job.
                if (_currentJobId == jobId) existing.NativeCleanupPending = true;
            }
            PersistToSessionState(force: true);
        }
    }
}
