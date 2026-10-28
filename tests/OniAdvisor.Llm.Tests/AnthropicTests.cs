using Newtonsoft.Json.Linq;
using Xunit;

namespace OniAdvisor.Llm.Tests;

public class AnthropicTests
{
    const string TextStream = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[],"usage":{"input_tokens":120,"cache_creation_input_tokens":3000,"cache_read_input_tokens":9000,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: ping
        data: {"type":"ping"}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Build a second "}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"electrolyzer."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":42}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    const string ToolStream = """
        event: message_start
        data: {"type":"message_start","message":{"model":"claude-sonnet-5-5","usage":{"input_tokens":50,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig123"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Let me check the pipes."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: content_block_start
        data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"map_query","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"q\": \"pip"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"es 120 200\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":2}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":30}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    static ChatRequest Request(string text = "What next?") =>
        new() { Model = "claude-sonnet-5-5", System = "You advise on ONI.", Conversation = Conv(text) };

    static Conversation Conv(string text) { var c = new Conversation(); c.AddUser(text); return c; }

    [Fact]
    public async Task Streams_text_and_reads_usage_including_cache()
    {
        var handler = new FakeHandler().Sse(TextStream);
        var provider = new AnthropicProvider(new HttpClient(handler), "key");
        var rec = new Recorder();

        var res = await provider.SendAsync(Request(), rec, CancellationToken.None);

        Assert.Equal("Build a second electrolyzer.", res.Message.Text);
        Assert.Equal("Build a second electrolyzer.", rec.Text.ToString());
        Assert.Equal(StopReason.EndTurn, res.Stop);
        Assert.Equal(120, res.Usage.InputTokens);
        Assert.Equal(3000, res.Usage.CacheWriteTokens);
        Assert.Equal(9000, res.Usage.CacheReadTokens);
        Assert.Equal(42, res.Usage.OutputTokens);
    }

    [Fact]
    public async Task Sends_the_documented_request_shape()
    {
        var handler = new FakeHandler().Sse(TextStream);
        var provider = new AnthropicProvider(new HttpClient(handler), "secret");
        var req = Request();
        req.Tools.Add(new EchoTools().Tools[0]);
        req.Conversation.Add(Role.User, new ImageBlock("image/jpeg", "AAAA"));

        await provider.SendAsync(req, null, CancellationToken.None);

        var (url, body, headers) = handler.Requests[0];
        Assert.Equal("https://api.anthropic.com/v1/messages", url);
        Assert.Equal("secret", headers.GetValues("x-api-key").Single());
        Assert.Equal(AnthropicProvider.ApiVersion, headers.GetValues("anthropic-version").Single());
        Assert.True((bool)body["stream"]);
        Assert.Equal("ephemeral", (string)body["cache_control"]["type"]);
        Assert.Equal("ephemeral", (string)body["system"][0]["cache_control"]["type"]);
        Assert.Equal("map_query", (string)body["tools"][0]["name"]);
        Assert.NotNull(body["tools"][0]["input_schema"]);
        Assert.Null(body["temperature"]);
        var content = (JArray)body["messages"][0]["content"];
        Assert.Equal("text", (string)content[0]["type"]);
        Assert.Equal("image", (string)content[1]["type"]);
        Assert.Equal("base64", (string)content[1]["source"]["type"]);
    }

    [Fact]
    public async Task Assembles_tool_calls_and_keeps_thinking_blocks_for_replay()
    {
        var handler = new FakeHandler().Sse(ToolStream).Sse(TextStream);
        var provider = new AnthropicProvider(new HttpClient(handler), "key");
        var rec = new Recorder();
        var req = Request();

        var res = await provider.SendAsync(req, rec, CancellationToken.None);

        Assert.Equal(StopReason.ToolUse, res.Stop);
        var call = res.ToolCalls.Single();
        Assert.Equal("toolu_1", call.Id);
        Assert.Equal("pipes 120 200", (string)call.Input["q"]);
        Assert.Equal(new[] { "map_query" }, rec.ToolStarts);
        var thinking = res.Message.Blocks.OfType<OpaqueBlock>().Single();
        Assert.Equal("sig123", (string)thinking.Raw["signature"]);

        // Next request: the thinking block goes back unchanged, before the text and the tool call; results come first in the user turn.
        req.Conversation.Add(Role.Assistant, res.Message.Blocks.ToArray());
        req.Conversation.Add(Role.User, new TextBlock("also this"), new ToolResultBlock("toolu_1", "3 pipes, 1 not connected"));
        await provider.SendAsync(req, null, CancellationToken.None);
        var messages = (JArray)handler.Requests[1].body["messages"];
        var assistant = (JArray)messages[1]["content"];
        Assert.Equal(new[] { "thinking", "text", "tool_use" }, assistant.Select(b => (string)b["type"]));
        Assert.Equal("sig123", (string)assistant[0]["signature"]);
        var user = (JArray)messages[2]["content"];
        Assert.Equal("tool_result", (string)user[0]["type"]);
        Assert.Equal("toolu_1", (string)user[0]["tool_use_id"]);
    }

    [Fact]
    public void Thinking_blocks_are_not_sent_to_another_provider()
    {
        var conv = new Conversation();
        conv.AddUser("hi");
        conv.Add(Role.Assistant, new OpaqueBlock("anthropic", JObject.Parse("{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"x\"}")), new TextBlock("hello"));
        var body = new OpenAiProvider(new HttpClient(), "k").BuildBody(new ChatRequest { Model = "gpt", Conversation = conv });
        Assert.Equal("hello", (string)body["messages"][1]["content"]);
    }

    [Fact]
    public async Task Retries_rate_limits_then_succeeds()
    {
        var handler = new FakeHandler()
            .Status(429, "{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"slow down\"}}", retryAfterSeconds: 0)
            .Status(529, "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"overloaded\"}}", retryAfterSeconds: 0)
            .Sse(TextStream);
        var provider = new AnthropicProvider(new HttpClient(handler), "key");

        var res = await provider.SendAsync(Request(), null, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("Build a second electrolyzer.", res.Message.Text);
    }

    [Fact]
    public async Task Does_not_retry_bad_requests_and_reports_the_api_message()
    {
        var handler = new FakeHandler().Status(400, "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"max_tokens: too large\"}}");
        var provider = new AnthropicProvider(new HttpClient(handler), "key");

        var e = await Assert.ThrowsAsync<LlmException>(() => provider.SendAsync(Request(), null, CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Equal(400, e.Status);
        Assert.False(e.Retryable);
        Assert.Contains("max_tokens: too large", e.Message);
    }

    [Fact]
    public async Task Reports_errors_sent_inside_the_stream()
    {
        var handler = new FakeHandler().Sse("""
            event: error
            data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}

            """);
        var provider = new AnthropicProvider(new HttpClient(handler), "key");

        var e = await Assert.ThrowsAsync<LlmException>(() => provider.SendAsync(Request(), null, CancellationToken.None));
        Assert.True(e.Retryable);
    }
}
