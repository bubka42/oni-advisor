using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OniAdvisor.Companion;

/// <summary>
/// Fetches the facts a live question most likely needs before asking Claude, so the fast model answers from
/// data instead of guessing (in testing it never ran a query on its own): buildings with their status around
/// the cursor and the selection, and the gas around the cursor. Uses the mod's query files (the same protocol
/// as MapQuery "live"), all requests in parallel; the game answers within ~0.2 s.
/// </summary>
public static class LiveFacts
{
    public static async Task<string> FetchAsync(string liveDir, string viewJson, string? question = null)
    {
        JsonNode? view;
        try { view = JsonNode.Parse(viewJson); } catch { return ""; }
        if (view == null) return "";

        var spots = new List<(string what, int x, int y)>();
        if (Coords((string?)view["cursor"], @"^\((\d+),(\d+)\)") is { } c) spots.Add(("cursor", c.x, c.y));
        if (Coords((string?)view["selected"], @"@(\d+),(\d+)") is { } s && !spots.Any(p => Math.Abs(p.x - s.x) < 8 && Math.Abs(p.y - s.y) < 5))
            spots.Add(("selection", s.x, s.y));
        if (spots.Count == 0) return "";

        var tasks = new List<(string title, Task<string?> answer)>();
        foreach (var (what, x, y) in spots)
        {
            tasks.Add(($"Buildings near the {what} (x{x - 12}-{x + 12}, y{y - 7}-{y + 7}), with status",
                       Query(liveDir, new JsonObject { ["kind"] = "buildings", ["x0"] = x - 12, ["y0"] = y - 7, ["x1"] = x + 12, ["y1"] = y + 7 })));
            tasks.Add(($"Gas around the {what}",
                       Query(liveDir, new JsonObject { ["kind"] = "region", ["x0"] = x - 10, ["y0"] = y - 6, ["x1"] = x + 10, ["y1"] = y + 6, ["layer"] = "gas" })));
            tasks.Add(($"Pipe and power ports of buildings near the {what}",
                       Query(liveDir, new JsonObject { ["kind"] = "ports", ["x0"] = x - 12, ["y0"] = y - 7, ["x1"] = x + 12, ["y1"] = y + 7 })));
            foreach (var layer in Layers((string?)view["overlay"], question))
                tasks.Add(($"{char.ToUpper(layer[0]) + layer[1..]} network around the {what}",
                           Query(liveDir, new JsonObject { ["kind"] = "pipes", ["x0"] = x - 12, ["y0"] = y - 7, ["x1"] = x + 12, ["y1"] = y + 7, ["layer"] = layer })));
        }
        await Task.WhenAll(tasks.Select(t => t.answer));
        var sb = new StringBuilder();
        foreach (var (title, answer) in tasks)
            if (answer.Result is { Length: > 0 } a && !a.StartsWith("no buildings") && !a.StartsWith("unknown")) sb.AppendLine(title + ":").AppendLine(a.Trim());
        return sb.ToString();
    }

    /// <summary>Network layers worth drawing: the open overlay, plus whatever the question is about.</summary>
    static IEnumerable<string> Layers(string? overlay, string? question)
    {
        var set = new List<string>();
        string q = (question ?? "").ToLowerInvariant();
        bool Has(params string[] words) => words.Any(q.Contains);
        if (overlay == "ventilation" || Has("pipe", "vent", "gas", "pump", "filter", "hydrogen", "oxygen", "co2", "carbon", "air")) set.Add("gas");
        if (overlay == "plumbing" || Has("pipe", "water", "liquid", "pump", "sink", "toilet", "lavatory", "shower", "plumb")) set.Add("liquid");
        if (overlay == "power" || Has("power", "wire", "battery", "batteries", "generator", "electric", "watt", "circuit")) set.Add("power");
        if (Has("conveyor", "rail", "loader")) set.Add("rail");
        return set.Distinct();
    }

    static (int x, int y)? Coords(string? text, string pattern)
    {
        if (text == null) return null;
        var m = Regex.Match(text, pattern);
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    /// <summary>One request to the running game; null when it doesn't answer within the timeout.</summary>
    public static async Task<string?> Query(string liveDir, JsonObject q, int timeoutMs = 1500)
    {
        try
        {
            var qdir = Path.Combine(liveDir, "queries");
            Directory.CreateDirectory(qdir);
            var id = Guid.NewGuid().ToString("N");
            var req = Path.Combine(qdir, $"req-{id}.json");
            File.WriteAllText(req + ".tmp", q.ToJsonString());
            File.Move(req + ".tmp", req);
            var res = Path.Combine(qdir, $"res-{id}.txt");
            for (int waited = 0; waited < timeoutMs; waited += 50)
            {
                if (File.Exists(res))
                {
                    await Task.Delay(20);
                    var text = File.ReadAllText(res);
                    try { File.Delete(res); } catch { }
                    return text;
                }
                await Task.Delay(50);
            }
            try { File.Delete(req); } catch { }
        }
        catch { }
        return null;
    }
}
