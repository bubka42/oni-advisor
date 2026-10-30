using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OniAdvisor.Companion;

/// <summary>
/// The player can't see tile coordinates, so a message saying "the wire at (116,199)" points nowhere. Before a
/// reply is shown, each coordinate in it gets a mark on the map: an advisor mark already near it (from this
/// turn) or a new numbered point mark, and the text says "mark 1" (or "marked") instead of the numbers.
/// Coordinates count only when they look like coordinates: in brackets, after "at/near/by/from/to/@", or
/// continuing such a list - so "1,230 W" or "12,000 kcal" stay as they are.
/// </summary>
public static class CoordinateMarks
{
    static readonly Regex Candidate = new(@"(?<open>\()?(?<x>\d{1,3}),\s?(?<y>\d{1,3})(?<close>\))?(?!\d)", RegexOptions.Compiled);
    static readonly Regex Lead = new(@"(?:\b(?:at|near|around|by|from|to|on|under|above|below)\s+(?:tile\s+)?|@\s?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Unit = new(@"^\s?(?:W|kW|kg|kcal|g|g/s|kJ|J|DTU|%|t)\b", RegexOptions.Compiled);
    static readonly Regex ListGap = new(@"^\s*(?:,|and|,\s*and)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns the text with coordinates replaced, and adds marks to live/plan/advisor-marks.json: appended when the
    /// advisor wrote marks this turn, otherwise replacing the old marks (they belonged to an earlier message).
    /// </summary>
    public static string Apply(string text, string liveDir, DateTime turnStartUtc, string? level)
    {
        var found = new List<(Match m, int x, int y)>();
        int lastEnd = -100;
        foreach (Match m in Candidate.Matches(text))
        {
            string before = text[..m.Index];
            bool bracketed = m.Groups["open"].Success && m.Groups["close"].Success;
            bool led = Lead.IsMatch(before);
            bool listed = found.Count > 0 && ListGap.IsMatch(text[lastEnd..m.Index]);
            if (!(bracketed || led || listed)) continue;
            if (Unit.IsMatch(text[(m.Index + m.Length)..])) continue;
            if (m.Index > 0 && (char.IsDigit(text[m.Index - 1]) || text[m.Index - 1] == ',' && !char.IsWhiteSpace(text[m.Index]))) continue;
            found.Add((m, int.Parse(m.Groups["x"].Value), int.Parse(m.Groups["y"].Value)));
            lastEnd = m.Index + m.Length;
        }
        if (found.Count == 0) return text;

        var path = Path.Combine(liveDir, "plan", "advisor-marks.json");
        JsonObject root;
        bool thisTurn = File.Exists(path) && File.GetLastWriteTimeUtc(path) >= turnStartUtc;
        try { root = thisTurn ? (JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()) : new JsonObject(); }
        catch { root = new JsonObject(); }
        var marks = root["marks"] as JsonArray ?? new JsonArray();
        root["marks"] = marks;

        string tone = level is "critical" or "urgent" ? "problem" : "check";
        var numbered = new Dictionary<(int, int), int>();
        int next = 1;
        var sb = new System.Text.StringBuilder();
        int pos = 0;
        bool added = false;
        foreach (var (m, x, y) in found)
        {
            string replacement;
            if (Near(marks, x, y)) replacement = "marked";
            else
            {
                if (!numbered.TryGetValue((x, y), out int n))
                {
                    n = numbered[(x, y)] = next++;
                    marks.Add(new JsonObject { ["kind"] = "point", ["x0"] = x, ["y0"] = y, ["tone"] = tone, ["label"] = n.ToString() });
                    added = true;
                }
                replacement = "mark " + n;
            }
            bool bracketed = m.Groups["open"].Success && m.Groups["close"].Success;
            sb.Append(text, pos, m.Index - pos).Append(bracketed && !Lead.IsMatch(text[..m.Index]) ? "(" + replacement + ")" : replacement);
            pos = m.Index + m.Length;
        }
        sb.Append(text, pos, text.Length - pos);

        if (added)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch { return text; } // without the marks, the numbers are still better than "mark 1"
        }
        return sb.ToString();
    }

    /// <summary>An advisor mark already within two tiles of (x, y).</summary>
    static bool Near(JsonArray marks, int x, int y)
    {
        static bool Close(int? ax, int? ay, int x, int y) => ax != null && ay != null && Math.Abs(ax.Value - x) <= 2 && Math.Abs(ay.Value - y) <= 2;
        foreach (var mk in marks)
        {
            if (mk is not JsonObject o) continue;
            if (Close((int?)o["x0"], (int?)o["y0"], x, y) || Close((int?)o["x1"], (int?)o["y1"], x, y)) return true;
            if (o["points"] is JsonArray pts)
                foreach (var p in pts)
                    if (p is JsonArray xy && xy.Count >= 2 && Close((int?)xy[0], (int?)xy[1], x, y)) return true;
        }
        return false;
    }
}
