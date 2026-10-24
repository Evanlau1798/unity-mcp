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
    /// <summary>
    /// Concrete implementation of <see cref="ITestRunnerService"/>.
    /// Coordinates Unity Test Runner operations and produces structured results.
    /// </summary>
    internal sealed partial class TestRunnerService : ITestRunnerService, IErrorCallbacks, IDisposable
    {
        private static readonly TestMode[] AllModes = { TestMode.EditMode, TestMode.PlayMode };

        private readonly TestRunnerApi _testRunnerApi;
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private readonly List<ITestResultAdaptor> _leafResults = new List<ITestResultAdaptor>();
        private TaskCompletionSource<TestRunResult> _runCompletionSource;
        private string _trackedJobId;
        public TestRunnerService()
        {
            _testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            _testRunnerApi.hideFlags = HideFlags.HideAndDontSave;
            _testRunnerApi.RegisterCallbacks(this);
        }

        private bool IsTrackingCurrentJob =>
            !string.IsNullOrEmpty(_trackedJobId) && TestJobManager.CurrentJobId == _trackedJobId;

        public async Task<IReadOnlyList<Dictionary<string, string>>> GetTestsAsync(TestMode? mode)
        {
            await _operationLock.WaitAsync().ConfigureAwait(true);
            try
            {
                var modes = mode.HasValue ? new[] { mode.Value } : AllModes;

                var results = new List<Dictionary<string, string>>();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var m in modes)
                {
                    var root = await RetrieveTestRootAsync(m).ConfigureAwait(true);
                    if (root != null)
                    {
                        CollectFromNode(root, m, results, seen, new List<string>());
                    }
                }

                return results;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public async Task<TestRunResult> RunTestsAsync(TestMode mode, TestFilterOptions filterOptions = null)
        {
            // The pre-reload Task no longer exists, but its Unity run may still be active.
            // Clearing a job must not let a second run consume the first run's callbacks.
            if (_trackedJobId != null && _runCompletionSource == null)
            {
                throw new InvalidOperationException("A recovered Unity test run is still in progress.");
            }

            await _operationLock.WaitAsync().ConfigureAwait(true);
            Task<TestRunResult> runTask = null;
            string ownerJobId = TestJobManager.CurrentJobId;
            bool dispatchStarted = false;
            bool adjustedPlayModeOptions = false;
            bool appliedNoThrottling = false;
            bool originalEnterPlayModeOptionsEnabled = false;
            EnterPlayModeOptions originalEnterPlayModeOptions = EnterPlayModeOptions.None;
            try
            {
                if (_runCompletionSource != null && !_runCompletionSource.Task.IsCompleted)
                {
                    throw new InvalidOperationException("A Unity test run is already in progress.");
                }

                if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    throw new InvalidOperationException("Cannot start a test run while the Editor is in or entering Play Mode. Stop Play Mode and try again.");
                }

                if (mode == TestMode.PlayMode)
                {
                    // PlayMode runs transition the editor into play across multiple update ticks. Unity's
                    // built-in pipeline schedules SaveModifiedSceneTask early, but that task uses
                    // EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo which throws once play mode is
                    // active. To minimize that window we pre-save dirty scenes and disable domain reload (so the
                    // MCP bridge stays alive). We do NOT force runSynchronously here because that can freeze the
                    // editor in some projects. If the TestRunner still hits the save task after entering play, the
                    // run can fail; in that case, rerun from a clean Edit Mode state.
                    adjustedPlayModeOptions = EnsurePlayModeRunsWithoutDomainReload(
                        out originalEnterPlayModeOptionsEnabled,
                        out originalEnterPlayModeOptions);
                }

                _leafResults.Clear();
                _trackedJobId = TestJobManager.CurrentJobId;
                _nativeQueryFailedSince = null;
                _runCompletionSource = new TaskCompletionSource<TestRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                runTask = _runCompletionSource.Task;
                // Mark running immediately so readiness snapshots reflect the busy state even before callbacks fire.
                TestRunStatus.MarkStarted(mode);

                var filter = new Filter
                {
                    testMode = mode,
                    testNames = filterOptions?.TestNames,
                    groupNames = filterOptions?.GroupNames,
                    categoryNames = filterOptions?.CategoryNames,
                    assemblyNames = filterOptions?.AssemblyNames
                };
                var settings = new ExecutionSettings(filter);

                // Save dirty scenes for all test modes to prevent modal dialogs blocking MCP
                // (Issue #525: EditMode tests were blocked by save dialog)
                SaveDirtyScenesIfNeeded();

                // EditMode setup also advances over editor frames before RunStarted.
                // Disable background throttling before Execute for both test modes.
                appliedNoThrottling = true;
                TestRunnerNoThrottle.ApplyNoThrottlingPreemptive();

                // Unity's overloaded null check also rejects a destroyed API object.
                // Once Execute is entered, an exception may follow native side effects.
                if (_testRunnerApi == null)
                    throw new InvalidOperationException("Unity Test Runner API is unavailable before native dispatch.");
                dispatchStarted = true;
                _unityRunId = _testRunnerApi.Execute(settings);
                TestJobManager.OnRunScheduled(_trackedJobId, _unityRunId);
            }
            catch (Exception ex)
            {
                if (dispatchStarted && _trackedJobId != null)
                {
                    // Execute may have scheduled native work before throwing. Keep the
                    // task, operation lock and settings until its cleanup is confirmed.
                    CompleteRun(null, ex);
                }
                else
                {
                    // This path never entered Execute, so there is no native side effect
                    // to await. Finalize only the original owner, then restore settings.
                    if (ownerJobId != null && TestJobManager.CurrentJobId == ownerJobId)
                        TestJobManager.FinalizeCurrentJobFromRunError(ex.Message);
                    _trackedJobId = null;
                    _unityRunId = null;
                    _runCompletionSource = null;
                    if (appliedNoThrottling) TestRunnerNoThrottle.RestoreThrottling();
                    TestRunStatus.MarkFinished();
                    if (adjustedPlayModeOptions)
                    {
                        RestoreEnterPlayModeOptions(originalEnterPlayModeOptionsEnabled, originalEnterPlayModeOptions);
                    }

                    _operationLock.Release();
                    throw;
                }
            }

            try
            {
                return await runTask.ConfigureAwait(true);
            }
            finally
            {
                if (adjustedPlayModeOptions)
                {
                    RestoreEnterPlayModeOptions(originalEnterPlayModeOptionsEnabled, originalEnterPlayModeOptions);
                }

                _operationLock.Release();
            }
        }

        public void Dispose()
        {
            EditorApplication.update -= CompleteRunWhenIdle;
            _nativeQueryFailedSince = null;
            try
            {
                _testRunnerApi?.UnregisterCallbacks(this);
            }
            catch
            {
                // Ignore cleanup errors
            }

            if (_testRunnerApi != null)
            {
                ScriptableObject.DestroyImmediate(_testRunnerApi);
            }

            _operationLock.Dispose();
        }

        private static int CountLeafTests(ITestAdaptor node)
        {
            if (node == null)
            {
                return 0;
            }

            if (!node.HasChildren)
            {
                return 1;
            }

            int total = 0;
            try
            {
                foreach (var child in node.Children)
                {
                    total += CountLeafTests(child);
                }
            }
            catch
            {
                // If Unity changes the adaptor behavior, treat it as "unknown total".
                return 0;
            }

            return total;
        }

        private static bool EnsurePlayModeRunsWithoutDomainReload(
            out bool originalEnterPlayModeOptionsEnabled,
            out EnterPlayModeOptions originalEnterPlayModeOptions)
        {
            originalEnterPlayModeOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            originalEnterPlayModeOptions = EditorSettings.enterPlayModeOptions;

            // When Play Mode triggers a domain reload, the MCP connection is torn down and the pending
            // test run response never makes it back to the caller. To keep the bridge alive for this
            // invocation, temporarily enable Enter Play Mode Options with domain reload disabled.
            bool domainReloadDisabled = (originalEnterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0;
            bool needsChange = !originalEnterPlayModeOptionsEnabled || !domainReloadDisabled;
            if (!needsChange)
            {
                return false;
            }

            // Persist originals to SessionState so they survive domain reloads. If the run is
            // interrupted (domain reload, crash), PlayModeOptionsGuard restores them on next load.
            PlayModeOptionsGuard.Save(originalEnterPlayModeOptionsEnabled, originalEnterPlayModeOptions);

            var desired = originalEnterPlayModeOptions | EnterPlayModeOptions.DisableDomainReload;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = desired;
            return true;
        }

        private static void RestoreEnterPlayModeOptions(bool originalEnabled, EnterPlayModeOptions originalOptions)
        {
            EditorSettings.enterPlayModeOptions = originalOptions;
            EditorSettings.enterPlayModeOptionsEnabled = originalEnabled;
            PlayModeOptionsGuard.Clear();
        }

        internal static void ValidateScenesBeforeTestRun()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty && string.IsNullOrEmpty(scene.path))
                    throw new UnsavedTestSceneException(scene.name);
            }
        }

        private static void SaveDirtyScenesIfNeeded()
        {
            // Recheck after waiting for the service lock, before any scene save or native dispatch.
            ValidateScenesBeforeTestRun();
            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                {
                    try
                    {
                        EditorSceneManager.SaveScene(scene);
                    }
                    catch (Exception ex)
                    {
                        McpLog.Warn($"[TestRunnerService] Failed to save dirty scene '{scene.name}': {ex.Message}");
                    }
                }
            }
        }

    }

    internal sealed class UnsavedTestSceneException : InvalidOperationException
    {
        public string SceneName { get; }

        public UnsavedTestSceneException(string sceneName)
            : base("Save or close the modified unnamed scene in the Unity Editor before running tests.")
        {
            SceneName = string.IsNullOrEmpty(sceneName) ? "Untitled" : sceneName;
        }
    }

}
