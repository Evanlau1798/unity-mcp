using System;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    public static partial class ManageScript
    {
        private static ErrorResponse BuildMethodInsertion(string source, JObject op, out int insertAt, out string text)
        {
            insertAt = 0;
            text = null;
            string className = op.Value<string>("className");
            string ns = op.Value<string>("namespace");
            string position = (op.Value<string>("position") ?? "end").ToLowerInvariant();
            string snippet = ExtractReplacement(op);
            if (string.IsNullOrWhiteSpace(snippet))
                return new ErrorResponse("insert_method requires a non-empty 'replacement' text.");
            if (string.IsNullOrWhiteSpace(className))
                return new ErrorResponse("insert_method requires 'className'.");
            if (!TryComputeClassSpan(source, className, ns, out int classStart, out int classLength, out string whyClass))
                return new ErrorResponse($"insert_method failed to locate class: {whyClass}");

            if (position == "before" || position == "after")
            {
                string name = op.Value<string>(position + "MethodName");
                if (string.IsNullOrEmpty(name))
                    return new ErrorResponse($"insert_method with position='{position}' requires '{position}MethodName'.");
                if (!TryComputeMethodSpan(source, classStart, classLength, name,
                    op.Value<string>(position + "ReturnType"), op.Value<string>(position + "ParametersSignature"),
                    op.Value<string>(position + "AttributesContains"), out int start, out int length, out string whyMethod))
                    return new ErrorResponse($"insert_method({position}) failed to locate anchor method: {whyMethod}");
                if (position == "before")
                {
                    // Conditional attributes/branches need compiler-symbol context; never transfer them to the new method.
                    var lexer = new CSharpLexer(source, classStart, start);
                    while (lexer.Advance(out char c))
                        if (!lexer.InNonCode && c == '#' && System.Text.RegularExpressions.Regex.IsMatch(
                            source.Substring(lexer.Position - 1), @"\A#\s*(if|elif|else|endif)\b"))
                            return new ErrorResponse("insert_method(before) cannot safely anchor after conditional compilation directives. Use explicit text edits instead.");
                }
                insertAt = position == "after" ? start + length : IncludeMethodDocumentation(source, start, classStart);
            }
            else if (!TryFindClassInsertionPoint(source, classStart, classLength, position, out insertAt, out string whyInsert))
            {
                return new ErrorResponse($"insert_method failed: {whyInsert}");
            }

            text = NormalizeNewlines("\n\n" + snippet.TrimEnd() + "\n");
            return null;
        }

        private static int IncludeMethodDocumentation(string source, int start, int classStart)
        {
            while (start > classStart)
            {
                int end = start;
                while (end > classStart && char.IsWhiteSpace(source[end - 1])) end--;
                if (end <= classStart) break;
                int previousStart = source.LastIndexOf('\n', end - 1) + 1;
                string previous = source.Substring(previousStart, end - previousStart).Trim();
                if (previous.StartsWith("//", StringComparison.Ordinal))
                {
                    start = previousStart;
                    continue;
                }
                if (previous.EndsWith("*/", StringComparison.Ordinal))
                {
                    int blockEnd = source.LastIndexOf("*/", end - 1, StringComparison.Ordinal);
                    int blockStart = -1;
                    var lexer = new CSharpLexer(source, 0, blockEnd + 2);
                    while (lexer.Position < blockEnd)
                    {
                        int at = lexer.Position;
                        if (!lexer.Advance(out char ignored)) break;
                        // The lexer consumes two characters only at a real comment opener.
                        if (source[at] == '/' && source[at + 1] == '*' && lexer.Position == at + 2)
                            blockStart = at;
                    }
                    if (blockStart >= classStart && source[blockStart + 2] == '*'
                        && source.IndexOf("*/", blockStart, StringComparison.Ordinal) == blockEnd)
                    {
                        int lineStart = source.LastIndexOf('\n', blockStart) + 1;
                        if (string.IsNullOrWhiteSpace(source.Substring(lineStart, blockStart - lineStart)))
                        {
                            start = lineStart;
                            continue;
                        }
                    }
                }
                break;
            }
            return start;
        }
    }
}
