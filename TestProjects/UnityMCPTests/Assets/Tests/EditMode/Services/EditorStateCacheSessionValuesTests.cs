using NUnit.Framework;
using UnityEditor;
using MCPForUnity.Editor.Services;

namespace MCPForUnityTests.Editor.Services
{
    [TestFixture]
    public class EditorStateCacheSessionValuesTests
    {
        private const string Key = "MCPForUnityTests.EditorStateCache.SessionUnixMs";

        [SetUp]
        public void SetUp() => SessionState.EraseString(Key);

        [TearDown]
        public void TearDown() => SessionState.EraseString(Key);

        [Test]
        public void SessionUnixMs_RoundTripsValueBeyondInt32()
        {
            // A unix-ms timestamp does not fit an int; SessionState has no long
            // overload, so the value goes through a string and must come back exact.
            const long value = 1788652878150L;
            Assert.Greater(value, int.MaxValue, "test value must exceed what SessionState.SetInt could hold");

            EditorStateCache.SetSessionUnixMs(Key, value);

            Assert.AreEqual(value, EditorStateCache.GetSessionUnixMs(Key));
        }

        [Test]
        public void GetSessionUnixMs_UnsetKey_ReturnsNull()
        {
            Assert.IsNull(EditorStateCache.GetSessionUnixMs(Key));
        }

        [TestCase("not-a-number")]
        [TestCase("1788652878150.5")]
        [TestCase("1,788,652,878,150")]
        public void GetSessionUnixMs_MalformedValue_ReturnsNull(string raw)
        {
            SessionState.SetString(Key, raw);

            Assert.IsNull(EditorStateCache.GetSessionUnixMs(Key));
        }

        [TestCase(25, 100, 100)]
        [TestCase(100, 25, 25)]
        [TestCase(25, 250, 100)]
        [TestCase(25, 0, 1)]
        public void BatchLimit_ReadObservesChangedSettingWithoutRebuildingIdleSnapshot(int initial, int changed, int expected)
        {
            const string setting = "MCPForUnity.BatchExecute.MaxCommands";
            bool existed = EditorPrefs.HasKey(setting);
            int original = EditorPrefs.GetInt(setting, 25);
            try
            {
                EditorPrefs.SetInt(setting, initial);
                typeof(EditorStateCache).GetMethod("ForceUpdate", System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { "test_initial_setting" });
                var before = EditorStateCache.GetSnapshot();
                Assert.AreEqual(initial, (int)before["settings"]["batch_execute_max_commands"]);
                EditorPrefs.SetInt(setting, changed);
                var after = EditorStateCache.GetSnapshot();

                Assert.AreEqual(expected, (int)after["settings"]["batch_execute_max_commands"]);
                Assert.AreEqual((long)before["sequence"], (long)after["sequence"],
                    "A setting read should not require a full scene/JSON snapshot rebuild.");
            }
            finally
            {
                if (existed) EditorPrefs.SetInt(setting, original);
                else EditorPrefs.DeleteKey(setting);
            }
        }
    }
}
