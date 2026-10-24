using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;


namespace MCPForUnity.Editor.Services
{
    internal sealed partial class TestRunnerService
    {
        private string _unityRunId;
        private bool _initializationTimedOut;
        private bool _nativeRunStarted;
        private TestRunResult _pendingResult;
        private Exception _pendingError;
        // A failed native activity query (for example around a domain reload) leaves the run's
        // state unknown. Retry it for this long, then end in an explicit job failure.
        private const double NativeQueryRecoveryWindowSeconds = 30.0;
        private const double NativeQueryFailureLogIntervalSeconds = 5.0;
        internal const string NativeStateUnconfirmedError =
            "Unity Test Runner state could not be confirmed, so this job keeps the test run lock to avoid overlapping runs. " +
            "If Unity is no longer running tests, reload scripts from the Unity Editor or restart it to re-check. " +
            "run_tests(clear_stuck=true) can release ownership only after the native run is confirmed inactive.";
        internal const string RunErrorMarker = " Run error: ";
        private double? _nativeQueryFailedSince;
        private double _nativeQueryFailureLoggedAt;
        private static readonly MethodInfo CancelRunMethod = typeof(TestRunnerApi).GetMethod("CancelTestRun", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
        private static readonly MethodInfo IsRunningMethod =
            typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null) ??
            typeof(TestRunnerApi).GetMethod("IsRunning", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
        private static readonly PropertyInfo RunnerHolderProperty = typeof(TestRunnerApi).GetProperty("m_testJobDataHolder", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo GetRunnerMethod = RunnerHolderProperty?.PropertyType.GetMethod("GetRunner");
        private static readonly MethodInfo IsRunningJobMethod = GetRunnerMethod?.ReturnType.GetMethod("IsRunningJob");
        // UTF 1.1.33 has neither a per-run API nor m_testJobDataHolder. Its
        // serialized TestRuns list owns each GUID from RunJob until StopRun, which
        // occurs only after the native undo and cleanup verification tasks finish.
        private static readonly Type LegacyHolderType = typeof(TestRunnerApi).Assembly.GetType(
            "UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder");
        private static readonly PropertyInfo LegacyHolderInstance = LegacyHolderType?.GetProperty(
            "instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        private static readonly FieldInfo LegacyRunsField = LegacyHolderType?.GetField("TestRuns");
        private static readonly Type LegacyJobType = typeof(TestRunnerApi).Assembly.GetType(
            "UnityEditor.TestTools.TestRunner.TestRun.TestJobData");
        private static readonly FieldInfo LegacyGuidField = LegacyJobType?.GetField("guid");
        private static readonly FieldInfo LegacyRunningField = LegacyJobType?.GetField("isRunning");
        private static readonly bool CanQueryNativeRun = IsRunningMethod != null
            || IsRunningJobMethod != null || (LegacyHolderInstance != null && LegacyRunsField != null
                && LegacyGuidField != null && LegacyRunningField != null);

        internal void ResumeJobAfterReload(string jobId, string mode, string unityRunId = null,
            bool initializationTimedOut = false, bool nativeRunStarted = false,
            bool nativeCleanupPending = false, TestRunResult pendingResult = null, string pendingError = null)
        {
            if (_runCompletionSource != null || _trackedJobId != null ||
                string.IsNullOrEmpty(jobId) || TestJobManager.CurrentJobId != jobId)
            {
                return;
            }

            _trackedJobId = jobId;
            _nativeQueryFailedSince = null;
            _unityRunId = unityRunId;
            _nativeRunStarted = nativeRunStarted;
            _initializationTimedOut = initializationTimedOut;
            _pendingResult = pendingResult;
            _pendingError = RestorePendingError(pendingError);
            if (Enum.TryParse<TestMode>(mode, out var testMode))
            {
                TestRunStatus.MarkStarted(testMode);
            }
            if (nativeCleanupPending)
            {
                EditorApplication.update += CompleteRunWhenIdle;
            }
            else if (initializationTimedOut) CancelForInitializationTimeout(jobId);
        }

        internal void CancelForInitializationTimeout(string jobId)
        {
            if (_trackedJobId != jobId) return;
            _initializationTimedOut = true;
            _pendingError = new InvalidOperationException(TestJobManager.InitializationTimeoutError);
            // Older Test Framework releases have no public per-run activity query. Keep
            // ownership until cleanup finishes, including cancellation before RunStarted
            // where Unity may skip RunFinished entirely.
            // Querying completion does not require a cancellation API. UTF 1.1.33
            // has no per-run cancellation, but its GUID remains observable until cleanup.
            EditorApplication.update -= CompleteRunWhenIdle;
            EditorApplication.update += CompleteRunWhenIdle;
            // Before RunStarted, cancellation can skip Unity's scene snapshot but still
            // run its scene restore task. Wait until setup has captured the open scenes.
            if (!_nativeRunStarted || CancelRunMethod == null || !CanQueryNativeRun
                || string.IsNullOrEmpty(_unityRunId)) return;
            try
            {
                if (IsNativeRunActive()) CancelRunMethod.Invoke(null, new object[] { _unityRunId });
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[TestRunnerService] Failed to cancel timed-out run: {ex.GetBaseException().Message}");
            }
        }

        private bool IsNativeRunActive()
        {
            if (string.IsNullOrEmpty(_unityRunId))
                throw new InvalidOperationException("The owned test job has no confirmed native run GUID.");
            if (IsRunningMethod != null)
            {
                return (bool)IsRunningMethod.Invoke(null, new object[] { _unityRunId });
            }
            if (RunnerHolderProperty != null && GetRunnerMethod != null && IsRunningJobMethod != null)
            {
                var holder = RunnerHolderProperty.GetValue(null);
                if (holder == null) throw new InvalidOperationException("Native runner holder is unavailable.");
                var runner = GetRunnerMethod.Invoke(holder, new object[] { _unityRunId });
                return runner != null && (bool)IsRunningJobMethod.Invoke(runner, null);
            }
            if (LegacyHolderInstance != null && LegacyRunsField != null
                && LegacyGuidField != null && LegacyRunningField != null)
            {
                var holder = LegacyHolderInstance.GetValue(null);
                var runs = holder == null ? null : LegacyRunsField.GetValue(holder) as System.Collections.IEnumerable;
                if (runs == null) throw new InvalidOperationException("Native test job list is unavailable.");
                foreach (var run in runs)
                {
                    if (run == null) throw new InvalidOperationException("Native test job entry is unavailable.");
                    if (string.Equals((string)LegacyGuidField.GetValue(run), _unityRunId, StringComparison.Ordinal))
                        return (bool)LegacyRunningField.GetValue(run);
                }
                return false;
            }
            throw new NotSupportedException("This Test Framework version has no supported per-run activity query.");
        }

        internal bool TryClearCompletedNativeRun(string jobId)
        {
            if (!IsTrackingCurrentJob || _trackedJobId != jobId) return false;
            // The query must positively confirm this GUID has stopped. Missing identity,
            // missing APIs and reflection failures all retain the current ownership.
            if (IsNativeRunActiveOrUnknown()) return false;
            // A terminal callback may already have supplied the result while cleanup
            // was active. Manual recovery only releases ownership; keep that outcome.
            var completionError = _pendingError;
            if (_pendingResult == null && completionError == null)
                completionError = new InvalidOperationException("Job cleared manually after native cleanup completed");
            CompleteRun(_pendingResult, completionError);
            return TestJobManager.CurrentJobId != jobId;
        }

        // A query exception means the native state is unknown. Report it as active so a failed
        // query can never release ownership; the idle monitor bounds the retries.
        private bool IsNativeRunActiveOrUnknown()
        {
            try
            {
                bool active = IsNativeRunActive();
                _nativeQueryFailedSince = null;
                return active;
            }
            catch (Exception ex)
            {
                double now = EditorApplication.timeSinceStartup;
                string reason = ex.GetBaseException().Message;
                if (!_nativeQueryFailedSince.HasValue)
                {
                    _nativeQueryFailedSince = now;
                    _nativeQueryFailureLoggedAt = now;
                    McpLog.Warn($"[TestRunnerService] Failed to query native test cleanup; retrying for up to {NativeQueryRecoveryWindowSeconds}s: {reason}");
                }
                else if (now - _nativeQueryFailureLoggedAt >= NativeQueryFailureLogIntervalSeconds)
                {
                    _nativeQueryFailureLoggedAt = now;
                    McpLog.Warn($"[TestRunnerService] Native test cleanup query still failing after {now - _nativeQueryFailedSince.Value:0}s: {reason}");
                }
                return true;
            }
        }

        // Pure predicate (unit-testable): keep retrying a failing native query until it has failed
        // continuously for the whole window. No recorded failure means keep monitoring.
        internal static bool ShouldKeepRetryingNativeQuery(double now, double? firstFailureAt, double windowSeconds)
            => !firstFailureAt.HasValue || now - firstFailureAt.Value < windowSeconds;

        // The persisted job error becomes the pending error again after a domain reload. The
        // "unconfirmed" text is this service's own annotation, not a run error, so drop it and keep only
        // the original run error. A later successful check then reports the run's real outcome.
        internal static Exception RestorePendingError(string persisted)
        {
            if (persisted == null) return null;
            if (persisted.StartsWith(NativeStateUnconfirmedError, StringComparison.Ordinal))
            {
                string rest = persisted.Substring(NativeStateUnconfirmedError.Length);
                persisted = rest.StartsWith(RunErrorMarker, StringComparison.Ordinal) ? rest.Substring(RunErrorMarker.Length) : null;
                if (string.IsNullOrEmpty(persisted)) return null;
            }
            return new InvalidOperationException(persisted);
        }

        // The native state stayed unknown for the whole window. Stop polling but keep ownership,
        // and fail the job with a message that tells the client how to clear it.
        private void StopMonitoringUnconfirmedRun()
        {
            EditorApplication.update -= CompleteRunWhenIdle;
            string message = NativeStateUnconfirmedError;
            string reason = _pendingError?.Message;
            if (!string.IsNullOrEmpty(reason)) message += RunErrorMarker + reason;
            McpLog.Warn($"[TestRunnerService] Stopped querying native test cleanup after {NativeQueryRecoveryWindowSeconds}s of failures. {message}");
            if (IsTrackingCurrentJob)
            {
                TestJobManager.FinalizeCurrentJobFromRunError(message, keepOwnership: true);
            }
        }

        private void CompleteRunWhenIdle()
        {
            try
            {
                if (!IsNativeRunActiveOrUnknown())
                {
                    CompleteRun(_pendingResult, _pendingError);
                }
                else if (!ShouldKeepRetryingNativeQuery(EditorApplication.timeSinceStartup, _nativeQueryFailedSince, NativeQueryRecoveryWindowSeconds))
                {
                    StopMonitoringUnconfirmedRun();
                }
            }
            catch (Exception ex)
            {
                EditorApplication.update -= CompleteRunWhenIdle;
                McpLog.Warn($"[TestRunnerService] Failed to monitor native test cleanup: {ex.GetBaseException().Message}");
            }
        }

    }
}
