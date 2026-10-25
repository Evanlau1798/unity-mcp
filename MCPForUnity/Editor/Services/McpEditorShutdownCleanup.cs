using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Best-effort cleanup when the Unity Editor is quitting.
    /// - Stops active transports so clients don't see a "hung" session longer than necessary.
    /// The managed HTTP server tracks Editor process identities and closes after the last owner exits.
    /// </summary>
    [InitializeOnLoad]
    internal static class McpEditorShutdownCleanup
    {
        static McpEditorShutdownCleanup()
        {
            // Guard against duplicate subscriptions across domain reloads.
            try { EditorApplication.quitting -= OnEditorQuitting; } catch { }
            EditorApplication.quitting += OnEditorQuitting;
        }

        // Mirror HttpAutoStartHandler/StdioBridgeHost: batch mode needs an explicit opt-in.
        internal static bool ShouldRunCleanup() =>
            ShouldRunCleanup(Application.isBatchMode, Environment.GetEnvironmentVariable("UNITY_MCP_ALLOW_BATCH"));

        internal static bool ShouldRunCleanup(bool isBatchMode, string allowBatchEnv) =>
            !isBatchMode || !string.IsNullOrWhiteSpace(allowBatchEnv);

        private static void OnEditorQuitting()
        {
            if (!ShouldRunCleanup()) return;

            // 1) Stop transports (best-effort, bounded wait).
            try
            {
                var transport = MCPServiceLocator.TransportManager;

                Task stopHttp = transport.StopAsync(TransportMode.Http);
                Task stopStdio = transport.StopAsync(TransportMode.Stdio);

                try { Task.WaitAll(new[] { stopHttp, stopStdio }, 750); } catch { }
            }
            catch (Exception ex)
            {
                // Avoid hard failures on quit.
                McpLog.Warn($"Shutdown cleanup: failed to stop transports: {ex.Message}");
            }

            // Do not stop the shared server here: another Editor may be connected or reloading.
            // Its own lifetime watcher observes OS process exit, independently of this launcher's lifetime.
        }
    }
}

