using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OniAdvisor.Llm
{
    public interface IChatProvider
    {
        /// <summary>Short id, e.g. "anthropic" or "openai"; OpaqueBlocks are replayed only to the same provider.</summary>
        string Id { get; }
        Task<ChatResponse> SendAsync(ChatRequest request, IStreamListener listener, CancellationToken ct);
    }

    /// <summary>An error from the model API. Retryable: rate limits, overload, server errors, dropped connections.</summary>
    public sealed class LlmException : Exception
    {
        public int Status { get; }
        public bool Retryable { get; }
        public TimeSpan? RetryAfter { get; }
        public LlmException(string message, int status = 0, bool retryable = false, TimeSpan? retryAfter = null, Exception inner = null)
            : base(message, inner) { Status = status; Retryable = retryable; RetryAfter = retryAfter; }
    }

    /// <summary>Shared HTTP plumbing: POST a JSON body, retry what is retryable before any data arrived, stream the reply.</summary>
    public abstract class ProviderBase : IChatProvider
    {
        protected readonly HttpClient Http;
        protected readonly string BaseUrl, ApiKey;
        public int MaxRetries = 3;

        protected ProviderBase(HttpClient http, string baseUrl, string apiKey)
        {
            Http = http;
            BaseUrl = baseUrl.TrimEnd('/');
            ApiKey = apiKey;
        }

        public abstract string Id { get; }
        public abstract Task<ChatResponse> SendAsync(ChatRequest request, IStreamListener listener, CancellationToken ct);

        protected abstract void AddHeaders(HttpRequestMessage req);

        /// <summary>Sends the request (with retries) and hands each server-sent event to onEvent.</summary>
        protected async Task Stream(string path, JObject body, Action<string, string> onEvent, CancellationToken ct)
        {
            string json = body.ToString(Formatting.None);
            for (int attempt = 0; ; attempt++)
            {
                HttpResponseMessage res = null;
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                    AddHeaders(req);
                    res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!res.IsSuccessStatusCode) throw await Failure(res).ConfigureAwait(false);
                }
                catch (LlmException e) when (e.Retryable && attempt < MaxRetries)
                {
                    res?.Dispose();
                    await Task.Delay(e.RetryAfter ?? Backoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                catch (HttpRequestException) when (attempt < MaxRetries)
                {
                    res?.Dispose();
                    await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                catch (HttpRequestException e)
                {
                    throw new LlmException("connection failed: " + e.Message, retryable: true, inner: e);
                }
                using (res) await Sse.Read(res, onEvent, ct).ConfigureAwait(false);
                return;
            }
        }

        static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt + 1)));

        static async Task<LlmException> Failure(HttpResponseMessage res)
        {
            int status = (int)res.StatusCode;
            string text = "";
            try { text = await res.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
            string message = text;
            try
            {
                var o = JObject.Parse(text);
                message = (string)o["error"]?["message"] ?? (string)o["message"] ?? (string)o["error"] ?? text;
            }
            catch { }
            TimeSpan? after = null;
            if (res.Headers.RetryAfter?.Delta is TimeSpan d) after = d;
            else if (res.Headers.TryGetValues("retry-after", out var v) && double.TryParse(v.FirstOrDefault(), out var secs)) after = TimeSpan.FromSeconds(secs);
            bool retryable = status == 408 || status == 409 || status == 429 || status >= 500;
            return new LlmException($"{status} {res.ReasonPhrase}: {Short(message)}", status, retryable, after);
        }

        protected static string Short(string s, int max = 400) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max) + "…";

        protected static JObject ParseOrNull(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            try { return JToken.Parse(json) as JObject; } catch { return null; }
        }
    }
}
