using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace OniAdvisor.Llm
{
    /// <summary>
    /// Anthropic's Messages API (POST /v1/messages, streamed). Caching: an explicit breakpoint on the system
    /// prompt (the big stable part) plus top-level automatic caching for the growing conversation. Thinking is
    /// left at the model's default; thinking blocks come back as OpaqueBlocks and are replayed unchanged.
    /// </summary>
    public sealed class AnthropicProvider : ProviderBase
    {
        public const string DefaultBaseUrl = "https://api.anthropic.com";
        public const string ApiVersion = "2026-10-21";

        public AnthropicProvider(HttpClient http, string apiKey, string baseUrl = DefaultBaseUrl) : base(http, baseUrl, apiKey) { }

        public override string Id => "anthropic";

        protected override void AddHeaders(HttpRequestMessage req)
        {
            req.Headers.Add("x-api-key", ApiKey);
            req.Headers.Add("anthropic-version", ApiVersion);
        }

        public JObject BuildBody(ChatRequest r)
        {
            var body = new JObject
            {
                ["model"] = r.Model,
                ["max_tokens"] = r.MaxTokens,
                ["stream"] = true,
                ["cache_control"] = new JObject { ["type"] = "ephemeral" },
                ["messages"] = new JArray(r.Conversation.Messages.Select(Message).Where(m => ((JArray)m["content"]).Count > 0)),
            };
            if (!string.IsNullOrEmpty(r.System))
                body["system"] = new JArray(new JObject { ["type"] = "text", ["text"] = r.System, ["cache_control"] = new JObject { ["type"] = "ephemeral" } });
            if (r.Tools.Count > 0)
                body["tools"] = new JArray(r.Tools.Select(t => new JObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = t.InputSchema }));
            if (!string.IsNullOrEmpty(r.Effort)) body["output_config"] = new JObject { ["effort"] = r.Effort };
            return body;
        }

        JObject Message(Message m)
        {
            var content = new JArray();
            foreach (var b in m.Blocks)
            {
                switch (b)
                {
                    case TextBlock t when t.Text.Length > 0: content.Add(new JObject { ["type"] = "text", ["text"] = t.Text }); break;
                    case ImageBlock i:
                        content.Add(new JObject { ["type"] = "image", ["source"] = new JObject { ["type"] = "base64", ["media_type"] = i.MediaType, ["data"] = i.Base64 } });
                        break;
                    case ToolCallBlock c:
                        content.Add(new JObject { ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = c.Input ?? new JObject() });
                        break;
                    case ToolResultBlock r:
                        var res = new JObject { ["type"] = "tool_result", ["tool_use_id"] = r.CallId, ["content"] = r.Content };
                        if (r.IsError) res["is_error"] = true;
                        content.Add(res);
                        break;
                    case OpaqueBlock o when o.Provider == Id: content.Add(o.Raw); break;
                }
            }
            // Tool results must come first in a user message.
            var ordered = new JArray(content.Where(x => (string)x["type"] == "tool_result").Concat(content.Where(x => (string)x["type"] != "tool_result")));
            return new JObject { ["role"] = m.Role == Role.User ? "user" : "assistant", ["content"] = ordered };
        }

        public override async Task<ChatResponse> SendAsync(ChatRequest request, IStreamListener listener, CancellationToken ct)
        {
            listener = listener ?? NullListener.Instance;
            var response = new ChatResponse { Message = new Message(Role.Assistant), Model = request.Model };
            var parts = new Dictionary<int, Part>();
            string streamError = null;

            await Stream("/v1/messages", BuildBody(request), (evt, data) =>
            {
                var o = ParseOrNull(data);
                if (o == null) return;
                switch ((string)o["type"])
                {
                    case "message_start":
                        response.Model = (string)o["message"]?["model"] ?? response.Model;
                        ReadUsage(o["message"]?["usage"], response.Usage);
                        break;
                    case "content_block_start":
                        {
                            var block = (JObject)o["content_block"];
                            var part = new Part { Type = (string)block["type"], Start = block };
                            parts[(int)o["index"]] = part;
                            if (part.Type == "tool_use") listener.OnToolCallStart((string)block["name"]);
                            break;
                        }
                    case "content_block_delta":
                        {
                            if (!parts.TryGetValue((int)o["index"], out var part)) break;
                            var d = o["delta"];
                            switch ((string)d["type"])
                            {
                                case "text_delta": part.Text.Append((string)d["text"]); listener.OnText((string)d["text"]); break;
                                case "input_json_delta": part.Json.Append((string)d["partial_json"]); break;
                                case "thinking_delta": part.Text.Append((string)d["thinking"]); break;
                                case "signature_delta": part.Signature = (part.Signature ?? "") + (string)d["signature"]; break;
                            }
                            break;
                        }
                    case "message_delta":
                        response.RawStopReason = (string)o["delta"]?["stop_reason"] ?? response.RawStopReason;
                        if (o["usage"]?["output_tokens"] != null) response.Usage.OutputTokens = (long)o["usage"]["output_tokens"];
                        break;
                    case "error":
                        streamError = (string)o["error"]?["type"] + ": " + (string)o["error"]?["message"];
                        break;
                }
            }, ct).ConfigureAwait(false);

            if (streamError != null) throw new LlmException("stream error " + streamError, retryable: streamError.StartsWith("overloaded"));
            foreach (var kv in parts.OrderBy(p => p.Key)) response.Message.Blocks.Add(kv.Value.ToBlock(Id));
            response.Stop = response.RawStopReason switch
            {
                "end_turn" => StopReason.EndTurn,
                "stop_sequence" => StopReason.EndTurn,
                "tool_use" => StopReason.ToolUse,
                "max_tokens" => StopReason.MaxTokens,
                "refusal" => StopReason.Refusal,
                _ => StopReason.Other,
            };
            return response;
        }

        static void ReadUsage(JToken u, Usage usage)
        {
            if (u == null) return;
            usage.InputTokens = (long?)u["input_tokens"] ?? 0;
            usage.CacheWriteTokens = (long?)u["cache_creation_input_tokens"] ?? 0;
            usage.CacheReadTokens = (long?)u["cache_read_input_tokens"] ?? 0;
            usage.OutputTokens = (long?)u["output_tokens"] ?? 0;
        }

        sealed class Part
        {
            public string Type, Signature;
            public JObject Start;
            public readonly StringBuilder Text = new StringBuilder(), Json = new StringBuilder();

            public Block ToBlock(string provider)
            {
                switch (Type)
                {
                    case "text": return new TextBlock(Text.ToString());
                    case "tool_use":
                        {
                            string raw = Json.ToString();
                            var input = raw.Length == 0 ? (JObject)Start["input"] ?? new JObject() : ParseOrNull(raw);
                            return new ToolCallBlock((string)Start["id"], (string)Start["name"], input, raw);
                        }
                    case "thinking":
                        {
                            var raw = (JObject)Start.DeepClone();
                            raw["thinking"] = (string)raw["thinking"] + Text;
                            if (Signature != null) raw["signature"] = (string)raw["signature"] + Signature;
                            return new OpaqueBlock(provider, raw);
                        }
                    default: return new OpaqueBlock(provider, Start); // redacted_thinking and anything newer: replay as received
                }
            }
        }
    }
}
