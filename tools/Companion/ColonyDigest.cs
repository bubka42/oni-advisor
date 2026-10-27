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
                sb.Append("Duplicants: ");
                foreach (var d in dupes.EnumerateArray())
                {
                    sb.Append($"{Str(d, "name")} ({Str(d, "doing")}, stress {Num(d, "stress")}");
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
                    if (room.TryGetProperty("known", out var k)) sb.Append(" (" + k.GetString() + ")");
                    sb.Append("; ");
                }
                sb.AppendLine();
            }

            if (r.TryGetProperty("power", out var power) && power.ValueKind == JsonValueKind.Array)
                foreach (var c in power.EnumerateArray())
                    sb.AppendLine($"Power circuit {Num(c, "id")}: using {Num(c, "wattsUsed")} W of {Num(c, "wattsGenerated")} W generated (safe {Num(c, "maxSafeWatts")} W), batteries {string.Join(", ", c.TryGetProperty("batteries", out var b) ? b.EnumerateArray().Select(x => x.GetString()) : Array.Empty<string?>())}.");

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
            return text.Length > 3500 ? text[..3500] + "…" : text;
        }
        catch (Exception e) { return "(no colony summary available: " + e.Message + ")"; }
    }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static string Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "?";
    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
