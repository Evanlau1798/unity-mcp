using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UnityEditor
{
    public sealed class InitializeOnLoadAttribute : Attribute { }
    public static class EditorApplication
    {
        public static int MainThread, BackgroundApiCalls;
        public static event Action update;
        public static event Action delayCall
        {
            add { CheckThread(); }
            remove { CheckThread(); }
        }
        public static void QueuePlayerLoopUpdate() => CheckThread();
        public static void Update() => update?.Invoke();
        private static void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != MainThread)
                Interlocked.Increment(ref BackgroundApiCalls);
        }
    }
}
namespace MCPForUnity.Editor.Helpers
{
    public static class McpLog
    {
        public static void Error(string text) { }
        public static void Warn(string text) { }
    }
    public sealed class ErrorResponse { public string Error { get; set; } }
    public static class McpLogRecord
    {
        public static bool IsEnabled => false;
        public static void Log(string type, JObject parameters, string logType, string status, long duration, string error)
            => UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
    }
}
namespace MCPForUnity.Editor.Services
{
    public sealed class Discovery
    {
        public object GetResourceMetadata(string type) => null;
        public bool IsResourceEnabled(string type) => true;
        public object GetToolMetadata(string type) => null;
        public bool IsToolEnabled(string type) => true;
    }
    public static class MCPServiceLocator
    {
        public static Discovery ResourceDiscovery = new Discovery(), ToolDiscovery = new Discovery();
    }
}
namespace MCPForUnity.Editor.Tools
{
    public static class CommandRegistry
    {
        public static int Executions;
        public static TaskCompletionSource<string> AsyncCompletion;
        public static Action OnExecute;
        public static void Initialize() { }
        public static bool HasHandler(string type) => true;
        public static object ExecuteCommand(string type, JObject parameters, TaskCompletionSource<string> completion)
        {
            Executions++;
            OnExecute?.Invoke();
            if (type == "async") { AsyncCompletion = completion; return null; }
            return new { message = "done" };
        }
    }
}
