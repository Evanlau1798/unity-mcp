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
        /// Apply simple text edits specified by line/column ranges. Applies transactionally and validates result.
        /// </summary>
        private const int MaxEditPayloadBytes = 64 * 1024;

        private static object ApplyTextEdits(
            string fullPath,
            string relativePath,
            string name,
            JArray edits,
            string preconditionSha256,
            string refreshModeFromCaller = null,
            string validateMode = null)
        {
            if (!File.Exists(fullPath))
                return new ErrorResponse($"Script not found at '{relativePath}'.");
            // Refuse edits if the target or any ancestor is a symlink
            try
            {
                var di = new DirectoryInfo(Path.GetDirectoryName(fullPath) ?? "");
                while (di != null && !string.Equals(di.FullName.Replace('\\', '/'), Application.dataPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    if (di.Exists && (di.Attributes & FileAttributes.ReparsePoint) != 0)
                        return new ErrorResponse("Refusing to edit a symlinked script path.");
                    di = di.Parent;
                }
            }
            catch
            {
                // If checking attributes fails, proceed without the symlink guard
            }
            if (edits == null || edits.Count == 0)
                return new ErrorResponse("No edits provided.");

            string original;
            try { original = File.ReadAllText(fullPath); }
            catch (Exception ex) { return new ErrorResponse($"Failed to read script: {ex.Message}"); }

            // Require precondition to avoid drift on large files
            string currentSha = ComputeSha256(original);
            if (string.IsNullOrEmpty(preconditionSha256))
                return new ErrorResponse("precondition_required", new { status = "precondition_required", current_sha256 = currentSha });
            if (!preconditionSha256.Equals(currentSha, StringComparison.OrdinalIgnoreCase))
                return new ErrorResponse("stale_file", new { status = "stale_file", expected_sha256 = preconditionSha256, current_sha256 = currentSha });

            // Convert edits to absolute index ranges
            var spans = new List<(int start, int end, string text)>();
            long totalBytes = 0;
            foreach (var e in edits)
            {
                try
                {
                    int sl = Math.Max(1, e.Value<int>("startLine"));
                    int sc = Math.Max(1, e.Value<int>("startCol"));
                    int el = Math.Max(1, e.Value<int>("endLine"));
                    int ec = Math.Max(1, e.Value<int>("endCol"));
                    string newText = e.Value<string>("newText") ?? string.Empty;

                    if (!TryIndexFromLineCol(original, sl, sc, out int sidx))
                        return new ErrorResponse($"apply_text_edits: start out of range (line {sl}, col {sc})");
                    if (!TryIndexFromLineCol(original, el, ec, out int eidx))
                        return new ErrorResponse($"apply_text_edits: end out of range (line {el}, col {ec})");
                    if (eidx < sidx) (sidx, eidx) = (eidx, sidx);

                    spans.Add((sidx, eidx, newText));
                    checked
                    {
                        totalBytes += System.Text.Encoding.UTF8.GetByteCount(newText);
                    }
                }
                catch (Exception ex)
                {
                    return new ErrorResponse($"Invalid edit payload: {ex.Message}");
                }
            }

            // Header guard: refuse edits that touch before the first 'using ' directive (after optional BOM) to prevent file corruption
            int headerBoundary = (original.Length > 0 && original[0] == '\uFEFF') ? 1 : 0; // skip BOM once if present
            // Find first top-level using (supports alias, static, and dotted namespaces)
            var mUsing = System.Text.RegularExpressions.Regex.Match(
                original,
                @"(?m)^\s*using\s+(?:static\s+)?(?:[A-Za-z_]\w*\s*=\s*)?[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*\s*;",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2)
            );
            if (mUsing.Success)
            {
                headerBoundary = Math.Min(Math.Max(headerBoundary, mUsing.Index), original.Length);
            }
            foreach (var sp in spans)
            {
                if (sp.start < headerBoundary)
                {
                    return new ErrorResponse("using_guard", new { status = "using_guard", hint = "Refusing to edit before the first 'using'. Use anchor_insert near a method or a structured edit." });
                }
            }

            // Attempt auto-upgrade: if a single edit targets a method header/body, re-route as structured replace_method
            if (spans.Count == 1)
            {
                var sp = spans[0];
                // Heuristic: around the start of the edit, try to match a method header in original
                int searchStart = Math.Max(0, sp.start - 200);
                int searchEnd = Math.Min(original.Length, sp.start + 200);
                string slice = original.Substring(searchStart, searchEnd - searchStart);
                var rx = new System.Text.RegularExpressions.Regex(@"(?m)^[\t ]*(?:\[[^\]]+\][\t ]*)*[\t ]*(?:public|private|protected|internal|static|virtual|override|sealed|async|extern|unsafe|new|partial)[\s\S]*?\b([A-Za-z_][A-Za-z0-9_]*)\s*\(");
                var mh = rx.Match(slice);
                if (mh.Success)
                {
                    string methodName = mh.Groups[1].Value;
                    // Find class span containing the edit
                    if (TryComputeClassSpan(original, name, null, out var clsStart, out var clsLen, out _))
                    {
                        if (TryComputeMethodSpan(original, clsStart, clsLen, methodName, null, null, null, out var mStart, out var mLen, out _))
                        {
                            // If the edit overlaps the method span significantly, treat as replace_method
                            if (sp.start <= mStart + 2 && sp.end >= mStart + 1)
                            {
                                var structEdits = new JArray();

                                // Apply the edit to get a candidate string, then recompute method span on the edited text
                                string candidate = original.Remove(sp.start, sp.end - sp.start).Insert(sp.start, sp.text ?? string.Empty);
                                string replacementText;
                                if (TryComputeClassSpan(candidate, name, null, out var cls2Start, out var cls2Len, out _)
                                    && TryComputeMethodSpan(candidate, cls2Start, cls2Len, methodName, null, null, null, out var m2Start, out var m2Len, out _))
                                {
                                    replacementText = candidate.Substring(m2Start, m2Len);
                                }
                                else
                                {
                                    // Fallback: adjust method start by the net delta if the edit was before the method
                                    int delta = (sp.text?.Length ?? 0) - (sp.end - sp.start);
                                    int adjustedStart = mStart + (sp.start <= mStart ? delta : 0);
                                    adjustedStart = Math.Max(0, Math.Min(adjustedStart, candidate.Length));

                                    // If the edit was within the original method span, adjust the length by the delta within-method
                                    int withinMethodDelta = 0;
                                    if (sp.start >= mStart && sp.start <= mStart + mLen)
                                    {
                                        withinMethodDelta = delta;
                                    }
                                    int adjustedLen = mLen + withinMethodDelta;
                                    adjustedLen = Math.Max(0, Math.Min(candidate.Length - adjustedStart, adjustedLen));
                                    replacementText = candidate.Substring(adjustedStart, adjustedLen);
                                }

                                var op = new JObject
                                {
                                    ["mode"] = "replace_method",
                                    ["className"] = name,
                                    ["methodName"] = methodName,
                                    ["replacement"] = replacementText
                                };
                                structEdits.Add(op);
                                // Reuse structured path
                                return EditScript(fullPath, relativePath, name, structEdits, new JObject { ["refresh"] = "immediate", ["validate"] = "standard" });
                            }
                        }
                    }
                }
            }

            if (totalBytes > MaxEditPayloadBytes)
            {
                return new ErrorResponse("too_large", new { status = "too_large", limitBytes = MaxEditPayloadBytes, hint = "split into smaller edits" });
            }

            // Ensure non-overlap and apply from back to front
            spans = spans.OrderByDescending(t => t.start).ToList();
            for (int i = 1; i < spans.Count; i++)
            {
                if (spans[i].end > spans[i - 1].start)
                {
                    var conflict = new[] { new { startA = spans[i].start, endA = spans[i].end, startB = spans[i - 1].start, endB = spans[i - 1].end } };
                    return new ErrorResponse("overlap", new { status = "overlap", conflicts = conflict, hint = "Sort ranges descending by start and compute from the same snapshot." });
                }
            }

            string working = original;
            bool syntaxOnly = string.Equals(validateMode, "syntax", StringComparison.OrdinalIgnoreCase);
            foreach (var sp in spans)
            {
                working = working.Remove(sp.start, sp.end - sp.start).Insert(sp.start, sp.text ?? string.Empty);
            }

            // No-op guard: if resulting text is identical, avoid writes and return explicit no-op
            if (string.Equals(working, original, StringComparison.Ordinal))
            {
                string noChangeSha = ComputeSha256(original);
                return new SuccessResponse(
                    $"No-op: contents unchanged for '{relativePath}'.",
                    new
                    {
                        uri = $"mcpforunity://path/{relativePath}",
                        path = relativePath,
                        editsApplied = 0,
                        no_op = true,
                        sha256 = noChangeSha,
                        evidence = new { reason = "identical_content" }
                    }
                );
            }

            // Always check final structural balance
            if (!CheckBalancedDelimiters(working, out int line, out char expected))
            {
                int startLine = Math.Max(1, line - 5);
                int endLine = line + 5;
                string hint = $"unbalanced_braces at line {line}. Call resources/read for lines {startLine}-{endLine} and resend a smaller apply_text_edits that restores balance.";
                return new ErrorResponse(hint, new { status = "unbalanced_braces", line, expected = expected.ToString(), evidenceWindow = new { startLine, endLine } });
            }

#if USE_ROSLYN
            if (!syntaxOnly)
            {
                var tree = CSharpSyntaxTree.ParseText(working);
                var diagnostics = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(3)
                    .Select(d => new {
                        line = d.Location.GetLineSpan().StartLinePosition.Line + 1,
                        col = d.Location.GetLineSpan().StartLinePosition.Character + 1,
                        code = d.Id,
                        message = d.GetMessage()
                    }).ToArray();
                if (diagnostics.Length > 0)
                {
                    int firstLine = diagnostics[0].line;
                    int startLineRos = Math.Max(1, firstLine - 5);
                    int endLineRos = firstLine + 5;
                    return new ErrorResponse("syntax_error", new { status = "syntax_error", diagnostics, evidenceWindow = new { startLine = startLineRos, endLine = endLineRos } });
                }
            }
#endif

            string newSha = ComputeSha256(working);

            // Atomic write and schedule refresh
            try
            {
                var enc = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                var tmp = fullPath + ".tmp";
                File.WriteAllText(tmp, working, enc);
                string backup = fullPath + ".bak";
                try
                {
                    File.Replace(tmp, fullPath, backup);
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { /* ignore */ }
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

                // Respect refresh mode: immediate vs debounced
                bool immediate = string.Equals(refreshModeFromCaller, "immediate", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(refreshModeFromCaller, "sync", StringComparison.OrdinalIgnoreCase);
                if (immediate)
                {
                    McpLog.Info($"[ManageScript] ApplyTextEdits: immediate refresh for '{relativePath}'");
                    AssetDatabase.ImportAsset(
                        relativePath,
                        ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate
                    );
#if UNITY_EDITOR
                    UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
#endif
                }
                else
                {
                    McpLog.Info($"[ManageScript] ApplyTextEdits: debounced refresh scheduled for '{relativePath}'");
                    ManageScriptRefreshHelpers.ScheduleScriptRefresh(relativePath);
                }

                return new SuccessResponse(
                    $"Applied {spans.Count} text edit(s) to '{relativePath}'.",
                    new
                    {
                        uri = $"mcpforunity://path/{relativePath}",
                        path = relativePath,
                        editsApplied = spans.Count,
                        sha256 = newSha,
                        scheduledRefresh = !immediate
                    }
                );
            }
            catch (Exception ex)
            {
                return new ErrorResponse($"Failed to write edits: {ex.Message}");
            }
        }

        private static bool TryIndexFromLineCol(string text, int line1, int col1, out int index)
        {
            // 1-based line/col to absolute index (0-based), col positions are counted in code points
            int line = 1, col = 1;
            for (int i = 0; i <= text.Length; i++)
            {
                if (line == line1 && col == col1)
                {
                    index = i;
                    return true;
                }
                if (i == text.Length) break;
                char c = text[i];
                if (c == '\r')
                {
                    // Treat CRLF as a single newline; skip the LF if present
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    line++;
                    col = 1;
                }
                else if (c == '\n')
                {
                    line++;
                    col = 1;
                }
                else
                {
                    col++;
                }
            }
            index = -1;
            return false;
        }

        private static string ComputeSha256(string contents)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(contents);
                var hash = sha.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

    }
}
