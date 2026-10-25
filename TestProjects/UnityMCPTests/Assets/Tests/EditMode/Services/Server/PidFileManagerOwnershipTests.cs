using System;
using System.IO;
using MCPForUnity.Editor.Services.Server;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services.Server
{
    public class PidFileManagerOwnershipTests
    {
        private string _root;
        private PidFileManager _owner;
        private PidFileManager _otherProject;
        private PidFileManager _otherPort;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "MCPForUnity-OwnershipTests", Guid.NewGuid().ToString("N"));
            _owner = new PidFileManager(Path.Combine(_root, "A"), 8080);
            _otherProject = new PidFileManager(Path.Combine(_root, "B"), 8080);
            _otherPort = new PidFileManager(Path.Combine(_root, "A"), 9090);
        }

        [TearDown]
        public void TearDown()
        {
            _owner.ClearTracking();
            _otherProject.ClearTracking();
            _otherPort.ClearTracking();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void AnotherProjectOrPort_CannotReadOrClearOwner()
        {
            _owner.StoreHandshake(_owner.GetPidFilePath(8080), "owner-token");
            _owner.StoreTracking(12345, 8080, "owner-fingerprint");
            foreach (var other in new[] { _otherProject, _otherPort })
            {
                Assert.IsFalse(other.TryGetHandshake(out _, out _));
                Assert.IsFalse(other.TryGetStoredPid(8080, out _));
                Assert.IsEmpty(other.GetStoredArgsHash());
                other.ClearTracking();
            }
            Assert.IsTrue(_owner.TryGetHandshake(out _, out var token));
            Assert.AreEqual("owner-token", token);
            Assert.IsTrue(_owner.TryGetStoredPid(8080, out var pid));
            Assert.AreEqual(12345, pid);
            Assert.AreEqual("owner-fingerprint", _owner.GetStoredArgsHash());
        }

        [Test]
        public void RecreatedManager_PreservesThisEditorOwnership()
        {
            _owner.StoreHandshake(_owner.GetPidFilePath(8080), "owner-token");
            var recreated = new PidFileManager(Path.Combine(_root, "A"), 8080);
            Assert.IsTrue(recreated.TryGetHandshake(out _, out var token));
            Assert.AreEqual("owner-token", token);
        }

        [Test]
        public void PartialOrForeignHandshake_DoesNotInheritOldLaunch()
        {
            _owner.StoreHandshake(_owner.GetPidFilePath(8080), "owner-token");
            _owner.StoreHandshake(null, "another-token");
            Assert.IsFalse(_owner.TryGetHandshake(out _, out _));
            _owner.StoreHandshake(_otherProject.GetPidFilePath(8080), "foreign-token");
            Assert.IsFalse(_owner.TryGetHandshake(out _, out _));
            _owner.StoreHandshake(_owner.GetPidFilePath(9090), "wrong-port-token");
            Assert.IsFalse(_owner.TryGetHandshake(out _, out _));
        }

        [TestCase(0)]
        [TestCase(65536)]
        [TestCase(-1)]
        public void InvalidPort_CannotBeUsedAsPidEndpoint(int port)
        {
            Assert.IsFalse(_owner.TryGetPortFromPidFilePath($"mcp_http_{port}.pid", out _));
        }
    }
}
