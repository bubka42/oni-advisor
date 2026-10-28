using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;
using OniAdvisor.Llm;

namespace OniAdvisor.Llm.Tests;

/// <summary>Answers requests from a queue of canned responses and records what was sent.</summary>
sealed class FakeHandler : HttpMessageHandler
{
    readonly Queue<Func<HttpResponseMessage>> responses = new();
    public readonly List<(string url, JObject body, HttpRequestHeaders headers)> Requests = new();

    public FakeHandler Sse(string events) { responses.Enqueue(() => Ok(events)); return this; }

    public FakeHandler Status(int status, string body, int? retryAfterSeconds = null)
    {
        responses.Enqueue(() =>
        {
            var res = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfterSeconds is int s) res.Headers.Add("retry-after", s.ToString());
            return res;
        });
        return this;
    }

    static HttpResponseMessage Ok(string events) =>
        new(HttpStatusCode.OK) { Content = new StringContent(events.Replace("\r\n", "\n"), Encoding.UTF8, "text/event-stream") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.RequestUri!.ToString(), JObject.Parse(await request.Content!.ReadAsStringAsync(ct)), request.Headers));
        return responses.Dequeue()();
    }
}

sealed class Recorder : IAgentListener
{
    public readonly StringBuilder Text = new();
    public readonly List<string> ToolStarts = new(), ToolRuns = new();
    public void OnText(string delta) => Text.Append(delta);
    public void OnToolCallStart(string name) => ToolStarts.Add(name);
    public void OnToolRun(string name, JObject input) => ToolRuns.Add(name + " " + input.ToString(Newtonsoft.Json.Formatting.None));
}

/// <summary>A provider that replays scripted responses (for the agent loop).</summary>
sealed class ScriptedProvider : IChatProvider
{
    readonly Queue<ChatResponse> script = new();
    public readonly List<int> MessageCounts = new();
    public string Id => "scripted";
    public ScriptedProvider Then(StopReason stop, params Block[] blocks)
    {
        script.Enqueue(new ChatResponse { Message = new Message(Role.Assistant, blocks), Stop = stop, RawStopReason = stop.ToString(), Model = "claude-haiku-5-5", Usage = new Usage { InputTokens = 1_000_000 } });
        return this;
    }
    public Task<ChatResponse> SendAsync(ChatRequest request, IStreamListener listener, CancellationToken ct)
    {
        MessageCounts.Add(request.Conversation.Messages.Count);
        return Task.FromResult(script.Dequeue());
    }
}

sealed class EchoTools : IToolRunner
{
    public IReadOnlyList<ToolDefinition> Tools { get; } = new[]
    {
        new ToolDefinition("map_query", "Ask the game about the map", JObject.Parse("{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}},\"required\":[\"q\"]}")),
    };
    public Task<string> RunAsync(string name, JObject input, CancellationToken ct) =>
        (string)input["q"] == "fail" ? throw new InvalidOperationException("no such region") : Task.FromResult("result for " + input["q"]);
}
