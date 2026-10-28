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
        private static bool TryComputeClassSpan(string source, string className, string ns, out int start, out int length, out string why)
        {
#if USE_ROSLYN
            try
            {
                var tree = CSharpSyntaxTree.ParseText(source);
                var root = tree.GetRoot();
                var classes = root.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                    .Where(c => c.Identifier.ValueText == className);

                if (!string.IsNullOrEmpty(ns))
                {
                    classes = classes.Where(c =>
                        (c.FirstAncestorOrSelf<Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax>()?.Name?.ToString() ?? "") == ns
                        || (c.FirstAncestorOrSelf<Microsoft.CodeAnalysis.CSharp.Syntax.FileScopedNamespaceDeclarationSyntax>()?.Name?.ToString() ?? "") == ns);
                }

                var list = classes.ToList();
                if (list.Count == 0) { start = length = 0; why = $"class '{className}' not found" + (ns != null ? $" in namespace '{ns}'" : ""); return false; }
                if (list.Count > 1) { start = length = 0; why = $"class '{className}' matched {list.Count} declarations (partial/nested?). Disambiguate."; return false; }

                var cls = list[0];
                var span = cls.FullSpan; // includes attributes & leading trivia
                start = span.Start; length = span.Length; why = null; return true;
            }
            catch
            {
                // fall back below
            }
#endif
            return TryComputeClassSpanBalanced(source, className, ns, out start, out length, out why);
        }

        private static bool TryComputeClassSpanBalanced(string source, string className, string ns, out int start, out int length, out string why)
        {
            start = length = 0; why = null;
            var idx = IndexOfClassToken(source, className);
            if (idx < 0) { why = $"class '{className}' not found (balanced scan)"; return false; }

            if (!string.IsNullOrEmpty(ns) && !AppearsWithinNamespaceHeader(source, idx, ns))
            { why = $"class '{className}' not under namespace '{ns}' (balanced scan)"; return false; }

            // Include modifiers/attributes on the same line: back up to the start of line
            int lineStart = idx;
            while (lineStart > 0 && source[lineStart - 1] != '\n' && source[lineStart - 1] != '\r') lineStart--;

            int i = idx;
            while (i < source.Length && source[i] != '{') i++;
            if (i >= source.Length) { why = "no opening brace after class header"; return false; }

            int depth = 0;
            int startSpan = lineStart;
            var lexer = new CSharpLexer(source, i);
            while (lexer.Advance(out char c))
            {
                if (lexer.InNonCode) continue;
                if (c == '{') { depth++; }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { start = startSpan; length = (lexer.Position - 1 - startSpan) + 1; return true; }
                    if (depth < 0) { why = "brace underflow"; return false; }
                }
            }
            why = "unterminated class block"; return false;
        }

        private static bool TryComputeMethodSpan(
            string source,
            int classStart,
            int classLength,
            string methodName,
            string returnType,
            string parametersSignature,
            string attributesContains,
            out int start,
            out int length,
            out string why)
        {
            start = length = 0; why = null;
            int searchStart = classStart;
            int searchEnd = Math.Min(source.Length, classStart + classLength);

            // 1) Find the method header using a stricter regex (allows optional attributes above)
            string rtPattern = string.IsNullOrEmpty(returnType) ? @"[^\s]+" : Regex.Escape(returnType).Replace("\\ ", "\\s+");
            string namePattern = Regex.Escape(methodName);
            // If a parametersSignature is provided, it may include surrounding parentheses. Strip them so
            // we can safely embed the signature inside our own parenthesis group without duplicating.
            string paramsPattern;
            if (string.IsNullOrEmpty(parametersSignature))
            {
                paramsPattern = @"[\s\S]*?"; // permissive when not specified
            }
            else
            {
                string ps = parametersSignature.Trim();
                if (ps.StartsWith("(") && ps.EndsWith(")") && ps.Length >= 2)
                {
                    ps = ps.Substring(1, ps.Length - 2);
                }
                // Escape literal text of the signature
                paramsPattern = Regex.Escape(ps);
            }
            string pattern =
                @"(?m)^[\t ]*(?<attributes>(?:\[[^\]]+\][\t ]*)*)[\t ]*" +
                @"(?:(?:public|private|protected|internal|static|virtual|override|sealed|async|extern|unsafe|new|partial|readonly|volatile|event|abstract|ref|in|out)\s+)*" +
                rtPattern + @"[\t ]+(?<method>" + namePattern + @")\s*(?:<[^>]+>)?\s*\(" + paramsPattern + @"\)";

            string slice = source.Substring(searchStart, searchEnd - searchStart);
            // Balanced spans may include a namespace or prior member on the same
            // line. Match the final class brace to start at its actual body.
            int classBodyStart = -1;
            var classBraces = new Stack<int>();
            var classLexer = new CSharpLexer(source, searchStart, searchEnd);
            while (classLexer.Advance(out char classChar))
            {
                if (classLexer.InNonCode) continue;
                if (classChar == '{') classBraces.Push(classLexer.Position - 1);
                else if (classChar == '}' && classBraces.Count > 0) classBodyStart = classBraces.Pop();
            }
            if (classBodyStart < 0) { why = "class body not found"; return false; }
            Match headerMatch = null;
            int attrStart = 0;
            int memberDepth = 0, attributeDepth = 0, pendingAttribute = -1, attributeGroupStart = -1;
            var precedingAttributes = new System.Text.StringBuilder();
            var scopeLexer = new CSharpLexer(source, classBodyStart, searchEnd);
            foreach (Match candidate in Regex.Matches(slice, pattern, RegexOptions.Multiline, TimeSpan.FromSeconds(2)))
            {
                int candidateIndex = searchStart + candidate.Index;
                if (candidateIndex <= classBodyStart) continue;
                // Only direct members in real code may anchor edits. Retain complete
                // attribute groups, including multiline arguments, ahead of the header.
                while (scopeLexer.Position < candidateIndex && scopeLexer.Advance(out char c))
                {
                    if (scopeLexer.InNonCode) continue;
                    if (attributeDepth > 0)
                    {
                        if (c == '[') attributeDepth++;
                        else if (c == ']' && --attributeDepth == 0)
                            precedingAttributes.Append(source, attributeGroupStart, scopeLexer.Position - attributeGroupStart);
                    }
                    else if (memberDepth == 1 && c == '[')
                    {
                        if (pendingAttribute < 0) pendingAttribute = scopeLexer.Position - 1;
                        attributeGroupStart = scopeLexer.Position - 1;
                        attributeDepth = 1;
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        pendingAttribute = -1;
                        precedingAttributes.Clear();
                    }
                    if (c == '{') memberDepth++;
                    else if (c == '}') memberDepth--;
                }
                if (scopeLexer.InNonCode || memberDepth != 1 || attributeDepth != 0) continue;
                int candidateAttributeStart = pendingAttribute < 0 ? candidateIndex : source.LastIndexOf('\n', pendingAttribute) + 1;
                string attributeBlock = precedingAttributes.ToString() + candidate.Groups["attributes"].Value;
                if (!string.IsNullOrEmpty(attributesContains) && attributeBlock.IndexOf(attributesContains, StringComparison.Ordinal) < 0)
                {
                    why = $"method '{methodName}' found but attributes filter did not match";
                    continue;
                }
                headerMatch = candidate;
                attrStart = candidateAttributeStart;
                break;
            }
            if (headerMatch == null)
            {
                why = why ?? $"method '{methodName}' header not found in class";
                return false;
            }
            why = null;

            // 2) Walk from the end of signature to detect body style ('{' or '=> ...;') and compute end
            // Find the '(' that belongs to the method signature, not attributes
            int nameTokenIdx = searchStart + headerMatch.Groups["method"].Index;
            int sigOpenParen = IndexOfTokenWithin(source, "(", nameTokenIdx, searchEnd);
            if (sigOpenParen < 0) { why = "method parameter list '(' not found"; return false; }

            int i = sigOpenParen;
            int parenDepth = 0;
            var parenLexer = new CSharpLexer(source, i, searchEnd);
            while (parenLexer.Advance(out char pc))
            {
                if (parenLexer.InNonCode) continue;
                if (pc == '(') parenDepth++;
                if (pc == ')') { parenDepth--; if (parenDepth == 0) break; }
            }
            i = parenLexer.Position;

            // After params: detect expression-bodied or block-bodied
            // Skip whitespace/comments
            for (; i < searchEnd; i++)
            {
                char c = source[i];
                char n = i + 1 < searchEnd ? source[i + 1] : '\0';
                if (char.IsWhiteSpace(c)) continue;
                if (c == '/' && n == '/') { while (i < searchEnd && source[i] != '\n') i++; continue; }
                if (c == '/' && n == '*') { i += 2; while (i + 1 < searchEnd && !(source[i] == '*' && source[i + 1] == '/')) i++; i++; continue; }
                break;
            }

            // Tolerate generic constraints between params and body: multiple 'where T : ...'
            for (; ; )
            {
                // Skip whitespace/comments before checking for 'where'
                for (; i < searchEnd; i++)
                {
                    char c = source[i];
                    char n = i + 1 < searchEnd ? source[i + 1] : '\0';
                    if (char.IsWhiteSpace(c)) continue;
                    if (c == '/' && n == '/') { while (i < searchEnd && source[i] != '\n') i++; continue; }
                    if (c == '/' && n == '*') { i += 2; while (i + 1 < searchEnd && !(source[i] == '*' && source[i + 1] == '/')) i++; i++; continue; }
                    break;
                }

                // Check word-boundary 'where'
                bool hasWhere = false;
                if (i + 5 <= searchEnd)
                {
                    hasWhere = source[i] == 'w' && source[i + 1] == 'h' && source[i + 2] == 'e' && source[i + 3] == 'r' && source[i + 4] == 'e';
                    if (hasWhere)
                    {
                        // Left boundary
                        if (i - 1 >= 0)
                        {
                            char lb = source[i - 1];
                            if (char.IsLetterOrDigit(lb) || lb == '_') hasWhere = false;
                        }
                        // Right boundary
                        if (hasWhere && i + 5 < searchEnd)
                        {
                            char rb = source[i + 5];
                            if (char.IsLetterOrDigit(rb) || rb == '_') hasWhere = false;
                        }
                    }
                }
                if (!hasWhere) break;

                // Advance past the entire where-constraint clause until we hit '{' or '=>' or ';'
                i += 5; // past 'where'
                while (i < searchEnd)
                {
                    char c = source[i];
                    char n = i + 1 < searchEnd ? source[i + 1] : '\0';
                    if (c == '{' || c == ';' || (c == '=' && n == '>')) break;
                    // Skip comments inline
                    if (c == '/' && n == '/') { while (i < searchEnd && source[i] != '\n') i++; continue; }
                    if (c == '/' && n == '*') { i += 2; while (i + 1 < searchEnd && !(source[i] == '*' && source[i + 1] == '/')) i++; i++; continue; }
                    i++;
                }
            }

            // Re-check for expression-bodied after constraints
            if (i < searchEnd - 1 && source[i] == '=' && source[i + 1] == '>')
            {
                // expression-bodied method: seek to terminating semicolon
                int j = i;
                bool done = false;
                while (j < searchEnd)
                {
                    char c = source[j];
                    if (c == ';') { done = true; break; }
                    j++;
                }
                if (!done) { why = "unterminated expression-bodied method"; return false; }
                start = attrStart; length = (j - attrStart) + 1; return true;
            }

            if (i >= searchEnd || source[i] != '{') { why = "no opening brace after method signature"; return false; }

            int depth = 0;
            int startSpan = attrStart;
            var bodyLexer = new CSharpLexer(source, i, searchEnd);
            while (bodyLexer.Advance(out char bc))
            {
                if (bodyLexer.InNonCode) continue;
                if (bc == '{') depth++;
                else if (bc == '}')
                {
                    depth--;
                    if (depth == 0) { start = startSpan; length = (bodyLexer.Position - 1 - startSpan) + 1; return true; }
                    if (depth < 0) { why = "brace underflow in method"; return false; }
                }
            }
            why = "unterminated method block"; return false;
        }

        private static int IndexOfTokenWithin(string s, string token, int start, int end)
        {
            int idx = s.IndexOf(token, start, StringComparison.Ordinal);
            return (idx >= 0 && idx < end) ? idx : -1;
        }

        private static bool TryFindClassInsertionPoint(string source, int classStart, int classLength, string position, out int insertAt, out string why)
        {
            insertAt = 0; why = null;
            int searchStart = classStart;
            int searchEnd = Math.Min(source.Length, classStart + classLength);

            if (position == "start")
            {
                // find first '{' after class header, insert just after with a newline
                int i = IndexOfTokenWithin(source, "{", searchStart, searchEnd);
                if (i < 0) { why = "could not find class opening brace"; return false; }
                insertAt = i + 1; return true;
            }
            else // end
            {
                // walk to matching closing brace of class and insert just before it
                int i = IndexOfTokenWithin(source, "{", searchStart, searchEnd);
                if (i < 0) { why = "could not find class opening brace"; return false; }
                int depth = 0;
                var lexer = new CSharpLexer(source, i, searchEnd);
                while (lexer.Advance(out char c))
                {
                    if (lexer.InNonCode) continue;
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0) { insertAt = lexer.Position - 1; return true; }
                        if (depth < 0) { why = "brace underflow while scanning class"; return false; }
                    }
                }
                why = "could not find class closing brace"; return false;
            }
        }
        private static int IndexOfClassToken(string s, string className)
        {
            var pattern = "class " + className;
            int searchFrom = 0;
            while (searchFrom < s.Length)
            {
                int idx = s.IndexOf(pattern, searchFrom, StringComparison.Ordinal);
                if (idx < 0) return -1;

                // Word boundary on left: char before "class" must not be letter/digit/_
                if (idx > 0)
                {
                    char left = s[idx - 1];
                    if (char.IsLetterOrDigit(left) || left == '_') { searchFrom = idx + 1; continue; }
                }

                // Word boundary on right: char after className must not be letter/digit/_
                int afterEnd = idx + pattern.Length;
                if (afterEnd < s.Length)
                {
                    char right = s[afterEnd];
                    if (char.IsLetterOrDigit(right) || right == '_') { searchFrom = idx + 1; continue; }
                }

                // Check that this position is not inside a string or comment
                var lexer = new CSharpLexer(s, 0, idx + 1);
                bool inNonCode = false;
                while (lexer.Advance(out _))
                {
                    inNonCode = lexer.InNonCode;
                }
                // After advancing to idx+1, check if the last character processed was in non-code
                if (inNonCode) { searchFrom = idx + 1; continue; }

                return idx;
            }
            return -1;
        }

        private static bool AppearsWithinNamespaceHeader(string s, int pos, string ns)
        {
            int from = Math.Max(0, pos - 2000);
            var slice = s.Substring(from, pos - from);
            return slice.Contains("namespace " + ns);
        }
    }
}
