using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OniAdvisor
{
    /// <summary>
    /// The network checks of the feasibility test (NetSpike), free of Unity so tools/NetSpikeHarness can run
    /// them outside the game too. TEMPORARY.
    /// </summary>
    internal static class NetChecks
    {
        /// <summary>
        /// config: "mock" (base URL, default http://127.0.0.1:8765), "mockExe" (tools/LlmMock, started for the
        /// test if given), "model" (for the real call).
        /// </summary>
        public static async Task<JObject> RunAll(JObject config)
        {
            var result = new JObject
            {
                ["utc"] = System.DateTime.UtcNow.ToString("o"),
                ["runtime"] = Environment.Version.ToString(),
                ["securityProtocol"] = ServicePointManager.SecurityProtocol.ToString(),
            };
            Process mockProcess = null;
            try
            {
                mockProcess = StartMock((string)config["mockExe"], result);
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                {
                    result["https"] = await Https(http);
                    result["httpWebRequest"] = await WebRequestCheck();
                    string mock = (string)config["mock"] ?? "http://127.0.0.1:8765";
                    result["stream"] = await Stream(http, mock + "/v1/messages", 1_500_000, null);
                    result["cancel"] = await Cancel(http, mock + "/v1/messages?slow=1");
                    var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                    result["real"] = string.IsNullOrEmpty(key) ? (JToken)"skipped (no ANTHROPIC_API_KEY)" : await Real(http, key, (string)config["model"] ?? "claude-haiku-5-5");
                }
            }
            catch (Exception e) { result["error"] = e.ToString(); }
            finally { try { if (mockProcess != null && !mockProcess.HasExited) mockProcess.Kill(); } catch { } }
            return result;
        }

        static Process StartMock(string exe, JObject result)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            if (!File.Exists(exe)) { result["mockStart"] = "not found: " + exe; return null; }
            var psi = new ProcessStartInfo(exe, "8765 --exit-after-seconds 180") { UseShellExecute = false, CreateNoWindow = true };
            var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
            if (Directory.Exists(dotnet)) psi.EnvironmentVariables["DOTNET_ROOT"] = dotnet;
            var p = Process.Start(psi);
            Thread.Sleep(2000); // let it start listening
            result["mockStart"] = p != null && !p.HasExited ? "started" : "exited at once";
            return p;
        }

        /// <summary>No key: the APIs answer 401/403/400, which still proves the TLS handshake and the request work.</summary>
        static async Task<JArray> Https(HttpClient http)
        {
            var checks = new (string name, HttpMethod method, string url, string body)[]
            {
                ("anthropic", HttpMethod.Post, "https://api.anthropic.com/v1/messages", "{}"),
                ("openai", HttpMethod.Get, "https://api.openai.com/v1/models", null),
                ("gemini", HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models", null),
                ("openrouter", HttpMethod.Get, "https://openrouter.ai/api/v1/models", null),
            };
            var arr = new JArray();
            foreach (var (name, method, url, body) in checks)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var req = new HttpRequestMessage(method, url);
                    if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    req.Headers.Add("anthropic-version", "2023-06-01");
                    using (var res = await http.SendAsync(req))
                        arr.Add(new JObject { ["api"] = name, ["status"] = (int)res.StatusCode, ["ms"] = sw.ElapsedMilliseconds });
                }
                catch (Exception e) { arr.Add(new JObject { ["api"] = name, ["error"] = Flatten(e), ["ms"] = sw.ElapsedMilliseconds }); }
            }
            return arr;
        }

        /// <summary>The older HTTP stack, in case HttpClient fails where it works.</summary>
        static async Task<JObject> WebRequestCheck()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/v1/messages");
                req.Method = "POST";
                req.ContentType = "application/json";
                using (var s = await req.GetRequestStreamAsync()) { var b = Encoding.UTF8.GetBytes("{}"); s.Write(b, 0, b.Length); }
                try { using (var res = (HttpWebResponse)await req.GetResponseAsync()) return new JObject { ["status"] = (int)res.StatusCode, ["ms"] = sw.ElapsedMilliseconds }; }
                catch (WebException e) when (e.Response is HttpWebResponse r) { return new JObject { ["status"] = (int)r.StatusCode, ["ms"] = sw.ElapsedMilliseconds }; }
            }
            catch (Exception e) { return new JObject { ["error"] = Flatten(e), ["ms"] = sw.ElapsedMilliseconds }; }
        }

        /// <summary>Posts a large body and reads the server-sent events as they arrive.</summary>
        static async Task<JObject> Stream(HttpClient http, string url, int bodyBytes, CancellationToken? ct)
        {
            var sw = Stopwatch.StartNew();
            var r = new JObject();
            try
            {
                var payload = new byte[bodyBytes * 3 / 4]; // base64 grows it back to ~bodyBytes
                new System.Random(1).NextBytes(payload);
                var body = new JObject
                {
                    ["model"] = "mock", ["max_tokens"] = 100, ["stream"] = true,
                    ["messages"] = new JArray(new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JArray(
                            new JObject { ["type"] = "image", ["source"] = new JObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(payload) } },
                            new JObject { ["type"] = "text", ["text"] = "hello" }),
                    }),
                }.ToString(Formatting.None);
                r["requestBytes"] = body.Length;
                var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                using (var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct ?? CancellationToken.None))
                {
                    r["status"] = (int)res.StatusCode;
                    r["headersMs"] = sw.ElapsedMilliseconds;
                    await ReadEvents(res, r, sw, ct ?? CancellationToken.None);
                }
            }
            catch (Exception e) { r["error"] = Flatten(e); }
            r["totalMs"] = sw.ElapsedMilliseconds;
            return r;
        }

        static async Task ReadEvents(HttpResponseMessage res, JObject r, Stopwatch sw, CancellationToken ct)
        {
            var text = new StringBuilder();
            int events = 0;
            // A pending read doesn't watch the token: closing the response on cancel ends it at once.
            using (ct.Register(() => res.Dispose()))
            using (var s = await res.Content.ReadAsStreamAsync())
            using (var reader = new StreamReader(s, Encoding.UTF8))
            {
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!line.StartsWith("data:")) continue;
                    events++;
                    if (events == 1) r["firstEventMs"] = sw.ElapsedMilliseconds;
                    JObject ev;
                    try { ev = JObject.Parse(line.Substring(5).Trim()); } catch { continue; }
                    if ((string)ev["type"] == "content_block_delta") text.Append((string)ev["delta"]?["text"]);
                    if (ev["received_bytes"] != null) r["serverReceivedBytes"] = ev["received_bytes"];
                    if ((string)ev["type"] == "error") r["apiError"] = ev["error"];
                }
            }
            r["events"] = events;
            r["text"] = text.ToString();
        }

        static async Task<JObject> Cancel(HttpClient http, string url)
        {
            var sw = Stopwatch.StartNew();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5)))
            {
                var r = await Stream(http, url, 1000, cts.Token);
                r["cancelledAfterMs"] = sw.ElapsedMilliseconds;
                r["cancelledCleanly"] = r["error"] != null && cts.IsCancellationRequested && sw.ElapsedMilliseconds < 2500;
                return r;
            }
        }

        static async Task<JObject> Real(HttpClient http, string key, string model)
        {
            var sw = Stopwatch.StartNew();
            var r = new JObject { ["model"] = model };
            try
            {
                var body = new JObject
                {
                    ["model"] = model, ["max_tokens"] = 40, ["stream"] = true,
                    ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = "Reply with five words about oxygen." }),
                }.ToString(Formatting.None);
                var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                req.Headers.Add("x-api-key", key);
                req.Headers.Add("anthropic-version", "2023-06-01");
                using (var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                {
                    r["status"] = (int)res.StatusCode;
                    r["headersMs"] = sw.ElapsedMilliseconds;
                    if (res.IsSuccessStatusCode) await ReadEvents(res, r, sw, CancellationToken.None);
                    else { var t = await res.Content.ReadAsStringAsync(); r["body"] = t.Substring(0, Math.Min(300, t.Length)); }
                }
            }
            catch (Exception e) { r["error"] = Flatten(e); }
            r["totalMs"] = sw.ElapsedMilliseconds;
            return r;
        }

        static string Flatten(Exception e)
        {
            var parts = new List<string>();
            for (var x = e; x != null; x = x.InnerException) parts.Add(x.GetType().Name + ": " + x.Message);
            return string.Join(" <- ", parts);
        }
    }
}
