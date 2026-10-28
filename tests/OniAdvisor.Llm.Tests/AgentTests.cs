using Newtonsoft.Json.Linq;
using Xunit;

namespace OniAdvisor.Llm.Tests;

public class AgentTests
{
    static ToolCallBlock Call(string id, string q) => new(id, "map_query", JObject.Parse($"{{\"q\":\"{q}\"}}"), $"{{\"q\":\"{q}\"}}");

    [Fact]
    public async Task Runs_tools_until_the_model_answers()
    {
        var provider = new ScriptedProvider()
            .Then(StopReason.ToolUse, new TextBlock("Looking."), Call("t1", "pipes"), Call("t2", "fail"))
            .Then(StopReason.EndTurn, new TextBlock("The gas pipe is not connected."));
        var agent = new Agent(provider, new EchoTools(), null) { Model = "claude-haiku-5-5" };
        var conv = new Conversation();
        conv.AddUser("Why no oxygen?");
        var rec = new Recorder();

        var result = await agent.RunTurnAsync(conv, rec, CancellationToken.None);

        Assert.Equal("The gas pipe is not connected.", result.Text);
        Assert.Equal(2, result.Requests);
        Assert.Equal(2, result.ToolCalls);
        Assert.Equal(new[] { 1, 3 }, provider.MessageCounts);
        var results = conv.Messages[2].Blocks.OfType<ToolResultBlock>().ToList();
        Assert.Equal("result for pipes", results[0].Content);
        Assert.True(results[1].IsError);
        Assert.Equal("no such region", results[1].Content);
        Assert.Equal(2, rec.ToolRuns.Count);
    }

    [Fact]
    public async Task Does_not_run_tools_from_a_cut_off_response()
    {
        var provider = new ScriptedProvider().Then(StopReason.MaxTokens, Call("t1", "pipes"));
        var agent = new Agent(provider, new EchoTools(), null) { Model = "m" };
        var conv = new Conversation();
        conv.AddUser("q");

        var result = await agent.RunTurnAsync(conv, null, CancellationToken.None);

        Assert.Equal(0, result.ToolCalls);
        var r = conv.Messages.Last().Blocks.OfType<ToolResultBlock>().Single();
        Assert.True(r.IsError);
        // The next question merges into that same user message, keeping roles alternating.
        conv.AddUser("next question");
        Assert.Equal(3, conv.Messages.Count);
    }

    [Fact]
    public async Task Invalid_tool_arguments_are_reported_not_run()
    {
        var provider = new ScriptedProvider()
            .Then(StopReason.ToolUse, new ToolCallBlock("t1", "map_query", null, "{\"q\": \"pip"))
            .Then(StopReason.EndTurn, new TextBlock("ok"));
        var agent = new Agent(provider, new EchoTools(), null) { Model = "m" };
        var conv = new Conversation();
        conv.AddUser("q");

        var result = await agent.RunTurnAsync(conv, null, CancellationToken.None);

        Assert.Equal(0, result.ToolCalls);
        Assert.Contains("not valid JSON", conv.Messages[2].Blocks.OfType<ToolResultBlock>().Single().Content);
    }

    [Fact]
    public async Task Stops_at_the_spending_limit()
    {
        // Each scripted response uses 1M input tokens of Claude Haiku 5.5 = $0.10.
        var provider = new ScriptedProvider()
            .Then(StopReason.ToolUse, Call("t1", "a"))
            .Then(StopReason.ToolUse, Call("t2", "b"))
            .Then(StopReason.EndTurn, new TextBlock("done"));
        var cost = new CostTracker { LimitDollars = 0.15 };
        var agent = new Agent(provider, new EchoTools(), cost) { Model = "claude-haiku-5-5" };
        var conv = new Conversation();
        conv.AddUser("q");

        await Assert.ThrowsAsync<BudgetExceededException>(() => agent.RunTurnAsync(conv, null, CancellationToken.None));
        Assert.Equal(0.20, cost.Dollars, 6);
    }

    [Fact]
    public void Prices_cache_reads_and_writes()
    {
        var p = Prices.For("claude-opus-5-5");
        var dollars = p.Cost(new Usage { InputTokens = 1_000_000, OutputTokens = 1_000_000, CacheWriteTokens = 1_000_000, CacheReadTokens = 1_000_000 });
        Assert.Equal(4 + 20 + 5 + 0.20, dollars, 6);
        Assert.Null(Prices.For("some-unknown-model"));
    }
}
