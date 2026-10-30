using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace OniAdvisor
{
    /// <summary>
    /// Measures a project milestone's "check" against the game, so milestones tick themselves and show partial
    /// progress. Each check is something the player does (builds, digs, researches, stores), never a state that
    /// unrelated events can spoil. The kinds (the advisor writes them in quests.json):
    ///   {"build": "Gas Pump", "count": 2, "area": [x0,y0,x1,y1]}   finished buildings by name (area optional)
    ///   {"project": "h2", "items": [1,2]}                           blueprint pieces built (items optional, as in h2.1)
    ///   {"room": "Barracks", "count": 2}                            rooms the game recognises
    ///   {"research": "Smelting"}                                    a finished tech
    ///   {"dig": [x0,y0,x1,y1]}                                      natural solid tiles dug out (revealed tiles only)
    ///   {"stored": "Copper Ore", "kg": 2000}                        in storage, colony-wide
    /// Returns (done so far, needed, a short text like "2/3 built"), or null when the check can't be measured.
    /// </summary>
    internal static class MilestoneChecks
    {
        public static (float have, float need, string text)? Measure(JObject c)
        {
            if (c == null) return null;
            try
            {
                if ((string)c["build"] is string name)
                {
                    int need = (int?)c["count"] ?? 1;
                    var area = Area(c["area"]);
                    int have = Components.BuildingCompletes.Items.Count(b => b != null
                        && string.Equals(Strip(b.GetProperName()), name, StringComparison.OrdinalIgnoreCase)
                        && (area == null || Inside(Grid.PosToCell(b), area)));
                    return (Math.Min(have, need), need, $"{Math.Min(have, need)}/{need} built");
                }
                if ((string)c["project"] is string pid)
                {
                    var numbers = (c["items"] as JArray)?.Select(x => (int)x).ToList();
                    var counted = ProjectStore.BuiltCount(pid, numbers);
                    if (counted == null || counted.Value.of == 0) return null;
                    return (counted.Value.built, counted.Value.of, $"{counted.Value.built}/{counted.Value.of} pieces built");
                }
                if ((string)c["room"] is string type)
                {
                    int need = (int?)c["count"] ?? 1;
                    int have = Game.Instance.roomProber.rooms.Count(r => r?.roomType != null && string.Equals(Strip(r.roomType.Name), type, StringComparison.OrdinalIgnoreCase));
                    return (Math.Min(have, need), need, need == 1 ? (have > 0 ? "recognised" : "not yet a " + type) : $"{Math.Min(have, need)}/{need} rooms");
                }
                if ((string)c["research"] is string techName)
                {
                    var tech = Db.Get().Techs.resources.FirstOrDefault(t => string.Equals(Strip(t.Name), techName, StringComparison.OrdinalIgnoreCase));
                    if (tech == null) return null;
                    bool done = Research.Instance.Get(tech)?.IsComplete() == true;
                    return (done ? 1 : 0, 1, done ? "researched" : "not researched yet");
                }
                if (c["dig"] is JArray digArea)
                {
                    var a = Area(digArea);
                    if (a == null) return null;
                    int total = 0, open = 0;
                    for (int y = a[1]; y <= a[3]; y++)
                        for (int x = a[0]; x <= a[2]; x++)
                        {
                            int cell = Grid.XYToCell(x, y);
                            if (!MapMemory.Revealed(cell)) continue;
                            total++;
                            if (!Grid.Element[cell].IsSolid) open++;
                        }
                    if (total == 0) return null;
                    return (open, total, $"{Math.Round(100f * open / total)}% dug");
                }
                if ((string)c["stored"] is string item)
                {
                    float need = (float?)c["kg"] ?? 1f;
                    var inv = ClusterManager.Instance.activeWorld.worldInventory;
                    var tag = DiscoveredResources.Instance.GetDiscovered().FirstOrDefault(t => string.Equals(Strip(TagManager.GetProperName(t, true)), item, StringComparison.OrdinalIgnoreCase));
                    float have = tag.IsValid ? inv.GetAmount(tag, false) : 0f;
                    return (Math.Min(have, need), need, $"{Math.Round(Math.Min(have, need))}/{Math.Round(need)} kg");
                }
            }
            catch (Exception e) { Log.Warn("milestone check: " + e.Message); }
            return null;
        }

        static string Strip(string s) => string.IsNullOrEmpty(s) ? "" : global::STRINGS.UI.StripLinkFormatting(s).Trim();

        /// <summary>[x0,y0,x1,y1] in any corner order, clamped to the map; null if malformed.</summary>
        static int[] Area(JToken t)
        {
            if (!(t is JArray a) || a.Count < 4) return null;
            int x0 = (int)a[0], y0 = (int)a[1], x1 = (int)a[2], y1 = (int)a[3];
            return new[]
            {
                Math.Max(0, Math.Min(x0, x1)), Math.Max(0, Math.Min(y0, y1)),
                Math.Min(Grid.WidthInCells - 1, Math.Max(x0, x1)), Math.Min(Grid.HeightInCells - 1, Math.Max(y0, y1)),
            };
        }

        static bool Inside(int cell, int[] a)
        {
            Grid.CellToXY(cell, out int x, out int y);
            return x >= a[0] && x <= a[2] && y >= a[1] && y <= a[3];
        }
    }
}
