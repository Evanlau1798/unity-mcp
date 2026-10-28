using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using System.Threading;
using System.Security.Cryptography;

#if USE_ROSLYN
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
#endif

#if UNITY_EDITOR
using UnityEditor.Compilation;
#endif


namespace MCPForUnity.Editor.Tools
{
    // Debounced refresh/compile scheduler to coalesce bursts of edits
    static class RefreshDebounce
    {
        private static int _pending;
        private static readonly object _lock = new object();
        private static readonly HashSet<string> _paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The timestamp of the most recent schedule request.
        private static DateTime _lastRequest;

        // Guard to ensure we only have a single ticking callback running.
        private static bool _scheduled;

        public static void Schedule(string relPath, TimeSpan window)
        {
            // Record that work is pending and track the path in a threadsafe way.
            Interlocked.Exchange(ref _pending, 1);
            lock (_lock)
            {
                _paths.Add(relPath);
                _lastRequest = DateTime.UtcNow;

                // If a debounce timer is already scheduled it will pick up the new request.
                if (_scheduled)
                    return;

                _scheduled = true;
            }

            // Kick off a ticking callback that waits until the window has elapsed
            // from the last request before performing the refresh.
            EditorApplication.delayCall += () => Tick(window);
            // Nudge the editor loop so ticks run even if the window is unfocused
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private static void Tick(TimeSpan window)
        {
            bool ready;
            lock (_lock)
            {
                // Only proceed once the debounce window has fully elapsed.
                ready = (DateTime.UtcNow - _lastRequest) >= window;
                if (ready)
                {
                    _scheduled = false;
                }
            }

            if (!ready)
            {
                // Window has not yet elapsed; check again on the next editor tick.
                EditorApplication.delayCall += () => Tick(window);
                return;
            }

            if (Interlocked.Exchange(ref _pending, 0) == 1)
            {
                string[] toImport;
                lock (_lock) { toImport = _paths.ToArray(); _paths.Clear(); }
                foreach (var p in toImport)
                {
                    var sp = ManageScriptRefreshHelpers.SanitizeAssetsPath(p);
                    AssetDatabase.ImportAsset(sp, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                }
#if UNITY_EDITOR
                UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
#endif
                // Fallback if needed:
                // AssetDatabase.Refresh();
            }
        }
    }

    static class ManageScriptRefreshHelpers
    {
        public static string SanitizeAssetsPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            p = AssetPathUtility.NormalizeSeparators(p).Trim();
            if (p.StartsWith("mcpforunity://path/", StringComparison.OrdinalIgnoreCase))
                p = p.Substring("mcpforunity://path/".Length);
            while (p.StartsWith("Assets/Assets/", StringComparison.OrdinalIgnoreCase))
                p = p.Substring("Assets/".Length);
            if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                p = "Assets/" + p.TrimStart('/');
            return p;
        }

        public static void ScheduleScriptRefresh(string relPath)
        {
            var sp = SanitizeAssetsPath(relPath);
            RefreshDebounce.Schedule(sp, TimeSpan.FromMilliseconds(200));
        }

        public static void ImportAndRequestCompile(string relPath, bool synchronous = true)
        {
            var sp = SanitizeAssetsPath(relPath);
            var opts = ImportAssetOptions.ForceUpdate;
            if (synchronous) opts |= ImportAssetOptions.ForceSynchronousImport;
            AssetDatabase.ImportAsset(sp, opts);
#if UNITY_EDITOR
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
#endif
        }
    }
}
