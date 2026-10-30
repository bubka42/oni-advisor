using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace OniAdvisor
{
    /// <summary>
    /// What lies just beyond the base in each direction, from the map memory (only revealed tiles, as last seen):
    /// the main gases, liquids and solids, the temperature range, germs, and the resources worth digging for. It is
    /// what the advisor needs to weigh which way to expand - and to notice a chlorine pocket creeping up on the
    /// base before anyone asks. The base is the middle 80% of the colony's buildings (so a long ladder or a lone
    /// outpost doesn't stretch it); each band reaches Depth tiles out from its edge.
    /// </summary>
    internal static class Frontier
    {
        const int Depth = 25, Margin = 5;

        static readonly string[] Hazards = { "Chlorine", "Polluted Oxygen", "Carbon Dioxide", "Hydrogen", "Natural Gas", "Slime", "Polluted Water", "Magma", "Brine" };
        static readonly string[] Resources = { "Copper Ore", "Iron Ore", "Gold Amalgam", "Coal", "Algae", "Water", "Dirt", "Phosphorite", "Oxylite", "Bleach Stone", "Fertilizer", "Clay", "Sand" };
        static readonly HashSet<string> Structural = new HashSet<string> { "Tile", "Ladder", "Wire", "Heavi-Watt Wire", "Gas Pipe", "Liquid Pipe", "Gas Bridge", "Liquid Bridge", "Fire Pole", "Wire Bridge", "Airflow Tile", "Mesh Tile", "Insulated Tile", "Pneumatic Door", "Manual Airlock", "Wall" };

        public static JToken Build()
        {
            var xs = new List<int>(); var ys = new List<int>();
            foreach (var b in Components.BuildingCompletes.Items)
            {
                if (b == null || Structural.Contains(global::STRINGS.UI.StripLinkFormatting(b.GetProperName()))) continue;
                Grid.CellToXY(Grid.PosToCell(b), out int x, out int y);
                xs.Add(x); ys.Add(y);
            }
            if (xs.Count < 4) return JValue.CreateNull();
            xs.Sort(); ys.Sort();
            int x0 = xs[xs.Count / 10], x1 = xs[xs.Count * 9 / 10], y0 = ys[ys.Count / 10], y1 = ys[ys.Count * 9 / 10];

            return new JObject
            {
                ["base"] = $"x{x0}-{x1} y{y0}-{y1}",
                ["above"] = Band(x0 - Margin, y1 + 1, x1 + Margin, y1 + Depth),
                ["below"] = Band(x0 - Margin, y0 - Depth, x1 + Margin, y0 - 1),
                ["left"] = Band(x0 - Depth, y0 - Margin, x0 - 1, y1 + Margin),
                ["right"] = Band(x1 + 1, y0 - Margin, x1 + Depth, y1 + Margin),
            };
        }

        /// <summary>One band: "62% revealed; Sandstone 30%, Carbon Dioxide 22%, ...; hazards: ...; resources: ...; 18–41 °C; germs: ...".</summary>
        static string Band(int x0, int y0, int x1, int y1)
        {
            var mem = MapMemory.Current;
            if (mem == null) return "unknown";
            int total = 0, known = 0;
            var counts = new Dictionary<string, int>();
            var germs = new Dictionary<string, int>();
            float tMin = float.MaxValue, tMax = float.MinValue;
            for (int y = Math.Max(0, y0); y <= Math.Min(Grid.HeightInCells - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(Grid.WidthInCells - 1, x1); x++)
                {
                    int cell = Grid.XYToCell(x, y);
                    total++;
                    if (!mem.TryElement(cell, out var el, out _) || el == null) continue;
                    known++;
                    string name = MapText.Name(el);
                    counts[name] = (counts.TryGetValue(name, out int n) ? n : 0) + 1;
                    if (mem.TryTemp(cell, out float k, out _) && k > 1f) { tMin = Math.Min(tMin, k - 273.15f); tMax = Math.Max(tMax, k - 273.15f); }
                    if (mem.TryGerms(cell, out byte gi, out int gc, out _) && gi != byte.MaxValue && gc > 1000)
                    {
                        string g = GermName(gi);
                        germs[g] = (germs.TryGetValue(g, out int m) ? m : 0) + 1;
                    }
                }
            if (known == 0) return total == 0 ? "off the map" : "unexplored";
            string Pct(int c) => $"{Math.Round(100.0 * c / known)}%";
            var main = counts.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} {Pct(kv.Value)}");
            var hazards = Hazards.Where(h => counts.Keys.Any(k => k.StartsWith(h))).Select(h => $"{h} ({counts.Where(kv => kv.Key.StartsWith(h)).Sum(kv => kv.Value)} tiles)");
            var res = Resources.Where(counts.ContainsKey).Select(r => $"{r} ({counts[r]})");
            string s = $"{Math.Round(100.0 * known / total)}% revealed; mostly {string.Join(", ", main)}";
            if (hazards.Any()) s += "; hazards: " + string.Join(", ", hazards);
            if (res.Any()) s += "; resources: " + string.Join(", ", res);
            if (tMin <= tMax) s += $"; {Math.Round(tMin)}–{Math.Round(tMax)} °C";
            if (germs.Count > 0) s += "; germs: " + string.Join(", ", germs.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ({kv.Value} tiles)"));
            return s;
        }

        static string GermName(byte i) { try { return global::STRINGS.UI.StripLinkFormatting(Db.Get().Diseases[i].Name); } catch { return "germs"; } }
    }
}
