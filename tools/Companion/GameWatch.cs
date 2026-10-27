using System.Text.Json;

namespace OniAdvisor.Companion;

/// <summary>
/// Follows the game between the player's messages: compares each fresh state.json (every ~20 s) with the
/// previous one and keeps a list of what changed - new game alerts, duplicants' new or cleared problems,
/// buildings built or removed, building problems appearing or resolved, batteries, research, the cycle. The
/// list goes into the advisor's next prompt ("since your last message ..."), so it never starts cold, and can
/// prompt an unasked comment.
/// </summary>
public sealed class GameWatch
{
    readonly string statePath;
    DateTime lastWrite;
    Snap? prev;
    readonly List<(DateTime utc, int cycle, string text, bool serious)> events = new();
    readonly object gate = new();

    public GameWatch(string liveDir) => statePath = Path.Combine(liveDir, "state.json");

    sealed class Snap
    {
        public int Cycle;
        public double Progress;
        public HashSet<string> Alerts = new();
        public Dictionary<string, HashSet<string>> DupeProblems = new();
        public Dictionary<string, string> DupeDoing = new();
        public Dictionary<string, int> Buildings = new();
        public HashSet<string> BuildingProblems = new(); // "Gas Pump @118,209: Pipe Blocked"
        public string Research = "";
        public double Joules;
        public string Batteries = "";
    }

    /// <summary>Reads state.json if it changed; returns true when new events were added.</summary>
    public bool Poll()
    {
        try
        {
            var t = File.GetLastWriteTimeUtc(statePath);
            if (t == lastWrite) return false;
            lastWrite = t;
            var cur = Read();
            if (cur == null) return false;
            bool added = false;
            if (prev != null) added = Diff(prev, cur);
            prev = cur;
            return added;
        }
        catch { return false; }
    }

    /// <summary>Events since the last call, as prompt text; clears them.</summary>
    public string Take()
    {
        lock (gate)
        {
            if (events.Count == 0) return "";
            var text = string.Join("\n", events.TakeLast(40).Select(e => $"- [cycle {e.cycle}{(e.serious ? ", !" : "")}] {e.text}"));
            events.Clear();
            return text;
        }
    }

    public bool HasEvents { get { lock (gate) return events.Count > 0; } }
    public bool HasSerious { get { lock (gate) return events.Any(e => e.serious); } }

    void Add(int cycle, string text, bool serious = false)
    {
        lock (gate)
        {
            events.Add((DateTime.UtcNow, cycle, text, serious));
            if (events.Count > 200) events.RemoveRange(0, events.Count - 200);
        }
    }

    bool Diff(Snap a, Snap b)
    {
        int n0;
        lock (gate) n0 = events.Count;
        int c = b.Cycle;
        if (b.Cycle != a.Cycle) Add(c, $"cycle {b.Cycle} began");

        foreach (var al in b.Alerts.Except(a.Alerts))
            Add(c, "game alert: " + al, al.StartsWith("Bad") || al.StartsWith("DuplicantThreatening"));
        foreach (var al in a.Alerts.Except(b.Alerts)) Add(c, "alert cleared: " + al);

        foreach (var (dupe, probs) in b.DupeProblems)
        {
            var old = a.DupeProblems.TryGetValue(dupe, out var o) ? o : new HashSet<string>();
            var got = probs.Except(old).ToList();
            var gone = old.Except(probs).ToList();
            if (got.Count > 0) Add(c, $"{dupe}: now {string.Join(", ", got)}", got.Any(Serious));
            if (gone.Count > 0) Add(c, $"{dupe}: no longer {string.Join(", ", gone)}");
        }
        foreach (var dupe in a.DupeProblems.Keys.Except(b.DupeProblems.Keys)) Add(c, $"{dupe} is gone (died or left)", true);
        foreach (var dupe in b.DupeProblems.Keys.Except(a.DupeProblems.Keys)) Add(c, $"new duplicant: {dupe}");

        foreach (var (name, count) in b.Buildings)
        {
            int was = a.Buildings.TryGetValue(name, out var w) ? w : 0;
            if (count > was) Add(c, $"built: {count - was}x {name}");
            else if (count < was) Add(c, $"removed: {was - count}x {name}");
        }
        foreach (var name in a.Buildings.Keys.Except(b.Buildings.Keys)) Add(c, $"removed: all {name}");

        foreach (var p in b.BuildingProblems.Except(a.BuildingProblems).Take(12)) Add(c, "building problem: " + p, Serious(p));
        foreach (var p in a.BuildingProblems.Except(b.BuildingProblems).Take(12)) Add(c, "resolved: " + p);

        if (b.Research != a.Research) Add(c, $"research now: {(b.Research.Length > 0 ? b.Research : "nothing selected")}");
        if (b.Batteries != a.Batteries && b.Batteries.Contains("draining") && !a.Batteries.Contains("draining")) Add(c, "batteries started draining: " + b.Batteries);
        if (a.Joules > 1000 && b.Joules < 1) Add(c, "batteries are empty", true);

        lock (gate) return events.Count > n0;
    }

    static readonly string[] SeriousWords = { "suffocat", "holding breath", "starv", "hungry", "scald", "flood", "overload", "dying", "incapacitated", "injur", "no oxygen", "low oxygen", "stress" };
    static bool Serious(string s) => SeriousWords.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase));

    Snap? Read()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
        var r = doc.RootElement;
        var s = new Snap();
        if (r.TryGetProperty("meta", out var meta))
        {
            if (meta.TryGetProperty("cycle", out var cy) && cy.ValueKind == JsonValueKind.Number) s.Cycle = cy.GetInt32();
            if (meta.TryGetProperty("cycleProgress", out var cp) && cp.ValueKind == JsonValueKind.Number) s.Progress = cp.GetDouble();
        }
        if (r.TryGetProperty("alerts", out var al) && al.ValueKind == JsonValueKind.Array)
            foreach (var x in al.EnumerateArray()) if (x.GetString() is { } t) s.Alerts.Add(t);
        if (r.TryGetProperty("dupes", out var dupes) && dupes.ValueKind == JsonValueKind.Array)
            foreach (var d in dupes.EnumerateArray())
            {
                var name = d.TryGetProperty("name", out var nm) ? nm.GetString() ?? "?" : "?";
                var set = new HashSet<string>();
                if (d.TryGetProperty("problems", out var pr) && pr.ValueKind == JsonValueKind.Array)
                    foreach (var p in pr.EnumerateArray()) if (p.GetString() is { } t) set.Add(t.TrimStart('!'));
                s.DupeProblems[name] = set;
            }
        if (r.TryGetProperty("buildings", out var b) && b.ValueKind == JsonValueKind.Object)
        {
            if (b.TryGetProperty("counts", out var counts))
                foreach (var p in counts.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) s.Buildings[p.Name] = p.Value.GetInt32();
            if (b.TryGetProperty("notable", out var notable))
                foreach (var n in notable.EnumerateArray())
                {
                    string name = n.TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "", pos = n.TryGetProperty("pos", out var pp) ? pp.GetString() ?? "" : "";
                    if (n.TryGetProperty("status", out var st))
                        foreach (var x in st.EnumerateArray())
                            if (x.GetString() is { } t && t.StartsWith("!")) s.BuildingProblems.Add($"{name} @{pos}: {t.TrimStart('!')}");
                }
        }
        if (r.TryGetProperty("research", out var res) && res.ValueKind == JsonValueKind.Object && res.TryGetProperty("active", out var act)) s.Research = act.GetString() ?? "";
        if (r.TryGetProperty("power", out var power) && power.ValueKind == JsonValueKind.Array)
            foreach (var cc in power.EnumerateArray())
            {
                if (cc.TryGetProperty("joulesStored", out var j) && j.ValueKind == JsonValueKind.Number) s.Joules += j.GetDouble();
                if (cc.TryGetProperty("batteries", out var bt)) s.Batteries += string.Join(", ", bt.EnumerateArray().Select(x => x.GetString())) + "; ";
            }
        return s;
    }
}
