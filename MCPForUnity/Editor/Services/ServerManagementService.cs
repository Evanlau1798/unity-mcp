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
    /// <summary>
    /// Service for managing MCP server lifecycle
    /// </summary>
    public partial class ServerManagementService : IServerManagementService
    {
        private readonly IProcessDetector _processDetector;
        private readonly IPidFileManager _pidFileManager;
        private readonly IServerCommandBuilder _commandBuilder;
        private readonly ITerminalLauncher _terminalLauncher;

        private System.Diagnostics.Process _lastLaunchedProcess;

        /// <summary>
        /// Creates a new ServerManagementService with default dependencies.
        /// </summary>
        public ServerManagementService() : this(null, null, null, null, null) { }

        /// <summary>
        /// Creates a new ServerManagementService with injected dependencies (for testing).
        /// </summary>
        /// <param name="processDetector">Process detector implementation (null for default)</param>
        /// <param name="pidFileManager">PID file manager implementation (null for default)</param>
        /// <param name="processTerminator">Process terminator implementation (null for default)</param>
        /// <param name="commandBuilder">Server command builder implementation (null for default)</param>
        /// <param name="terminalLauncher">Terminal launcher implementation (null for default)</param>
        public ServerManagementService(
            IProcessDetector processDetector,
            IPidFileManager pidFileManager = null,
            IProcessTerminator processTerminator = null,
            IServerCommandBuilder commandBuilder = null,
            ITerminalLauncher terminalLauncher = null)
        {
            _processDetector = processDetector ?? new ProcessDetector();
            _pidFileManager = pidFileManager ?? new PidFileManager();
            _commandBuilder = commandBuilder ?? new ServerCommandBuilder();
            _terminalLauncher = terminalLauncher ?? new TerminalLauncher();
        }

        private string QuoteIfNeeded(string s)
        {
            return _commandBuilder.QuoteIfNeeded(s);
        }

        private string NormalizeForMatch(string s)
        {
            return _processDetector.NormalizeForMatch(s);
        }

        private void ClearLocalServerPidTracking()
        {
            _pidFileManager.ClearTracking();
        }

        private void StoreLocalHttpServerHandshake(string pidFilePath, string instanceToken)
        {
            _pidFileManager.StoreHandshake(pidFilePath, instanceToken);
        }

        private bool TryGetLocalHttpServerHandshake(out string pidFilePath, out string instanceToken)
        {
            return _pidFileManager.TryGetHandshake(out pidFilePath, out instanceToken);
        }

        private string GetLocalHttpServerPidFilePath(int port)
        {
            return _pidFileManager.GetPidFilePath(port);
        }

        private bool TryReadPidFromPidFile(string pidFilePath, out int pid)
        {
            return _pidFileManager.TryReadPid(pidFilePath, out pid);
        }

        private bool TryGetStoredLocalServerPid(int expectedPort, out int pid)
        {
            return _pidFileManager.TryGetStoredPid(expectedPort, out pid);
        }

        /// <summary>
        /// Clear the local uvx cache for the MCP server package
        /// </summary>
        /// <returns>True if successful, false otherwise</returns>
        public bool ClearUvxCache()
        {
            try
            {
                string uvxPath = MCPServiceLocator.Paths.GetUvxPath();
                string uvCommand = BuildUvPathFromUvx(uvxPath);

                // Get the package name
                string packageName = "mcp-for-unity";

                // Run uvx cache clean command
                string args = $"cache clean {packageName}";

                bool success;
                string stdout;
                string stderr;

                success = ExecuteUvCommand(uvCommand, args, out stdout, out stderr);

                if (success)
                {
                    McpLog.Info($"uv cache cleared successfully: {stdout}");
                    return true;
                }
                string combinedOutput = string.Join(
                    Environment.NewLine,
                    new[] { stderr, stdout }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

                string lockHint = (!string.IsNullOrEmpty(combinedOutput) &&
                                   combinedOutput.IndexOf("currently in-use", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? "Another uv process may be holding the cache lock; wait a moment and try again or clear with '--force' from a terminal."
                    : string.Empty;

                if (string.IsNullOrEmpty(combinedOutput))
                {
                    combinedOutput = "Command failed with no output. Ensure uv is installed, on PATH, or set an override in Advanced Settings.";
                }

                McpLog.Error(
                    $"Failed to clear uv cache using '{uvCommand} {args}'. " +
                    $"Details: {combinedOutput}{(string.IsNullOrEmpty(lockHint) ? string.Empty : " Hint: " + lockHint)}");
                return false;
            }
            catch (Exception ex)
            {
                McpLog.Error($"Error clearing uv cache: {ex.Message}");
                return false;
            }
        }

        private bool ExecuteUvCommand(string uvCommand, string args, out string stdout, out string stderr)
        {
            stdout = null;
            stderr = null;

            string uvxPath = MCPServiceLocator.Paths.GetUvxPath();
            string uvPath = BuildUvPathFromUvx(uvxPath);

            if (!string.Equals(uvCommand, uvPath, StringComparison.OrdinalIgnoreCase))
            {
                return ExecPath.TryRun(uvCommand, args, Application.dataPath, out stdout, out stderr, 30000);
            }

            string command = $"{uvPath} {args}";
            string extraPathPrepend = GetPlatformSpecificPathPrepend();

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                return ExecPath.TryRun("cmd.exe", $"/c {command}", Application.dataPath, out stdout, out stderr, 30000, extraPathPrepend);
            }

            string shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";

            if (!string.IsNullOrEmpty(shell) && File.Exists(shell))
            {
                string escaped = command.Replace("\"", "\\\"");
                return ExecPath.TryRun(shell, $"-lc \"{escaped}\"", Application.dataPath, out stdout, out stderr, 30000, extraPathPrepend);
            }

            return ExecPath.TryRun(uvPath, args, Application.dataPath, out stdout, out stderr, 30000, extraPathPrepend);
        }

        private string BuildUvPathFromUvx(string uvxPath)
        {
            return _commandBuilder.BuildUvPathFromUvx(uvxPath);
        }

        private string GetPlatformSpecificPathPrepend()
        {
            return _commandBuilder.GetPlatformSpecificPathPrepend();
        }

        /// <summary>
        /// Request shutdown of this Editor's verified managed local HTTP server.
        /// </summary>
        public bool StopLocalHttpServer()
        {
            return StopLocalHttpServerInternal(quiet: false);
        }

        public bool StopManagedLocalHttpServer() => StopLocalHttpServerInternal(quiet: true);

        public bool IsLocalHttpServerRunning()
        {
            try
            {
                string httpUrl = HttpEndpointUtility.GetLocalBaseUrl();
                if (!IsLocalUrl(httpUrl))
                {
                    return false;
                }

                if (!Uri.TryCreate(httpUrl, UriKind.Absolute, out var uri) || uri.Port <= 0)
                {
                    return false;
                }

                int port = uri.Port;

                // Handshake path: if we have a pidfile+token and the PID is still the listener, treat as running.
                if (TryGetLocalHttpServerHandshake(out var pidFilePath, out var instanceToken)
                    && TryReadPidFromPidFile(pidFilePath, out var pidFromFile)
                    && pidFromFile > 0)
                {
                    var pidsNow = GetListeningProcessIdsForPort(port);
                    if (pidsNow.Contains(pidFromFile))
                    {
                        return true;
                    }
                }

                var pids = GetListeningProcessIdsForPort(port);
                if (pids.Count == 0)
                {
                    return false;
                }

                // Strong signal: stored PID is still the listener.
                if (TryGetStoredLocalServerPid(port, out int storedPid) && storedPid > 0)
                {
                    if (pids.Contains(storedPid))
                    {
                        return true;
                    }
                }

                // Best-effort: if anything listening looks like our server, treat as running.
                foreach (var pid in pids)
                {
                    if (pid <= 0) continue;
                    if (LooksLikeMcpServerProcess(pid))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public bool IsLocalHttpServerReachable()
        {
            try
            {
                string httpUrl = HttpEndpointUtility.GetLocalBaseUrl();
                if (!IsLocalUrl(httpUrl))
                {
                    return false;
                }

                if (!Uri.TryCreate(httpUrl, UriKind.Absolute, out var uri) || uri.Port <= 0)
                {
                    return false;
                }

                // 250ms, not 50ms: on a machine busy with test runs or domain reloads a 50ms
                // connect wait produces false "server gone" readings that tore down healthy
                // sessions via the orphaned-session detector (#1207).
                return TryConnectToLocalPort(uri.Host, uri.Port, timeoutMs: 250);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryConnectToLocalPort(string host, int port, int timeoutMs)
        {
            try
            {
                // timeoutMs is an overall budget shared across candidate hosts, so a
                // filtered/dropped first candidate cannot multiply the worst-case wait
                // (this can run on the editor UI tick).
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                foreach (string target in BuildLocalProbeHosts(host))
                {
                    int remainingMs = timeoutMs - (int)elapsed.ElapsedMilliseconds;
                    if (remainingMs <= 0)
                    {
                        break;
                    }

                    try
                    {
                        using (var client = new TcpClient())
                        {
                            var connectTask = client.ConnectAsync(target, port);
                            if (connectTask.Wait(remainingMs) && client.Connected)
                            {
                                return true;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore per-host failures.
                    }
                }
            }
            catch
            {
                // Ignore probe failures and treat as unreachable.
            }

            return false;
        }

        private static IReadOnlyList<string> BuildLocalProbeHosts(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                host = "127.0.0.1";
            }
            else
            {
                host = host.Trim();
            }

            var hosts = new List<string>();
            AddHostCandidate(hosts, host);

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                // Probe both loopback families for localhost to avoid false negatives on systems where
                // localhost resolution prefers an address family different from the server bind.
                AddHostCandidate(hosts, "127.0.0.1");
                AddHostCandidate(hosts, "::1");
            }
            else if (string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
            {
                AddHostCandidate(hosts, "127.0.0.1");
            }
            else if (string.Equals(host, "::", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(host, "0:0:0:0:0:0:0:0", StringComparison.OrdinalIgnoreCase))
            {
                AddHostCandidate(hosts, "::1");
            }

            return hosts;
        }

        private static void AddHostCandidate(List<string> hosts, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            if (hosts.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            hosts.Add(candidate);
        }

        private bool StopLocalHttpServerInternal(bool quiet)
        {
            string url = HttpEndpointUtility.GetLocalBaseUrl();
            if (!IsLocalUrl(url)
                || !TryGetLocalHttpServerHandshake(out var path, out var token)
                || !TryReadPidFromPidFile(path, out var pid)
                || !LocalHttpServerControl.TryStop(url, token, pid))
            {
                if (!quiet) McpLog.Warn("HTTP server stop refused: ownership is unknown, another Editor is alive, or control request timed out.");
                return false;
            }
            ClearLocalServerPidTracking();
            return true;
        }

        private void DeletePidFile(string pidFilePath)
        {
            _pidFileManager.DeletePidFile(pidFilePath);
        }

        private List<int> GetListeningProcessIdsForPort(int port)
        {
            return _processDetector.GetListeningProcessIdsForPort(port);
        }

        private bool LooksLikeMcpServerProcess(int pid)
        {
            return _processDetector.LooksLikeMcpServerProcess(pid);
        }

        /// <summary>
        /// Attempts to build the command used for starting the local HTTP server
        /// </summary>
        public bool TryGetLocalHttpServerCommand(out string command, out string error)
        {
            command = null;
            error = null;
            if (!TryGetLocalHttpServerCommandParts(out var fileName, out var args, out var displayCommand, out error))
            {
                return false;
            }

            // Maintain existing behavior: return a single command string suitable for display/copy.
            command = displayCommand;
            return true;
        }

        private bool TryGetLocalHttpServerCommandParts(out string fileName, out string arguments, out string displayCommand, out string error)
        {
            return _commandBuilder.TryBuildCommand(out fileName, out arguments, out displayCommand, out error);
        }

        /// <summary>
        /// Check if the configured HTTP URL is a local address
        /// </summary>
        public bool IsLocalUrl()
        {
            string httpUrl = HttpEndpointUtility.GetLocalBaseUrl();
            return IsLocalUrl(httpUrl);
        }

        /// <summary>
        /// Check if a URL is local or bind-all (localhost/loopback and 0.0.0.0/::).
        /// This helper is intentionally broader than local-launch policy checks.
        /// </summary>
        private static bool IsLocalUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;

            try
            {
                var uri = new Uri(url);
                string host = uri.Host;
                return HttpEndpointUtility.IsLoopbackHost(host) || HttpEndpointUtility.IsBindAllInterfacesHost(host);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if the local HTTP server can be started
        /// </summary>
        public bool CanStartLocalServer()
        {
            bool useHttpTransport = EditorConfigurationCache.Instance.UseHttpTransport;
            if (!useHttpTransport)
            {
                return false;
            }

            string httpUrl = HttpEndpointUtility.GetLocalBaseUrl();
            return HttpEndpointUtility.IsHttpLocalUrlAllowedForLaunch(httpUrl, out _);
        }

    }
}
