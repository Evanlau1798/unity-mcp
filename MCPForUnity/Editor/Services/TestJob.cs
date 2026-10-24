using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditorInternal;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnity.Editor.Services
{
    internal enum TestJobStatus
    {
        Running,
        Succeeded,
        Failed
    }

    internal sealed class TestJobFailure
    {
        public string FullName { get; set; }
        public string Message { get; set; }
    }

    internal sealed class TestJob
    {
        public string JobId { get; set; }
        public TestJobStatus Status { get; set; }
        public string Mode { get; set; }
        public long StartedUnixMs { get; set; }
        public long? FinishedUnixMs { get; set; }
        public long LastUpdateUnixMs { get; set; }
        public int? TotalTests { get; set; }
        public int CompletedTests { get; set; }
        public string CurrentTestFullName { get; set; }
        public long? CurrentTestStartedUnixMs { get; set; }
        public string LastFinishedTestFullName { get; set; }
        public long? LastFinishedUnixMs { get; set; }
        public List<TestJobFailure> FailuresSoFar { get; set; }
        public string Error { get; set; }
        public TestRunResult Result { get; set; }
        public long InitTimeoutMs { get; set; }
        public string UnityRunId { get; set; }
        public bool InitializationTimedOut { get; set; }
        public bool NativeRunStarted { get; set; }
        public bool NativeCleanupPending { get; set; }
    }

}
