using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Server;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services.Server
{
    public class LocalHttpServerControlTests
    {
        [Test]
        public void MissingOwnershipOrNonLoopback_IsRejectedBeforeNetwork()
        {
            Assert.IsFalse(LocalHttpServerControl.TryStop("http://127.0.0.1:1", null, 123));
            Assert.IsFalse(LocalHttpServerControl.TryStop("http://127.0.0.1:1", "token", 0));
            Assert.IsFalse(LocalHttpServerControl.TryStop("http://192.0.2.1", "token", 123));
            Assert.IsFalse(LocalHttpServerControl.TryStop("https://127.0.0.1:1", "token", 123));
        }

        [TestCase("127.0.0.1", 202, true)]
        [TestCase("127.0.0.1", 409, false)]
        [TestCase("127.0.0.2", 202, true)]
        [TestCase("0.0.0.0", 202, true)]
        [TestCase("::", 202, true)]
        public async Task Stop_UsesVerifiedControlAndCurrentProcessIdentity(string host, int status, bool expected)
        {
            var listener = new TcpListener(host == "::" ? IPAddress.IPv6Loopback
                : IPAddress.Parse(host == "0.0.0.0" ? "127.0.0.1" : host), 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var request = ReplyOnce(listener, status, "{\"success\":true}");
                string urlHost = host == "::" ? "[::]" : host;
                Assert.AreEqual(expected, LocalHttpServerControl.TryStop($"http://{urlHost}:{port}", "token", 123));
                Assert.AreSame(request, await Task.WhenAny(request, Task.Delay(3000)), "No isolated control request received");
                var payload = JObject.Parse(await request);
                Assert.AreEqual("token", (string)payload["token"]);
                Assert.AreEqual(123, (int)payload["server_pid"]);
                using var process = Process.GetCurrentProcess();
                Assert.AreEqual(process.Id, (int)payload["editor_pid"]);
                Assert.AreEqual(new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds(),
                    (long)payload["editor_started_ms"]);
            }
            finally { listener.Stop(); }
        }

        [Test]
        public async Task OversizedControlResponse_IsRejected()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var reply = ReplyOnce(listener, 202, "{\"success\":true,\"padding\":\"" + new string('x', 5000) + "\"}");
                Assert.IsFalse(LocalHttpServerControl.TryStop($"http://127.0.0.1:{port}", "token", 123));
                Assert.AreSame(reply, await Task.WhenAny(reply, Task.Delay(3000)));
                await reply;
            }
            finally { listener.Stop(); }
        }

        [Test]
        public void FailedProbe_IsBounded()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var timer = Stopwatch.StartNew();
            Assert.IsFalse(LocalHttpServerControl.TryStop($"http://127.0.0.1:{port}", "token", 123));
            Assert.Less(timer.ElapsedMilliseconds, 1000, "Control exceeded its bounded budget");
        }

        private static async Task<string> ReplyOnce(TcpListener listener, int status, string body)
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            string line = await reader.ReadLineAsync();
            Assert.That(line, Does.StartWith("POST /api/managed/stop "));
            int length = 0;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line.Substring("Content-Length:".Length).Trim());
            var chars = new char[length];
            for (int read = 0; read < length;)
            {
                int count = await reader.ReadAsync(chars, read, length - read);
                if (count == 0) throw new EndOfStreamException();
                read += count;
            }
            byte[] response = Encoding.UTF8.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(response, 0, response.Length);
            return new string(chars);
        }
    }
}
