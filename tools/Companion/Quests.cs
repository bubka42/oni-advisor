using System.Text.Json.Nodes;

namespace OniAdvisor.Companion;

/// <summary>
/// What the advisor needs to know about the projects (quests): the advisor proposes them in live/quests.json,
/// the player decides in live/quests-state.json (written by the mod). This reads both: a one-line summary
/// for every prompt, what the player changed since the last prompt, and what a quest thread is about.
/// </summary>
public sealed class Quests
{
    readonly string questsFile, stateFile;
    Dictionary<string, (string status, HashSet<string> done)>? last;

    public Quests(string liveDir)
    {
        questsFile = Path.Combine(liveDir, "quests.json");
        stateFile = Path.Combine(liveDir, "quests-state.json");
    }

    sealed record Quest(string Id, string Title, string Status, List<(string id, string text)> Milestones, HashSet<string> Done);

    List<Quest> Read()
    {
        var list = new List<Quest>();
        JsonNode? root = null, state = null;
        try { if (File.Exists(questsFile)) root = JsonNode.Parse(File.ReadAllText(questsFile)); } catch { }
        try { if (File.Exists(stateFile)) state = JsonNode.Parse(File.ReadAllText(stateFile)); } catch { }
        var arr = root is JsonArray a ? a : root?["quests"]?.AsArray();
        if (arr == null) return list;
        int n = 0;
        foreach (var q in arr)
        {
            string id = (string?)q?["id"] ?? $"q{++n}";
            var s = state?[id];
            string status = (string?)s?["status"] ?? (string?)q?["status"] ?? "proposed";
            var ms = new List<(string, string)>();
            int m = 0;
            foreach (var x in q?["milestones"]?.AsArray() ?? new JsonArray())
                ms.Add(x is JsonValue v ? ($"m{++m}", (string?)v ?? "") : ((string?)x?["id"] ?? $"m{++m}", (string?)x?["text"] ?? ""));
            var done = new HashSet<string>(ms.Where(x => (bool?)s?["milestones"]?[x.Item1] == true).Select(x => x.Item1));
            list.Add(new Quest(id, (string?)q?["title"] ?? id, status, ms, done));
        }
        return list;
    }

    /// <summary>"Projects: ongoing: Water loop (2/5); proposed: …; on hold: …" for every prompt, or "".</summary>
    public string Summary()
    {
        var qs = Read();
        string Part(string status, string label)
        {
            var items = qs.Where(q => q.Status == status).Select(q => $"{q.Title} [{q.Id}]" + (q.Milestones.Count > 0 ? $" ({q.Done.Count}/{q.Milestones.Count})" : "")).ToList();
            return items.Count == 0 ? "" : $"{label}: {string.Join("; ", items)}";
        }
        var parts = new[] { Part("accepted", "ongoing"), Part("proposed", "proposed"), Part("stalled", "on hold") }.Where(p => p.Length > 0).ToList();
        return parts.Count == 0 ? "" : "Projects (live/quests.json + the player's live/quests-state.json): " + string.Join(" | ", parts);
    }

    /// <summary>What the player did with the projects since the last call (accepted, ticked, held …), one line each.</summary>
    public string News()
    {
        var qs = Read();
        var now = qs.ToDictionary(q => q.Id, q => (q.Status, q.Done));
        var lines = new List<string>();
        if (last != null)
        {
            foreach (var q in qs)
            {
                last.TryGetValue(q.Id, out var before);
                if (before.status != null && before.status != q.Status)
                    lines.Add(q.Status switch
                    {
                        "accepted" => before.status == "stalled" ? $"- the player resumed the project \"{q.Title}\"" : $"- the player ACCEPTED your proposed project \"{q.Title}\"",
                        "declined" => $"- the player declined your proposed project \"{q.Title}\"",
                        "stalled" => $"- the player put the project \"{q.Title}\" on hold",
                        "abandoned" => $"- the player abandoned the project \"{q.Title}\"",
                        "completed" => $"- the player COMPLETED the project \"{q.Title}\"",
                        _ => $"- project \"{q.Title}\" is now {q.Status}",
                    });
                var wasDone = before.done ?? new HashSet<string>();
                foreach (var (id, text) in q.Milestones)
                {
                    if (q.Done.Contains(id) && !wasDone.Contains(id)) lines.Add($"- the player ticked off \"{text}\" ({q.Title})");
                    else if (!q.Done.Contains(id) && wasDone.Contains(id)) lines.Add($"- the player un-ticked \"{text}\" ({q.Title})");
                }
            }
        }
        last = now;
        return lines.Count == 0 ? "" : "\n" + string.Join("\n", lines.Take(12));
    }

    /// <summary>What a "quest:ID" or "quest:ID:MILESTONE" thread is about.</summary>
    public string About(string thread)
    {
        var parts = thread.Split(':');
        var q = parts.Length > 1 ? Read().FirstOrDefault(x => x.Id == parts[1]) : null;
        if (q == null) return "a project in live/quests.json";
        string where = $"the project \"{q.Title}\" [{q.Id}], {q.Status}, {q.Done.Count}/{q.Milestones.Count} milestones done";
        if (parts.Length > 2)
        {
            var m = q.Milestones.FirstOrDefault(x => x.id == parts[2]);
            return $"the milestone \"{m.text}\" ({(q.Done.Contains(parts[2]) ? "ticked" : "not ticked yet")}) of {where}";
        }
        return where;
    }
}
