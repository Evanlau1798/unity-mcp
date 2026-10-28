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
        private static object CreateScript(
            string fullPath,
            string relativePath,
            string name,
            string contents,
            string scriptType,
            string namespaceName
        )
        {
            // Check if script already exists
            if (File.Exists(fullPath))
            {
                return new ErrorResponse(
                    $"Script already exists at '{relativePath}'. Use 'update' action to modify."
                );
            }

            // Generate default content if none provided
            if (string.IsNullOrEmpty(contents))
            {
                contents = GenerateDefaultScriptContent(name, scriptType, namespaceName);
            }

            // Validate syntax with detailed error reporting using GUI setting
            ValidationLevel validationLevel = GetValidationLevelFromGUI();
            bool isValid = ValidateScriptSyntax(contents, validationLevel, out string[] validationErrors);
            if (!isValid)
            {
                return new ErrorResponse("validation_failed", new { status = "validation_failed", diagnostics = validationErrors ?? Array.Empty<string>() });
            }
            else if (validationErrors != null && validationErrors.Length > 0)
            {
                // Log warnings but don't block creation
                McpLog.Warn($"Script validation warnings for {name}:\n" + string.Join("\n", validationErrors));
            }

            try
            {
                // Atomic create without BOM; schedule refresh after reply
                var enc = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                var tmp = fullPath + ".tmp";
                File.WriteAllText(tmp, contents, enc);
                try
                {
                    File.Move(tmp, fullPath);
                }
                catch (IOException)
                {
                    File.Copy(tmp, fullPath, overwrite: true);
                    try { File.Delete(tmp); } catch { }
                }

                var uri = $"mcpforunity://path/{relativePath}";
                var ok = new SuccessResponse(
                    $"Script '{name}.cs' created successfully at '{relativePath}'.",
                    new { uri, scheduledRefresh = false }
                );

                ManageScriptRefreshHelpers.ImportAndRequestCompile(relativePath);

                return ok;
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to create script '{relativePath}': {e.Message}");
            }
        }

        private static object ReadScript(string fullPath, string relativePath)
        {
            if (!File.Exists(fullPath))
            {
                return new ErrorResponse($"Script not found at '{relativePath}'.");
            }

            try
            {
                string contents = File.ReadAllText(fullPath);

                // Return both normal and encoded contents for larger files
                bool isLarge = contents.Length > 10000; // If content is large, include encoded version
                var uri = $"mcpforunity://path/{relativePath}";
                var responseData = new
                {
                    uri,
                    path = relativePath,
                    contents = contents,
                    // For large files, also include base64-encoded version
                    encodedContents = isLarge ? EncodeBase64(contents) : null,
                    contentsEncoded = isLarge,
                };

                return new SuccessResponse(
                    $"Script '{Path.GetFileName(relativePath)}' read successfully.",
                    responseData
                );
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to read script '{relativePath}': {e.Message}");
            }
        }

        private static object UpdateScript(
            string fullPath,
            string relativePath,
            string name,
            string contents
        )
        {
            if (!File.Exists(fullPath))
            {
                return new ErrorResponse(
                    $"Script not found at '{relativePath}'. Use 'create' action to add a new script."
                );
            }
            if (string.IsNullOrEmpty(contents))
            {
                return new ErrorResponse("Content is required for the 'update' action.");
            }

            // Validate syntax with detailed error reporting using GUI setting
            ValidationLevel validationLevel = GetValidationLevelFromGUI();
            bool isValid = ValidateScriptSyntax(contents, validationLevel, out string[] validationErrors);
            if (!isValid)
            {
                return new ErrorResponse("validation_failed", new { status = "validation_failed", diagnostics = validationErrors ?? Array.Empty<string>() });
            }
            else if (validationErrors != null && validationErrors.Length > 0)
            {
                // Log warnings but don't block update
                McpLog.Warn($"Script validation warnings for {name}:\n" + string.Join("\n", validationErrors));
            }

            try
            {
                // Safe write with atomic replace when available, without BOM
                var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                string tempPath = fullPath + ".tmp";
                File.WriteAllText(tempPath, contents, encoding);

                string backupPath = fullPath + ".bak";
                try
                {
                    File.Replace(tempPath, fullPath, backupPath);
                    try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(tempPath, fullPath, true);
                    try { File.Delete(tempPath); } catch { }
                    try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
                }
                catch (IOException)
                {
                    File.Copy(tempPath, fullPath, true);
                    try { File.Delete(tempPath); } catch { }
                    try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
                }

                // Prepare success response BEFORE any operation that can trigger a domain reload
                var uri = $"mcpforunity://path/{relativePath}";
                var ok = new SuccessResponse(
                    $"Script '{name}.cs' updated successfully at '{relativePath}'.",
                    new { uri, path = relativePath, scheduledRefresh = true }
                );

                // Schedule a debounced import/compile on next editor tick to avoid stalling the reply
                ManageScriptRefreshHelpers.ScheduleScriptRefresh(relativePath);

                return ok;
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to update script '{relativePath}': {e.Message}");
            }
        }
        private static object DeleteScript(string fullPath, string relativePath)
        {
            if (!File.Exists(fullPath))
            {
                return new ErrorResponse($"Script not found at '{relativePath}'. Cannot delete.");
            }

            try
            {
                // Use AssetDatabase.MoveAssetToTrash for safer deletion (allows undo)
                bool deleted = AssetDatabase.MoveAssetToTrash(relativePath);
                if (deleted)
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    return new SuccessResponse(
                        $"Script '{Path.GetFileName(relativePath)}' moved to trash successfully.",
                        new { deleted = true }
                    );
                }
                else
                {
                    // Fallback or error if MoveAssetToTrash fails
                    return new ErrorResponse(
                        $"Failed to move script '{relativePath}' to trash. It might be locked or in use."
                    );
                }
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Error deleting script '{relativePath}': {e.Message}");
            }
        }


        /// <summary>
        /// Generates basic C# script content based on name and type.
        /// </summary>
        private static string GenerateDefaultScriptContent(
            string name,
            string scriptType,
            string namespaceName
        )
        {
            string usingStatements = "using UnityEngine;\nusing System.Collections;\n";
            string classDeclaration;
            string body =
                "\n    // Use this for initialization\n    void Start() {\n\n    }\n\n    // Update is called once per frame\n    void Update() {\n\n    }\n";

            string baseClass = "";
            if (!string.IsNullOrEmpty(scriptType))
            {
                if (scriptType.Equals("MonoBehaviour", StringComparison.OrdinalIgnoreCase))
                    baseClass = " : MonoBehaviour";
                else if (scriptType.Equals("ScriptableObject", StringComparison.OrdinalIgnoreCase))
                {
                    baseClass = " : ScriptableObject";
                    body = ""; // ScriptableObjects don't usually need Start/Update
                }
                else if (
                    scriptType.Equals("Editor", StringComparison.OrdinalIgnoreCase)
                    || scriptType.Equals("EditorWindow", StringComparison.OrdinalIgnoreCase)
                )
                {
                    usingStatements += "using UnityEditor;\n";
                    if (scriptType.Equals("Editor", StringComparison.OrdinalIgnoreCase))
                        baseClass = " : Editor";
                    else
                        baseClass = " : EditorWindow";
                    body = ""; // Editor scripts have different structures
                }
                // Add more types as needed
            }

            classDeclaration = $"public class {name}{baseClass}";

            string fullContent = $"{usingStatements}\n";
            bool useNamespace = !string.IsNullOrEmpty(namespaceName);

            if (useNamespace)
            {
                fullContent += $"namespace {namespaceName}\n{{\n";
                // Indent class and body if using namespace
                classDeclaration = "    " + classDeclaration;
                body = string.Join("\n", body.Split('\n').Select(line => "    " + line));
            }

            fullContent += $"{classDeclaration}\n{{\n{body}\n}}";

            if (useNamespace)
            {
                fullContent += "\n}"; // Close namespace
            }

            return fullContent.Trim() + "\n"; // Ensure a trailing newline
        }

    }
}
