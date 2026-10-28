using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace OniAdvisor.Llm
{
    /// <summary>Runs the advisor's tools (map queries, reading/writing its files, ...).</summary>
    public interface IToolRunner
    {
        IReadOnlyList<ToolDefinition> Tools { get; }
        /// <summary>The result text for the model. Throwing reports the error to the model instead.</summary>
        Task<string> RunAsync(string name, JObject input, CancellationToken ct);
    }

    public interface IAgentListener : IStreamListener
    {
        /// <summary>A tool is about to run (for "checking which ports are connected…" status lines).</summary>
        void OnToolRun(string name, JObject input);
    }

    public sealed class TurnResult
    {
        /// <summary>The model's final text (the last response's text).</summary>
        public string Text;
        public StopReason Stop;
        public int Requests, ToolCalls;
        public readonly Usage Usage = new Usage();
    }

    /// <summary>
    /// One advisor turn: ask the model, run the tools it calls, send back the results, repeat until it answers.
    /// Everything said and done is appended to the conversation.
    /// </summary>
    public sealed class Agent
    {
        readonly IChatProvider provider;
        readonly IToolRunner tools;
        public readonly CostTracker Cost;
        public string Model, System, Effort;
        public int MaxTokens = 16000;
        public int MaxSteps = 12;

        public Agent(IChatProvider provider, IToolRunner tools, CostTracker cost)
        {
            this.provider = provider;
            this.tools = tools;
            Cost = cost ?? new CostTracker();
        }

        public async Task<TurnResult> RunTurnAsync(Conversation conversation, IAgentListener listener, CancellationToken ct)
        {
            var result = new TurnResult();
            for (int step = 0; step < MaxSteps; step++)
            {
                Cost.CheckBudget();
                var request = new ChatRequest
                {
                    Model = Model, System = System, Conversation = conversation, MaxTokens = MaxTokens, Effort = Effort,
                    Tools = tools?.Tools.ToList() ?? new List<ToolDefinition>(),
                };
                var response = await provider.SendAsync(request, listener, ct).ConfigureAwait(false);
                result.Requests++;
                result.Usage.Add(response.Usage);
                Cost.Add(response.Model ?? Model, response.Usage);
                conversation.Add(Role.Assistant, response.Message.Blocks.ToArray());
                result.Text = response.Message.Text;
                result.Stop = response.Stop;

                var calls = response.ToolCalls.ToList();
                if (calls.Count == 0) return result;

                // Every tool call needs a result in the next message, even when it isn't run.
                var results = new List<Block>();
                bool runnable = response.Stop == StopReason.ToolUse;
                foreach (var call in calls)
                {
                    if (!runnable)
                    {
                        results.Add(new ToolResultBlock(call.Id, $"Not run: the response stopped early ({response.RawStopReason}).", true));
                        continue;
                    }
                    if (call.Input == null || tools == null)
                    {
                        results.Add(new ToolResultBlock(call.Id, call.Input == null ? "Not run: the arguments were not valid JSON: " + call.RawInput : "No tools are available.", true));
                        continue;
                    }
                    result.ToolCalls++;
                    (listener as IAgentListener)?.OnToolRun(call.Name, call.Input);
                    try { results.Add(new ToolResultBlock(call.Id, await tools.RunAsync(call.Name, call.Input, ct).ConfigureAwait(false))); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { results.Add(new ToolResultBlock(call.Id, e.Message, true)); }
                }
                conversation.Add(Role.User, results.ToArray());
                if (!runnable) return result;
            }
            result.Text = (result.Text ?? "") + "\n(The advisor stopped after " + MaxSteps + " steps.)";
            return result;
        }
    }
}
