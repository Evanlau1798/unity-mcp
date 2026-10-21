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
    /// Summary of a Unity test run.
    /// </summary>
    public sealed class TestRunResult
    {
        [JsonConstructor]
        internal TestRunResult(TestRunSummary summary, IReadOnlyList<TestRunTestResult> results)
        {
            Summary = summary;
            Results = results;
        }

        public TestRunSummary Summary { get; }
        public IReadOnlyList<TestRunTestResult> Results { get; }

        public int Total => Summary.Total;
        public int Passed => Summary.Passed;
        public int Failed => Summary.Failed;
        public int Skipped => Summary.Skipped;

        public object ToSerializable(string mode, bool includeDetails = false, bool includeFailedTests = false)
        {
            // Determine which results to include
            IEnumerable<object> resultsToSerialize;
            if (includeDetails)
            {
                // Include all test results
                resultsToSerialize = Results.Select(r => r.ToSerializable());
            }
            else if (includeFailedTests)
            {
                // Include only failed and skipped tests
                resultsToSerialize = Results
                    .Where(r => !string.Equals(r.State, "Passed", StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.ToSerializable());
            }
            else
            {
                // No individual test results
                resultsToSerialize = null;
            }

            return new
            {
                mode,
                summary = Summary.ToSerializable(),
                results = resultsToSerialize?.ToList(),
            };
        }

        internal static TestRunResult Create(ITestResultAdaptor summary, IReadOnlyList<ITestResultAdaptor> tests)
        {
            // RunFinished includes the complete result tree, including tests completed
            // before a domain reload erased the service's per-test callback list.
            var resultLeaves = new List<ITestResultAdaptor>();
            if (summary != null && summary.HasChildren)
            {
                CollectResultLeaves(summary, resultLeaves);
            }
            var materializedTests = (resultLeaves.Count > 0 ? resultLeaves : tests)
                .Where(t => t != null && t.Test?.IsSuite != true)
                .Select(TestRunTestResult.FromAdaptor).ToList();

            int passed = summary?.PassCount
                ?? materializedTests.Count(t => string.Equals(t.State, "Passed", StringComparison.OrdinalIgnoreCase));
            int failed = summary?.FailCount
                ?? materializedTests.Count(t => string.Equals(t.State, "Failed", StringComparison.OrdinalIgnoreCase));
            int skipped = summary?.SkipCount
                ?? materializedTests.Count(t => string.Equals(t.State, "Skipped", StringComparison.OrdinalIgnoreCase));

            double duration = summary?.Duration
                ?? materializedTests.Sum(t => t.DurationSeconds);

            int total = summary != null ? passed + failed + skipped : materializedTests.Count;

            var summaryPayload = new TestRunSummary(
                total,
                passed,
                failed,
                skipped,
                duration,
                summary?.ResultState ?? "Unknown");

            return new TestRunResult(summaryPayload, materializedTests);
        }

        private static void CollectResultLeaves(ITestResultAdaptor result, List<ITestResultAdaptor> leaves)
        {
            if (result == null)
            {
                return;
            }
            if (!result.HasChildren)
            {
                if (result.Test?.IsSuite != true) leaves.Add(result);
                return;
            }
            if (result.Children != null)
            {
                foreach (var child in result.Children)
                {
                    CollectResultLeaves(child, leaves);
                }
            }
        }
    }

    public sealed class TestRunSummary
    {
        [JsonConstructor]
        internal TestRunSummary(int total, int passed, int failed, int skipped, double durationSeconds, string resultState)
        {
            Total = total;
            Passed = passed;
            Failed = failed;
            Skipped = skipped;
            DurationSeconds = durationSeconds;
            ResultState = resultState;
        }

        public int Total { get; }
        public int Passed { get; }
        public int Failed { get; }
        public int Skipped { get; }
        public double DurationSeconds { get; }
        public string ResultState { get; }

        internal object ToSerializable()
        {
            return new
            {
                total = Total,
                passed = Passed,
                failed = Failed,
                skipped = Skipped,
                durationSeconds = DurationSeconds,
                resultState = ResultState,
            };
        }
    }

    public sealed class TestRunTestResult
    {
        [JsonConstructor]
        internal TestRunTestResult(
            string name,
            string fullName,
            string state,
            double durationSeconds,
            string message,
            string stackTrace,
            string output)
        {
            Name = name;
            FullName = fullName;
            State = state;
            DurationSeconds = durationSeconds;
            Message = message;
            StackTrace = stackTrace;
            Output = output;
        }

        public string Name { get; }
        public string FullName { get; }
        public string State { get; }
        public double DurationSeconds { get; }
        public string Message { get; }
        public string StackTrace { get; }
        public string Output { get; }

        internal object ToSerializable()
        {
            return new
            {
                name = Name,
                fullName = FullName,
                state = State,
                durationSeconds = DurationSeconds,
                message = Message,
                stackTrace = StackTrace,
                output = Output,
            };
        }

        internal static TestRunTestResult FromAdaptor(ITestResultAdaptor adaptor)
        {
            if (adaptor == null)
            {
                return new TestRunTestResult(string.Empty, string.Empty, "Unknown", 0.0, string.Empty, string.Empty, string.Empty);
            }

            return new TestRunTestResult(
                adaptor.Name,
                adaptor.FullName,
                adaptor.ResultState,
                adaptor.Duration,
                adaptor.Message,
                adaptor.StackTrace,
                adaptor.Output);
        }
    }
}
