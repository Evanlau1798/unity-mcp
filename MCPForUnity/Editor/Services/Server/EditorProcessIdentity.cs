using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Services.Server
{
    // Only System APIs: registration may run on a background transport task.
    internal static class EditorProcessIdentity
    {
        internal static void AddTo(JObject registration)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                registration["editor_pid"] = process.Id;
                registration["editor_started_ms"] = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
            }
            catch { } // Missing identity retains the server rather than risking a live peer.
        }

        internal static void AddTo(ProcessStartInfo launch)
        {
            var identity = new JObject();
            AddTo(identity);
            launch.EnvironmentVariables["UNITY_MCP_EDITOR_PID"] = (string)identity["editor_pid"] ?? "";
            launch.EnvironmentVariables["UNITY_MCP_EDITOR_STARTED_MS"] = (string)identity["editor_started_ms"] ?? "";
        }
    }
}
