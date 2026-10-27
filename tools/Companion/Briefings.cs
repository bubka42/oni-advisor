using System.Text.Json.Nodes;

namespace OniAdvisor.Companion;

/// <summary>
/// Keeps every briefing: live/briefing/archive/&lt;time&gt;-&lt;id&gt;.json plus archive/index.json
/// [{file, id, title, cycle, steps, utc}] (newest last) for the in-game panel's briefing list.
/// </summary>
public static class Briefings
{
    /// <returns>the briefing's title</returns>
    public static string Archive(string liveDir, string json)
    {
        var b = JsonNode.Parse(json)!;
        string id = (string?)b["id"] ?? "briefing", title = (string?)b["title"] ?? id;
        var dir = Path.Combine(liveDir, "briefing", "archive");
        Directory.CreateDirectory(dir);
        string safeId = string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
        string file = $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeId}.json";
        File.WriteAllText(Path.Combine(dir, file), json);

        var indexPath = Path.Combine(dir, "index.json");
        JsonArray index;
        try { index = File.Exists(indexPath) ? JsonNode.Parse(File.ReadAllText(indexPath))?.AsArray() ?? new JsonArray() : new JsonArray(); }
        catch { index = new JsonArray(); }
        index.Add(new JsonObject
        {
            ["file"] = file, ["id"] = id, ["title"] = title, ["cycle"] = (int?)b["cycle"],
            ["steps"] = b["steps"]?.AsArray().Count ?? 0, ["utc"] = DateTime.UtcNow.ToString("o"),
        });
        File.WriteAllText(indexPath + ".tmp", index.ToJsonString());
        File.Move(indexPath + ".tmp", indexPath, overwrite: true);
        return title;
    }
}
