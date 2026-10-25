using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Server
{
    /// <summary>
    /// Manages PID files and handshake state for the local HTTP server.
    /// Keeps launch ownership across domain reloads, without sharing it with other Editor processes.
    /// </summary>
    public class PidFileManager : IPidFileManager
    {
        private readonly string _projectRoot;
        private readonly int? _port;

        public PidFileManager() { }

        internal PidFileManager(string projectRoot, int port)
        {
            _projectRoot = Path.GetFullPath(projectRoot);
            _port = port;
        }

        private int CurrentPort => _port ?? (Uri.TryCreate(HttpEndpointUtility.GetLocalBaseUrl(), UriKind.Absolute, out var uri)
            ? uri.Port : 0);

        private string ScopedKey(string key) => $"{key}.{ComputeShortHash(GetProjectRootPath())}.{CurrentPort}";

        private bool IsCurrentPidFile(string path) =>
            CurrentPort > 0 && TryGetPortFromPidFilePath(path, out var port) && port == CurrentPort
            && string.Equals(Path.GetFullPath(path),
                Path.Combine(GetPidDirectory(), $"mcp_http_{port}.pid"),
                Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        /// <inheritdoc/>
        public string GetPidDirectory()
        {
            return Path.Combine(GetProjectRootPath(), "Library", "MCPForUnity", "RunState");
        }

        /// <inheritdoc/>
        public string GetPidFilePath(int port)
        {
            string dir = GetPidDirectory();
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"mcp_http_{port}.pid");
        }

        /// <inheritdoc/>
        public bool TryReadPid(string pidFilePath, out int pid)
        {
            pid = 0;
            try
            {
                if (string.IsNullOrEmpty(pidFilePath) || !File.Exists(pidFilePath))
                {
                    return false;
                }

                string text = File.ReadAllText(pidFilePath).Trim();
                if (int.TryParse(text, out pid))
                {
                    return pid > 0;
                }

                // Best-effort: tolerate accidental extra whitespace/newlines.
                var firstLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (int.TryParse(firstLine, out pid))
                {
                    return pid > 0;
                }

                pid = 0;
                return false;
            }
            catch
            {
                pid = 0;
                return false;
            }
        }

        /// <inheritdoc/>
        public bool TryGetPortFromPidFilePath(string pidFilePath, out int port)
        {
            port = 0;
            if (string.IsNullOrEmpty(pidFilePath))
            {
                return false;
            }

            try
            {
                string fileName = Path.GetFileNameWithoutExtension(pidFilePath);
                if (string.IsNullOrEmpty(fileName))
                {
                    return false;
                }

                const string prefix = "mcp_http_";
                if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string portText = fileName.Substring(prefix.Length);
                return int.TryParse(portText, out port) && port > 0 && port <= 65535;
            }
            catch
            {
                port = 0;
                return false;
            }
        }

        /// <inheritdoc/>
        public void DeletePidFile(string pidFilePath)
        {
            try
            {
                if (!string.IsNullOrEmpty(pidFilePath) && File.Exists(pidFilePath))
                {
                    File.Delete(pidFilePath);
                }
            }
            catch { }
        }

        /// <inheritdoc/>
        public void StoreHandshake(string pidFilePath, string instanceToken)
        {
            // SessionState survives reloads but is discarded when this Editor process exits.
            try
            {
                SessionState.EraseString(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidFilePath));
                SessionState.EraseString(ScopedKey(EditorPrefKeys.LastLocalHttpServerInstanceToken));
                if (!string.IsNullOrEmpty(pidFilePath) && !string.IsNullOrEmpty(instanceToken)
                    && IsCurrentPidFile(pidFilePath))
                {
                    SessionState.SetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidFilePath), pidFilePath);
                    SessionState.SetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerInstanceToken), instanceToken);
                }
            }
            catch { }
        }

        /// <inheritdoc/>
        public bool TryGetHandshake(out string pidFilePath, out string instanceToken)
        {
            pidFilePath = null;
            instanceToken = null;
            try
            {
                pidFilePath = SessionState.GetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidFilePath), string.Empty);
                instanceToken = SessionState.GetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerInstanceToken), string.Empty);
                if (string.IsNullOrEmpty(pidFilePath) || string.IsNullOrEmpty(instanceToken) || !IsCurrentPidFile(pidFilePath))
                {
                    pidFilePath = null;
                    instanceToken = null;
                    return false;
                }
                return true;
            }
            catch
            {
                pidFilePath = null;
                instanceToken = null;
                return false;
            }
        }

        /// <inheritdoc/>
        public void StoreTracking(int pid, int port, string argsHash = null)
        {
            if (pid <= 0 || port != CurrentPort || port <= 0 || port > 65535) return;
            try { SessionState.SetInt(ScopedKey(EditorPrefKeys.LastLocalHttpServerPid), pid); } catch { }
            try { SessionState.SetInt(ScopedKey(EditorPrefKeys.LastLocalHttpServerPort), port); } catch { }
            try { SessionState.SetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerStartedUtc), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); } catch { }
            try
            {
                if (!string.IsNullOrEmpty(argsHash))
                {
                    SessionState.SetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidArgsHash), argsHash);
                }
                else
                {
                    SessionState.EraseString(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidArgsHash));
                }
            }
            catch { }
        }

        /// <inheritdoc/>
        public bool TryGetStoredPid(int expectedPort, out int pid)
        {
            pid = 0;
            try
            {
                int storedPid = SessionState.GetInt(ScopedKey(EditorPrefKeys.LastLocalHttpServerPid), 0);
                int storedPort = SessionState.GetInt(ScopedKey(EditorPrefKeys.LastLocalHttpServerPort), 0);
                string storedUtc = SessionState.GetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerStartedUtc), string.Empty);

                if (storedPid <= 0 || expectedPort != CurrentPort || storedPort != expectedPort)
                {
                    return false;
                }

                // Only trust the stored PID for a short window to avoid PID reuse issues.
                // (We still verify the PID is listening on the expected port before killing.)
                if (!DateTime.TryParse(storedUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startedAt)) return false;
                var age = DateTime.UtcNow - startedAt;
                if (age < TimeSpan.Zero || age > TimeSpan.FromHours(6))
                {
                    return false;
                }

                pid = storedPid;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <inheritdoc/>
        public string GetStoredArgsHash()
        {
            try
            {
                return SessionState.GetString(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidArgsHash), string.Empty);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <inheritdoc/>
        public void ClearTracking()
        {
            try { SessionState.EraseInt(ScopedKey(EditorPrefKeys.LastLocalHttpServerPid)); } catch { }
            try { SessionState.EraseInt(ScopedKey(EditorPrefKeys.LastLocalHttpServerPort)); } catch { }
            try { SessionState.EraseString(ScopedKey(EditorPrefKeys.LastLocalHttpServerStartedUtc)); } catch { }
            try { SessionState.EraseString(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidArgsHash)); } catch { }
            try { SessionState.EraseString(ScopedKey(EditorPrefKeys.LastLocalHttpServerPidFilePath)); } catch { }
            try { SessionState.EraseString(ScopedKey(EditorPrefKeys.LastLocalHttpServerInstanceToken)); } catch { }
        }

        /// <inheritdoc/>
        public string ComputeShortHash(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            try
            {
                using var sha = SHA256.Create();
                byte[] bytes = Encoding.UTF8.GetBytes(input);
                byte[] hash = sha.ComputeHash(bytes);
                // 8 bytes => 16 hex chars is plenty as a stable fingerprint for our purposes.
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8 && i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private string GetProjectRootPath()
        {
            if (_projectRoot != null) return _projectRoot;
            try
            {
                // Application.dataPath is ".../<Project>/Assets"
                return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            }
            catch
            {
                return Application.dataPath;
            }
        }
    }
}
