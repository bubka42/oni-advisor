using System.Text;
using System.Text.Json.Nodes;

namespace OniAdvisor.Companion;

/// <summary>
/// One conversation history shared by live mode and briefings (live/conversation.json). Live answers and
/// briefings come from two separate Claude sessions; each prompt to either gets the recent entries from both,
/// so neither denies or contradicts what the other just told the player.
/// </summary>
public sealed class ConversationLog
{
    readonly string path;
    readonly object gate = new();

    public ConversationLog(string liveDir)
    {
        path = Path.Combine(liveDir, "conversation.json");
        // First run: start from the answers the in-game panel already keeps (cycle unknown there).
        var panelLog = Path.Combine(liveDir, "advisor-log.json");
        if (File.Exists(path) || !File.Exists(panelLog)) return;
        try
        {
            var arr = new JsonArray();
            foreach (var e in JsonNode.Parse(File.ReadAllText(panelLog))?.AsArray() ?? new JsonArray())
                arr.Add(new JsonObject { ["utc"] = (string?)e?["utc"], ["cycle"] = "?", ["kind"] = "live", ["q"] = (string?)e?["q"], ["a"] = (string?)e?["a"] });
            var briefing = Path.Combine(liveDir, "briefing", "briefing.json");
            if (File.Exists(briefing))
                arr.Add(new JsonObject { ["utc"] = File.GetLastWriteTimeUtc(briefing).ToString("o"), ["cycle"] = "?", ["kind"] = "briefing", ["q"] = null, ["a"] = Summarize(File.ReadAllText(briefing)) });
            var sorted = arr.OrderBy(x => (string?)x?["utc"] is { } u && DateTime.TryParse(u, out var d) ? d.ToUniversalTime() : DateTime.MinValue).Select(x => x?.DeepClone()).ToArray();
            File.WriteAllText(path, new JsonArray(sorted).ToJsonString());
        }
        catch { }
    }

    /// <param name="kind">live, look, pod, plan or briefing</param>
    public void Add(string kind, int cycle, string? question, string answer)
    {
        lock (gate)
        {
            try
            {
                var arr = Load();
                arr.Add(new JsonObject
                {
                    ["utc"] = DateTime.UtcNow.ToString("o"), ["cycle"] = cycle, ["kind"] = kind,
                    ["q"] = question, ["a"] = answer,
                });
                while (arr.Count > 60) arr.RemoveAt(0);
                File.WriteAllText(path, arr.ToJsonString());
            }
            catch { }
        }
    }

    /// <summary>The last entries as prompt text, oldest first; briefings get more room than live answers.</summary>
    public string Recent(int count, int maxChars = 4000)
    {
        JsonArray arr;
        lock (gate) { try { arr = Load(); } catch { return ""; } }
        var lines = new List<string>();
        int total = 0;
        for (int i = arr.Count - 1; i >= 0 && lines.Count < count; i--)
        {
            var e = arr[i];
            string kind = (string?)e?["kind"] ?? "live", q = (string?)e?["q"] ?? "", a = (string?)e?["a"] ?? "";
            DateTime.TryParse((string?)e?["utc"], out var utc);
            string ago = Ago(DateTime.UtcNow - utc.ToUniversalTime());
            string line = kind == "briefing"
                ? $"- [{ago}, cycle {e?["cycle"]}, briefing{(q.Length > 0 ? " on: " + Cut(q, 120) : "")}] {Cut(a, 1200)}"
                : $"- [{ago}, cycle {e?["cycle"]}, {kind}] Player: {Cut(q, 200)} / You: {Cut(a, 350)}";
            if (total + line.Length > maxChars) break;
            total += line.Length;
            lines.Add(line);
        }
        lines.Reverse();
        return string.Join("\n", lines);
    }

    /// <summary>A briefing as one paragraph for the log: title, then each step's spoken and panel text.</summary>
    public static string Summarize(string briefingJson)
    {
        try
        {
            var b = JsonNode.Parse(briefingJson)!;
            var sb = new StringBuilder($"\"{b["title"]}\": ");
            foreach (var s in b["steps"]?.AsArray() ?? new JsonArray())
            {
                sb.Append($"({s?["title"]}) {s?["say"]}");
                if ((string?)s?["text"] is { Length: > 0 } t) sb.Append(" " + t);
                sb.Append(' ');
            }
            return sb.ToString().Trim();
        }
        catch { return "(briefing unreadable)"; }
    }

    JsonArray Load() => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsArray() ?? new JsonArray() : new JsonArray();

    static string Cut(string s, int max)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s[..max] + "…";
    }

    static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? "just now" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min ago" : $"{(int)t.TotalHours} h ago";
}
