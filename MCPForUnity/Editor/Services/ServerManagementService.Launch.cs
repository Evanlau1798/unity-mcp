using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Server;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    public partial class ServerManagementService
    {
        /// <summary>
        /// Start the local HTTP server headless (no terminal window), redirecting its
        /// stdout/stderr to Library/MCPForUnity/Logs/server-launch-{port}.log.
        /// Refuses occupied ports; a shared or externally launched server is never replaced.
        /// </summary>
        public bool StartLocalHttpServer(bool quiet = false)
        {
            /// Clean stale Python build artifacts when using a local dev server path
            AssetPathUtility.CleanLocalServerBuildArtifacts();

            if (!TryGetLocalHttpServerCommandParts(out _, out _, out var displayCommand, out var error))
            {
                if (!quiet)
                {
                    EditorUtility.DisplayDialog(
                        "Cannot Start HTTP Server",
                        error ?? "The server command could not be constructed with the current settings.",
                        "OK");
                }
                return false;
            }

            // A listener may serve other Editors. Starting must never terminate it.

            // If the port is still occupied, don't start and explain why (avoid confusing "refusing to stop" warnings).
            try
            {
                string httpUrl = HttpEndpointUtility.GetLocalBaseUrl();
                if (Uri.TryCreate(httpUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
                {
                    var remaining = GetListeningProcessIdsForPort(uri.Port);
                    if (remaining.Count > 0)
                    {
                        if (!quiet)
                        {
                            EditorUtility.DisplayDialog(
                                "Port In Use",
                                $"Cannot start the local HTTP server because port {uri.Port} is already in use by PID(s): " +
                                $"{string.Join(", ", remaining)}\n\n" +
                                $"{ProductInfo.ProductName} will not terminate unrelated processes. Stop the owning process manually or change the HTTP URL.",
                                "OK");
                        }
                        return false;
                    }
                }
            }
            catch { }

            // Note: Dev mode cache-busting is handled by `uvx --no-cache --refresh` in the generated command.

            // Create a per-launch token + pidfile path so Stop can be deterministic without relying on port/PID heuristics.
            string baseUrlForPid = HttpEndpointUtility.GetLocalBaseUrl();
            Uri.TryCreate(baseUrlForPid, UriKind.Absolute, out var uriForPid);
            int portForPid = uriForPid?.Port ?? 0;
            string instanceToken = Guid.NewGuid().ToString("N");
            string pidFilePath = portForPid > 0 ? GetLocalHttpServerPidFilePath(portForPid) : null;

            string launchCommand = displayCommand;
            if (!string.IsNullOrEmpty(pidFilePath))
            {
                launchCommand = $"{displayCommand} --pidfile {QuoteIfNeeded(pidFilePath)} --unity-instance-token {instanceToken}";
            }

            // First-time-only confirmation. Subsequent launches (and the quiet auto-start path) skip the dialog.
            if (!quiet && !EditorPrefs.GetBool(EditorPrefKeys.HttpServerLaunchConfirmed, false))
            {
                if (!EditorUtility.DisplayDialog(
                    "Start Local HTTP Server",
                    "Start the local MCP server in the background?\n\n" +
                    "It launches headless (no terminal window) and logs progress to the Unity Console. " +
                    "This confirmation is shown only once.",
                    "Start",
                    "Cancel"))
                {
                    return false;
                }
                try { EditorPrefs.SetBool(EditorPrefKeys.HttpServerLaunchConfirmed, true); } catch { }
            }

            string launchLog = portForPid > 0 ? GetLocalHttpServerLaunchLogPath(portForPid) : null;

            try
            {
                // Clear any stale handshake state from prior launches.
                ClearLocalServerPidTracking();
                _lastLaunchedProcess = null;

                // Best-effort: delete stale pidfile if it exists.
                try
                {
                    if (!string.IsNullOrEmpty(pidFilePath) && File.Exists(pidFilePath))
                    {
                        DeletePidFile(pidFilePath);
                    }
                }
                catch { }

                // Truncate the launch log so the tail always reflects the current launch.
                if (!string.IsNullOrEmpty(launchLog))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(launchLog));
                        File.WriteAllText(launchLog, string.Empty);
                    }
                    catch { }
                }

                McpLog.Info("Starting local HTTP server… (first run may take a minute while dependencies install)");

                // Launch the server headless (no terminal window); stdout+stderr go to the launch log.
                string effectiveLog = launchLog ?? Path.Combine(Path.GetTempPath(), "mcp-for-unity-server-launch.log");
                var startInfo = CreateHeadlessProcessStartInfo(launchCommand, effectiveLog);
                EditorProcessIdentity.AddTo(startInfo);

                // The headless shell is not a login shell, so it does not inherit the user's
                // profile PATH (on macOS, GUI-launched Unity has a minimal PATH). Prepend the
                // platform uv/uvx locations so a bare `uvx`/`uv` resolves the same way the old
                // terminal (login shell) launch did.
                string extraPathPrepend = GetPlatformSpecificPathPrepend();
                if (!string.IsNullOrEmpty(extraPathPrepend))
                {
                    string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    startInfo.EnvironmentVariables["PATH"] = string.IsNullOrEmpty(currentPath)
                        ? extraPathPrepend
                        : (extraPathPrepend + Path.PathSeparator + currentPath);
                }

                _lastLaunchedProcess = System.Diagnostics.Process.Start(startInfo);
                if (!string.IsNullOrEmpty(pidFilePath))
                {
                    StoreLocalHttpServerHandshake(pidFilePath, instanceToken);
                }
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Error($"Failed to start server: {ex.Message}");
                if (!quiet)
                {
                    EditorUtility.DisplayDialog(
                        "Error",
                        $"Failed to start server: {ex.Message}",
                        "OK");
                }
                return false;
            }
        }

        private System.Diagnostics.ProcessStartInfo CreateTerminalProcessStartInfo(string command)
        {
            return _terminalLauncher.CreateTerminalProcessStartInfo(command);
        }

        private System.Diagnostics.ProcessStartInfo CreateHeadlessProcessStartInfo(string command, string logFilePath)
        {
            return _terminalLauncher.CreateHeadlessProcessStartInfo(command, logFilePath);
        }

        public string GetLocalHttpServerLaunchLogPath()
        {
            string baseUrl = HttpEndpointUtility.GetLocalBaseUrl();
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return GetLocalHttpServerLaunchLogPath(uri.Port);
            }
            return null;
        }

        private string GetLocalHttpServerLaunchLogPath(int port)
        {
            string dir = Path.Combine(_terminalLauncher.GetProjectRootPath(), "Library", "MCPForUnity", "Logs");
            return Path.Combine(dir, $"server-launch-{port}.log");
        }

        public bool HasManagedServerLaunchHandle => _lastLaunchedProcess != null;

        public bool IsManagedServerLaunchProcessAlive()
        {
            try
            {
                var proc = _lastLaunchedProcess;
                return proc != null && !proc.HasExited;
            }
            catch
            {
                // If we cannot query the process (e.g. it was disposed), assume it is no longer alive
                // so callers stop waiting on a dead handle.
                return false;
            }
        }

        public void LogLocalHttpServerLaunchFailure()
        {
            string logPath = GetLocalHttpServerLaunchLogPath();
            string tail = TailFile(logPath, 40);

            string copyHint;
            if (TryGetLocalHttpServerCommand(out var command, out _) && !string.IsNullOrEmpty(command))
            {
                copyHint = $"To run it yourself, copy this command into a terminal:\n{command}";
            }
            else
            {
                copyHint = "Use the \"Manual Server Launch\" foldout to copy the command and run it yourself.";
            }

            string logRef = string.IsNullOrEmpty(logPath) ? "(launch log unavailable)" : logPath;
            string body = string.IsNullOrEmpty(tail) ? "(no output captured)" : tail;

            McpLog.Error(
                "Local HTTP server did not become reachable. " +
                $"Launch log: {logRef}\n{body}\n{copyHint}");
        }

        private static string TailFile(string path, int maxLines)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return string.Empty;
                }

                var lines = File.ReadAllLines(path);
                if (lines.Length <= maxLines)
                {
                    return string.Join(Environment.NewLine, lines).Trim();
                }

                return string.Join(Environment.NewLine, lines.Skip(lines.Length - maxLines)).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
