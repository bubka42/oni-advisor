using System.Text.Json;
using System.Text.Json.Nodes;

namespace OniAdvisor.Companion;

/// <summary>
/// Where the advisor's tokens go: one line per turn in advisor/usage.jsonl (trigger, colony, model calls,
/// uncached input, cache writes, cache reads, output, conversation size), and a summary in live/usage.json for
/// the game (the conversation's size, the last hour's totals, the share read from the cache). Cache reads are
/// cheap per token but count against usage limits, so the conversation's size matters as much as the cache
/// hit rate: every model call re-reads the whole conversation.
/// </summary>
public sealed class UsageLog
{
    readonly string logFile, summaryFile;
    readonly List<(DateTime utc, AdvisorSession.Usage u)> recent = new();

    public UsageLog(string advisorDir, string liveDir)
    {
        logFile = Path.Combine(advisorDir, "usage.jsonl");
        summaryFile = Path.Combine(liveDir, "usage.json");
    }

    public void Add(string trigger, string colony, AdvisorSession.Usage u)
    {
        if (u.Calls == 0) return;
        var now = DateTime.UtcNow;
        try
        {
            File.AppendAllText(logFile, JsonSerializer.Serialize(new
            {
                utc = now.ToString("o"), trigger, colony, calls = u.Calls, input = u.Input, cacheWrite = u.CacheWrite,
                cacheRead = u.CacheRead, output = u.Output, context = u.Context,
            }) + "\n");
        }
        catch { }
        recent.Add((now, u));
        recent.RemoveAll(r => (now - r.utc).TotalHours > 1);
        WriteSummary(u.Context);
    }

    void WriteSummary(long context)
    {
        long prompt = recent.Sum(r => r.u.PromptTotal), read = recent.Sum(r => r.u.CacheRead);
        var o = new JsonObject
        {
            ["context"] = context,
            ["lastHourTurns"] = recent.Count,
            ["lastHourCalls"] = recent.Sum(r => r.u.Calls),
            ["lastHourPromptTokens"] = prompt,
            ["lastHourOutputTokens"] = recent.Sum(r => r.u.Output),
            ["lastHourCacheShare"] = prompt > 0 ? Math.Round((double)read / prompt, 3) : 0,
            ["utc"] = DateTime.UtcNow.ToString("o"),
        };
        try { File.WriteAllText(summaryFile + ".tmp", o.ToJsonString()); File.Move(summaryFile + ".tmp", summaryFile, overwrite: true); } catch { }
    }

    /// <summary>One line for the companion window: "18k tokens (97% cached) in 2 calls; conversation 41k".</summary>
    public static string Describe(AdvisorSession.Usage u) =>
        $"{K(u.PromptTotal)} tokens in ({(u.PromptTotal > 0 ? 100 * u.CacheRead / u.PromptTotal : 0)}% from cache), {K(u.Output)} out, {u.Calls} call(s); conversation {K(u.Context)}";

    public static string K(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M" : n >= 1000 ? $"{n / 1000}k" : n.ToString();
}
