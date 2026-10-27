using System.Text.Json.Nodes;

namespace OniAdvisor.Companion;

/// <summary>
/// The chat as the in-game advisor panel shows it:
///   live/chat/chat.json    - the last 200 entries {n, who: you|advisor|thought|activity|note, text, utc, cycle}
///   live/chat/stream.json  - the turn in progress {n, state: thinking|writing|idle, text, activity, utc}
/// The panel polls both while open; "n" only grows, so it can tell what is new.
/// </summary>
public sealed class ChatLog
{
    readonly string dir, chatPath, streamPath;
    readonly object gate = new();
    JsonArray entries;
    long n;

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

    public void Add(string who, string text, int cycle)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (gate)
        {
            entries.Add(new JsonObject { ["n"] = ++n, ["who"] = who, ["text"] = text.Trim(), ["utc"] = DateTime.UtcNow.ToString("o"), ["cycle"] = cycle });
            while (entries.Count > 200) entries.RemoveAt(0);
            Write(chatPath, entries.ToJsonString());
        }
    }

    public void Stream(string state, string text = "", string activity = "")
    {
        lock (gate)
            Write(streamPath, new JsonObject { ["n"] = n, ["state"] = state, ["text"] = text, ["activity"] = activity, ["utc"] = DateTime.UtcNow.ToString("o") }.ToJsonString());
    }

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
