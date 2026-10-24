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
    /// Restores <see cref="EditorSettings.enterPlayModeOptionsEnabled"/> and
    /// <see cref="EditorSettings.enterPlayModeOptions"/> if a previous test run was interrupted
    /// (e.g. by domain reload or editor crash) before <see cref="TestRunnerService"/> could restore them.
    /// Two persistence layers:
    /// <list type="bullet">
    /// <item><see cref="SessionState"/> — survives domain reloads within the same editor session.</item>
    /// <item>A marker file in <c>Library/</c> — survives editor crashes and force quits.</item>
    /// </list>
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayModeOptionsGuard
    {
        private const string KeyPending = "MCPForUnity.PlayModeOptions.PendingRestore";
        private const string KeyEnabled = "MCPForUnity.PlayModeOptions.OriginalEnabled";
        private const string KeyOptions = "MCPForUnity.PlayModeOptions.OriginalOptions";

        // Library/ is project-local and gitignored by default.
        private static readonly string MarkerPath = Path.Combine("Library", "MCPPlayModeOptionsBackup.txt");

        static PlayModeOptionsGuard()
        {
            RestoreIfIdle();
        }

        internal static void RestoreIfIdle()
        {
            // After domain reload or editor restart: if a restore is pending and no test run
            // is active, restore now. TryLoad checks SessionState first, then the marker file.
            if (TryLoad(out _, out _) && !TestRunStatus.IsRunning && !TestJobManager.HasRunningJob)
            {
                Restore();
            }
        }

        internal static bool IsPending => TryLoad(out _, out _);

        internal static void Save(bool originalEnabled, EnterPlayModeOptions originalOptions)
        {
            // SessionState (domain reload)
            SessionState.SetBool(KeyEnabled, originalEnabled);
            SessionState.SetInt(KeyOptions, (int)originalOptions);
            SessionState.SetBool(KeyPending, true);

            // Marker file (crash recovery). Two lines: enabled flag, then options int.
            try
            {
                File.WriteAllText(MarkerPath, $"{(originalEnabled ? 1 : 0)}\n{(int)originalOptions}");
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[PlayModeOptionsGuard] Failed to write marker file: {ex.Message}");
            }
        }

        internal static void Restore()
        {
            if (!TryLoad(out bool origEnabled, out EnterPlayModeOptions origOptions))
            {
                return;
            }

            EditorSettings.enterPlayModeOptions = origOptions;
            EditorSettings.enterPlayModeOptionsEnabled = origEnabled;
            Clear();
            McpLog.Info("[PlayModeOptionsGuard] Restored enterPlayModeOptions after interrupted test run.");
        }

        internal static void Clear()
        {
            SessionState.SetBool(KeyPending, false);
            try
            {
                if (File.Exists(MarkerPath))
                {
                    File.Delete(MarkerPath);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        /// <summary>
        /// Checks SessionState first (available after domain reload), then falls back to the
        /// marker file (available after editor crash/restart).
        /// </summary>
        private static bool TryLoad(out bool originalEnabled, out EnterPlayModeOptions originalOptions)
        {
            // Fast path: SessionState is available after domain reload.
            if (SessionState.GetBool(KeyPending, false))
            {
                originalEnabled = SessionState.GetBool(KeyEnabled, false);
                originalOptions = (EnterPlayModeOptions)SessionState.GetInt(KeyOptions, 0);
                return true;
            }

            // Slow path: marker file survives editor crash/restart.
            originalEnabled = false;
            originalOptions = EnterPlayModeOptions.None;
            try
            {
                if (!File.Exists(MarkerPath))
                {
                    return false;
                }

                string[] lines = File.ReadAllLines(MarkerPath);
                if (lines.Length < 2)
                {
                    return false;
                }

                if (!int.TryParse(lines[0].Trim(), out int enabledInt) ||
                    !int.TryParse(lines[1].Trim(), out int optionsInt))
                {
                    return false;
                }

                originalEnabled = enabledInt != 0;
                originalOptions = (EnterPlayModeOptions)optionsInt;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

}
