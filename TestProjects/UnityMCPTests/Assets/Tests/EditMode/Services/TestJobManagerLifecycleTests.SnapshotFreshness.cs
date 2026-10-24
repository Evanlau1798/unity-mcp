using System;
using System.Linq;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;

namespace MCPForUnityTests.Editor.Services
{
    public partial class TestJobManagerLifecycleTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void EditorSnapshot_UnchangedIdleObservationRenewsHeartbeatWithoutRebuild(bool readThroughGetter)
        {
            WithSnapshotFreshnessState(() =>
            {
                var cached = PrimeSnapshotTracking();
                Assert.AreEqual("idle", cached["activity"]?["phase"]?.ToString());
                long sequence = cached.Value<long>("sequence");
                long staleAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
                cached["observed_at_unix_ms"] = staleAt;
                SetSnapshotField("_observedUnixMs", staleAt);
                double previousTick = EditorApplication.timeSinceStartup - (readThroughGetter ? 0 : 2);
                SetSnapshotField("_lastUpdateTimeSinceStartup", previousTick);
                long beforeObservation = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (readThroughGetter) EditorStateCache.GetSnapshot();
                else typeof(EditorStateCache).GetMethod("OnUpdate", PrivateStatic).Invoke(null, null);

                var observed = (JObject)SnapshotField("_cached");
                Assert.GreaterOrEqual(observed.Value<long>("observed_at_unix_ms"), beforeObservation,
                    "A real unchanged-state observation must keep idle snapshots fresh even while focused.");
                Assert.AreEqual(observed.Value<long>("observed_at_unix_ms"), SnapshotField("_observedUnixMs"));
                Assert.AreSame(cached, observed, "Heartbeat updates must reuse the unchanged snapshot.");
                Assert.AreEqual(sequence, observed.Value<long>("sequence"));
                Assert.GreaterOrEqual((double)SnapshotField("_lastUpdateTimeSinceStartup"), previousTick);
            });
        }

        [Test]
        public void EditorSnapshot_ReadReobservesChangedImportAndFocusInsideTickInterval()
        {
            WithSnapshotFreshnessState(() =>
            {
                var cached = PrimeSnapshotTracking();
                bool isUpdating = EditorApplication.isUpdating;
                bool isFocused = InternalEditorUtility.isApplicationActive;
                cached["assets"]["is_updating"] = !isUpdating;
                cached["editor"]["is_focused"] = !isFocused;
                SetSnapshotField("_lastTrackedIsUpdating", !isUpdating);
                SetSnapshotField("_lastTrackedIsFocused", !isFocused);
                SetSnapshotField("_lastUpdateTimeSinceStartup", EditorApplication.timeSinceStartup);
                long staleAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
                cached["observed_at_unix_ms"] = staleAt;
                SetSnapshotField("_observedUnixMs", staleAt);
                long sequence = cached.Value<long>("sequence");
                long beforeRead = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                var snapshot = EditorStateCache.GetSnapshot();

                Assert.AreEqual(isUpdating, snapshot["assets"].Value<bool>("is_updating"),
                    "Refreshing only the timestamp would conceal a stale import signal.");
                Assert.AreEqual(isFocused, snapshot["editor"].Value<bool>("is_focused"));
                Assert.GreaterOrEqual(snapshot.Value<long>("observed_at_unix_ms"), beforeRead);
                Assert.Greater(snapshot.Value<long>("sequence"), sequence, "Changed signals require a new snapshot.");
            });
        }

        private static object SnapshotField(string name) => typeof(EditorStateCache).GetField(name, PrivateStatic).GetValue(null);
        private static void SetSnapshotField(string name, object value) => typeof(EditorStateCache).GetField(name, PrivateStatic).SetValue(null, value);

        private static JObject PrimeSnapshotTracking()
        {
            ForceEditorSnapshot();
            var cached = (JObject)SnapshotField("_cached");
            var scene = EditorSceneManager.GetActiveScene();
            SetSnapshotField("_lastTrackedScenePath", string.IsNullOrEmpty(scene.path) ? null : scene.path);
            SetSnapshotField("_lastTrackedSceneName", scene.name ?? string.Empty);
            SetSnapshotField("_lastTrackedIsFocused", InternalEditorUtility.isApplicationActive);
            SetSnapshotField("_lastTrackedIsPlaying", EditorApplication.isPlaying);
            SetSnapshotField("_lastTrackedIsPaused", EditorApplication.isPaused);
            SetSnapshotField("_lastTrackedIsUpdating", EditorApplication.isUpdating);
            SetSnapshotField("_lastTrackedTestsRunning", TestRunStatus.IsRunning || TestJobManager.HasRunningJob);
            SetSnapshotField("_lastTrackedCurrentJobId", TestJobManager.CurrentJobId);
            SetSnapshotField("_lastTrackedActivityPhase", cached["activity"]?["phase"]?.ToString());
            return cached;
        }

        private static void WithSnapshotFreshnessState(Action action)
        {
            var fields = typeof(EditorStateCache).GetFields(PrivateStatic)
                .Where(field => !field.IsLiteral && !field.IsInitOnly).ToArray();
            var saved = fields.Select(field => field.GetValue(null)).ToArray();
            try { action(); }
            finally
            {
                for (int i = 0; i < fields.Length; i++) fields[i].SetValue(null, saved[i]);
            }
        }
    }
}
