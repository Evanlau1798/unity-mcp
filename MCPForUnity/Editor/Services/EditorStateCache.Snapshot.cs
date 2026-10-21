using Newtonsoft.Json;

namespace MCPForUnity.Editor.Services
{
    // Snapshot wire models are grouped separately from editor lifecycle tracking.
    internal static partial class EditorStateCache
    {
        private sealed class EditorStateSnapshot
        {
            [JsonProperty("schema_version")]
            public string SchemaVersion { get; set; }

            [JsonProperty("observed_at_unix_ms")]
            public long ObservedAtUnixMs { get; set; }

            [JsonProperty("sequence")]
            public long Sequence { get; set; }

            [JsonProperty("unity")]
            public EditorStateUnity Unity { get; set; }

            [JsonProperty("editor")]
            public EditorStateEditor Editor { get; set; }

            [JsonProperty("activity")]
            public EditorStateActivity Activity { get; set; }

            [JsonProperty("compilation")]
            public EditorStateCompilation Compilation { get; set; }

            [JsonProperty("assets")]
            public EditorStateAssets Assets { get; set; }

            [JsonProperty("tests")]
            public EditorStateTests Tests { get; set; }

            [JsonProperty("transport")]
            public EditorStateTransport Transport { get; set; }

            [JsonProperty("settings")]
            public EditorStateSettings Settings { get; set; }
        }

        private sealed class EditorStateUnity
        {
            [JsonProperty("instance_id")]
            public string InstanceId { get; set; }

            [JsonProperty("unity_version")]
            public string UnityVersion { get; set; }

            [JsonProperty("project_id")]
            public string ProjectId { get; set; }

            [JsonProperty("platform")]
            public string Platform { get; set; }

            [JsonProperty("is_batch_mode")]
            public bool? IsBatchMode { get; set; }
        }

        private sealed class EditorStateEditor
        {
            [JsonProperty("is_focused")]
            public bool? IsFocused { get; set; }

            [JsonProperty("play_mode")]
            public EditorStatePlayMode PlayMode { get; set; }

            [JsonProperty("active_scene")]
            public EditorStateActiveScene ActiveScene { get; set; }
        }

        private sealed class EditorStatePlayMode
        {
            [JsonProperty("is_playing")]
            public bool? IsPlaying { get; set; }

            [JsonProperty("is_paused")]
            public bool? IsPaused { get; set; }

            [JsonProperty("is_changing")]
            public bool? IsChanging { get; set; }
        }

        private sealed class EditorStateActiveScene
        {
            [JsonProperty("path")]
            public string Path { get; set; }

            [JsonProperty("guid")]
            public string Guid { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }
        }

        private sealed class EditorStateActivity
        {
            [JsonProperty("phase")]
            public string Phase { get; set; }

            [JsonProperty("since_unix_ms")]
            public long SinceUnixMs { get; set; }

            [JsonProperty("reasons")]
            public string[] Reasons { get; set; }
        }

        private sealed class EditorStateCompilation
        {
            [JsonProperty("is_compiling")]
            public bool? IsCompiling { get; set; }

            [JsonProperty("is_domain_reload_pending")]
            public bool? IsDomainReloadPending { get; set; }

            [JsonProperty("last_compile_started_unix_ms")]
            public long? LastCompileStartedUnixMs { get; set; }

            [JsonProperty("last_compile_finished_unix_ms")]
            public long? LastCompileFinishedUnixMs { get; set; }

            [JsonProperty("last_domain_reload_before_unix_ms")]
            public long? LastDomainReloadBeforeUnixMs { get; set; }

            [JsonProperty("last_domain_reload_after_unix_ms")]
            public long? LastDomainReloadAfterUnixMs { get; set; }
        }

        private sealed class EditorStateAssets
        {
            [JsonProperty("is_updating")]
            public bool? IsUpdating { get; set; }

            [JsonProperty("external_changes_dirty")]
            public bool? ExternalChangesDirty { get; set; }

            [JsonProperty("external_changes_last_seen_unix_ms")]
            public long? ExternalChangesLastSeenUnixMs { get; set; }

            [JsonProperty("external_changes_dirty_since_unix_ms")]
            public long? ExternalChangesDirtySinceUnixMs { get; set; }

            [JsonProperty("external_changes_last_cleared_unix_ms")]
            public long? ExternalChangesLastClearedUnixMs { get; set; }

            [JsonProperty("refresh")]
            public EditorStateRefresh Refresh { get; set; }
        }

        private sealed class EditorStateRefresh
        {
            [JsonProperty("is_refresh_in_progress")]
            public bool? IsRefreshInProgress { get; set; }

            [JsonProperty("last_refresh_requested_unix_ms")]
            public long? LastRefreshRequestedUnixMs { get; set; }

            [JsonProperty("last_refresh_finished_unix_ms")]
            public long? LastRefreshFinishedUnixMs { get; set; }

            [JsonProperty("operation_id")]
            public string OperationId { get; set; }

            [JsonProperty("status")]
            public string Status { get; set; }

            [JsonProperty("completed")]
            public bool? Completed { get; set; }
        }

        private sealed class EditorStateTests
        {
            [JsonProperty("is_running")]
            public bool? IsRunning { get; set; }

            [JsonProperty("mode")]
            public string Mode { get; set; }

            [JsonProperty("current_job_id")]
            public string CurrentJobId { get; set; }

            [JsonProperty("started_unix_ms")]
            public long? StartedUnixMs { get; set; }

            [JsonProperty("started_by")]
            public string StartedBy { get; set; }

            [JsonProperty("last_run")]
            public EditorStateLastRun LastRun { get; set; }
        }

        private sealed class EditorStateLastRun
        {
            [JsonProperty("finished_unix_ms")]
            public long? FinishedUnixMs { get; set; }

            [JsonProperty("result")]
            public string Result { get; set; }

            [JsonProperty("counts")]
            public object Counts { get; set; }
        }

        private sealed class EditorStateTransport
        {
            [JsonProperty("unity_bridge_connected")]
            public bool? UnityBridgeConnected { get; set; }

            [JsonProperty("last_message_unix_ms")]
            public long? LastMessageUnixMs { get; set; }
        }

        private sealed class EditorStateSettings
        {
            [JsonProperty("batch_execute_max_commands")]
            public int BatchExecuteMaxCommands { get; set; }
        }

    }
}
