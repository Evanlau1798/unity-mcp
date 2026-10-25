using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Services.Server
{
    internal static class LocalHttpServerControl
    {
        internal static bool TryStop(string baseUrl, string token, int expectedPid)
        {
            if (expectedPid <= 1 || string.IsNullOrEmpty(token)
                || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http") return false;
            string host = uri.Host.Trim('[', ']');
            if (IPAddress.TryParse(host, out var bindAddress))
            {
                if (bindAddress.Equals(IPAddress.Any)) host = "127.0.0.1";
                else if (bindAddress.Equals(IPAddress.IPv6Any)) host = "::1";
            }
            bool namedLocal = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
            if (!namedLocal && (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address))) return false;

            var payload = new JObject { ["token"] = token, ["server_pid"] = expectedPid };
            EditorProcessIdentity.AddTo(payload);
            var timer = Stopwatch.StartNew();
            // Numeric hosts avoid DNS consuming the whole quit/control budget.
            foreach (string candidate in namedLocal ? new[] { "127.0.0.1", "::1" } : new[] { host })
            {
                int budget = Math.Min(150, 500 - (int)timer.ElapsedMilliseconds);
                if (budget <= 0) break;
                var endpoint = new UriBuilder("http", candidate, uri.Port, "/api/managed/stop").Uri;
                // Mono's connection establishment can outlive HttpClient.Timeout; keep it off the Editor thread.
                Task<bool> request = Task.Run(() => SendAsync(endpoint, payload, budget));
                if (request.Wait(budget) && request.Result) return true;
            }
            return false;
        }

        private static async Task<bool> SendAsync(Uri endpoint, JObject payload, int timeoutMs)
        {
            try
            {
                using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromMilliseconds(timeoutMs),
                    MaxResponseContentBufferSize = 4096
                };
                using var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(endpoint, content).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.Accepted) return false;
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return (bool?)JObject.Parse(body)["success"] == true;
            }
            catch { return false; }
        }
    }
}
