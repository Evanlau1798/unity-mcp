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
        /// Gets the validation level from the GUI settings
        /// </summary>
        private static ValidationLevel GetValidationLevelFromGUI()
        {
            int savedLevel = EditorPrefs.GetInt(EditorPrefKeys.ValidationLevel, (int)ValidationLevel.Standard);
            return (ValidationLevel)Mathf.Clamp(savedLevel, 0, 3);
        }

        /// <summary>
        /// Validates C# script syntax using multiple validation layers.
        /// </summary>
        private static bool ValidateScriptSyntax(string contents)
        {
            return ValidateScriptSyntax(contents, ValidationLevel.Standard, out _);
        }

        /// <summary>
        /// Advanced syntax validation with detailed diagnostics and configurable strictness.
        /// </summary>
        private static bool ValidateScriptSyntax(string contents, ValidationLevel level, out string[] errors)
        {
            var errorList = new System.Collections.Generic.List<string>();
            errors = null;

            if (string.IsNullOrEmpty(contents))
            {
                return true; // Empty content is valid
            }

            // Basic structural validation: check balanced delimiters
            if (!CheckBalancedDelimiters(contents, out int errLine, out char errExpected))
            {
                errorList.Add($"ERROR: Unbalanced delimiter at line {errLine} (expected '{errExpected}')");
                errors = errorList.ToArray();
                return false;
            }

            // Basic structural validation: check for duplicate method signatures
            CheckDuplicateMethodSignatures(contents, errorList);

#if USE_ROSLYN
            // Advanced Roslyn-based validation: only run for Standard+; fail on Roslyn errors
            if (level >= ValidationLevel.Standard)
            {
                if (!ValidateScriptSyntaxRoslyn(contents, level, errorList))
                {
                    errors = errorList.ToArray();
                    return false;
                }
            }
#endif

            // Unity-specific validation
            if (level >= ValidationLevel.Standard)
            {
                ValidateScriptSyntaxUnity(contents, errorList);
            }

            // Semantic analysis for common issues
            if (level >= ValidationLevel.Comprehensive)
            {
                ValidateSemanticRules(contents, errorList);
            }

#if USE_ROSLYN
            // Full semantic compilation validation for Strict level
            if (level == ValidationLevel.Strict)
            {
                if (!ValidateScriptSemantics(contents, errorList))
                {
                    errors = errorList.ToArray();
                    return false; // Strict level fails on any semantic errors
                }
            }
#endif

            errors = errorList.ToArray();
            return errorList.Count == 0 || (level != ValidationLevel.Strict && !errorList.Any(e => e.StartsWith("ERROR:")));
        }

        /// <summary>
        /// Validation strictness levels
        /// </summary>
        private enum ValidationLevel
        {
            Basic,        // Only syntax errors
            Standard,     // Syntax + Unity best practices
            Comprehensive, // All checks + semantic analysis
            Strict        // Treat all issues as errors
        }

        /// <summary>
        /// Validates Unity-specific coding rules and best practices
        /// //TODO: Naive Unity Checks and not really yield any results, need to be improved
        /// </summary>
        private static void ValidateScriptSyntaxUnity(string contents, System.Collections.Generic.List<string> errors)
        {
            // Check for common Unity anti-patterns
            if (contents.Contains("FindObjectOfType") && contents.Contains("Update()"))
            {
                errors.Add("WARNING: FindObjectOfType in Update() can cause performance issues");
            }

            if (contents.Contains("GameObject.Find") && contents.Contains("Update()"))
            {
                errors.Add("WARNING: GameObject.Find in Update() can cause performance issues");
            }

            // Check for proper MonoBehaviour usage
            if (contents.Contains(": MonoBehaviour") && !contents.Contains("using UnityEngine"))
            {
                errors.Add("WARNING: MonoBehaviour requires 'using UnityEngine;'");
            }

            // Check for SerializeField usage
            if (contents.Contains("[SerializeField]") && !contents.Contains("using UnityEngine"))
            {
                errors.Add("WARNING: SerializeField requires 'using UnityEngine;'");
            }

            // Check for proper coroutine usage
            if (contents.Contains("StartCoroutine") && !contents.Contains("IEnumerator"))
            {
                errors.Add("WARNING: StartCoroutine typically requires IEnumerator methods");
            }

            // Check for Update without FixedUpdate for physics
            if (contents.Contains("Rigidbody") && contents.Contains("Update()") && !contents.Contains("FixedUpdate()"))
            {
                errors.Add("WARNING: Consider using FixedUpdate() for Rigidbody operations");
            }

            // Check for missing null checks on Unity objects
            if (contents.Contains("GetComponent<") && !contents.Contains("!= null"))
            {
                errors.Add("WARNING: Consider null checking GetComponent results");
            }

            // Check for proper event function signatures
            if (contents.Contains("void Start(") && !contents.Contains("void Start()"))
            {
                errors.Add("WARNING: Start() should not have parameters");
            }

            if (contents.Contains("void Update(") && !contents.Contains("void Update()"))
            {
                errors.Add("WARNING: Update() should not have parameters");
            }

            // Check for inefficient string operations
            if (contents.Contains("Update()") && contents.Contains("\"") && contents.Contains("+"))
            {
                errors.Add("WARNING: String concatenation in Update() can cause garbage collection issues");
            }

        }

        /// <summary>
        /// Checks for duplicate method signatures (same name + param types + scope).
        /// Catches corruption from anchor_replace overshoot and similar edit mistakes.
        /// Runs at Basic level since duplicates are structural errors, not style warnings.
        /// </summary>
        private static void CheckDuplicateMethodSignatures(string contents, System.Collections.Generic.List<string> errors)
        {
            // Step 1: Build a code-only view (comments/strings replaced with spaces)
            var codeChars = contents.ToCharArray();
            {
                var lexer = new CSharpLexer(contents);
                while (true)
                {
                    int startPos = lexer.Position;
                    if (!lexer.Advance(out _)) break;
                    if (lexer.InNonCode)
                    {
                        for (int i = startPos; i < lexer.Position && i < codeChars.Length; i++)
                            if (codeChars[i] != '\n') codeChars[i] = ' ';
                    }
                }
            }
            var codeOnly = new string(codeChars);

            // Step 2: Build containing type name at each position via single pass
            var typePattern = new Regex(
                @"\b(?:class|struct|interface|record)\s+(\w+)",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            // Map opening-brace position -> fully qualified type name
            var typeBraceMap = new System.Collections.Generic.Dictionary<int, string>();
            {
                var typeMatches = typePattern.Matches(codeOnly);
                // Pre-scan: for each type declaration, find its opening brace and record full name
                // We need to process in order and track nesting, so do a single forward pass
                var preStack = new System.Collections.Generic.List<(string name, int openDepth)>();
                int preBd = 0;
                int nextTm = 0;
                for (int i = 0; i < codeOnly.Length; i++)
                {
                    while (nextTm < typeMatches.Count && typeMatches[nextTm].Index == i)
                    {
                        int bp = codeOnly.IndexOf('{', typeMatches[nextTm].Index + typeMatches[nextTm].Length);
                        if (bp >= 0)
                        {
                            string tn = typeMatches[nextTm].Groups[1].Value;
                            string fn = preStack.Count > 0 ? preStack[preStack.Count - 1].name + "." + tn : tn;
                            typeBraceMap[bp] = fn;
                        }
                        nextTm++;
                    }
                    if (codeOnly[i] == '{')
                    {
                        if (typeBraceMap.TryGetValue(i, out string mappedType))
                            preStack.Add((mappedType, preBd));
                        preBd++;
                    }
                    else if (codeOnly[i] == '}')
                    {
                        preBd = Math.Max(0, preBd - 1);
                        if (preStack.Count > 0 && preStack[preStack.Count - 1].openDepth == preBd)
                            preStack.RemoveAt(preStack.Count - 1);
                    }
                }
            }
            // Second pass: build per-position containing type array
            var containingTypeArr = new string[codeOnly.Length];
            var braceDepthArr = new int[codeOnly.Length];
            var typeMemberDepthArr = new int[codeOnly.Length];
            {
                var stack = new System.Collections.Generic.List<(string name, int openDepth)>();
                int bd2 = 0;
                string current = "";
                for (int i = 0; i < codeOnly.Length; i++)
                {
                    containingTypeArr[i] = current;
                    braceDepthArr[i] = bd2;
                    typeMemberDepthArr[i] = stack.Count > 0 ? stack[stack.Count - 1].openDepth + 1 : -1;

                    if (codeOnly[i] == '{')
                    {
                        if (typeBraceMap.TryGetValue(i, out string tn))
                        {
                            stack.Add((tn, bd2));
                            current = tn;
                        }
                        bd2++;
                    }
                    else if (codeOnly[i] == '}')
                    {
                        bd2 = Math.Max(0, bd2 - 1);
                        if (stack.Count > 0 && stack[stack.Count - 1].openDepth == bd2)
                        {
                            stack.RemoveAt(stack.Count - 1);
                            current = stack.Count > 0 ? stack[stack.Count - 1].name : "";
                        }
                    }
                }
            }

            // Step 3: Match method signatures on code-only text (includes => for expression-bodied)
            var methodSigPattern = new Regex(
                @"(?:(?:public|private|protected|internal)\s+)?(?:(?:static|virtual|override|abstract|sealed|async|new)\s+)*(\S+)\s+(\w+)\s*\(([^)]*)\)\s*(?:where\s+\S+\s*:\s*\S+\s*)?(?:[{;]|=>)",
                RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            var sigMatches = methodSigPattern.Matches(codeOnly);
            var seen = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
            foreach (Match sm in sigMatches)
            {
                string returnType = sm.Groups[1].Value;
                string methodName = sm.Groups[2].Value;
                if (string.Equals(returnType, "new", StringComparison.Ordinal)) continue; // constructor invocation, not a method declaration
                // A punctuation "return type" means this match is a CALL, not a declaration:
                // the opening brace of a method body ("{ Foo();") or an expression-bodied
                // member ("=> Foo();"). Both sit at class-member depth, so the brace-depth
                // guard below cannot reject them.
                if (returnType.Length == 0) continue;
                char returnTypeStart = returnType[0];
                if (!char.IsLetter(returnTypeStart) && returnTypeStart != '_'
                    && returnTypeStart != '@' && returnTypeStart != '(') continue;
                if (IsCSharpKeyword(methodName)) continue;
                int paramCount = CountTopLevelParams(sm.Groups[3].Value);
                string paramTypes = ExtractParamTypes(sm.Groups[3].Value);
                string containingType = containingTypeArr[sm.Index];
                if (string.IsNullOrEmpty(containingType)) continue;
                if (braceDepthArr[sm.Index] != typeMemberDepthArr[sm.Index]) continue;
                string key = $"{containingType}/{methodName}/{paramCount}/{paramTypes}";
                if (seen.TryGetValue(key, out _))
                    errors.Add($"ERROR: Duplicate method signature detected: '{methodName}' with {paramCount} parameter(s). This may indicate a corrupted edit.");
                else
                    seen[key] = 1;
            }
        }

        private static readonly System.Collections.Generic.HashSet<string> CSharpKeywords =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
            {
                "if", "else", "for", "foreach", "while", "do", "switch", "case",
                "try", "catch", "finally", "throw", "return", "yield", "await",
                "lock", "using", "fixed", "checked", "unchecked", "typeof", "sizeof",
                "nameof", "default", "new", "stackalloc", "when", "in", "is", "as",
                "ref", "out", "params", "this", "base", "null", "true", "false",
                "get", "set", "var", "dynamic", "where", "from", "select", "group",
                "into", "orderby", "join", "let", "on", "equals", "by", "ascending",
                "descending"
            };

        private static bool IsCSharpKeyword(string name) => CSharpKeywords.Contains(name);

        /// <summary>
        /// Validates semantic rules and common coding issues
        /// </summary>
        private static void ValidateSemanticRules(string contents, System.Collections.Generic.List<string> errors)
        {
            // Check for potential memory leaks
            if (contents.Contains("new ") && contents.Contains("Update()"))
            {
                errors.Add("WARNING: Creating objects in Update() may cause memory issues");
            }

            // Check for magic numbers
            var magicNumberPattern = new Regex(@"\b\d+\.?\d*f?\b(?!\s*[;})\]])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            var matches = magicNumberPattern.Matches(contents);
            if (matches.Count > 5)
            {
                errors.Add("WARNING: Consider using named constants instead of magic numbers");
            }

            // Check for long methods (simple line count check)
            var methodPattern = new Regex(@"(public|private|protected|internal)?\s*(static)?\s*\w+\s+\w+\s*\([^)]*\)\s*{", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            var methodMatches = methodPattern.Matches(contents);
            foreach (Match match in methodMatches)
            {
                int startIndex = match.Index;
                int braceCount = 0;
                int lineCount = 0;
                bool inMethod = false;

                for (int i = startIndex; i < contents.Length; i++)
                {
                    if (contents[i] == '{')
                    {
                        braceCount++;
                        inMethod = true;
                    }
                    else if (contents[i] == '}')
                    {
                        braceCount--;
                        if (braceCount == 0 && inMethod)
                            break;
                    }
                    else if (contents[i] == '\n' && inMethod)
                    {
                        lineCount++;
                    }
                }

                if (lineCount > 50)
                {
                    errors.Add("WARNING: Method is very long, consider breaking it into smaller methods");
                    break; // Only report once
                }
            }

            // Check for proper exception handling
            if (contents.Contains("catch") && contents.Contains("catch()"))
            {
                errors.Add("WARNING: Empty catch blocks should be avoided");
            }

            // Check for proper async/await usage
            if (contents.Contains("async ") && !contents.Contains("await"))
            {
                errors.Add("WARNING: Async method should contain await or return Task");
            }

            // Check for hardcoded tags and layers
            if (contents.Contains("\"Player\"") || contents.Contains("\"Enemy\""))
            {
                errors.Add("WARNING: Consider using constants for tags instead of hardcoded strings");
            }
        }

        //TODO: A easier way for users to update incorrect scripts (now duplicated with the updateScript method and need to also update server side, put aside for now)
        /// <summary>
        /// Public method to validate script syntax with configurable validation level
        /// Returns detailed validation results including errors and warnings
        /// </summary>
        // public static object ValidateScript(JObject @params)
        // {
        //     string contents = @params["contents"]?.ToString();
        //     string validationLevel = @params["validationLevel"]?.ToString() ?? "standard";

        //     if (string.IsNullOrEmpty(contents))
        //     {
        //         return new ErrorResponse("Contents parameter is required for validation.");
        //     }

        //     // Parse validation level
        //     ValidationLevel level = ValidationLevel.Standard;
        //     switch (validationLevel.ToLower())
        //     {
        //         case "basic": level = ValidationLevel.Basic; break;
        //         case "standard": level = ValidationLevel.Standard; break;
        //         case "comprehensive": level = ValidationLevel.Comprehensive; break;
        //         case "strict": level = ValidationLevel.Strict; break;
        //         default:
        //             return new ErrorResponse($"Invalid validation level: '{validationLevel}'. Valid levels are: basic, standard, comprehensive, strict.");
        //     }

        //     // Perform validation
        //     bool isValid = ValidateScriptSyntax(contents, level, out string[] validationErrors);

        //     var errors = validationErrors?.Where(e => e.StartsWith("ERROR:")).ToArray() ?? new string[0];
        //     var warnings = validationErrors?.Where(e => e.StartsWith("WARNING:")).ToArray() ?? new string[0];

        //     var result = new
        //     {
        //         isValid = isValid,
        //         validationLevel = validationLevel,
        //         errorCount = errors.Length,
        //         warningCount = warnings.Length,
        //         errors = errors,
        //         warnings = warnings,
        //         summary = isValid
        //             ? (warnings.Length > 0 ? $"Validation passed with {warnings.Length} warnings" : "Validation passed with no issues")
        //             : $"Validation failed with {errors.Length} errors and {warnings.Length} warnings"
        //     };

        //     if (isValid)
        //     {
        //         return new SuccessResponse("Script validation completed successfully.", result);
        //     }
        //     else
        //     {
        //         return new ErrorResponse("Script validation failed.", result);
        //     }
        // }
    }
}
