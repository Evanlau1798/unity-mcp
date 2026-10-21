using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Resources.Tests;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Starts a Unity Test Runner run asynchronously and returns a job id immediately.
    /// Use get_test_job(job_id) to poll status/results.
    /// </summary>
    [McpForUnityTool("run_tests", AutoRegister = false, Group = "testing")]
    public static class RunTests
    {
        public static Task<object> HandleCommand(JObject @params)
        {
            try
            {
                // Check for clear_stuck action first
                if (ParamCoercion.CoerceBool(@params?["clear_stuck"], false))
                {
                    string originalJobId = TestJobManager.CurrentJobId;
                    bool wasCleared = TestJobManager.ClearStuckJob();
                    if (TestJobManager.HasRunningJob)
                        return Task.FromResult<object>(TestsBusyResponse(wasCleared));
                    return Task.FromResult<object>(new SuccessResponse(
                        wasCleared ? "Stuck job cleared." : "No running job to clear.",
                        new { cleared = wasCleared, job_id = originalJobId, native_ownership_retained = false }
                    ));
                }

                string modeStr = @params?["mode"]?.ToString();
                if (string.IsNullOrWhiteSpace(modeStr))
                {
                    modeStr = "EditMode";
                }

                if (!ModeParser.TryParse(modeStr, out var parsedMode, out var parseError))
                {
                    return Task.FromResult<object>(new ErrorResponse(parseError));
                }

                // Both owners are checked without native dispatch. StartJob repeats
                // the guards at acquisition, so its callers share the same contract.
                if (TestJobManager.HasRunningJob)
                    return Task.FromResult<object>(TestsBusyResponse());
                if (RefreshOperationTracker.IsInProgress)
                    return Task.FromResult<object>(RefreshBusyResponse());

                var p = new ToolParams(@params);
                bool includeDetails = p.GetBool("includeDetails");
                bool includeFailedTests = p.GetBool("includeFailedTests");

                var filterOptions = GetFilterOptions(@params);
                long initTimeoutMs = p.GetInt("initTimeout") ?? 0;
                string jobId = TestJobManager.StartJob(parsedMode.Value, filterOptions, initTimeoutMs);

                return Task.FromResult<object>(new SuccessResponse("Test job started.", new
                {
                    job_id = jobId,
                    status = "running",
                    mode = parsedMode.Value.ToString(),
                    include_details = includeDetails,
                    include_failed_tests = includeFailedTests
                }));
            }
            catch (UnsavedTestSceneException ex)
            {
                return Task.FromResult<object>(new ErrorResponse("unsaved_scene", new
                {
                    reason = "unsaved_scene", scene_name = ex.SceneName,
                    message = ex.Message, request_accepted = false
                }));
            }
            catch (Exception ex)
            {
                // Classify by the actual owner, not overlapping exception wording.
                if (TestJobManager.HasRunningJob)
                    return Task.FromResult<object>(TestsBusyResponse());
                if (RefreshOperationTracker.IsInProgress)
                    return Task.FromResult<object>(RefreshBusyResponse());
                // Preserve compatibility with any non-owned runner rejection.
                if (ex.Message != null && ex.Message.IndexOf("already in progress", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Task.FromResult<object>(new ErrorResponse("tests_running", new { reason = "tests_running", retry_after_ms = 5000 }));
                }
                return Task.FromResult<object>(new ErrorResponse($"Failed to start test job: {ex.Message}"));
            }
        }

        private static ErrorResponse TestsBusyResponse(bool? cleared = null) => new ErrorResponse(
            "tests_running", new
            {
                reason = "tests_running", job_id = TestJobManager.CurrentJobId,
                native_ownership_retained = TestJobManager.HasRunningJob,
                cleared, retry_after_ms = 5000
            });

        private static ErrorResponse RefreshBusyResponse() => new ErrorResponse(
            "refresh_in_progress", new
            {
                reason = "refresh_in_progress",
                active_operation_id = RefreshOperationTracker.ActiveOperationId,
                retry_after_ms = 250
            });

        private static TestFilterOptions GetFilterOptions(JObject @params)
        {
            if (@params == null)
            {
                return null;
            }

            var p = new ToolParams(@params);
            var testNames = p.GetStringArray("testNames");
            var groupNames = p.GetStringArray("groupNames");
            var categoryNames = p.GetStringArray("categoryNames");
            var assemblyNames = p.GetStringArray("assemblyNames");

            if (testNames == null && groupNames == null && categoryNames == null && assemblyNames == null)
            {
                return null;
            }

            return new TestFilterOptions
            {
                TestNames = testNames,
                GroupNames = groupNames,
                CategoryNames = categoryNames,
                AssemblyNames = assemblyNames
            };
        }
    }
}
