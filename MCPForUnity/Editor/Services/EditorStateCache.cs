using System;
using System.Globalization;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Maintains a cached readiness snapshot (v2) so status reads remain fast even when Unity is busy.
    /// Updated on the main thread via Editor callbacks and periodic update ticks.
    /// </summary>
    [InitializeOnLoad]
    internal static partial class EditorStateCache
    {
        private static readonly object LockObj = new();
        private static long _sequence;
        private static long _observedUnixMs;

        private static bool _lastIsCompiling;

        // Compile edges live in SessionState, recorded from the CompilationPipeline
        // events, rather than in statics sampled off the update tick. Two reasons,
        // both load-bearing:
        //
        //  - A successful compile ends in a domain reload that wipes every static in
        //    this class, including the "was compiling" flag the falling edge was
        //    derived from. The finish of the very compile a client is waiting on was
        //    therefore unobservable: both timestamps read null afterwards, so nothing
        //    downstream could tell "finished" from "never started" (issue #814).
        //  - The events fire at the true edges. Sampling quantised them to the 1s
        //    update throttle and dropped any compile shorter than one tick entirely.
        //
        // SessionState survives domain reloads and dies with the editor session,
        // which is exactly the lifetime these values describe.
        private const string CompileStartedKey = "MCPForUnity.EditorState.CompileStartedUnixMs";
        private const string CompileFinishedKey = "MCPForUnity.EditorState.CompileFinishedUnixMs";
        private const string CompileCountKey = "MCPForUnity.EditorState.CompileCount";

        private static bool _domainReloadPending;
        private static long? _domainReloadBeforeUnixMs;
        private static long? _domainReloadAfterUnixMs;

        private static double _lastUpdateTimeSinceStartup;
        private const double MinUpdateIntervalSeconds = 1.0; // Reduced frequency: 1s instead of 0.25s

        // State tracking to detect when snapshot actually changes (checked BEFORE building)
        private static string _lastTrackedScenePath;
        private static string _lastTrackedSceneName;
        private static bool _lastTrackedIsFocused;
        private static bool _lastTrackedIsPlaying;
        private static bool _lastTrackedIsPaused;
        private static bool _lastTrackedIsUpdating;
        private static bool _lastTrackedTestsRunning;
        private static string _lastTrackedCurrentJobId;
        private static string _lastTrackedActivityPhase;

        private static JObject _cached;

        static EditorStateCache()
        {
            try
            {
                _sequence = 0;
                _observedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _cached = BuildSnapshot("init");

                EditorApplication.update += OnUpdate;
                EditorApplication.playModeStateChanged += _ => ForceUpdate("playmode");

                // Tracks whether an assembly compilation is actually running, for
                // GetActualIsCompiling. Statics reset on domain reload and this
                // [InitializeOnLoad] ctor re-subscribes, so the flag is per-domain.
                // The timestamps beside it are not per-domain — see the SessionState
                // note on CompileStartedKey.
                UnityEditor.Compilation.CompilationPipeline.compilationStarted += _ =>
                {
                    _pipelineCompilationRunning = true;
                    SetSessionUnixMs(CompileStartedKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    SessionState.SetInt(CompileCountKey, SessionState.GetInt(CompileCountKey, 0) + 1);
                    ForceUpdate("compilation_started");
                };
                UnityEditor.Compilation.CompilationPipeline.compilationFinished += _ =>
                {
                    _pipelineCompilationRunning = false;
                    // Fires before the domain reload, which is what makes the finish
                    // observable at all: the write lands while this domain is alive and
                    // is read back by the next one.
                    SetSessionUnixMs(CompileFinishedKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    ForceUpdate("compilation_finished");
                };

                AssemblyReloadEvents.beforeAssemblyReload += () =>
                {
                    _domainReloadPending = true;
                    _domainReloadBeforeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    ForceUpdate("before_domain_reload");
                };
                AssemblyReloadEvents.afterAssemblyReload += () =>
                {
                    _domainReloadPending = false;
                    _domainReloadAfterUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    ForceUpdate("after_domain_reload");
                };
            }
            catch (Exception ex)
            {
                McpLog.Error($"[EditorStateCache] Failed to initialise: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void OnUpdate() => UpdateSnapshot(forceObservation: false);

        private static void UpdateSnapshot(bool forceObservation)
        {
            // Throttle to reduce overhead while keeping the snapshot fresh enough for polling clients.
            double now = EditorApplication.timeSinceStartup;
            // Use GetActualIsCompiling() to avoid isCompiling false positives (issues #549, #1276)
            bool isCompiling = GetActualIsCompiling();

            // Check for compilation edge transitions (always update on these)
            bool compilationEdge = isCompiling != _lastIsCompiling;

            if (!forceObservation && !compilationEdge && now - _lastUpdateTimeSinceStartup < MinUpdateIntervalSeconds)
            {
                return;
            }

            // Fast state-change detection BEFORE building snapshot.
            // This avoids the expensive BuildSnapshot() call entirely when nothing changed.
            // These checks are much cheaper than building a full JSON snapshot.
            var scene = EditorSceneManager.GetActiveScene();
            string scenePath = string.IsNullOrEmpty(scene.path) ? null : scene.path;
            string sceneName = scene.name ?? string.Empty;
            bool isFocused = InternalEditorUtility.isApplicationActive;
            bool isPlaying = EditorApplication.isPlaying;
            bool isPaused = EditorApplication.isPaused;
            bool isUpdating = EditorApplication.isUpdating;
            bool testsRunning = TestRunStatus.IsRunning || TestJobManager.HasRunningJob;
            string currentJobId = TestJobManager.CurrentJobId;

            var activityPhase = GetActivityPhase(testsRunning, isCompiling, isUpdating);

            bool hasChanges = _cached == null || compilationEdge
                || _lastTrackedScenePath != scenePath
                || _lastTrackedSceneName != sceneName
                || _lastTrackedIsFocused != isFocused
                || _lastTrackedIsPlaying != isPlaying
                || _lastTrackedIsPaused != isPaused
                || _lastTrackedIsUpdating != isUpdating
                || _lastTrackedTestsRunning != testsRunning
                || _lastTrackedCurrentJobId != currentJobId
                || _lastTrackedActivityPhase != activityPhase;

            _lastUpdateTimeSinceStartup = now;
            if (!hasChanges)
            {
                // These cheap live reads are a new observation even when unchanged.
                // Renew the heartbeat without rebuilding the scene GUID or JSON model.
                lock (LockObj)
                {
                    _observedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _cached["observed_at_unix_ms"] = _observedUnixMs;
                }
                return;
            }

            // Update tracked state
            _lastTrackedScenePath = scenePath;
            _lastTrackedSceneName = sceneName;
            _lastTrackedIsFocused = isFocused;
            _lastTrackedIsPlaying = isPlaying;
            _lastTrackedIsPaused = isPaused;
            _lastTrackedIsUpdating = isUpdating;
            _lastTrackedTestsRunning = testsRunning;
            _lastTrackedCurrentJobId = currentJobId;
            _lastTrackedActivityPhase = activityPhase;

            ForceUpdate(forceObservation ? "read" : "tick");
        }

        private static void ForceUpdate(string reason)
        {
            lock (LockObj)
            {
                _cached = BuildSnapshot(reason);
            }
        }

        private static JObject BuildSnapshot(string reason)
        {
            _sequence++;
            _observedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            bool isCompiling = GetActualIsCompiling();
            _lastIsCompiling = isCompiling;

            var scene = EditorSceneManager.GetActiveScene();
            string scenePath = string.IsNullOrEmpty(scene.path) ? null : scene.path;
            string sceneGuid = !string.IsNullOrEmpty(scenePath) ? AssetDatabase.AssetPathToGUID(scenePath) : null;

            bool testsRunning = TestRunStatus.IsRunning || TestJobManager.HasRunningJob;
            var testsMode = TestRunStatus.Mode?.ToString();
            string currentJobId = TestJobManager.CurrentJobId;
            bool isFocused = InternalEditorUtility.isApplicationActive;

            var activityPhase = GetActivityPhase(testsRunning, isCompiling, EditorApplication.isUpdating);

            var snapshot = new EditorStateSnapshot
            {
                SchemaVersion = "unity-mcp/editor_state@2",
                ObservedAtUnixMs = _observedUnixMs,
                Sequence = _sequence,
                Unity = new EditorStateUnity
                {
                    InstanceId = null,
                    UnityVersion = Application.unityVersion,
                    ProjectId = null,
                    Platform = Application.platform.ToString(),
                    IsBatchMode = Application.isBatchMode
                },
                Editor = new EditorStateEditor
                {
                    IsFocused = isFocused,
                    PlayMode = new EditorStatePlayMode
                    {
                        IsPlaying = EditorApplication.isPlaying,
                        IsPaused = EditorApplication.isPaused,
                        IsChanging = EditorApplication.isPlayingOrWillChangePlaymode
                    },
                    ActiveScene = new EditorStateActiveScene
                    {
                        Path = scenePath,
                        Guid = sceneGuid,
                        Name = scene.name ?? string.Empty
                    }
                },
                Activity = new EditorStateActivity
                {
                    Phase = activityPhase,
                    SinceUnixMs = _observedUnixMs,
                    Reasons = new[] { reason }
                },
                Compilation = new EditorStateCompilation
                {
                    IsCompiling = isCompiling,
                    IsDomainReloadPending = _domainReloadPending,
                    LastCompileStartedUnixMs = GetSessionUnixMs(CompileStartedKey),
                    LastCompileFinishedUnixMs = GetSessionUnixMs(CompileFinishedKey),
                    LastDomainReloadBeforeUnixMs = _domainReloadBeforeUnixMs,
                    LastDomainReloadAfterUnixMs = _domainReloadAfterUnixMs
                },
                Assets = new EditorStateAssets
                {
                    IsUpdating = EditorApplication.isUpdating,
                    ExternalChangesDirty = false,
                    ExternalChangesLastSeenUnixMs = null,
                    ExternalChangesDirtySinceUnixMs = null,
                    ExternalChangesLastClearedUnixMs = null,
                    Refresh = RefreshOperationTracker.GetRefreshSnapshot().ToObject<EditorStateRefresh>()
                },
                Tests = new EditorStateTests
                {
                    IsRunning = testsRunning,
                    Mode = testsMode,
                    CurrentJobId = string.IsNullOrEmpty(currentJobId) ? null : currentJobId,
                    StartedUnixMs = TestRunStatus.StartedUnixMs,
                    StartedBy = "unknown",
                    LastRun = TestRunStatus.FinishedUnixMs.HasValue
                        ? new EditorStateLastRun
                        {
                            FinishedUnixMs = TestRunStatus.FinishedUnixMs,
                            Result = "unknown",
                            Counts = null
                        }
                        : null
                },
                Transport = new EditorStateTransport
                {
                    UnityBridgeConnected = null,
                    LastMessageUnixMs = null
                },
                Settings = new EditorStateSettings
                {
                    BatchExecuteMaxCommands = Tools.BatchExecute.GetMaxCommandsPerBatch()
                }
            };

            return JObject.FromObject(snapshot);
        }

        private static string GetActivityPhase(bool testsRunning, bool isCompiling, bool isUpdating)
        {
            if (testsRunning) return "running_tests";
            if (isCompiling) return "compiling";
            if (_domainReloadPending) return "domain_reload";
            if (isUpdating) return "asset_import";
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "playmode_transition";
            return "idle";
        }

        public static JObject GetSnapshot()
        {
            // A synchronous read must observe current signals even within the tick throttle.
            UpdateSnapshot(forceObservation: true);
            lock (LockObj)
            {
                // Defensive: if something went wrong early, rebuild once.
                if (_cached == null)
                {
                    _cached = BuildSnapshot("rebuild");
                }

                // Always return a fresh clone to prevent mutation bugs.
                // The main GC optimization comes from state-change detection (OnUpdate)
                // which prevents unnecessary _cached rebuilds, not from caching the clone.
                var clone = (JObject)_cached.DeepClone();

                // Ownership can change before the next cache tick, including an orphan
                // restored without a known mode. It must never appear ready in that gap.
                string currentJobId = TestJobManager.CurrentJobId;
                bool testsRunning = TestRunStatus.IsRunning || !string.IsNullOrEmpty(currentJobId);
                if (clone["tests"] is JObject tests)
                {
                    tests["is_running"] = testsRunning;
                    tests["current_job_id"] = currentJobId == null ? JValue.CreateNull() : new JValue(currentJobId);
                    string testsMode = TestRunStatus.Mode?.ToString();
                    tests["mode"] = testsMode == null ? JValue.CreateNull() : new JValue(testsMode);
                }
                if (clone["activity"] is JObject activity
                    && (testsRunning || activity.Value<string>("phase") == "running_tests"))
                    activity["phase"] = GetActivityPhase(testsRunning, GetActualIsCompiling(), EditorApplication.isUpdating);

                // Receipt changes are independent of the cached editor signals.
                // Expose pending refresh immediately, even before compilation starts.
                if (clone["assets"] is JObject assets)
                    assets["refresh"] = RefreshOperationTracker.GetRefreshSnapshot();

                return clone;
            }
        }

        /// <summary>
        /// Compilations begun this editor session, surviving domain reloads. Callers
        /// that trigger a compile snapshot this first, then wait for it to move — the
        /// only signal that separates "a compile ran" from "one never started", which
        /// <see cref="GetActualIsCompiling"/> reads as idle either way.
        /// </summary>
        internal static int CompileCount => SessionState.GetInt(CompileCountKey, 0);

        internal static long? GetSessionUnixMs(string key)
        {
            string raw = SessionState.GetString(key, string.Empty);
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                ? value
                : (long?)null;
        }

        // SessionState has no long overload, so these round-trip through an
        // invariant string rather than losing precision through int or float.
        internal static void SetSessionUnixMs(string key, long value)
            => SessionState.SetString(key, value.ToString(CultureInfo.InvariantCulture));

        // Set/cleared by the CompilationPipeline.compilationStarted/Finished events
        // subscribed in the static ctor. NOTE: CompilationPipeline.isCompiling does not
        // exist on the supported Unity range (verified by reflection probe on 2021.3 and
        // 6000.4 — neither public nor non-public), so the reflection this replaced never
        // resolved and always fell back to the raw signal.
        private static bool _pipelineCompilationRunning;

        /// <summary>
        /// Returns the actual compilation state, working around known Unity quirks where
        /// EditorApplication.isCompiling reports false positives while no compilation is
        /// running: a recompile deferred by Recompile-After-Finished-Playing keeps it true
        /// for the whole play session (issue #549), and a project holding
        /// EditorApplication.LockReloadAssemblies keeps it true until the lock is released
        /// (issue #1276). In both cases the event-tracked pipeline flag is authoritative.
        /// </summary>
        internal static bool GetActualIsCompiling()
        {
            // If EditorApplication.isCompiling is false, Unity is definitely not compiling
            if (!EditorApplication.isCompiling)
            {
                return false;
            }

            // Otherwise trust the event-tracked pipeline state: isCompiling stays true for as
            // long as an assembly reload is deferred, with no compilation actually running.
            return _pipelineCompilationRunning;
        }
    }
}
