using System;
using System.Collections.Generic;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Services.Server;
using UnityEditor;
using UnityEngine;

// Run the production class against in-memory Unity persistence, without an Editor or registry access.
internal static class PidFileManagerContracts
{
    private static int Main()
    {
        var tests = new Action[] {
            DifferentProjectsCannotReadOrClearOwnership,
            DifferentPortsCannotReadOrClearOwnership,
            DomainReloadPreservesOwnership,
            NewEditorDoesNotInheritOwnership,
            PartialHandshakeCannotCombineLaunches,
            HandshakeMustMatchConfiguredPort,
            InvalidPortsAreRejected,
            TrackingRequiresValidTimestamp,
            TrackingPreservesFingerprintAndRejectsExpiry,
        };
        int failures = 0;
        foreach (var test in tests)
        {
            EditorPrefs.Values.Clear();
            SessionState.Values.Clear();
            Application.dataPath = "C:/contracts/ProjectA/Assets";
            MCPForUnity.Editor.Helpers.HttpEndpointUtility.Url = "http://127.0.0.1:8080";
            try { test(); Console.WriteLine($"PASS {test.Method.Name}"); }
            catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Method.Name}: {ex.Message}"); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} ownership contracts passed");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Store(PidFileManager manager, int port = 8080)
    {
        manager.StoreHandshake($"C:/contracts/ProjectA/Library/MCPForUnity/RunState/mcp_http_{port}.pid", "token-a");
        manager.StoreTracking(12345, port, "fingerprint-a");
    }

    private static void DifferentProjectsCannotReadOrClearOwnership()
    {
        var manager = new PidFileManager();
        Store(manager);
        Application.dataPath = "C:/contracts/ProjectB/Assets";
        Check(!manager.TryGetHandshake(out _, out _), "Project B inherited Project A handshake");
        Check(!manager.TryGetStoredPid(8080, out _), "Project B inherited Project A PID");
        manager.ClearTracking();
        Application.dataPath = "C:/contracts/ProjectA/Assets";
        Check(manager.TryGetHandshake(out _, out _), "Project B cleared Project A handshake");
        Check(manager.TryGetStoredPid(8080, out _), "Project B cleared Project A PID");
    }

    private static void DifferentPortsCannotReadOrClearOwnership()
    {
        var manager = new PidFileManager();
        Store(manager);
        MCPForUnity.Editor.Helpers.HttpEndpointUtility.Url = "http://127.0.0.1:9090";
        Check(!manager.TryGetHandshake(out _, out _), "Port 9090 inherited port 8080 handshake");
        manager.StoreHandshake("C:/contracts/ProjectA/Library/MCPForUnity/RunState/mcp_http_9090.pid", "token-b");
        manager.StoreTracking(54321, 9090, "fingerprint-b");
        Check(manager.GetStoredArgsHash() == "fingerprint-b", "Wrong port fingerprint");
        manager.ClearTracking();
        MCPForUnity.Editor.Helpers.HttpEndpointUtility.Url = "http://127.0.0.1:8080";
        Check(manager.TryGetHandshake(out _, out var token) && token == "token-a", "Cleared another port");
        Check(manager.TryGetStoredPid(8080, out var pid) && pid == 12345, "Lost another port PID");
        Check(manager.GetStoredArgsHash() == "fingerprint-a", "Lost another port fingerprint");
    }

    private static void DomainReloadPreservesOwnership()
    {
        Store(new PidFileManager());
        var reloaded = new PidFileManager();
        Check(reloaded.TryGetHandshake(out _, out var token) && token == "token-a", "Lost handshake after reload");
        Check(reloaded.TryGetStoredPid(8080, out var pid) && pid == 12345, "Lost PID after reload");
    }

    private static void NewEditorDoesNotInheritOwnership()
    {
        Store(new PidFileManager());
        SessionState.Values.Clear(); // Unity clears SessionState on process exit, but keeps EditorPrefs.
        var nextEditor = new PidFileManager();
        Check(!nextEditor.TryGetHandshake(out _, out _), "New process inherited old handshake");
        Check(!nextEditor.TryGetStoredPid(8080, out _), "New process inherited old PID");
        EditorPrefs.SetString(EditorPrefKeys.LastLocalHttpServerPidFilePath, "external.pid");
        EditorPrefs.SetString(EditorPrefKeys.LastLocalHttpServerInstanceToken, "external-token");
        nextEditor.ClearTracking();
        Check(EditorPrefs.GetString(EditorPrefKeys.LastLocalHttpServerInstanceToken, "") == "external-token",
            "Cleared legacy/global state owned by another Editor");
        Check(!nextEditor.TryGetHandshake(out _, out _), "Adopted legacy/global ownership");
    }

    private static void PartialHandshakeCannotCombineLaunches()
    {
        var manager = new PidFileManager();
        Store(manager);
        manager.StoreHandshake(null, "token-b");
        Check(!manager.TryGetHandshake(out _, out _), "Combined old path with new launch token");
    }

    private static void HandshakeMustMatchConfiguredPort()
    {
        var manager = new PidFileManager();
        Store(manager, 9090);
        Check(!manager.TryGetHandshake(out _, out _), "Accepted handshake for a different endpoint");
        Check(!manager.TryGetStoredPid(9090, out _), "Read tracking for an unselected port");
    }

    private static void InvalidPortsAreRejected()
    {
        var manager = new PidFileManager();
        foreach (int port in new[] { -1, 0, 65536 })
            Check(!manager.TryGetPortFromPidFilePath($"mcp_http_{port}.pid", out _), $"Accepted invalid port {port}");
    }

    private static void TrackingRequiresValidTimestamp()
    {
        var manager = new PidFileManager();
        Store(manager);
        CorruptTimestamp("not-a-timestamp");
        Check(!manager.TryGetStoredPid(8080, out _), "Trusted PID with corrupt timestamp");
    }

    private static void TrackingPreservesFingerprintAndRejectsExpiry()
    {
        var manager = new PidFileManager();
        Store(manager);
        Check(manager.GetStoredArgsHash() == "fingerprint-a", "Lost process fingerprint");
        CorruptTimestamp(DateTime.UtcNow.AddHours(-7).ToString("O"));
        Check(!manager.TryGetStoredPid(8080, out _), "Trusted expired PID");
        CorruptTimestamp(DateTime.UtcNow.AddHours(1).ToString("O"));
        Check(!manager.TryGetStoredPid(8080, out _), "Trusted future timestamp");
    }

    private static void CorruptTimestamp(string value)
    {
        // Exercise both pre-fix EditorPrefs and the corrected SessionState path.
        foreach (var store in new[] { EditorPrefs.Values, SessionState.Values })
            foreach (var key in new List<string>(store.Keys))
                if (key.StartsWith(EditorPrefKeys.LastLocalHttpServerStartedUtc)) store[key] = value;
    }
}

namespace UnityEngine
{
    public static class Application { public static string dataPath; }
}

namespace MCPForUnity.Editor.Helpers
{
    public static class HttpEndpointUtility
    {
        public static string Url;
        public static string GetLocalBaseUrl() => Url;
    }
}

namespace UnityEditor
{
    public static class EditorPrefs
    {
        public static readonly Dictionary<string, object> Values = new();
        public static void SetString(string key, string value) => Values[key] = value;
        public static string GetString(string key, string fallback) => Values.TryGetValue(key, out var value) ? (string)value : fallback;
        public static void SetInt(string key, int value) => Values[key] = value;
        public static int GetInt(string key, int fallback) => Values.TryGetValue(key, out var value) ? (int)value : fallback;
        public static void DeleteKey(string key) => Values.Remove(key);
    }

    public static class SessionState
    {
        public static readonly Dictionary<string, object> Values = new();
        public static void SetString(string key, string value) => Values[key] = value;
        public static string GetString(string key, string fallback) => Values.TryGetValue(key, out var value) ? (string)value : fallback;
        public static void SetInt(string key, int value) => Values[key] = value;
        public static int GetInt(string key, int fallback) => Values.TryGetValue(key, out var value) ? (int)value : fallback;
        public static void EraseString(string key) => Values.Remove(key);
        public static void EraseInt(string key) => Values.Remove(key);
    }
}
