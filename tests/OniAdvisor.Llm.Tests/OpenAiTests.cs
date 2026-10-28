using Newtonsoft.Json.Linq;
using Xunit;

namespace OniAdvisor.Llm.Tests;

public class OpenAiTests
{
    const string TextStream = """
        data: {"id":"c1","object":"chat.completion.chunk","model":"gpt-x","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

        data: {"id":"c1","object":"chat.completion.chunk","model":"gpt-x","choices":[{"index":0,"delta":{"content":"Dig out "},"finish_reason":null}]}

        data: {"id":"c1","object":"chat.completion.chunk","model":"gpt-x","choices":[{"index":0,"delta":{"content":"the copper."},"finish_reason":null}]}

        data: {"id":"c1","object":"chat.completion.chunk","model":"gpt-x","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: {"id":"c1","object":"chat.completion.chunk","model":"gpt-x","choices":[],"usage":{"prompt_tokens":1000,"completion_tokens":12,"prompt_tokens_details":{"cached_tokens":800}}}

        data: [DONE]

        """;

    const string ToolStream = """
        data: {"choices":[{"index":0,"delta":{"role":"assistant","content":null,"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"map_query","arguments":""}}]},"finish_reason":null}]}

        data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"q\":\"bui"}}]},"finish_reason":null}]}

        data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"map_query","arguments":"{\"q\":\"ports\"}"}}]},"finish_reason":null}]}

        data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ldings\"}"}}]},"finish_reason":null}]}

        data: {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

        data: [DONE]

        """;

    static ChatRequest Request()
    {
        var c = new Conversation();
        c.AddUser("What next?");
        return new ChatRequest { Model = "gpt-x", System = "You advise on ONI.", Conversation = c };
    }

    [Fact]
    public async Task Streams_text_and_splits_cached_input()
    {
        var handler = new FakeHandler().Sse(TextStream);
        var rec = new Recorder();
        var res = await new OpenAiProvider(new HttpClient(handler), "key").SendAsync(Request(), rec, CancellationToken.None);

        Assert.Equal("Dig out the copper.", res.Message.Text);
        Assert.Equal("Dig out the copper.", rec.Text.ToString());
        Assert.Equal(StopReason.EndTurn, res.Stop);
        Assert.Equal(200, res.Usage.InputTokens);
        Assert.Equal(800, res.Usage.CacheReadTokens);
        Assert.Equal(12, res.Usage.OutputTokens);
    }

    [Fact]
    public async Task Assembles_parallel_tool_calls_by_index()
    {
        var handler = new FakeHandler().Sse(ToolStream);
        var res = await new OpenAiProvider(new HttpClient(handler), "key").SendAsync(Request(), null, CancellationToken.None);

        Assert.Equal(StopReason.ToolUse, res.Stop);
        var calls = res.ToolCalls.ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(("call_a", "buildings"), (calls[0].Id, (string)calls[0].Input["q"]));
        Assert.Equal(("call_b", "ports"), (calls[1].Id, (string)calls[1].Input["q"]));
    }

    [Fact]
    public async Task Sends_tools_images_and_tool_results_in_chat_completions_shape()
    {
        var handler = new FakeHandler().Sse(TextStream);
        var req = Request();
        req.Tools.Add(new EchoTools().Tools[0]);
        req.Conversation.Add(Role.User, new ImageBlock("image/png", "BBBB"));
        req.Conversation.Add(Role.Assistant, new TextBlock("Checking."), new ToolCallBlock("call_a", "map_query", JObject.Parse("{\"q\":\"x\"}"), "{\"q\":\"x\"}"));
        req.Conversation.Add(Role.User, new ToolResultBlock("call_a", "nothing there", isError: true));

        await new OpenAiProvider(new HttpClient(handler), "secret").SendAsync(req, null, CancellationToken.None);

        var (url, body, headers) = handler.Requests[0];
        Assert.Equal("https://api.openai.com/v1/chat/completions", url);
        Assert.Equal("Bearer secret", headers.GetValues("Authorization").Single());
        Assert.NotNull(body["max_completion_tokens"]);
        Assert.True((bool)body["stream_options"]["include_usage"]);
        Assert.Equal("function", (string)body["tools"][0]["type"]);
        var m = (JArray)body["messages"];
        Assert.Equal(new[] { "system", "user", "assistant", "tool" }, m.Select(x => (string)x["role"]));
        Assert.Equal("image_url", (string)m[1]["content"][1]["type"]);
        Assert.StartsWith("data:image/png;base64,", (string)m[1]["content"][1]["image_url"]["url"]);
        Assert.Equal("{\"q\":\"x\"}", (string)m[2]["tool_calls"][0]["function"]["arguments"]);
        Assert.Equal("call_a", (string)m[3]["tool_call_id"]);
        Assert.StartsWith("ERROR:", (string)m[3]["content"]);
    }

    [Fact]
    public void Other_compatible_providers_get_max_tokens()
    {
        var body = new OpenAiProvider(new HttpClient(), "k", OpenAiProvider.GeminiUrl, "gemini").BuildBody(Request());
        Assert.NotNull(body["max_tokens"]);
        Assert.Null(body["max_completion_tokens"]);
    }
}
