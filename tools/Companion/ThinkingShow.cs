using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OniAdvisor.Companion;

/// <summary>
/// "Show the advisor's thinking" (optional): while the advisor works on something the player asked for, each
/// map query it runs becomes a "look" event in live/think.json, with where it is looking (from the query's
/// coordinates), the matching overlay, and its latest thought as a caption. The mod circles the spot, shows
/// the caption, and (if the player chose so) glides the camera there; at the end of the turn it puts the
/// camera and overlay back. Grounded in what the advisor actually checks, so it never shows a made-up look.
/// </summary>
public sealed class ThinkingShow
{
    readonly string file;
    int seq;
    bool active;
    string lastThought = "";

    public ThinkingShow(string liveDir) { file = Path.Combine(liveDir, "think.json"); }

    public void Begin() { active = true; lastThought = ""; }

    /// <summary>The text the advisor wrote before its tool call ("Let me check the pump's intake.").</summary>
    public void Thought(string text)
    {
        if (!active) return;
        var t = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (t.Length > 160) t = t[..157] + "…";
        lastThought = t;
    }

    /// <summary>A tool call: a map query with coordinates becomes a look event.</summary>
    public void Tool(string tool, JsonNode? input, string description)
    {
        if (!active || tool != "Bash") return;
        var cmd = (string?)input?["command"] ?? "";
        var m = Regex.Match(cmd, @"MapQuery\.dll""?\s+(?:live\s+)?(\w+)((?:\s+-?\d+)*)\s*(\w+)?");
        if (!m.Success) return;
        string kind = m.Groups[1].Value;
        var nums = m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        string layer = m.Groups[3].Value;
        if (nums.Count < 2) return; // "rates", "summary", "view": nowhere in particular
        var o = new JsonObject
        {
            ["seq"] = ++seq, ["active"] = true, ["utc"] = DateTime.UtcNow.ToString("o"),
            ["x0"] = nums[0], ["y0"] = nums[1],
            ["x1"] = nums.Count >= 4 ? nums[2] : nums[0], ["y1"] = nums.Count >= 4 ? nums[3] : nums[1],
            ["overlay"] = Overlay(kind, layer),
            ["text"] = lastThought.Length > 0 ? lastThought : Capitalise(description) + "…",
        };
        lastThought = "";
        Write(o);
    }

    public void End()
    {
        if (!active) return;
        active = false;
        Write(new JsonObject { ["seq"] = ++seq, ["active"] = false, ["utc"] = DateTime.UtcNow.ToString("o") });
    }

    static string Overlay(string kind, string layer) => kind switch
    {
        "pipes" => layer switch { "liquid" => "liquid", "power" => "power", "rail" => "rail", "logic" or "automation" => "logic", _ => "gas" },
        "region" => layer switch { "gas" => "oxygen", "temp" => "temperature", _ => "" },
        _ => "",
    };

    static string Capitalise(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    void Write(JsonObject o)
    {
        try
        {
            File.WriteAllText(file + ".tmp", o.ToJsonString());
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch { }
    }
}
