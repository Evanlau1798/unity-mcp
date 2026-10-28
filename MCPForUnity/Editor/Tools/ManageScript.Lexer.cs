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
        /// Lightweight lexer that tracks whether the current position is inside a
        /// string literal (regular, verbatim, interpolated, raw) or a comment.
        /// Callers advance character-by-character and check <see cref="InNonCode"/>.
        /// </summary>
        private struct CSharpLexer
        {
            private readonly string _text;
            private int _pos;
            private readonly int _end;
            private int _line;

            // String/comment state
            private bool _inSingleComment;
            private bool _inMultiComment;

            public CSharpLexer(string text, int start = 0, int end = -1)
            {
                _text = text;
                _pos = start;
                _end = end < 0 ? text.Length : end;
                _line = 1;
                // count newlines before start
                for (int i = 0; i < start && i < text.Length; i++)
                    if (text[i] == '\n') _line++;
                _inSingleComment = false;
                _inMultiComment = false;
                InNonCode = false;
            }

            public bool InNonCode { get; private set; }
            public int Position => _pos;
            public int Line => _line;

            /// <summary>
            /// Advance to the next character, updating all state.
            /// Returns false at end of range.
            /// </summary>
            public bool Advance(out char c)
            {
                if (_pos >= _end) { c = '\0'; return false; }

                c = _text[_pos];
                char next = _pos + 1 < _end ? _text[_pos + 1] : '\0';

                if (c == '\n')
                {
                    _line++;
                    if (_inSingleComment) _inSingleComment = false;
                }

                // Inside single-line comment
                if (_inSingleComment) { InNonCode = true; _pos++; return true; }

                // Inside multi-line comment
                if (_inMultiComment)
                {
                    if (c == '*' && next == '/') { _inMultiComment = false; InNonCode = true; _pos += 2; c = '/'; return true; }
                    InNonCode = true; _pos++; return true;
                }

                // Start of comment
                if (c == '/' && next == '/') { _inSingleComment = true; InNonCode = true; _pos += 2; return true; }
                if (c == '/' && next == '*') { _inMultiComment = true; InNonCode = true; _pos += 2; return true; }

                // Interpolated raw string: $"""...""" or $$"""...""" etc. (C# 11)
                // Must check BEFORE regular $" and BEFORE plain """
                if (c == '$')
                {
                    int dollarCount = 1;
                    while (_pos + dollarCount < _end && _text[_pos + dollarCount] == '$') dollarCount++;
                    int afterDollars = _pos + dollarCount;
                    if (afterDollars + 2 < _end && _text[afterDollars] == '"' && _text[afterDollars + 1] == '"' && _text[afterDollars + 2] == '"')
                    {
                        int q = 3;
                        while (afterDollars + q < _end && _text[afterDollars + q] == '"') q++;
                        _pos = afterDollars + q; // past all opening quotes
                        SkipInterpolatedRawStringBody(dollarCount, q);
                        InNonCode = true; return true;
                    }
                }

                // Raw string literal: """...""" (C# 11, non-interpolated)
                if (c == '"' && next == '"' && _pos + 2 < _end && _text[_pos + 2] == '"')
                {
                    int q = 3;
                    while (_pos + q < _end && _text[_pos + q] == '"') q++;
                    _pos += q; // past opening quotes
                    int closeCount = 0;
                    while (_pos < _end)
                    {
                        if (_text[_pos] == '\n') _line++;
                        if (_text[_pos] == '"') { closeCount++; if (closeCount >= q) { _pos++; break; } }
                        else closeCount = 0;
                        _pos++;
                    }
                    InNonCode = true; return true;
                }

                // Interpolated string: $"..." or $@"..." or @$"..."
                if ((c == '$' && next == '"') ||
                    (c == '$' && next == '@' && _pos + 2 < _end && _text[_pos + 2] == '"') ||
                    (c == '@' && next == '$' && _pos + 2 < _end && _text[_pos + 2] == '"'))
                {
                    bool isVerbatim = (next == '@') || (c == '@');
                    _pos += (c == '$' && next == '"') ? 2 : 3;
                    SkipInterpolatedStringBody(isVerbatim);
                    InNonCode = true; return true;
                }

                // Verbatim string: @"..."
                if (c == '@' && next == '"')
                {
                    _pos += 2;
                    while (_pos < _end)
                    {
                        if (_text[_pos] == '\n') _line++;
                        if (_text[_pos] == '"')
                        {
                            if (_pos + 1 < _end && _text[_pos + 1] == '"') { _pos += 2; continue; }
                            _pos++; break;
                        }
                        _pos++;
                    }
                    InNonCode = true; return true;
                }

                // Regular string: "..."
                if (c == '"')
                {
                    _pos++;
                    while (_pos < _end)
                    {
                        if (_text[_pos] == '\\') { _pos += 2; continue; }
                        if (_text[_pos] == '"') { _pos++; break; }
                        if (_text[_pos] == '\n') _line++;
                        _pos++;
                    }
                    InNonCode = true; return true;
                }

                // Char literal: '...'
                if (c == '\'')
                {
                    _pos++;
                    while (_pos < _end)
                    {
                        if (_text[_pos] == '\\') { _pos += 2; continue; }
                        if (_text[_pos] == '\'') { _pos++; break; }
                        _pos++;
                    }
                    InNonCode = true; return true;
                }

                InNonCode = false;
                _pos++;
                return true;
            }

            /// <summary>
            /// Skip the body of an interpolated string, handling nested interpolation holes.
            /// _pos should be right after the opening quote.
            /// </summary>
            private void SkipInterpolatedStringBody(bool isVerbatim)
            {
                int interpDepth = 0;
                while (_pos < _end)
                {
                    char ch = _text[_pos];
                    if (ch == '\n') _line++;

                    if (interpDepth > 0)
                    {
                        // Inside interpolation hole — this is code, scan for nested strings/braces
                        if (ch == '{') { interpDepth++; _pos++; continue; }
                        if (ch == '}') { interpDepth--; _pos++; continue; }
                        if (ch == '"')
                        {
                            // Nested string inside interpolation hole
                            _pos++;
                            while (_pos < _end)
                            {
                                if (_text[_pos] == '\\') { _pos += 2; continue; }
                                if (_text[_pos] == '"') { _pos++; break; }
                                if (_text[_pos] == '\n') _line++;
                                _pos++;
                            }
                            continue;
                        }
                        if (ch == '/' && _pos + 1 < _end)
                        {
                            if (_text[_pos + 1] == '/') { _pos += 2; while (_pos < _end && _text[_pos] != '\n') _pos++; continue; }
                            if (_text[_pos + 1] == '*') { _pos += 2; while (_pos + 1 < _end && !(_text[_pos] == '*' && _text[_pos + 1] == '/')) { if (_text[_pos] == '\n') _line++; _pos++; } if (_pos + 1 < _end) _pos += 2; continue; }
                        }
                        if (ch == '\'') { _pos++; while (_pos < _end) { if (_text[_pos] == '\\') { _pos += 2; continue; } if (_text[_pos] == '\'') { _pos++; break; } _pos++; } continue; }
                        _pos++;
                        continue;
                    }

                    // interpDepth == 0: inside string content
                    if (ch == '{')
                    {
                        if (_pos + 1 < _end && _text[_pos + 1] == '{') { _pos += 2; continue; } // escaped {{
                        interpDepth = 1; _pos++; continue;
                    }
                    if (ch == '}')
                    {
                        if (_pos + 1 < _end && _text[_pos + 1] == '}') { _pos += 2; continue; } // escaped }}
                        // Stray } at depth 0 — shouldn't happen in valid code, just advance
                        _pos++; continue;
                    }
                    if (ch == '"')
                    {
                        if (isVerbatim && _pos + 1 < _end && _text[_pos + 1] == '"') { _pos += 2; continue; } // doubled quote
                        _pos++; return; // closing quote
                    }
                    if (!isVerbatim && ch == '\\') { _pos += 2; continue; } // escape in regular interpolated
                    _pos++;
                }
            }

            /// <summary>
            /// Skip the body of an interpolated raw string ($"""...""", $$"""...""", etc.).
            /// dollarCount determines how many consecutive { start an interpolation hole.
            /// quoteCount is the number of " that close the string.
            /// _pos should be right after the opening quotes.
            /// </summary>
            private void SkipInterpolatedRawStringBody(int dollarCount, int quoteCount)
            {
                int interpDepth = 0;
                while (_pos < _end)
                {
                    char ch = _text[_pos];
                    if (ch == '\n') _line++;

                    if (interpDepth > 0)
                    {
                        // Inside interpolation hole — code context
                        if (ch == '{') { interpDepth++; _pos++; continue; }
                        if (ch == '}') { interpDepth--; _pos++; continue; }
                        if (ch == '"')
                        {
                            _pos++;
                            while (_pos < _end)
                            {
                                if (_text[_pos] == '\\') { _pos += 2; continue; }
                                if (_text[_pos] == '"') { _pos++; break; }
                                if (_text[_pos] == '\n') _line++;
                                _pos++;
                            }
                            continue;
                        }
                        if (ch == '/' && _pos + 1 < _end)
                        {
                            if (_text[_pos + 1] == '/') { _pos += 2; while (_pos < _end && _text[_pos] != '\n') _pos++; continue; }
                            if (_text[_pos + 1] == '*') { _pos += 2; while (_pos + 1 < _end && !(_text[_pos] == '*' && _text[_pos + 1] == '/')) { if (_text[_pos] == '\n') _line++; _pos++; } if (_pos + 1 < _end) _pos += 2; continue; }
                        }
                        if (ch == '\'') { _pos++; while (_pos < _end) { if (_text[_pos] == '\\') { _pos += 2; continue; } if (_text[_pos] == '\'') { _pos++; break; } _pos++; } continue; }
                        _pos++;
                        continue;
                    }

                    // String content (interpDepth == 0)
                    // Check for closing quote sequence
                    if (ch == '"')
                    {
                        int qc = 1;
                        while (_pos + qc < _end && _text[_pos + qc] == '"') qc++;
                        if (qc >= quoteCount) { _pos += quoteCount; return; }
                        // Fewer quotes than needed — literal content
                        _pos += qc;
                        continue;
                    }

                    // Check for interpolation hole: dollarCount consecutive {'s
                    if (ch == '{')
                    {
                        int bc = 1;
                        while (_pos + bc < _end && _text[_pos + bc] == '{') bc++;
                        if (bc >= dollarCount)
                        {
                            // Exactly dollarCount opens an interpolation hole; extras are literal
                            _pos += dollarCount;
                            interpDepth = 1;
                        }
                        else
                        {
                            // Fewer than dollarCount — literal braces
                            _pos += bc;
                        }
                        continue;
                    }

                    // Closing braces with dollarCount threshold — literal if fewer
                    if (ch == '}')
                    {
                        int bc = 1;
                        while (_pos + bc < _end && _text[_pos + bc] == '}') bc++;
                        _pos += bc; // all literal at depth 0
                        continue;
                    }

                    _pos++;
                }
            }
        }

        private static bool CheckBalancedDelimiters(string text, out int line, out char expected)
        {
            var braceStack = new Stack<int>();
            var parenStack = new Stack<int>();
            var bracketStack = new Stack<int>();
            line = 1; expected = '\0';

            var lexer = new CSharpLexer(text);
            while (lexer.Advance(out char c))
            {
                if (lexer.InNonCode) continue;

                switch (c)
                {
                    case '{': braceStack.Push(lexer.Line); break;
                    case '}':
                        if (braceStack.Count == 0) { line = lexer.Line; expected = '{'; return false; }
                        braceStack.Pop();
                        break;
                    case '(': parenStack.Push(lexer.Line); break;
                    case ')':
                        if (parenStack.Count == 0) { line = lexer.Line; expected = '('; return false; }
                        parenStack.Pop();
                        break;
                    case '[': bracketStack.Push(lexer.Line); break;
                    case ']':
                        if (bracketStack.Count == 0) { line = lexer.Line; expected = '['; return false; }
                        bracketStack.Pop();
                        break;
                }
            }

            if (braceStack.Count > 0) { line = braceStack.Peek(); expected = '}'; return false; }
            if (parenStack.Count > 0) { line = parenStack.Peek(); expected = ')'; return false; }
            if (bracketStack.Count > 0) { line = bracketStack.Peek(); expected = ']'; return false; }

            return true;
        }

    }
}
