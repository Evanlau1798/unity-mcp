using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Models;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Services.Transport
{
    internal static partial class TransportCommandDispatcher
    {
        private static void ProcessCommand(string id, PendingCommand pending)
        {
            bool canceled;
            lock (PendingLock)
            {
                if (!Pending.ContainsKey(id)) return;
                canceled = pending.CancellationToken.IsCancellationRequested;
                if (!canceled) pending.DispatchWaitMs = pending.Clock.ElapsedMilliseconds;
            }
            if (canceled)
            {
                CancelPending(id, pending.CancellationToken);
                return;
            }

            string commandText = pending.CommandJson?.Trim();
            if (string.IsNullOrEmpty(commandText))
            {
                pending.TrySetResult(SerializeError("Empty command received"));
                RemovePending(id, pending);
                return;
            }

            if (string.Equals(commandText, "ping", StringComparison.OrdinalIgnoreCase))
            {
                var pingResponse = new
                {
                    status = "success",
                    result = new { message = "pong" }
                };
                pending.TrySetResult(JsonConvert.SerializeObject(pingResponse));
                RemovePending(id, pending);
                return;
            }

            if (!IsValidJson(commandText))
            {
                var invalidJsonResponse = new
                {
                    status = "error",
                    error = "Invalid JSON format",
                    receivedText = commandText.Length > 50 ? commandText[..50] + "..." : commandText
                };
                pending.TrySetResult(JsonConvert.SerializeObject(invalidJsonResponse));
                RemovePending(id, pending);
                return;
            }

            try
            {
                var command = JsonConvert.DeserializeObject<Command>(commandText);
                if (command == null)
                {
                    pending.TrySetResult(SerializeError("Command deserialized to null", "Unknown", commandText));
                    RemovePending(id, pending);
                    return;
                }

                if (string.IsNullOrWhiteSpace(command.type))
                {
                    pending.TrySetResult(SerializeError("Command type cannot be empty"));
                    RemovePending(id, pending);
                    return;
                }

                if (string.Equals(command.type, "ping", StringComparison.OrdinalIgnoreCase))
                {
                    var pingResponse = new
                    {
                        status = "success",
                        result = new { message = "pong" }
                    };
                    pending.TrySetResult(JsonConvert.SerializeObject(pingResponse));
                    RemovePending(id, pending);
                    return;
                }

                var parameters = command.@params ?? new JObject();

                // An unknown command is the caller's mistake or a version mismatch (a server or
                // CLI newer than this package), not an Editor fault: answer it without logging
                // an error and a stack trace to the user's console.
                if (!CommandRegistry.HasHandler(command.type))
                {
                    string unknown = $"Unity has no tool or resource named '{command.type}'. If the MCP server " +
                        "or CLI is newer than the MCP for Unity package in this project, update the package.";
                    McpLog.Warn(unknown);
                    pending.TrySetResult(SerializeError(unknown, command.type));
                    RemovePending(id, pending);
                    return;
                }

                // Block execution of disabled resources
                var resourceMeta = MCPServiceLocator.ResourceDiscovery.GetResourceMetadata(command.type);
                if (resourceMeta != null && !MCPServiceLocator.ResourceDiscovery.IsResourceEnabled(command.type))
                {
                    pending.TrySetResult(SerializeError(
                        $"Resource '{command.type}' is disabled in the Unity Editor."));
                    RemovePending(id, pending);
                    return;
                }

                // Block execution of disabled tools
                var toolMeta = MCPServiceLocator.ToolDiscovery.GetToolMetadata(command.type);
                if (toolMeta != null && !MCPServiceLocator.ToolDiscovery.IsToolEnabled(command.type))
                {
                    pending.TrySetResult(SerializeError(
                        $"Tool '{command.type}' is disabled in the Unity Editor."));
                    RemovePending(id, pending);
                    return;
                }

                var logType = resourceMeta != null ? "resource" : toolMeta != null ? "tool" : "unknown";
                var sw = McpLogRecord.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
                var result = CommandRegistry.ExecuteCommand(command.type, parameters, pending.CompletionSource);

                if (result == null)
                {
                    // Async command – cleanup after completion on next editor frame to preserve order.
                    var capturedType = command.type;
                    var capturedParams = parameters;
                    var capturedLogType = logType;
                    pending.CompletionSource.Task.ContinueWith(t =>
                    {
                        sw?.Stop();
                        var logStatus = "SUCCESS";
                        string logError = null;
                        if (t.IsFaulted)
                        {
                            logStatus = "ERROR";
                            logError = t.Exception?.InnerException?.Message;
                        }
                        else if (t.IsCompletedSuccessfully && t.Result != null)
                        {
                            try
                            {
                                var resultObj = JObject.Parse(t.Result);
                                if (string.Equals(resultObj.Value<string>("status"), "error", StringComparison.OrdinalIgnoreCase))
                                {
                                    logStatus = "ERROR";
                                    logError = resultObj.Value<string>("error");
                                }
                            }
                            catch { }
                        }
                        // The logger reads Unity metadata; only cleanup itself is safe on this worker.
                        _mainThreadContext?.Post(_ => McpLogRecord.Log(capturedType, capturedParams, capturedLogType,
                            logStatus, sw?.ElapsedMilliseconds ?? 0, logError), null);
                        RemovePending(id, pending);
                    }, TaskScheduler.Default);
                    return;
                }

                sw?.Stop();

                string syncLogStatus = "SUCCESS";
                string syncLogError = null;
                if (result is ErrorResponse errResp)
                {
                    syncLogStatus = "ERROR";
                    syncLogError = errResp.Error;
                }
                McpLogRecord.Log(command.type, parameters, logType, syncLogStatus, sw?.ElapsedMilliseconds ?? 0, syncLogError);

                var response = new { status = "success", result };
                pending.TrySetResult(JsonConvert.SerializeObject(response));
                RemovePending(id, pending);
            }
            catch (Exception ex)
            {
                McpLog.Error($"Error processing command: {ex.Message}\n{ex.StackTrace}");
                pending.TrySetResult(SerializeError(ex.Message, "Unknown (error during processing)", ex.StackTrace));
                RemovePending(id, pending);
            }
        }
    }
}
