using System;
using System.IO;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Tools
{
    public class ManageScriptMethodInsertionTests
    {
        private string _directory;
        private string _file;
        private const string Source = "class C\n{\n    void First() {}\n    void Target() {}\n    void Last() {}\n}\n";

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "unity-mcp-method-insertion-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _file = Path.Combine(_directory, "C.txt");
        }

        [TearDown]
        public void TearDown()
        {
            if (!Directory.Exists(_directory)) return;
            foreach (string path in new[] { _file, _file + ".tmp", _file + ".bak" })
                if (File.Exists(path)) File.Delete(path);
            Directory.Delete(_directory);
        }

        private object Apply(string source, JToken edit, string mode)
        {
            File.WriteAllText(_file, source);
            return typeof(ManageScript).GetMethod("EditScript", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { _file, "Assets/MethodInsertionContract.txt", "C", edit as JArray ?? new JArray(edit),
                    new JObject { ["applyMode"] = mode, ["validate"] = "basic", ["refresh"] = "immediate" } });
        }

        private static JObject Insert(string position, string anchor = "Target") => new JObject
        {
            ["op"] = "insert_method", ["className"] = "C", ["position"] = position,
            [position == "before" ? "beforeMethodName" : "afterMethodName"] = anchor,
            ["replacement"] = "void Added() {}"
        };

        [TestCase("atomic")]
        [TestCase("sequential")]
        public void Before_InsertsBetweenPriorMethodAndAnchor(string mode)
        {
            Assert.IsInstanceOf<SuccessResponse>(Apply(Source, Insert("before"), mode));
            string result = File.ReadAllText(_file);
            Assert.Less(result.IndexOf("First()", StringComparison.Ordinal), result.IndexOf("Added()", StringComparison.Ordinal));
            Assert.Less(result.IndexOf("Added()", StringComparison.Ordinal), result.IndexOf("Target()", StringComparison.Ordinal));
            Assert.Less(result.IndexOf("Target()", StringComparison.Ordinal), result.IndexOf("Last()", StringComparison.Ordinal));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Absent")]
        public void Before_InvalidAnchorDoesNotWrite(string anchor)
        {
            Assert.IsInstanceOf<ErrorResponse>(Apply(Source, Insert("before", anchor), "atomic"));
            Assert.AreEqual(Source, File.ReadAllText(_file));
        }

        [Test]
        public void Before_DoesNotUseAfterAnchorAsFallback()
        {
            var edit = Insert("before", null);
            edit["afterMethodName"] = "Target";
            Assert.IsInstanceOf<ErrorResponse>(Apply(Source, edit, "sequential"));
            Assert.AreEqual(Source, File.ReadAllText(_file));
        }

        [TestCase("/// <summary>Target documentation</summary>")]
        [TestCase("/**\n     * Target documentation\n     */")]
        public void Before_PreservesTargetAttributesAndDocumentation(string documentation)
        {
            string source = "class C\n{\n    void First() {}\n    " + documentation + "\n    [System.Obsolete]\n    void Target() {}\n}\n";
            Assert.IsInstanceOf<SuccessResponse>(Apply(source, Insert("before"), "atomic"));
            string result = File.ReadAllText(_file);
            Assert.Less(result.IndexOf("Added()", StringComparison.Ordinal), result.IndexOf(documentation, StringComparison.Ordinal));
            StringAssert.Contains(documentation + "\n    [System.Obsolete]\n    void Target()", result);
        }

        [TestCase("atomic")]
        [TestCase("sequential")]
        public void Before_FailedLaterAnchorLeavesWholeFileUnchanged(string mode)
        {
            var first = Insert("end");
            first["replacement"] = "void Earlier() {}";
            var edits = new JArray(first, Insert("before", "Absent"));
            Assert.IsInstanceOf<ErrorResponse>(Apply(Source, edits, mode));
            Assert.AreEqual(Source, File.ReadAllText(_file));
        }

        [Test]
        public void Before_UsesQualifiedOverloadWithinNamespace()
        {
            const string source = "namespace Scoped\n{\nclass C\n{\n    void Target() {}\n    int Target(int value) { return value; }\n    void Last() {}\n}\n}\n";
            var edit = Insert("before");
            edit["namespace"] = "Scoped";
            edit["beforeReturnType"] = "int";
            edit["beforeParametersSignature"] = "int value";
            Assert.IsInstanceOf<SuccessResponse>(Apply(source, edit, "atomic"));
            string result = File.ReadAllText(_file);
            Assert.Less(result.IndexOf("void Target()", StringComparison.Ordinal), result.IndexOf("Added()", StringComparison.Ordinal));
            Assert.Less(result.IndexOf("Added()", StringComparison.Ordinal), result.IndexOf("int Target(", StringComparison.Ordinal));
        }

        [TestCase("before")]
        [TestCase("after")]
        public void MethodAnchor_AllowsNamespaceAndClassOnSameLine(string position)
        {
            const string source = "namespace Scoped { class C\n{\n    void Target() {}\n    void Last() {}\n}\n}\n";
            var edit = Insert(position);
            edit["namespace"] = "Scoped";
            Assert.IsInstanceOf<SuccessResponse>(Apply(source, edit, "atomic"));
            string result = File.ReadAllText(_file);
            int added = result.IndexOf("Added()", StringComparison.Ordinal);
            int target = result.IndexOf("void Target()", StringComparison.Ordinal);
            if (position == "before") Assert.Less(added, target);
            else Assert.Greater(added, target);
        }

        [TestCase("before")]
        [TestCase("after")]
        public void MethodAnchor_DoesNotUsePriorClassMemberOnSharedDeclarationLine(string position)
        {
            const string source = "class Other\n{\n    void Target() {} } class C\n{\n    void Target() {}\n}\n";
            Assert.IsInstanceOf<SuccessResponse>(Apply(source, Insert(position), "atomic"));
            string result = File.ReadAllText(_file);
            int added = result.IndexOf("Added()", StringComparison.Ordinal);
            Assert.Greater(added, result.IndexOf("class C", StringComparison.Ordinal));
            int target = result.LastIndexOf("void Target()", StringComparison.Ordinal);
            if (position == "before") Assert.Less(added, target);
            else Assert.Greater(added, target);
        }

        [TestCase("before", "nested")]
        [TestCase("after", "nested")]
        [TestCase("before", "comment")]
        [TestCase("after", "comment")]
        [TestCase("before", "verbatim")]
        [TestCase("after", "verbatim")]
        public void MethodAnchor_UsesDirectMemberRatherThanNestedOrNonCode(string position, string decoy)
        {
            string prefix = decoy == "nested"
                ? "    class Nested\n    {\n        void Target() {}\n    }\n"
                : decoy == "comment" ? "    /*\n    void Target() {}\n    */\n"
                : "    string code = @\"\n    void Target() {}\n\";\n";
            string source = "class C\n{\n" + prefix + "    void Target() {}\n    void Last() {}\n}\n";
            Assert.IsInstanceOf<SuccessResponse>(Apply(source, Insert(position), "atomic"));
            string result = File.ReadAllText(_file);
            int added = result.IndexOf("Added()", StringComparison.Ordinal);
            int direct = result.LastIndexOf("void Target()", StringComparison.Ordinal);
            int boundary = result.IndexOf(decoy == "nested" ? "\n    }" : decoy == "comment" ? "*/" : "\";", StringComparison.Ordinal);
            Assert.Greater(added, boundary, "Inserted code must belong to C, outside the decoy.");
            if (position == "before") Assert.Less(added, direct);
            else Assert.Greater(added, direct);
        }

        [TestCase("before")]
        [TestCase("after")]
        public void MethodAnchor_SelectsMatchingAttributedOverload(string position)
        {
            const string source = "class C\n{\n    void Target() {}\n    [System.Obsolete(\"marker\")]\n    void Target(int value) {}\n    void Last() {}\n}\n";
            var edit = Insert(position);
            edit[position + "AttributesContains"] = "marker";
            Assert.IsInstanceOf<SuccessResponse>(Apply(source, edit, "atomic"));
            string result = File.ReadAllText(_file);
            int added = result.IndexOf("Added()", StringComparison.Ordinal);
            Assert.Greater(added, result.IndexOf("void Target()", StringComparison.Ordinal));
            if (position == "before") Assert.Less(added, result.IndexOf("[System.Obsolete", StringComparison.Ordinal));
            else Assert.Greater(added, result.IndexOf("void Target(int", StringComparison.Ordinal));
        }

        [TestCase("before")]
        [TestCase("after")]
        public void AttributeFilter_DoesNotMatchInterveningComment(string position)
        {
            const string source = "class C\n{\n    [System.Obsolete(\"other\")]\n    // marker is not an attribute\n    void Target() {}\n    [System.Obsolete(\"marker\")]\n    void Target(int value) {}\n}\n";
            var edit = Insert(position);
            edit[position + "AttributesContains"] = "marker";
            Assert.IsInstanceOf<SuccessResponse>(Apply(source, edit, "atomic"));
            string result = File.ReadAllText(_file);
            int added = result.IndexOf("Added()", StringComparison.Ordinal);
            Assert.Greater(added, result.IndexOf("void Target()", StringComparison.Ordinal));
            if (position == "before") Assert.Less(added, result.IndexOf("[System.Obsolete(\"marker\")", StringComparison.Ordinal));
            else Assert.Greater(added, result.IndexOf("void Target(int", StringComparison.Ordinal));
        }

        [TestCase("before")]
        [TestCase("after")]
        public void MethodAnchor_DoesNotParseSignatureInsideInlineAttributeString(string position)
        {
            const string source = "class C\n{\n    [System.Obsolete(\"Target()\")] void Target() {}\n    void Last() {}\n}\n";
            Assert.IsInstanceOf<SuccessResponse>(Apply(source, Insert(position), "atomic"));
            string result = File.ReadAllText(_file);
            StringAssert.Contains("[System.Obsolete(\"Target()\")] void Target() {}", result);
            if (position == "before") Assert.Less(result.IndexOf("Added()", StringComparison.Ordinal), result.IndexOf("[System.Obsolete", StringComparison.Ordinal));
            else Assert.Greater(result.IndexOf("Added()", StringComparison.Ordinal), result.IndexOf("void Target()", StringComparison.Ordinal));
        }

        [Test]
        public void Before_PreservesMultilineAttributeWithDocumentation()
        {
            const string attached = "/// <summary>Target</summary>\n    [System.Obsolete(\n        \"marker\")]\n    void Target() {}";
            string source = "class C\n{\n    " + attached + "\n}\n";
            Assert.IsInstanceOf<SuccessResponse>(Apply(source, Insert("before"), "atomic"));
            string result = File.ReadAllText(_file);
            StringAssert.Contains(attached, result);
            Assert.Less(result.IndexOf("Added()", StringComparison.Ordinal), result.IndexOf("/// <summary>", StringComparison.Ordinal));
        }

        [TestCase("    \n")]
        [TestCase("    // Keep this explanation\n    \n")]
        public void Before_PreservesDocumentationAcrossTrivia(string trivia)
        {
            string attached = "/// <summary>Target</summary>\n" + trivia + "    void Target() {}";
            string source = "class C\n{\n    " + attached + "\n}\n";
            Assert.IsInstanceOf<SuccessResponse>(Apply(source, Insert("before"), "atomic"));
            string result = File.ReadAllText(_file);
            StringAssert.Contains(attached, result);
            Assert.Less(result.IndexOf("Added()", StringComparison.Ordinal), result.IndexOf("/// <summary>", StringComparison.Ordinal));
        }

        [TestCase("atomic")]
        [TestCase("sequential")]
        public void Before_ConditionalAttributeFailsWithoutWriting(string mode)
        {
            const string source = "class C\n{\n#if UNITY_EDITOR\n    [System.Obsolete]\n#endif\n    void Target() {}\n}\n";
            Assert.IsInstanceOf<ErrorResponse>(Apply(source, Insert("before"), mode));
            Assert.AreEqual(source, File.ReadAllText(_file));
        }

        [Test]
        public void Before_DoesNotTreatDirectiveTextInCommentOrStringAsCode()
        {
            const string source = "class C\n{\n    string label = @\"\n#if EXAMPLE\n\";\n    // #if EXAMPLE\n    void Target() {}\n}\n";
            Assert.IsInstanceOf<SuccessResponse>(Apply(source, Insert("before"), "atomic"));
        }

        [TestCase("/*", false)]
        [TestCase("/**", true)]
        public void Before_UsesRealBlockCommentStartRatherThanEmbeddedDocumentationText(string opening, bool documentation)
        {
            string comment = opening + "\n    /** example documentation opener\n    */";
            string source = "class C\n{\n    " + comment + "\n    \n    void Target() {}\n}\n";
            Assert.IsInstanceOf<SuccessResponse>(Apply(source, Insert("before"), "atomic"));
            string result = File.ReadAllText(_file);
            StringAssert.Contains(comment, result, "Never insert inside a block comment.");
            int added = result.IndexOf("Added()", StringComparison.Ordinal);
            if (documentation) Assert.Less(added, result.IndexOf(comment, StringComparison.Ordinal));
            else Assert.Greater(added, result.IndexOf("*/", StringComparison.Ordinal));
        }

        [TestCase("replace_method")]
        [TestCase("delete_method")]
        public void SharedMethodSelector_PreservesNestedMethodForSiblingOperations(string operation)
        {
            const string nested = "    class Nested\n    {\n        void Target() {}\n    }";
            string source = "class C\n{\n" + nested + "\n    void Target() {}\n    void Last() {}\n}\n";
            var edit = new JObject { ["op"] = operation, ["className"] = "C", ["methodName"] = "Target" };
            if (operation == "replace_method") edit["replacement"] = "void Target() { int changed = 1; }";
            Assert.IsInstanceOf<SuccessResponse>(Apply(source, edit, "atomic"));
            string result = File.ReadAllText(_file);
            StringAssert.Contains(nested, result);
            if (operation == "replace_method") StringAssert.Contains("int changed = 1", result);
            else Assert.AreEqual(result.IndexOf("Target()", StringComparison.Ordinal), result.LastIndexOf("Target()", StringComparison.Ordinal));
        }

        [TestCase("after")]
        [TestCase("start")]
        [TestCase("end")]
        public void ExistingPositionsKeepTheirPlacement(string position)
        {
            Assert.IsInstanceOf<SuccessResponse>(Apply(Source, Insert(position), "atomic"));
            string result = File.ReadAllText(_file);
            int added = result.IndexOf("Added()", StringComparison.Ordinal);
            if (position == "start") Assert.Less(added, result.IndexOf("First()", StringComparison.Ordinal));
            if (position == "end") Assert.Greater(added, result.IndexOf("Last()", StringComparison.Ordinal));
            if (position == "after")
            {
                Assert.Greater(added, result.IndexOf("Target()", StringComparison.Ordinal));
                Assert.Less(added, result.IndexOf("Last()", StringComparison.Ordinal));
            }
        }
    }
}
