using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OniAdvisor.Llm
{
    /// <summary>
    /// The OpenAI Chat Completions format (POST {base}/chat/completions, streamed), which most providers speak:
    /// OpenAI, Google Gemini (its OpenAI-compatible endpoint), OpenRouter, Ollama, LM Studio, and others.
    /// Caching is automatic on the providers that cache; cached input shows up in usage.
    /// </summary>
    public sealed class OpenAiProvider : ProviderBase
    {
        public const string OpenAiUrl = "https://api.openai.com/v1";
        public const string GeminiUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
        public const string OpenRouterUrl = "https://openrouter.ai/api/v1";
        public const string OllamaUrl = "http://localhost:11434/v1";

        readonly string id;
        /// <summary>OpenAI's own API wants "max_completion_tokens"; most others still read "max_tokens".</summary>
        public bool UseMaxCompletionTokens;

        public OpenAiProvider(HttpClient http, string apiKey, string baseUrl = OpenAiUrl, string id = "openai") : base(http, baseUrl, apiKey)
        {
            this.id = id;
            UseMaxCompletionTokens = baseUrl.StartsWith(OpenAiUrl);
        }

        public override string Id => id;

        protected override void AddHeaders(HttpRequestMessage req)
        {
            if (!string.IsNullOrEmpty(ApiKey)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey);
        }

        public JObject BuildBody(ChatRequest r)
        {
            var messages = new JArray();
            if (!string.IsNullOrEmpty(r.System)) messages.Add(new JObject { ["role"] = "system", ["content"] = r.System });
            foreach (var m in r.Conversation.Messages) foreach (var x in Messages(m)) messages.Add(x);
            var body = new JObject
            {
                ["model"] = r.Model,
                ["stream"] = true,
                ["stream_options"] = new JObject { ["include_usage"] = true },
                ["messages"] = messages,
            };
            body[UseMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = r.MaxTokens;
            if (r.Tools.Count > 0)
                body["tools"] = new JArray(r.Tools.Select(t => new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.InputSchema },
                }));
            if (!string.IsNullOrEmpty(r.Effort)) body["reasoning_effort"] = r.Effort;
            return body;
        }

        /// <summary>One neutral message can become several: tool results are separate "tool" messages here.</summary>
        IEnumerable<JObject> Messages(Message m)
        {
            if (m.Role == Role.Assistant)
            {
                var msg = new JObject { ["role"] = "assistant" };
                string text = m.Text;
                msg["content"] = text.Length > 0 ? (JToken)text : JValue.CreateNull();
                var calls = m.Blocks.OfType<ToolCallBlock>().ToList();
                if (calls.Count > 0)
                    msg["tool_calls"] = new JArray(calls.Select(c => new JObject
                    {
                        ["id"] = c.Id, ["type"] = "function",
                        ["function"] = new JObject { ["name"] = c.Name, ["arguments"] = c.Input != null ? c.Input.ToString(Formatting.None) : (c.RawInput ?? "{}") },
                    }));
                yield return msg;
                yield break;
            }
            foreach (var r in m.Blocks.OfType<ToolResultBlock>())
                yield return new JObject { ["role"] = "tool", ["tool_call_id"] = r.CallId, ["content"] = r.IsError ? "ERROR: " + r.Content : r.Content };
            var parts = new JArray();
            foreach (var b in m.Blocks)
            {
                if (b is TextBlock t && t.Text.Length > 0) parts.Add(new JObject { ["type"] = "text", ["text"] = t.Text });
                else if (b is ImageBlock i) parts.Add(new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = $"data:{i.MediaType};base64,{i.Base64}" } });
            }
            if (parts.Count > 0)
                yield return new JObject { ["role"] = "user", ["content"] = parts.Count == 1 && (string)parts[0]["type"] == "text" ? parts[0]["text"] : parts };
        }

        public override async Task<ChatResponse> SendAsync(ChatRequest request, IStreamListener listener, CancellationToken ct)
        {
            listener = listener ?? NullListener.Instance;
            var response = new ChatResponse { Message = new Message(Role.Assistant), Model = request.Model };
            var text = new StringBuilder();
            var calls = new SortedDictionary<int, Call>();

            await Stream("/chat/completions", BuildBody(request), (evt, data) =>
            {
                if (data == "[DONE]") return;
                var o = ParseOrNull(data);
                if (o == null) return;
                if (o["error"] != null) throw new LlmException("stream error: " + ((string)o["error"]?["message"] ?? o["error"].ToString()));
                response.Model = (string)o["model"] ?? response.Model;
                if (o["usage"] is JObject u && u.HasValues)
                {
                    long cached = (long?)u["prompt_tokens_details"]?["cached_tokens"] ?? 0;
                    response.Usage.InputTokens = ((long?)u["prompt_tokens"] ?? 0) - cached;
                    response.Usage.CacheReadTokens = cached;
                    response.Usage.OutputTokens = (long?)u["completion_tokens"] ?? 0;
                }
                if (!(o["choices"] is JArray choices) || choices.Count == 0) return;
                var choice = choices[0];
                var delta = choice["delta"];
                if (delta?["content"] is JValue c && c.Type == JTokenType.String)
                {
                    text.Append((string)c);
                    listener.OnText((string)c);
                }
                if (delta?["tool_calls"] is JArray tcs)
                    foreach (var tc in tcs)
                    {
                        int index = (int?)tc["index"] ?? 0;
                        if (!calls.TryGetValue(index, out var call)) { calls[index] = call = new Call(); }
                        if (tc["id"] != null) call.Id = (string)tc["id"];
                        if (tc["function"]?["name"] != null && call.Name == null) { call.Name = (string)tc["function"]["name"]; listener.OnToolCallStart(call.Name); }
                        call.Args.Append((string)tc["function"]?["arguments"]);
                    }
                if (choice["finish_reason"] is JValue f && f.Type == JTokenType.String) response.RawStopReason = (string)f;
            }, ct).ConfigureAwait(false);

            if (text.Length > 0) response.Message.Blocks.Add(new TextBlock(text.ToString()));
            int n = 0;
            foreach (var call in calls.Values)
            {
                string raw = call.Args.ToString();
                response.Message.Blocks.Add(new ToolCallBlock(call.Id ?? "call_" + n++, call.Name, ParseOrNull(raw), raw));
            }
            response.Stop = response.RawStopReason switch
            {
                "stop" => calls.Count > 0 ? StopReason.ToolUse : StopReason.EndTurn,
                "tool_calls" => StopReason.ToolUse,
                "function_call" => StopReason.ToolUse,
                "length" => StopReason.MaxTokens,
                "content_filter" => StopReason.Refusal,
                _ => calls.Count > 0 ? StopReason.ToolUse : StopReason.Other,
            };
            return response;
        }

        sealed class Call
        {
            public string Id, Name;
            public readonly StringBuilder Args = new StringBuilder();
        }
    }
}
