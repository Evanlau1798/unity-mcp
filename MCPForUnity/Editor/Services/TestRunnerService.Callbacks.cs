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
        #region TestRunnerApi callbacks

        public void RunStarted(ITestAdaptor testsToRun)
        {
            _leafResults.Clear();
            if (!IsTrackingCurrentJob)
            {
                return;
            }
            _nativeRunStarted = true;
            if (_initializationTimedOut) CancelForInitializationTimeout(_trackedJobId);
            try
            {
                // Best-effort progress info for async polling (avoid heavy payloads).
                int? total = null;
                if (testsToRun != null)
                {
                    total = CountLeafTests(testsToRun);
                }
                TestJobManager.OnRunStarted(total);
            }
            catch
            {
                TestJobManager.OnRunStarted(null);
            }
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            // Always create payload and clean up job state, even if _runCompletionSource is null.
            // This handles domain reload scenarios (e.g., PlayMode tests) where the TestRunnerService
            // is recreated and _runCompletionSource is lost, but TestJobManager state persists via
            // SessionState and the Test Runner still delivers the RunFinished callback.
            var payload = TestRunResult.Create(result, _leafResults);
            CompleteRun(payload, null);
        }

        public void OnError(string message)
        {
            // Build/prebuild failures use IErrorCallbacks instead of RunFinished.
            CompleteRun(null, new InvalidOperationException(message ?? "Unity test run failed."));
        }

        internal bool TryCompleteFromTask(string jobId, TestRunResult payload, Exception error)
        {
            if (!IsTrackingCurrentJob || _trackedJobId != jobId) return false;
            CompleteRun(payload, error);
            return true;
        }

        private void CompleteRun(TestRunResult payload, Exception error)
        {
            // Keep the first native failure when later success/error callbacks arrive
            // during teardown. The monitor's uncertainty annotation is not stored in
            // _pendingError, so a later confirmed result can still recover from that.
            if (_pendingError != null) error = _pendingError;
            if (_initializationTimedOut)
            {
                error = new InvalidOperationException(TestJobManager.InitializationTimeoutError);
            }
            if (_trackedJobId != null)
            {
                _pendingResult = payload;
                _pendingError = error;
                // A failed query is handled like an active run: the monitor retries it.
                if (IsNativeRunActiveOrUnknown())
                {
                    // RunFinished/OnError precede native scene and preference teardown.
                    // Persist the result, but retain ownership until all cleanup is done.
                    if (IsTrackingCurrentJob)
                    {
                        if (error == null) TestJobManager.FinalizeCurrentJobFromRunFinished(payload, keepOwnership: true);
                        else TestJobManager.FinalizeCurrentJobFromRunError(error.Message, keepOwnership: true);
                    }
                    EditorApplication.update -= CompleteRunWhenIdle;
                    EditorApplication.update += CompleteRunWhenIdle;
                    return;
                }
            }
            EditorApplication.update -= CompleteRunWhenIdle;

            // A late callback from a cleared job must not finish a newer job or restore
            // settings belonging to it. Callbacks do not contain an MCP job identifier.
            bool ownsCurrentJob = IsTrackingCurrentJob;
            bool canCleanUp = ownsCurrentJob || !TestJobManager.HasRunningJob;
            if (canCleanUp)
            {
                TestRunStatus.MarkFinished();
                TestRunnerNoThrottle.RestoreThrottling();
            }
            if (ownsCurrentJob)
            {
                if (error == null)
                {
                    TestJobManager.OnRunFinished();
                    TestJobManager.FinalizeCurrentJobFromRunFinished(payload);
                }
                else
                {
                    TestJobManager.FinalizeCurrentJobFromRunError(error.Message);
                }
            }

            // If a domain reload destroyed the original RunTestsAsync caller, the finally block
            // that would normally restore EditorSettings never ran. Restore from SessionState.
            if (canCleanUp && _trackedJobId != null && _runCompletionSource == null && PlayModeOptionsGuard.IsPending)
            {
                PlayModeOptionsGuard.Restore();
            }

            // Report result to awaiting caller if we have a completion source.
            // The caller's finally block handles restoration in this case.
            var completion = _runCompletionSource;
            _runCompletionSource = null;
            _trackedJobId = null;
            _unityRunId = null;
            _initializationTimedOut = false;
            _nativeRunStarted = false;
            _pendingResult = null;
            _pendingError = null;
            if (completion != null)
            {
                if (error == null) completion.TrySetResult(payload);
                else completion.TrySetException(error);
            }
        }

        public void TestStarted(ITestAdaptor test)
        {
            if (!IsTrackingCurrentJob)
            {
                return;
            }
            try
            {
                // Prefer FullName for uniqueness; fall back to Name.
                string fullName = test?.FullName;
                if (string.IsNullOrWhiteSpace(fullName))
                {
                    fullName = test?.Name;
                }
                TestJobManager.OnTestStarted(fullName);
            }
            catch
            {
                // ignore
            }
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            if (result == null)
            {
                return;
            }

            if (!result.HasChildren && result.Test?.IsSuite != true)
            {
                _leafResults.Add(result);
                try
                {
                    string fullName = result.Test?.FullName;
                    if (string.IsNullOrWhiteSpace(fullName))
                    {
                        fullName = result.Test?.Name;
                    }

                    bool isFailure = false;
                    string message = null;
                    try
                    {
                        // NUnit outcomes are strings in the adaptor; keep it simple.
                        string outcome = result.ResultState;
                        if (!string.IsNullOrWhiteSpace(outcome))
                        {
                            var o = outcome.Trim().ToLowerInvariant();
                            isFailure = o.Contains("failed") || o.Contains("error");
                        }
                        message = result.Message;
                    }
                    catch
                    {
                        // ignore adaptor quirks
                    }

                    if (IsTrackingCurrentJob)
                    {
                        TestJobManager.OnLeafTestFinished(fullName, isFailure, message);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        #endregion

    }
}
