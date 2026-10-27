using System.Text.Json.Nodes;

namespace OniAdvisor.Companion;

/// <summary>
/// The chat as the in-game advisor panel shows it, Slack-style: a main channel plus threads.
///   live/chat/chat.json    - the last 400 entries {n, who, text, utc, cycle, thread?, briefing?}
///                            who: you | advisor | tip-critical | tip-urgent | tip-later | briefing | thought | activity | note
///                            thread: the id of the thread an entry belongs to (absent = main channel). A thread's id
///                            is its root entry's n as text (any tip, briefing or advisor message), or "plan" for the
///                            colony plan. briefing: the archived briefing file a briefing entry opens.
///   live/chat/stream.json  - the turn in progress {n, state: thinking|writing|idle, text, activity, thread, utc}
/// The panel polls both while open; "n" only grows, so it can tell what is new.
/// </summary>
public sealed class ChatLog
{
    readonly string dir, chatPath, streamPath;
    readonly object gate = new();
    JsonArray entries;
    long n;

    /// <summary>The thread the current turn belongs to; activity and thoughts go there too.</summary>
    public string? CurrentThread { get; set; }

    public ChatLog(string liveDir)
    {
        dir = Path.Combine(liveDir, "chat");
        Directory.CreateDirectory(dir);
        chatPath = Path.Combine(dir, "chat.json");
        streamPath = Path.Combine(dir, "stream.json");
        try { entries = File.Exists(chatPath) ? JsonNode.Parse(File.ReadAllText(chatPath))?.AsArray() ?? new JsonArray() : new JsonArray(); }
        catch { entries = new JsonArray(); }
        n = entries.Count > 0 ? (long?)entries[^1]?["n"] ?? 0 : 0;
    }

    /// <returns>the entry's number (also its thread id, as text, if someone replies to it)</returns>
    public long Add(string who, string text, int cycle, string? thread = null, string? briefing = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        lock (gate)
        {
            var e = new JsonObject { ["n"] = ++n, ["who"] = who, ["text"] = text.Trim(), ["utc"] = DateTime.UtcNow.ToString("o"), ["cycle"] = cycle };
            if (thread != null) e["thread"] = thread;
            if (briefing != null) e["briefing"] = briefing;
            entries.Add(e);
            while (entries.Count > 400) entries.RemoveAt(0);
            Write(chatPath, entries.ToJsonString());
            return n;
        }
    }

    /// <summary>Activity lines and thoughts of the current turn, filed under its thread.</summary>
    public void AddInTurn(string who, string text, int cycle) => Add(who, text, cycle, CurrentThread);

    public void Stream(string state, string text = "", string activity = "")
    {
        lock (gate)
            Write(streamPath, new JsonObject { ["n"] = n, ["state"] = state, ["text"] = text, ["activity"] = activity, ["thread"] = CurrentThread, ["utc"] = DateTime.UtcNow.ToString("o") }.ToJsonString());
    }

    /// <summary>A thread for the prompt: what it is about and what was said in it so far.</summary>
    public (string root, string replies) Thread(string id)
    {
        lock (gate)
        {
            string root = "";
            if (long.TryParse(id, out long rootN))
            {
                var r = entries.FirstOrDefault(e => (long?)e?["n"] == rootN);
                if (r != null) root = $"({r["who"]}) {r["text"]}";
            }
            var said = entries.Where(e => (string?)e?["thread"] == id && (string?)e?["who"] is "you" or "advisor")
                              .TakeLast(12)
                              .Select(e => ((string?)e?["who"] == "you" ? "Player: " : "You: ") + Cut((string?)e?["text"] ?? "", 600));
            return (root, string.Join("\n", said));
        }
    }

    static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    static void Write(string path, string json)
    {
        try
        {
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch { }
    }
}
