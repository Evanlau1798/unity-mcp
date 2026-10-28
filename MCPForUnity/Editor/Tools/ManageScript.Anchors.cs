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
        private static bool HasOverlaps(IEnumerable<(int start, int length, string text)> list)
        {
            var arr = list.OrderBy(x => x.start).ToArray();
            for (int i = 1; i < arr.Length; i++)
            {
                if (arr[i - 1].start + arr[i - 1].length > arr[i].start)
                    return true;
            }
            return false;
        }

        private static string ExtractReplacement(JObject op)
        {
            var inline = op.Value<string>("replacement");
            if (!string.IsNullOrEmpty(inline)) return inline;

            var b64 = op.Value<string>("replacementBase64");
            if (!string.IsNullOrEmpty(b64))
            {
                try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
                catch { return null; }
            }
            return null;
        }

        private static string NormalizeNewlines(string t)
        {
            if (string.IsNullOrEmpty(t)) return t;
            return t.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static bool ValidateClassSnippet(string snippet, string expectedName, out string err)
        {
#if USE_ROSLYN
            try
            {
                var tree = CSharpSyntaxTree.ParseText(snippet);
                var root = tree.GetRoot();
                var classes = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().ToList();
                if (classes.Count != 1) { err = "snippet must contain exactly one class declaration"; return false; }
                // Optional: enforce expected name
                // if (classes[0].Identifier.ValueText != expectedName) { err = $"snippet declares '{classes[0].Identifier.ValueText}', expected '{expectedName}'"; return false; }
                err = null; return true;
            }
            catch (Exception ex) { err = ex.Message; return false; }
#else
            if (string.IsNullOrWhiteSpace(snippet) || !snippet.Contains("class ")) { err = "no 'class' keyword found in snippet"; return false; }
            err = null; return true;
#endif
        }


        /// <summary>
        /// Given multiple regex matches in C# source, pick the best one for
        /// anchor-based insertions.  For closing-brace patterns, uses actual
        /// brace-depth analysis to prefer the outermost (class-level) brace.
        /// Otherwise, returns the last match.
        /// </summary>
        private static Match FindBestAnchorMatch(MatchCollection matches, string text, string pattern)
        {
            if (matches.Count == 0) return null;
            if (matches.Count == 1) return matches[0];

            bool isClosingBracePattern = pattern.Contains("}") &&
                (pattern.Contains("$") || pattern.EndsWith(@"\s*"));

            if (isClosingBracePattern)
            {
                // Compute brace depth at each match's '}' position using CSharpLexer
                Match best = null;
                int bestDepth = int.MaxValue;
                int bestPos = -1;

                // Single-pass: scan text and record depth at every '}' in real code
                var depthMap = new Dictionary<int, int>();
                int depth = 0;
                var lexer = new CSharpLexer(text);
                while (lexer.Advance(out char c))
                {
                    if (lexer.InNonCode) continue;
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depthMap[lexer.Position - 1] = depth;
                        depth = Math.Max(0, depth - 1);
                    }
                }

                foreach (Match m in matches)
                {
                    // Find the '}' within the match span
                    int bracePos = -1;
                    for (int k = m.Index; k < m.Index + m.Length && k < text.Length; k++)
                    {
                        if (text[k] == '}') { bracePos = k; break; }
                    }
                    if (bracePos < 0) continue;
                    if (!depthMap.TryGetValue(bracePos, out int d)) continue; // in string/comment

                    // Prefer shallowest depth, then latest position
                    if (d < bestDepth || (d == bestDepth && bracePos > bestPos))
                    {
                        bestDepth = d;
                        bestPos = bracePos;
                        best = m;
                    }
                }
                return best ?? matches[matches.Count - 1];
            }

            // Default: prefer last match
            return matches[matches.Count - 1];
        }

        private static int CountTopLevelParams(string paramStr)
        {
            if (string.IsNullOrWhiteSpace(paramStr)) return 0;
            int depth = 0, count = 1;
            foreach (char c in paramStr)
            {
                if (c == '<' || c == '(' || c == '[') depth++;
                else if (c == '>' || c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0) count++;
            }
            return count;
        }

        /// <summary>
        /// Extracts only the type portions from a parameter list, dropping parameter names.
        /// e.g. "Dictionary&lt;string, int&gt; data, List&lt;Vector3&gt; points" → "Dictionary&lt;string, int&gt;, List&lt;Vector3&gt;"
        /// </summary>
        private static string ExtractParamTypes(string paramStr)
        {
            if (string.IsNullOrWhiteSpace(paramStr)) return "";
            var types = new System.Text.StringBuilder();
            // Split at top-level commas (respecting <> depth)
            int depth = 0, start = 0;
            for (int i = 0; i <= paramStr.Length; i++)
            {
                char c = i < paramStr.Length ? paramStr[i] : ','; // sentinel
                if (c == '<' || c == '(' || c == '[') depth++;
                else if (c == '>' || c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0)
                {
                    string param = paramStr.Substring(start, i - start).Trim();
                    if (types.Length > 0) types.Append(", ");
                    // The type is everything except the last token (the name).
                    // But if the last token ends with '>' or ']', it's all type (e.g. "List<int>").
                    // Find the last whitespace that is NOT inside <> brackets.
                    int lastSplit = -1;
                    int d2 = 0;
                    for (int j = 0; j < param.Length; j++)
                    {
                        char pc = param[j];
                        if (pc == '<' || pc == '(' || pc == '[') d2++;
                        else if (pc == '>' || pc == ')' || pc == ']') d2--;
                        else if (d2 == 0 && char.IsWhiteSpace(pc)) lastSplit = j;
                    }
                    types.Append(lastSplit > 0 ? param.Substring(0, lastSplit).Trim() : param);
                    start = i + 1;
                }
            }
            return Regex.Replace(types.ToString(), @"\s+", " ");
        }

    }
}
