using System.Text.Json.Nodes;

namespace OniAdvisor.Companion;

/// <summary>
/// Keeps every briefing: live/briefing/archive/&lt;time&gt;-&lt;id&gt;.json plus archive/index.json
/// [{file, id, title, cycle, steps, utc, chat}] (newest last) for the in-game panel's briefing list. "chat" is the
/// briefing's entry in the chat, whose number is also the id of its discussion thread.
/// </summary>
public static class Briefings
{
    /// <returns>the briefing's title, archive file name and a one-line outline of its steps</returns>
    public static (string title, string file, string outline) Archive(string liveDir, string json)
    {
        var b = JsonNode.Parse(json)!;
        string id = (string?)b["id"] ?? "briefing", title = (string?)b["title"] ?? id;
        var dir = Path.Combine(liveDir, "briefing", "archive");
        Directory.CreateDirectory(dir);
        string safeId = string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
        string file = $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeId}.json";
        File.WriteAllText(Path.Combine(dir, file), json);

        var index = LoadIndex(dir);
        index.Add(new JsonObject
        {
            ["file"] = file, ["id"] = id, ["title"] = title, ["cycle"] = (int?)b["cycle"],
            ["steps"] = b["steps"]?.AsArray().Count ?? 0, ["utc"] = DateTime.UtcNow.ToString("o"),
        });
        SaveIndex(dir, index);
        var steps = b["steps"]?.AsArray().Select(s => (string?)s?["title"]).Where(t => !string.IsNullOrEmpty(t)) ?? Enumerable.Empty<string?>();
        return (title, file, string.Join(" · ", steps));
    }

    /// <summary>Links an archived briefing to its chat entry (= its thread).</summary>
    public static void SetChat(string liveDir, string file, long chatN)
    {
        var dir = Path.Combine(liveDir, "briefing", "archive");
        var index = LoadIndex(dir);
        foreach (var e in index) if ((string?)e?["file"] == file) e!["chat"] = chatN;
        SaveIndex(dir, index);
    }

    static JsonArray LoadIndex(string dir)
    {
        var path = Path.Combine(dir, "index.json");
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsArray() ?? new JsonArray() : new JsonArray(); }
        catch { return new JsonArray(); }
    }

    static void SaveIndex(string dir, JsonArray index)
    {
        var path = Path.Combine(dir, "index.json");
        File.WriteAllText(path + ".tmp", index.ToJsonString());
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
