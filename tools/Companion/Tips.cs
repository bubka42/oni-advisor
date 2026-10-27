using System.Text.Json.Nodes;

namespace OniAdvisor.Companion;

/// <summary>
/// Unasked tips for the game's light bulb: live/tips.json = the last 50 {n, level: critical|urgent|later, text,
/// cycle, utc}. The mod shows a hanging bulb in the colour of the most urgent unseen tip (it keeps its own
/// record of what the player has seen) and pauses the game for a critical one.
/// </summary>
public static class Tips
{
    public static void Add(string liveDir, string level, string text, int cycle, long chatN)
    {
        try
        {
            var path = Path.Combine(liveDir, "tips.json");
            JsonArray arr;
            try { arr = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsArray() ?? new JsonArray() : new JsonArray(); }
            catch { arr = new JsonArray(); }
            long n = arr.Count > 0 ? ((long?)arr[^1]?["n"] ?? 0) + 1 : 1;
            arr.Add(new JsonObject { ["n"] = n, ["level"] = level, ["text"] = text, ["cycle"] = cycle, ["chat"] = chatN, ["utc"] = DateTime.UtcNow.ToString("o") });
            while (arr.Count > 50) arr.RemoveAt(0);
            File.WriteAllText(path + ".tmp", arr.ToJsonString());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch { }
    }
}
