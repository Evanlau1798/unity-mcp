using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;

internal static class DispatcherContracts
{
    private sealed class MainContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<Action> queue = new();
        public int Count => queue.Count;
        public override void Post(SendOrPostCallback callback, object state) => queue.Enqueue(() => callback(state));
        public void Drain() { while (queue.TryDequeue(out var next)) next(); }
    }

    private static MainContext context = new();
    private static int passed, failed;

    static int Main()
    {
        EditorApplication.MainThread = Thread.CurrentThread.ManagedThreadId;
        SynchronizationContext.SetSynchronizationContext(context);
        RuntimeHelpers.RunClassConstructor(typeof(TransportCommandDispatcher).TypeHandle);
        Check("background main-thread invocation waits without Unity API calls", () =>
        {
            var task = Task.Run(() => TransportCommandDispatcher.RunOnMainThreadAsync(
                () => Thread.CurrentThread.ManagedThreadId, CancellationToken.None));
            SpinWait.SpinUntil(() => context.Count > 0, 1000);
            Require(!task.IsCompleted, "background callback ran before main pump");
            Require(EditorApplication.BackgroundApiCalls == 0, "Unity API called on background thread");
            context.Drain();
            Require(task.GetAwaiter().GetResult() == EditorApplication.MainThread, "wrong dispatch thread");
        });
        Check("missing main context refuses background execution", () =>
        {
            var field = typeof(TransportCommandDispatcher).GetField("_mainThreadContext", BindingFlags.Static | BindingFlags.NonPublic);
            field.SetValue(null, null);
            try
            {
                var invoked = 0;
                var task = Task.Run(() => TransportCommandDispatcher.RunOnMainThreadAsync(
                    () => Interlocked.Increment(ref invoked), CancellationToken.None));
                Require(SpinWait.SpinUntil(() => task.IsCompleted, 1000), "missing context hung");
                Require(task.IsFaulted && invoked == 0, "background work ran without main context");
                _ = task.Exception;
            }
            finally { field.SetValue(null, context); }
        });
        Check("already canceled command settles without waiting for editor update", () =>
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var task = Task.Run(() => TransportCommandDispatcher.ExecuteCommandJsonAsync("{\"type\":\"mutation\"}", cts.Token));
            Require(SpinWait.SpinUntil(() => task.IsCompleted, 200), "already canceled command stuck in queue");
            if (task.IsFaulted) _ = task.Exception;
            Require(CommandRegistry.Executions == 0, "canceled mutation executed");
        });
        Check("queued deadline reports queue phase and cannot execute later", () =>
        {
            using var cts = new CancellationTokenSource();
            var task = Task.Run(() => TransportCommandDispatcher.ExecuteCommandJsonAsync("{\"type\":\"mutation\"}", cts.Token));
            SpinWait.SpinUntil(() => context.Count > 0, 1000);
            cts.Cancel();
            Require(SpinWait.SpinUntil(() => task.IsCompleted, 1000), "queue deadline did not settle");
            var response = JObject.Parse(task.GetAwaiter().GetResult());
            Require((string)response["result"]?["data"]?["phase"] == "editor_queue", "missing queue phase");
            Require((string)response["result"]?["hint"] == "retry", "missing safe queue retry hint");
            Require((bool?)response["result"]?["data"]?["execution_may_have_started"] == false, "queue outcome not safe");
            context.Drain();
            Require(CommandRegistry.Executions == 0, "expired queued mutation replayed");
        });
        Check("executing deadline reports possible execution", () =>
        {
            using var cts = new CancellationTokenSource();
            CommandRegistry.OnExecute = () => cts.Cancel();
            try
            {
                var response = JObject.Parse(TransportCommandDispatcher.ExecuteCommandJsonAsync(
                    "{\"type\":\"mutation\"}", cts.Token).GetAwaiter().GetResult());
                Require((string)response["result"]?["data"]?["phase"] == "editor_execution", "missing execution phase");
                Require((string)response["result"]?["hint"] == "check_state_before_retry", "missing uncertain execution hint");
                Require((bool?)response["result"]?["data"]?["execution_may_have_started"] == true, "execution certainty overstated");
            }
            finally { CommandRegistry.OnExecute = null; }
        });
        Check("concurrent canceled queue cannot replay mutations", () =>
        {
            var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource();
                var reply = TransportCommandDispatcher.ExecuteCommandJsonAsync("{\"type\":\"mutation\"}", cts.Token);
                cts.Cancel();
                return JObject.Parse(await reply);
            })).ToArray();
            Require(Task.WaitAll(tasks, 3000), "canceled queue did not settle without Editor update");
            Require(tasks.All(t => (string)t.Result["result"]?["data"]?["phase"] == "editor_queue"), "lost queue diagnostics");
            context.Drain();
            Require(CommandRegistry.Executions == 0, "canceled concurrent mutation executed");
            Require(EditorApplication.BackgroundApiCalls == 0, "background pump touched Unity API");
        });
        Check("async completion avoids background Editor event subscription", () =>
        {
            var task = TransportCommandDispatcher.ExecuteCommandJsonAsync("{\"type\":\"async\"}", CancellationToken.None);
            Task.Run(() => CommandRegistry.AsyncCompletion.TrySetResult("{\"status\":\"success\"}")).Wait();
            task.GetAwaiter().GetResult();
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            object pendingLock = typeof(TransportCommandDispatcher).GetField("PendingLock", flags).GetValue(null);
            var pending = (IDictionary)typeof(TransportCommandDispatcher).GetField("Pending", flags).GetValue(null);
            Require(SpinWait.SpinUntil(() => { lock (pendingLock) return pending.Count == 0; }, 1000), "async cleanup never finished");
            Require(EditorApplication.BackgroundApiCalls == 0, "completion accessed Editor events from worker");
        });
        Console.WriteLine($"{passed} passed, {failed} failed (actual dispatcher; simulated Editor thread/update)");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(string name, Action test)
    {
        EditorApplication.BackgroundApiCalls = 0;
        CommandRegistry.Executions = 0;
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        finally { context.Drain(); EditorApplication.Update(); }
    }
    private static void Require(bool condition, string error) { if (!condition) throw new Exception(error); }
}
