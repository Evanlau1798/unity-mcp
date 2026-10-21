using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEditor.TestTools.TestRunner.Api;


namespace MCPForUnity.Editor.Services
{
    internal static partial class TestJobManager
    {
        private sealed class PersistedState
        {
            public string current_job_id { get; set; }
            public List<PersistedJob> jobs { get; set; }
        }

        private sealed class PersistedJob
        {
            public string job_id { get; set; }
            public string status { get; set; }
            public string mode { get; set; }
            public long started_unix_ms { get; set; }
            public long? finished_unix_ms { get; set; }
            public long last_update_unix_ms { get; set; }
            public int? total_tests { get; set; }
            public int completed_tests { get; set; }
            public string current_test_full_name { get; set; }
            public long? current_test_started_unix_ms { get; set; }
            public string last_finished_test_full_name { get; set; }
            public long? last_finished_unix_ms { get; set; }
            public List<TestJobFailure> failures_so_far { get; set; }
            public string error { get; set; }
            public long init_timeout_ms { get; set; }
            public string unity_run_id { get; set; }
            public bool initialization_timed_out { get; set; }
            public bool native_run_started { get; set; }
            public bool native_cleanup_pending { get; set; }
            public TestRunResult pending_result { get; set; }
        }

        private static TestJobStatus ParseStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return TestJobStatus.Running;
            }

            string s = status.Trim().ToLowerInvariant();
            return s switch
            {
                "succeeded" => TestJobStatus.Succeeded,
                "failed" => TestJobStatus.Failed,
                _ => TestJobStatus.Running
            };
        }

        private static void TryRestoreFromSessionState()
        {
            try
            {
                // The separate owner key is written before the history payload. Read it
                // first so malformed or partially persisted history cannot unlock a run.
                string legacy = SessionState.GetString(SessionKeyCurrentJobId, string.Empty);
                lock (LockObj)
                {
                    if (!string.IsNullOrWhiteSpace(legacy)) _currentJobId = legacy;
                }
                string json = SessionState.GetString(SessionKeyJobs, string.Empty);
                if (string.IsNullOrWhiteSpace(json)) return;

                // Recover identity before materializing job details: an invalid jobs list
                // must not discard an otherwise readable owner. Never invent a job/mode.
                var document = JObject.Parse(json);
                string persistedOwner = document.Value<string>("current_job_id");
                lock (LockObj)
                {
                    if (string.IsNullOrWhiteSpace(legacy) && !string.IsNullOrWhiteSpace(persistedOwner))
                        _currentJobId = persistedOwner;
                }
                var state = document.ToObject<PersistedState>();
                if (state?.jobs == null)
                {
                    return;
                }

                lock (LockObj)
                {
                    Jobs.Clear();
                    foreach (var pj in state.jobs)
                    {
                        if (pj == null || string.IsNullOrWhiteSpace(pj.job_id))
                        {
                            continue;
                        }

                        Jobs[pj.job_id] = new TestJob
                        {
                            JobId = pj.job_id,
                            Status = ParseStatus(pj.status),
                            Mode = pj.mode,
                            StartedUnixMs = pj.started_unix_ms,
                            FinishedUnixMs = pj.finished_unix_ms,
                            LastUpdateUnixMs = pj.last_update_unix_ms,
                            TotalTests = pj.total_tests,
                            CompletedTests = pj.completed_tests,
                            CurrentTestFullName = pj.current_test_full_name,
                            CurrentTestStartedUnixMs = pj.current_test_started_unix_ms,
                            LastFinishedTestFullName = pj.last_finished_test_full_name,
                            LastFinishedUnixMs = pj.last_finished_unix_ms,
                            FailuresSoFar = pj.failures_so_far ?? new List<TestJobFailure>(),
                            Error = pj.error,
                            InitTimeoutMs = pj.init_timeout_ms,
                            UnityRunId = pj.unity_run_id,
                            InitializationTimedOut = pj.initialization_timed_out,
                            NativeRunStarted = pj.native_run_started,
                            NativeCleanupPending = pj.native_cleanup_pending,
                            // Persist terminal results only while native cleanup is pending.
                            Result = pj.pending_result
                        };
                    }

                    // Neither a missing job record nor elapsed time proves native
                    // cleanup. Keep the recovered identity until inactivity is known.
                }
            }
            catch (Exception ex)
            {
                // Restoration is best-effort; never block editor load.
                McpLog.Warn($"[TestJobManager] Failed to restore SessionState: {ex.Message}");
            }
        }

        private static void PersistToSessionState(bool force = false)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Throttle non-critical updates to reduce overhead during large test runs
            if (!force && (now - _lastPersistUnixMs) < MinPersistIntervalMs)
            {
                return;
            }

            try
            {
                PersistedState snapshot;
                lock (LockObj)
                {
                    var jobs = Jobs.Values
                        .OrderByDescending(j => j.JobId == _currentJobId)
                        .ThenByDescending(j => j.LastUpdateUnixMs)
                        .Take(MaxJobsToKeep)
                        .Select(j => new PersistedJob
                        {
                            job_id = j.JobId,
                            status = j.Status.ToString().ToLowerInvariant(),
                            mode = j.Mode,
                            started_unix_ms = j.StartedUnixMs,
                            finished_unix_ms = j.FinishedUnixMs,
                            last_update_unix_ms = j.LastUpdateUnixMs,
                            total_tests = j.TotalTests,
                            completed_tests = j.CompletedTests,
                            current_test_full_name = j.CurrentTestFullName,
                            current_test_started_unix_ms = j.CurrentTestStartedUnixMs,
                            last_finished_test_full_name = j.LastFinishedTestFullName,
                            last_finished_unix_ms = j.LastFinishedUnixMs,
                            failures_so_far = (j.FailuresSoFar ?? new List<TestJobFailure>()).Take(FailureCap).ToList(),
                            error = j.Error,
                            init_timeout_ms = j.InitTimeoutMs,
                            unity_run_id = j.UnityRunId,
                            initialization_timed_out = j.InitializationTimedOut,
                            native_run_started = j.NativeRunStarted,
                            native_cleanup_pending = j.NativeCleanupPending,
                            pending_result = j.NativeCleanupPending ? j.Result : null
                        })
                        .ToList();

                    snapshot = new PersistedState
                    {
                        current_job_id = _currentJobId,
                        jobs = jobs
                    };
                }

                SessionState.SetString(SessionKeyCurrentJobId, snapshot.current_job_id ?? string.Empty);
                SessionState.SetString(SessionKeyJobs, JsonConvert.SerializeObject(snapshot));
                _lastPersistUnixMs = now;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[TestJobManager] Failed to persist SessionState: {ex.Message}");
            }
        }

    }
}
