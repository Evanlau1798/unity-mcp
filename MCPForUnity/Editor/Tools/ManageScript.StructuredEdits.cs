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
    public static partial class ManageScript
    {
        /// <summary>
        /// Structured edits (AST-backed where available) on existing scripts.
        /// Supports class-level replace/delete with Roslyn span computation if USE_ROSLYN is defined,
        /// otherwise falls back to a conservative balanced-brace scan.
        /// </summary>
        private static object EditScript(
            string fullPath,
            string relativePath,
            string name,
            JArray edits,
            JObject options)
        {
            if (!File.Exists(fullPath))
                return new ErrorResponse($"Script not found at '{relativePath}'.");
            // Refuse edits if the target is a symlink
            try
            {
                var attrs = File.GetAttributes(fullPath);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    return new ErrorResponse("Refusing to edit a symlinked script path.");
            }
            catch
            {
                // ignore failures checking attributes and proceed
            }
            if (edits == null || edits.Count == 0)
                return new ErrorResponse("No edits provided.");

            string original;
            try { original = File.ReadAllText(fullPath); }
            catch (Exception ex) { return new ErrorResponse($"Failed to read script: {ex.Message}"); }

            string working = original;

            try
            {
                var replacements = new List<(int start, int length, string text)>();
                int appliedCount = 0;

                // Apply mode: atomic (default) computes all spans against original and applies together.
                // Sequential applies each edit immediately to the current working text (useful for dependent edits).
                string applyMode = options?["applyMode"]?.ToString()?.ToLowerInvariant();
                bool applySequentially = applyMode == "sequential";

                foreach (var e in edits)
                {
                    var op = (JObject)e;
                    var mode = (op.Value<string>("mode") ?? op.Value<string>("op") ?? string.Empty).ToLowerInvariant();

                    switch (mode)
                    {
                        case "replace_class":
                            {
                                string className = op.Value<string>("className");
                                string ns = op.Value<string>("namespace");
                                string replacement = ExtractReplacement(op);

                                if (string.IsNullOrWhiteSpace(className))
                                    return new ErrorResponse("replace_class requires 'className'.");
                                if (replacement == null)
                                    return new ErrorResponse("replace_class requires 'replacement' (inline or base64).");

                                if (!TryComputeClassSpan(working, className, ns, out var spanStart, out var spanLength, out var why))
                                    return new ErrorResponse($"replace_class failed: {why}");

                                if (!ValidateClassSnippet(replacement, className, out var vErr))
                                    return new ErrorResponse($"Replacement snippet invalid: {vErr}");

                                if (applySequentially)
                                {
                                    working = working.Remove(spanStart, spanLength).Insert(spanStart, NormalizeNewlines(replacement));
                                    appliedCount++;
                                }
                                else
                                {
                                    replacements.Add((spanStart, spanLength, NormalizeNewlines(replacement)));
                                }
                                break;
                            }

                        case "delete_class":
                            {
                                string className = op.Value<string>("className");
                                string ns = op.Value<string>("namespace");
                                if (string.IsNullOrWhiteSpace(className))
                                    return new ErrorResponse("delete_class requires 'className'.");

                                if (!TryComputeClassSpan(working, className, ns, out var s, out var l, out var why))
                                    return new ErrorResponse($"delete_class failed: {why}");

                                if (applySequentially)
                                {
                                    working = working.Remove(s, l);
                                    appliedCount++;
                                }
                                else
                                {
                                    replacements.Add((s, l, string.Empty));
                                }
                                break;
                            }

                        case "replace_method":
                            {
                                string className = op.Value<string>("className");
                                string ns = op.Value<string>("namespace");
                                string methodName = op.Value<string>("methodName");
                                string replacement = ExtractReplacement(op);
                                string returnType = op.Value<string>("returnType");
                                string parametersSignature = op.Value<string>("parametersSignature");
                                string attributesContains = op.Value<string>("attributesContains");

                                if (string.IsNullOrWhiteSpace(className)) return new ErrorResponse("replace_method requires 'className'.");
                                if (string.IsNullOrWhiteSpace(methodName)) return new ErrorResponse("replace_method requires 'methodName'.");
                                if (replacement == null) return new ErrorResponse("replace_method requires 'replacement' (inline or base64).");

                                if (!TryComputeClassSpan(working, className, ns, out var clsStart, out var clsLen, out var whyClass))
                                    return new ErrorResponse($"replace_method failed to locate class: {whyClass}");

                                if (!TryComputeMethodSpan(working, clsStart, clsLen, methodName, returnType, parametersSignature, attributesContains, out var mStart, out var mLen, out var whyMethod))
                                {
                                    bool hasDependentInsert = edits.Any(j => j is JObject jo &&
                                        string.Equals(jo.Value<string>("className"), className, StringComparison.Ordinal) &&
                                        string.Equals(jo.Value<string>("methodName"), methodName, StringComparison.Ordinal) &&
                                        ((jo.Value<string>("mode") ?? jo.Value<string>("op") ?? string.Empty).ToLowerInvariant() == "insert_method"));
                                    string hint = hasDependentInsert && !applySequentially ? " Hint: This batch inserts this method. Use options.applyMode='sequential' or split into separate calls." : string.Empty;
                                    return new ErrorResponse($"replace_method failed: {whyMethod}.{hint}");
                                }

                                if (applySequentially)
                                {
                                    working = working.Remove(mStart, mLen).Insert(mStart, NormalizeNewlines(replacement));
                                    appliedCount++;
                                }
                                else
                                {
                                    replacements.Add((mStart, mLen, NormalizeNewlines(replacement)));
                                }
                                break;
                            }

                        case "delete_method":
                            {
                                string className = op.Value<string>("className");
                                string ns = op.Value<string>("namespace");
                                string methodName = op.Value<string>("methodName");
                                string returnType = op.Value<string>("returnType");
                                string parametersSignature = op.Value<string>("parametersSignature");
                                string attributesContains = op.Value<string>("attributesContains");

                                if (string.IsNullOrWhiteSpace(className)) return new ErrorResponse("delete_method requires 'className'.");
                                if (string.IsNullOrWhiteSpace(methodName)) return new ErrorResponse("delete_method requires 'methodName'.");

                                if (!TryComputeClassSpan(working, className, ns, out var clsStart, out var clsLen, out var whyClass))
                                    return new ErrorResponse($"delete_method failed to locate class: {whyClass}");

                                if (!TryComputeMethodSpan(working, clsStart, clsLen, methodName, returnType, parametersSignature, attributesContains, out var mStart, out var mLen, out var whyMethod))
                                {
                                    bool hasDependentInsert = edits.Any(j => j is JObject jo &&
                                        string.Equals(jo.Value<string>("className"), className, StringComparison.Ordinal) &&
                                        string.Equals(jo.Value<string>("methodName"), methodName, StringComparison.Ordinal) &&
                                        ((jo.Value<string>("mode") ?? jo.Value<string>("op") ?? string.Empty).ToLowerInvariant() == "insert_method"));
                                    string hint = hasDependentInsert && !applySequentially ? " Hint: This batch inserts this method. Use options.applyMode='sequential' or split into separate calls." : string.Empty;
                                    return new ErrorResponse($"delete_method failed: {whyMethod}.{hint}");
                                }

                                if (applySequentially)
                                {
                                    working = working.Remove(mStart, mLen);
                                    appliedCount++;
                                }
                                else
                                {
                                    replacements.Add((mStart, mLen, string.Empty));
                                }
                                break;
                            }

                        case "insert_method":
                            {
                                var error = BuildMethodInsertion(working, op, out int insAt, out string text);
                                if (error != null) return error;
                                if (applySequentially)
                                {
                                    working = working.Insert(insAt, text);
                                    appliedCount++;
                                }
                                else
                                {
                                    replacements.Add((insAt, 0, text));
                                }
                                break;
                            }

                        case "anchor_insert":
                            {
                                string anchor = op.Value<string>("anchor");
                                string position = (op.Value<string>("position") ?? "before").ToLowerInvariant();
                                string text = op.Value<string>("text") ?? ExtractReplacement(op);
                                if (string.IsNullOrWhiteSpace(anchor)) return new ErrorResponse("anchor_insert requires 'anchor' (regex).");
                                if (string.IsNullOrEmpty(text)) return new ErrorResponse("anchor_insert requires non-empty 'text'.");

                                try
                                {
                                    var rx = new Regex(anchor, RegexOptions.Multiline, TimeSpan.FromSeconds(2));
                                    var allMatches = rx.Matches(working);
                                    if (allMatches.Count == 0) return new ErrorResponse($"anchor_insert: anchor not found: {anchor}");
                                    var m = FindBestAnchorMatch(allMatches, working, anchor);
                                    if (m == null) return new ErrorResponse($"anchor_insert: anchor not found (filtered): {anchor}");
                                    int insAt = position == "after" ? m.Index + m.Length : m.Index;
                                    string norm = NormalizeNewlines(text);
                                    if (!norm.EndsWith("\n"))
                                    {
                                        norm += "\n";
                                    }

                                    // Duplicate guard: if identical snippet already exists within this class, skip insert
                                    if (TryComputeClassSpan(working, name, null, out var clsStartDG, out var clsLenDG, out _))
                                    {
                                        string classSlice = working.Substring(clsStartDG, Math.Min(clsLenDG, working.Length - clsStartDG));
                                        if (classSlice.IndexOf(norm, StringComparison.Ordinal) >= 0)
                                        {
                                            // Do not insert duplicate; treat as no-op
                                            break;
                                        }
                                    }
                                    if (applySequentially)
                                    {
                                        working = working.Insert(insAt, norm);
                                        appliedCount++;
                                    }
                                    else
                                    {
                                        replacements.Add((insAt, 0, norm));
                                    }
                                }
                                catch (Exception ex)
                                {
                                    return new ErrorResponse($"anchor_insert failed: {ex.Message}");
                                }
                                break;
                            }

                        case "anchor_delete":
                            {
                                string anchor = op.Value<string>("anchor");
                                if (string.IsNullOrWhiteSpace(anchor)) return new ErrorResponse("anchor_delete requires 'anchor' (regex).");
                                try
                                {
                                    var rx = new Regex(anchor, RegexOptions.Multiline, TimeSpan.FromSeconds(2));
                                    var allDelMatches = rx.Matches(working);
                                    if (allDelMatches.Count == 0) return new ErrorResponse($"anchor_delete: anchor not found: {anchor}");
                                    var m = FindBestAnchorMatch(allDelMatches, working, anchor);
                                    if (m == null) return new ErrorResponse($"anchor_delete: anchor not found (filtered): {anchor}");
                                    int delAt = m.Index;
                                    int delLen = m.Length;
                                    if (applySequentially)
                                    {
                                        working = working.Remove(delAt, delLen);
                                        appliedCount++;
                                    }
                                    else
                                    {
                                        replacements.Add((delAt, delLen, string.Empty));
                                    }
                                }
                                catch (Exception ex)
                                {
                                    return new ErrorResponse($"anchor_delete failed: {ex.Message}");
                                }
                                break;
                            }

                        case "anchor_replace":
                            {
                                string anchor = op.Value<string>("anchor");
                                string replacement = op.Value<string>("text") ?? op.Value<string>("replacement") ?? ExtractReplacement(op) ?? string.Empty;
                                if (string.IsNullOrWhiteSpace(anchor)) return new ErrorResponse("anchor_replace requires 'anchor' (regex).");
                                try
                                {
                                    var rx = new Regex(anchor, RegexOptions.Multiline, TimeSpan.FromSeconds(2));
                                    var allReplMatches = rx.Matches(working);
                                    if (allReplMatches.Count == 0) return new ErrorResponse($"anchor_replace: anchor not found: {anchor}");
                                    var m = FindBestAnchorMatch(allReplMatches, working, anchor);
                                    if (m == null) return new ErrorResponse($"anchor_replace: anchor not found (filtered): {anchor}");
                                    int at = m.Index;
                                    int len = m.Length;
                                    string norm = NormalizeNewlines(replacement);
                                    if (applySequentially)
                                    {
                                        working = working.Remove(at, len).Insert(at, norm);
                                        appliedCount++;
                                    }
                                    else
                                    {
                                        replacements.Add((at, len, norm));
                                    }
                                }
                                catch (Exception ex)
                                {
                                    return new ErrorResponse($"anchor_replace failed: {ex.Message}");
                                }
                                break;
                            }

                        default:
                            return new ErrorResponse($"Unknown edit mode: '{mode}'. Allowed: replace_class, delete_class, replace_method, delete_method, insert_method, anchor_insert, anchor_delete, anchor_replace.");
                    }
                }

                if (!applySequentially)
                {
                    if (HasOverlaps(replacements))
                    {
                        var ordered = replacements.OrderByDescending(r => r.start).ToList();
                        for (int i = 1; i < ordered.Count; i++)
                        {
                            if (ordered[i].start + ordered[i].length > ordered[i - 1].start)
                            {
                                var conflict = new[] { new { startA = ordered[i].start, endA = ordered[i].start + ordered[i].length, startB = ordered[i - 1].start, endB = ordered[i - 1].start + ordered[i - 1].length } };
                                return new ErrorResponse("overlap", new { status = "overlap", conflicts = conflict, hint = "Sort ranges descending by start and compute from the same snapshot." });
                            }
                        }
                        return new ErrorResponse("overlap", new { status = "overlap" });
                    }

                    foreach (var r in replacements.OrderByDescending(r => r.start))
                        working = working.Remove(r.start, r.length).Insert(r.start, r.text);
                    appliedCount = replacements.Count;
                }

                // Guard against structural imbalance before validation
                if (!CheckBalancedDelimiters(working, out int lineBal, out char expectedBal))
                    return new ErrorResponse("unbalanced_braces", new { status = "unbalanced_braces", line = lineBal, expected = expectedBal.ToString() });

                // No-op guard for structured edits: if text unchanged, return explicit no-op
                if (string.Equals(working, original, StringComparison.Ordinal))
                {
                    var sameSha = ComputeSha256(original);
                    return new SuccessResponse(
                        $"No-op: contents unchanged for '{relativePath}'.",
                        new
                        {
                            path = relativePath,
                            uri = $"mcpforunity://path/{relativePath}",
                            editsApplied = 0,
                            no_op = true,
                            sha256 = sameSha,
                            evidence = new { reason = "identical_content" }
                        }
                    );
                }

                // Validate result using override from options if provided; otherwise GUI strictness
                var level = GetValidationLevelFromGUI();
                try
                {
                    var validateOpt = options?["validate"]?.ToString()?.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(validateOpt))
                    {
                        level = validateOpt switch
                        {
                            "basic" => ValidationLevel.Basic,
                            "standard" => ValidationLevel.Standard,
                            "comprehensive" => ValidationLevel.Comprehensive,
                            "strict" => ValidationLevel.Strict,
                            _ => level
                        };
                    }
                }
                catch { /* ignore option parsing issues */ }
                if (!ValidateScriptSyntax(working, level, out var errors))
                    return new ErrorResponse("validation_failed", new { status = "validation_failed", diagnostics = errors ?? Array.Empty<string>() });
                else if (errors != null && errors.Length > 0)
                    McpLog.Warn($"Script validation warnings for {name}:\n" + string.Join("\n", errors));

                // Atomic write with backup; schedule refresh
                // Decide refresh behavior
                string refreshMode = options?["refresh"]?.ToString()?.ToLowerInvariant();
                bool immediate = refreshMode == "immediate" || refreshMode == "sync";

                // Persist changes atomically (no BOM), then compute/return new file SHA
                var enc = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                var tmp = fullPath + ".tmp";
                File.WriteAllText(tmp, working, enc);
                var backup = fullPath + ".bak";
                try
                {
                    File.Replace(tmp, fullPath, backup);
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(tmp, fullPath, true);
                    try { File.Delete(tmp); } catch { }
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                }
                catch (IOException)
                {
                    File.Copy(tmp, fullPath, true);
                    try { File.Delete(tmp); } catch { }
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                }

                var newSha = ComputeSha256(working);
                var ok = new SuccessResponse(
                    $"Applied {appliedCount} structured edit(s) to '{relativePath}'.",
                    new
                    {
                        path = relativePath,
                        uri = $"mcpforunity://path/{relativePath}",
                        editsApplied = appliedCount,
                        scheduledRefresh = !immediate,
                        sha256 = newSha
                    }
                );

                if (immediate)
                {
                    McpLog.Info($"[ManageScript] EditScript: immediate refresh for '{relativePath}'", always: false);
                    ManageScriptRefreshHelpers.ImportAndRequestCompile(relativePath);
                }
                else
                {
                    ManageScriptRefreshHelpers.ScheduleScriptRefresh(relativePath);
                }
                return ok;
            }
            catch (Exception ex)
            {
                return new ErrorResponse($"Edit failed: {ex.Message}");
            }
        }

    }
}
