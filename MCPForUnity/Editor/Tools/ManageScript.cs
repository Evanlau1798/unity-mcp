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
    /// <summary>
    /// Handles CRUD operations for C# scripts within the Unity project.
    /// </summary>
    /// <remarks>
    /// ROSLYN INSTALLATION GUIDE:
    /// To enable advanced syntax validation with Roslyn compiler services:
    /// 
    /// 1. Install Microsoft.CodeAnalysis.CSharp NuGet package:
    ///    - Open Package Manager in Unity
    ///    - Follow the instruction on https://github.com/GlitchEnzo/NuGetForUnity
    ///    
    /// 2. Open NuGet Package Manager and Install Microsoft.CodeAnalysis.CSharp:
    ///    
    /// 3. Alternative: Manual DLL installation:
    ///    - Download Microsoft.CodeAnalysis.CSharp.dll and dependencies
    ///    - Place in Assets/Plugins/ folder
    ///    - Ensure .NET compatibility settings are correct
    ///    
    /// 4. Define USE_ROSLYN symbol:
    ///    - Go to Player Settings > Scripting Define Symbols
    ///    - Add "USE_ROSLYN" to enable Roslyn-based validation
    ///    
    /// 5. Restart Unity after installation
    /// 
    /// Note: Without Roslyn, the system falls back to basic structural validation.
    /// Roslyn provides full C# compiler diagnostics with line numbers and detailed error messages.
    /// </remarks>
    [McpForUnityTool("manage_script", AutoRegister = false)]
    public static partial class ManageScript
    {
        /// <summary>
        /// Resolves a directory under Assets/, preventing traversal and escaping.
        /// Returns fullPathDir on disk and canonical 'Assets/...' relative path.
        /// </summary>
        private static bool TryResolveUnderAssets(string relDir, out string fullPathDir, out string relPathSafe)
        {
            string assets = AssetPathUtility.NormalizeSeparators(Application.dataPath);

            // Normalize caller path: allow both "Scripts/..." and "Assets/Scripts/..."
            string rel = AssetPathUtility.NormalizeSeparators(relDir ?? "Scripts").Trim();
            if (string.IsNullOrEmpty(rel)) rel = "Scripts";

            // Handle both "Assets" and "Assets/" prefixes
            if (rel.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                rel = string.Empty;
            }
            else if (rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                rel = rel.Substring(7);
            }

            rel = rel.TrimStart('/');

            string targetDir = AssetPathUtility.NormalizeSeparators(Path.Combine(assets, rel));
            string full = AssetPathUtility.NormalizeSeparators(Path.GetFullPath(targetDir));

            bool underAssets = full.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(full, assets, StringComparison.OrdinalIgnoreCase);
            if (!underAssets)
            {
                fullPathDir = null;
                relPathSafe = null;
                return false;
            }

            // Best-effort symlink guard: if the directory OR ANY ANCESTOR (up to Assets/) is a reparse point/symlink, reject
            try
            {
                var di = new DirectoryInfo(full);
                while (di != null)
                {
                    if (di.Exists && (di.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        fullPathDir = null;
                        relPathSafe = null;
                        return false;
                    }
                    var atAssets = string.Equals(
                        di.FullName.Replace('\\', '/'),
                        assets,
                        StringComparison.OrdinalIgnoreCase
                    );
                    if (atAssets) break;
                    di = di.Parent;
                }
            }
            catch { /* best effort; proceed */ }

            fullPathDir = full;
            string tail = full.Length > assets.Length ? full.Substring(assets.Length).TrimStart('/') : string.Empty;
            relPathSafe = ("Assets/" + tail).TrimEnd('/');
            return true;
        }
        /// <summary>
        /// Main handler for script management actions.
        /// </summary>
        public static object HandleCommand(JObject @params)
        {
            // Handle null parameters
            if (@params == null)
            {
                return new ErrorResponse("invalid_params", "Parameters cannot be null.");
            }

            var p = new ToolParams(@params);

            // Extract and validate required parameters
            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
            {
                return new ErrorResponse(actionResult.ErrorMessage);
            }
            string action = actionResult.Value.ToLowerInvariant();

            var nameResult = p.GetRequired("name");
            if (!nameResult.IsSuccess)
            {
                return new ErrorResponse(nameResult.ErrorMessage);
            }
            string name = nameResult.Value;

            // Optional parameters
            string path = p.Get("path"); // Relative to Assets/
            // If the caller passed a full file path (e.g. "Assets/Scripts/Foo.cs"),
            // strip the filename so path is treated as a directory.
            if (path != null && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                path = Path.GetDirectoryName(path)?.Replace('\\', '/');
            }
            string contents = null;

            // Check if we have base64 encoded contents
            bool contentsEncoded = p.GetBool("contentsEncoded", false);
            if (contentsEncoded && p.Has("encodedContents"))
            {
                try
                {
                    contents = DecodeBase64(p.Get("encodedContents"));
                }
                catch (Exception e)
                {
                    return new ErrorResponse($"Failed to decode script contents: {e.Message}");
                }
            }
            else
            {
                contents = p.Get("contents");
            }

            string scriptType = p.Get("scriptType"); // For templates/validation
            string namespaceName = p.Get("namespace"); // For organizing code
            // Basic name validation (alphanumeric, underscores, cannot start with number)
            if (!Regex.IsMatch(name, @"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
            {
                return new ErrorResponse(
                    $"Invalid script name: '{name}'. Use only letters, numbers, underscores, and don't start with a number."
                );
            }

            // Resolve and harden target directory under Assets/
            if (!TryResolveUnderAssets(path, out string fullPathDir, out string relPathSafeDir))
            {
                return new ErrorResponse($"Invalid path. Target directory must be within 'Assets/'. Provided: '{(path ?? "(null)")}'");
            }

            // Construct file paths
            string scriptFileName = $"{name}.cs";
            string fullPath = Path.Combine(fullPathDir, scriptFileName);
            string relativePath = AssetPathUtility.NormalizeSeparators(Path.Combine(relPathSafeDir, scriptFileName));

            // Ensure the target directory exists for create/update
            if (action == "create" || action == "update")
            {
                try
                {
                    Directory.CreateDirectory(fullPathDir);
                }
                catch (Exception e)
                {
                    return new ErrorResponse(
                        $"Could not create directory '{fullPathDir}': {e.Message}"
                    );
                }
            }

            // Route to specific action handlers
            switch (action)
            {
                case "create":
                    return CreateScript(
                        fullPath,
                        relativePath,
                        name,
                        contents,
                        scriptType,
                        namespaceName
                    );
                case "read":
                    McpLog.Warn("manage_script.read is deprecated; prefer resources/read. Serving read for backward compatibility.");
                    return ReadScript(fullPath, relativePath);
                case "update":
                    McpLog.Warn("manage_script.update is deprecated; prefer apply_text_edits. Serving update for backward compatibility.");
                    return UpdateScript(fullPath, relativePath, name, contents);
                case "delete":
                    return DeleteScript(fullPath, relativePath);
                case "apply_text_edits":
                    {
                        var textEdits = p.GetRaw("edits") as JArray;
                        string precondition = p.Get("precondition_sha256");
                        // Respect optional options (guard type before indexing)
                        var optionsObj = p.GetRaw("options") as JObject;
                        string refreshOpt = optionsObj?["refresh"]?.ToString()?.ToLowerInvariant();
                        string validateOpt = optionsObj?["validate"]?.ToString()?.ToLowerInvariant();
                        return ApplyTextEdits(fullPath, relativePath, name, textEdits, precondition, refreshOpt, validateOpt);
                    }
                case "validate":
                    {
                        string level = p.Get("level", "standard").ToLowerInvariant();
                        var chosen = level switch
                        {
                            "basic" => ValidationLevel.Basic,
                            "standard" => ValidationLevel.Standard,
                            "strict" => ValidationLevel.Strict,
                            "comprehensive" => ValidationLevel.Comprehensive,
                            _ => ValidationLevel.Standard
                        };
                        string fileText;
                        try { fileText = File.ReadAllText(fullPath); }
                        catch (Exception ex) { return new ErrorResponse($"Failed to read script: {ex.Message}"); }

                        bool ok = ValidateScriptSyntax(fileText, chosen, out string[] diagsRaw);
                        var diags = (diagsRaw ?? Array.Empty<string>()).Select(s =>
                        {
                            var m = Regex.Match(
                                s,
                                @"^(ERROR|WARNING|INFO): (.*?)(?: \(Line (\d+)\))?$",
                                RegexOptions.CultureInvariant | RegexOptions.Multiline,
                                TimeSpan.FromMilliseconds(250)
                            );
                            string severity = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "info";
                            string message = m.Success ? m.Groups[2].Value : s;
                            int lineNum = m.Success && int.TryParse(m.Groups[3].Value, out var l) ? l : 0;
                            return new { line = lineNum, col = 0, severity, message };
                        }).ToArray();

                        var result = new { diagnostics = diags };
                        return ok ? new SuccessResponse("Validation completed.", result)
                                   : new ErrorResponse("Validation failed.", result);
                    }
                case "edit":
                    McpLog.Warn("manage_script.edit is deprecated; prefer apply_text_edits. Serving structured edit for backward compatibility.");
                    var structEdits = @params["edits"] as JArray;
                    var options = @params["options"] as JObject;
                    return EditScript(fullPath, relativePath, name, structEdits, options);
                case "get_sha":
                    {
                        try
                        {
                            if (!File.Exists(fullPath))
                                return new ErrorResponse($"Script not found at '{relativePath}'.");

                            string text = File.ReadAllText(fullPath);
                            string sha = ComputeSha256(text);
                            var fi = new FileInfo(fullPath);
                            long lengthBytes;
                            try { lengthBytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetByteCount(text); }
                            catch { lengthBytes = fi.Exists ? fi.Length : 0; }
                            var data = new
                            {
                                uri = $"mcpforunity://path/{relativePath}",
                                path = relativePath,
                                sha256 = sha,
                                lengthBytes,
                                lastModifiedUtc = fi.Exists ? fi.LastWriteTimeUtc.ToString("o") : string.Empty
                            };
                            return new SuccessResponse($"SHA computed for '{relativePath}'.", data);
                        }
                        catch (Exception ex)
                        {
                            return new ErrorResponse($"Failed to compute SHA: {ex.Message}");
                        }
                    }
                default:
                    return new ErrorResponse(
                        $"Unknown action: '{action}'. Valid actions are: create, delete, apply_text_edits, validate, read (deprecated), update (deprecated), edit (deprecated)."
                    );
            }
        }

        /// <summary>
        /// Decode base64 string to normal text
        /// </summary>
        private static string DecodeBase64(string encoded)
        {
            byte[] data = Convert.FromBase64String(encoded);
            return System.Text.Encoding.UTF8.GetString(data);
        }

        /// <summary>
        /// Encode text to base64 string
        /// </summary>
        private static string EncodeBase64(string text)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(text);
            return Convert.ToBase64String(data);
        }

    }
}
