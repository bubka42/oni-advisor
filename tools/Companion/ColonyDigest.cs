using System.Text;
using System.Text.Json;

namespace OniAdvisor.Companion;

/// <summary>
/// A compact (~2 KB) text summary of live/state.json, sent with every live question so the advisor can
/// answer at once instead of spending round trips reading files.
/// </summary>
public static class ColonyDigest
{
    public static string Build(string liveDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(liveDir, "state.json")));
            var r = doc.RootElement;
            var sb = new StringBuilder();

            var meta = r.GetProperty("meta");
            sb.Append($"Cycle {Num(meta, "cycle")} ({Num(meta, "cycleProgress")} done{(Bool(meta, "isNight") ? ", night" : "")}), map mode {Str(meta, "mapMode")}. ");
            if (r.TryGetProperty("colony", out var col))
                sb.AppendLine($"{Num(col, "dupeCount")} duplicants, food {Num(col, "foodKcal")} kcal.");

            if (r.TryGetProperty("dupes", out var dupes) && dupes.ValueKind == JsonValueKind.Array)
            {
                // The crew roster: who can do what (skills learned, interests, traits) next to what they are doing now.
                sb.Append("Crew: ");
                foreach (var d in dupes.EnumerateArray())
                {
                    string List(string prop) => d.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0
                        ? string.Join("/", a.EnumerateArray().Select(x => x.GetString())) : "";
                    string skills = List("skills"), interests = List("interests"), traits = List("traits");
                    sb.Append($"{Str(d, "name")} [{(skills.Length > 0 ? skills : "no skills yet")}; likes {(interests.Length > 0 ? interests : "nothing")}{(traits.Length > 0 ? "; " + traits : "")}] ({Str(d, "doing")}, stress {Num(d, "stress")}");
                    if (d.TryGetProperty("skillPointsFree", out var sp) && sp.ValueKind == JsonValueKind.Number && sp.GetInt32() > 0) sb.Append($", {sp.GetInt32()} skill pt");
                    if (d.TryGetProperty("problems", out var pr) && pr.ValueKind == JsonValueKind.Array && pr.GetArrayLength() > 0)
                        sb.Append(", " + string.Join("/", pr.EnumerateArray().Select(x => x.GetString())));
                    sb.Append("); ");
                }
                sb.AppendLine();
            }

            if (r.TryGetProperty("rooms", out var rooms) && rooms.ValueKind == JsonValueKind.Array)
            {
                sb.Append("Rooms: ");
                foreach (var room in rooms.EnumerateArray().Take(14))
                {
                    sb.Append($"{Str(room, "type")} {Str(room, "bounds")}");
                    if (room.TryGetProperty("gasGramsPerTile", out var g) && g.ValueKind == JsonValueKind.Object)
                    {
                        var gases = g.EnumerateObject().Take(3).Select(p => $"{p.Name} {p.Value}g").ToList();
                        if (gases.Count > 0) sb.Append(" [" + string.Join(", ", gases) + "]");
                    }
                    if (room.TryGetProperty("gasTypes", out var gt)) sb.Append(" [" + gt.GetString() + "]");
                    if (room.TryGetProperty("known", out var k)) sb.Append(" (" + k.GetString() + ")");
                    sb.Append("; ");
                }
                sb.AppendLine();
            }

            if (r.TryGetProperty("power", out var power) && power.ValueKind == JsonValueKind.Array)
                foreach (var c in power.EnumerateArray())
                {
                    string used = c.TryGetProperty("wattsUsed", out var u) && u.ValueKind == JsonValueKind.Number ? Math.Max(0, u.GetDouble()).ToString("0") : "?";
                    string gens = c.TryGetProperty("generators", out var g) && g.ValueKind == JsonValueKind.Object ? string.Join(", ", g.EnumerateObject().Select(p => $"{p.Value}x {p.Name}")) : "";
                    sb.AppendLine($"Power circuit {Num(c, "id")}: using {used} W of {Num(c, "wattsGenerated")} W generated now (max {Num(c, "wattsPotential")} W; wire limit {Num(c, "maxSafeWatts")} W){(gens.Length > 0 ? ", generators " + gens : ", no generators")}, stored {Num(c, "joulesStored")} J, batteries {string.Join(", ", c.TryGetProperty("batteries", out var b) ? b.EnumerateArray().Select(x => x.GetString()) : Array.Empty<string?>())}.");
                }

            if (r.TryGetProperty("rates", out var rates) && rates.ValueKind == JsonValueKind.Object && rates.TryGetProperty("byElement", out var by))
                sb.AppendLine("Production/consumption (" + Str(rates, "note") + "): " + string.Join("; ", by.EnumerateObject().Take(10).Select(p => $"{p.Name} {p.Value.GetString()}")) + ".");

            if (r.TryGetProperty("buildings", out var bl) && bl.TryGetProperty("notable", out var notable))
            {
                var problems = notable.EnumerateArray()
                    .Select(n => (name: Str(n, "name"), st: n.TryGetProperty("status", out var s) ? s.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.StartsWith("!")).ToList() : new List<string>()))
                    .Where(x => x.st.Count > 0).Take(12)
                    .Select(x => $"{x.name}: {string.Join("/", x.st.Select(s => s.TrimStart('!')))}");
                var list = problems.ToList();
                if (list.Count > 0) sb.AppendLine("Building problems: " + string.Join("; ", list) + ".");
            }

            if (r.TryGetProperty("construction", out var con))
            {
                var sites = con.TryGetProperty("sites", out var s) ? s.EnumerateArray().Select(x => x.GetString()).Take(6).ToList() : new List<string?>();
                var idle = con.TryGetProperty("idleDupes", out var i) ? i.EnumerateArray().Select(x => x.GetString()).ToList() : new List<string?>();
                if (sites.Count > 0) sb.AppendLine("Construction backlog: " + string.Join("; ", sites) + ".");
                if (idle.Count > 0) sb.AppendLine("Idle: " + string.Join(", ", idle) + ".");
            }

            if (r.TryGetProperty("research", out var res))
                sb.AppendLine($"Research: {Str(res, "active")}" + (res.TryGetProperty("queue", out var q) && q.GetArrayLength() > 0 ? $" (queue: {string.Join("; ", q.EnumerateArray().Select(x => x.GetString()))})" : "") + ".");

            if (r.TryGetProperty("resources", out var rs) && rs.ValueKind == JsonValueKind.Object)
                sb.AppendLine("Stored (kg): " + string.Join(", ", rs.EnumerateObject().Take(14).Select(p => $"{p.Name} {p.Value}")) + ".");

            if (r.TryGetProperty("projects", out var pj) && pj.ValueKind == JsonValueKind.Array && pj.GetArrayLength() > 0)
                sb.AppendLine("Projects: " + string.Join("; ", pj.EnumerateArray().Select(x => x.GetString())) + ".");

            if (r.TryGetProperty("pod", out var pod) && Bool(pod, "printablesReady")) sb.AppendLine("Printing Pod: options ready (live/pod.json).");

            var text = sb.ToString();
            return text.Length > 7000 ? text[..7000] + "…" : text; // room for the crew roster of a large colony
        }
        catch (Exception e) { return "(no colony summary available: " + e.Message + ")"; }
    }

    /// <summary>
    /// The colony survey for cycle briefings and plan reviews: the questions that tracking events and projects
    /// never asks - are there real rooms, who is idle, is research running, can the wiring carry the load, what
    /// is researched but never built. Short lines with a verdict each, so the gaps stand out.
    /// </summary>
    public static string Survey(string liveDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(liveDir, "state.json")));
            var r = doc.RootElement;
            var lines = new List<string>();

            if (r.TryGetProperty("rooms", out var rooms) && rooms.ValueKind == JsonValueKind.Array)
            {
                var types = rooms.EnumerateArray().Select(x => Str(x, "type")).ToList();
                var real = types.Where(t => t != "Miscellaneous Room").GroupBy(t => t).Select(g => g.Count() > 1 ? $"{g.Count()}x {g.Key}" : g.Key).ToList();
                var missing = new[] { ("Barracks", new[] { "Barracks", "Bedroom", "Luxury Barracks", "Private Bedroom" }),
                                      ("Washroom or Latrine", new[] { "Washroom", "Latrine" }),
                                      ("Mess Hall", new[] { "Mess Hall", "Great Hall", "Banquet Hall" }) }
                    .Where(m => !types.Any(t => m.Item2.Contains(t))).Select(m => m.Item1).ToList();
                lines.Add($"Rooms the game recognises: {(real.Count > 0 ? string.Join(", ", real) : "none")}." + (missing.Count > 0 ? $" Missing: {string.Join(", ", missing)} (no morale bonus from them)." : ""));
            }
            if (r.TryGetProperty("dupes", out var dupes) && dupes.ValueKind == JsonValueKind.Array)
            {
                var all = dupes.EnumerateArray().ToList();
                var low = all.Where(d => d.TryGetProperty("morale", out var m) && d.TryGetProperty("moraleNeed", out var n) && m.ValueKind == JsonValueKind.Number && n.ValueKind == JsonValueKind.Number && m.GetDouble() < n.GetDouble()).Select(d => Str(d, "name")).ToList();
                var pts = all.Where(d => d.TryGetProperty("skillPointsFree", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() > 0).Select(d => Str(d, "name")).ToList();
                if (low.Count > 0) lines.Add($"Morale below need: {string.Join(", ", low)}.");
                if (pts.Count > 0) lines.Add($"Unspent skill points: {string.Join(", ", pts)}.");
                if (r.TryGetProperty("construction", out var con) && con.TryGetProperty("idleDupes", out var idle))
                {
                    int n = idle.GetArrayLength();
                    if (n > 0) lines.Add($"Idle: {n} of {all.Count} duplicants{(n * 3 >= all.Count ? " — a lot of free labour for a project" : "")}.");
                }
            }
            if (r.TryGetProperty("crops", out var crops) && crops.ValueKind == JsonValueKind.Object
                && crops.TryGetProperty("stalled", out var stalled) && stalled.GetArrayLength() > 0)
                lines.Add($"Crops: {Num(crops, "planted")} planted, only {Num(crops, "growing")} growing. Stalled: " + string.Join("; ", stalled.EnumerateArray().Select(x => x.GetString()).Take(5)) + ".");
            if (r.TryGetProperty("research", out var res))
            {
                string active = Str(res, "active");
                int queued = res.TryGetProperty("queue", out var q) ? q.GetArrayLength() : 0;
                lines.Add(active.Length == 0 && queued == 0 ? "Research: nothing selected (stations idle)." : $"Research: {(active.Length > 0 ? active : "?")}{(queued > 1 ? $", {queued} queued" : "")}.");
                if (res.TryGetProperty("researchedNotBuilt", out var nb) && nb.GetArrayLength() > 0)
                    lines.Add("Researched but none built (does any of these solve a current problem?): " + string.Join(", ", nb.EnumerateArray().Select(x => x.GetString()).Take(40)) + ".");
            }
            // Power only when it is a real problem now: a circuit drawing near or over its wire limit, or batteries
            // empty. (What a circuit could draw with every machine on is not one: machines never all run at once.)
            if (r.TryGetProperty("power", out var power) && power.ValueKind == JsonValueKind.Array)
            {
                bool trouble = false;
                foreach (var c in power.EnumerateArray())
                {
                    double used = c.TryGetProperty("wattsUsed", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
                    double safe = c.TryGetProperty("maxSafeWatts", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
                    double stored = c.TryGetProperty("joulesStored", out var j) && j.ValueKind == JsonValueKind.Number ? j.GetDouble() : -1;
                    double gen = c.TryGetProperty("wattsGenerated", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetDouble() : 0;
                    if (safe > 0 && used > safe * 0.9) { trouble = true; lines.Add($"Power: circuit {Num(c, "id")} is drawing {used:0} W now on wire rated {safe:0} W."); }
                    bool hasBatteries = c.TryGetProperty("batteries", out var bl) && bl.ValueKind == JsonValueKind.Array && bl.GetArrayLength() > 0; // transformer-fed branches have none
                    if (hasBatteries && stored == 0 && used > gen) { trouble = true; lines.Add($"Power: circuit {Num(c, "id")} has empty batteries and uses more than it makes."); }
                }
                if (!trouble) lines.Add("Power: no circuit is near its wire limit and none has run dry. Not a topic for this briefing unless the player asks.");
            }
            if (r.TryGetProperty("frontier", out var fr) && fr.ValueKind == JsonValueKind.Object)
                lines.Add($"Around the base ({Str(fr, "base")}; within 25 tiles, revealed tiles as last seen):" + string.Concat(
                    new[] { "above", "below", "left", "right" }.Where(d => Str(fr, d).Length > 0).Select(d => $"\n    {d}: {Str(fr, d)}")));
            return lines.Count == 0 ? "" : "Colony survey:\n- " + string.Join("\n- ", lines);
        }
        catch { return ""; }
    }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static string Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "?";
    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
