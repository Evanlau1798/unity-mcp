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
    internal sealed partial class TestRunnerService
    {
        #region Test list helpers

        private async Task<ITestAdaptor> RetrieveTestRootAsync(TestMode mode)
        {
            var tcs = new TaskCompletionSource<ITestAdaptor>(TaskCreationOptions.RunContinuationsAsynchronously);

            _testRunnerApi.RetrieveTestList(mode, root =>
            {
                tcs.TrySetResult(root);
            });

            // Ensure the editor pumps at least one additional update in case the window is unfocused.
            EditorApplication.QueuePlayerLoopUpdate();

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(true);
            if (completed != tcs.Task)
            {
                McpLog.Warn($"[TestRunnerService] Timeout waiting for test retrieval callback for {mode}");
                return null;
            }

            try
            {
                return await tcs.Task.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                McpLog.Error($"[TestRunnerService] Error retrieving tests for {mode}: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        private static void CollectFromNode(
            ITestAdaptor node,
            TestMode mode,
            List<Dictionary<string, string>> output,
            HashSet<string> seen,
            List<string> path)
        {
            if (node == null)
            {
                return;
            }

            bool hasName = !string.IsNullOrEmpty(node.Name);
            if (hasName)
            {
                path.Add(node.Name);
            }

            bool hasChildren = node.HasChildren && node.Children != null;

            if (!hasChildren)
            {
                string fullName = string.IsNullOrEmpty(node.FullName) ? node.Name ?? string.Empty : node.FullName;
                string key = $"{mode}:{fullName}";

                if (!string.IsNullOrEmpty(fullName) && seen.Add(key))
                {
                    string computedPath = path.Count > 0 ? string.Join("/", path) : fullName;
                    output.Add(new Dictionary<string, string>
                    {
                        ["name"] = node.Name ?? fullName,
                        ["full_name"] = fullName,
                        ["path"] = computedPath,
                        ["mode"] = mode.ToString(),
                    });
                }
            }
            else if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    CollectFromNode(child, mode, output, seen, path);
                }
            }

            if (hasName && path.Count > 0)
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        #endregion
    }
}
