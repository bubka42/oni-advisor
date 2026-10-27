using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace OniAdvisor
{
    /// <summary>
    /// Compact text renderings of parts of the map for the advisor, always through MapMemory, so memory mode
    /// and the fog of war are respected. Buildings and duplicants are the player's own and drawn live.
    /// </summary>
    internal static class MapText
    {
        const string ElemSymbols = "abcdefghijklmnopqrstuvwxyz";
        const string BuildSymbols = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        /// <summary>
        /// layer: "terrain" (element per tile, buildings as capitals, duplicants @),
        ///        "gas" (gas letter + mass digit, 0-9 in 0.2 kg steps), "temp" (°C, 4 chars per tile).
        /// Rows run top (high y) to bottom. Unrevealed '~', never seen in this view '?'.
        /// </summary>
        public static string Region(int x0, int y0, int x1, int y1, string layer, int maxTiles = 2400)
        {
            var mem = MapMemory.Current;
            if (mem == null) return "(map not ready)";
            if (x1 < x0) { int t = x0; x0 = x1; x1 = t; }
            if (y1 < y0) { int t = y0; y0 = y1; y1 = t; }
            x0 = Mathf.Clamp(x0, 0, Grid.WidthInCells - 1); x1 = Mathf.Clamp(x1, 0, Grid.WidthInCells - 1);
            y0 = Mathf.Clamp(y0, 0, Grid.HeightInCells - 1); y1 = Mathf.Clamp(y1, 0, Grid.HeightInCells - 1);
            if ((x1 - x0 + 1) * (y1 - y0 + 1) > maxTiles) return $"(region too large: max {maxTiles} tiles)";

            var elemLegend = new Dictionary<string, char>();
            var buildLegend = new Dictionary<string, char>();
            char Sym(Dictionary<string, char> d, string symbols, string key)
            {
                if (!d.TryGetValue(key, out var c)) { c = d.Count < symbols.Length ? symbols[d.Count] : '*'; d[key] = c; }
                return c;
            }
            var dupeCells = new HashSet<int>(Components.LiveMinionIdentities.Items.Where(m => m != null).Select(m => Grid.PosToCell(m)));

            var sb = new StringBuilder();
            int w = layer == "temp" ? 4 : layer == "gas" ? 2 : 1;
            sb.Append("     ");
            for (int x = x0; x <= x1; x++) sb.Append(x % 10 == 0 ? (x % 1000).ToString().PadRight(w) : new string(' ', w));
            sb.AppendLine();
            for (int y = y1; y >= y0; y--)
            {
                sb.Append(y.ToString().PadLeft(4)).Append(' ');
                for (int x = x0; x <= x1; x++)
                {
                    int cell = Grid.XYToCell(x, y);
                    if (!MapMemory.Revealed(cell)) { sb.Append(new string('~', w)); continue; }
                    switch (layer)
                    {
                        case "gas":
                            if (!mem.TryGas(cell, out var g, out float kg, out _) || g == null) sb.Append("? ");
                            else if (g.IsSolid) sb.Append("##");
                            else if (g.IsLiquid) sb.Append("==");
                            else if (g.IsVacuum) sb.Append("  ");
                            else sb.Append(Sym(elemLegend, ElemSymbols, Name(g))).Append((char)('0' + Mathf.Clamp(Mathf.RoundToInt(kg * 5f), 0, 9)));
                            break;
                        case "temp":
                            sb.Append(mem.TryTemp(cell, out float k, out _) ? Mathf.RoundToInt(k - 273.15f).ToString().PadLeft(4) : "   ?");
                            break;
                        default:
                            if (dupeCells.Contains(cell)) { sb.Append('@'); break; }
                            var b = Grid.Objects[cell, (int)ObjectLayer.Building];
                            if (b != null) { sb.Append(Sym(buildLegend, BuildSymbols, Strip(b.GetProperName()))); break; }
                            if (!mem.TryElement(cell, out var e, out _) || e == null) sb.Append('?');
                            else if (e.IsVacuum) sb.Append(' ');
                            else sb.Append(Sym(elemLegend, ElemSymbols, Name(e)));
                            break;
                    }
                }
                sb.AppendLine();
            }
            sb.Append("legend: ");
            sb.Append(string.Join(", ", elemLegend.Select(kv => $"{kv.Value}={kv.Key}")));
            if (buildLegend.Count > 0) sb.Append("; buildings: ").Append(string.Join(", ", buildLegend.Select(kv => $"{kv.Value}={kv.Key}")));
            if (layer == "terrain") sb.Append("; @=duplicant");
            if (layer == "gas") sb.Append("; digit = gas mass in 0.2 kg steps (5 = 1 kg, 9 = 1.8 kg+); ## solid; == liquid");
            sb.Append("; ~ unrevealed; ? not seen in this view");
            return sb.ToString();
        }

        /// <summary>Everything the advisor may know about one tile.</summary>
        public static string Cell(int x, int y)
        {
            var mem = MapMemory.Current;
            int cell = Grid.XYToCell(x, y);
            if (mem == null || !Grid.IsValidCell(cell)) return "invalid tile";
            if (!MapMemory.Revealed(cell)) return $"({x},{y}) unrevealed";
            var sb = new StringBuilder($"({x},{y}): ");
            if (mem.TryElement(cell, out var e, out float es) && e != null) sb.Append($"{Name(e)}{Age(es)}");
            if (mem.TryGas(cell, out var g, out float kg, out float gs) && g != null && !g.IsSolid) sb.Append($", {Math.Round(kg * 1000)} g{Age(gs)}");
            if (mem.TryTemp(cell, out float k, out float ts)) sb.Append($", {Math.Round(k - 273.15f, 1)} °C{Age(ts)}");
            if (mem.TryGerms(cell, out byte gi, out int gc, out float ds) && gi != byte.MaxValue && gc > 0) sb.Append($", {gc} {DiseaseName(gi)}{Age(ds)}");
            var b = Grid.Objects[cell, (int)ObjectLayer.Building];
            if (b != null)
            {
                sb.Append($"; building: {Strip(b.GetProperName())}");
                var st = Snapshot.Statuses(b, problemsOnly: false);
                if (st.Count > 0) sb.Append(" [" + string.Join("; ", st.Select(s => (string)s)) + "]");
            }
            string nets = Networks.AtCell(cell);
            if (nets.Length > 0) sb.Append("; ").Append(nets);
            var room = Game.Instance.roomProber.GetCavityForCell(cell)?.room;
            if (room != null && room.roomType != null) sb.Append($"; room: {Strip(room.roomType.Name)}");
            return sb.ToString();
        }

        /// <summary>Buildings in a region with their status lines.</summary>
        public static string Buildings(int x0, int y0, int x1, int y1)
        {
            var lines = new List<string>();
            foreach (var bc in Components.BuildingCompletes.Items)
            {
                if (bc == null || Networks.IsNetworkPiece(bc)) continue; // pipes, wires and tiles: see the pipes query
                var xy = Grid.CellToXY(Grid.PosToCell(bc));
                if (xy.x < Math.Min(x0, x1) || xy.x > Math.Max(x0, x1) || xy.y < Math.Min(y0, y1) || xy.y > Math.Max(y0, y1)) continue;
                var st = Snapshot.Statuses(bc.gameObject, problemsOnly: false);
                string rates = Networks.Rates(bc.gameObject);
                lines.Add($"{Strip(bc.GetProperName())} @{xy.x},{xy.y}" + (st.Count > 0 ? ": " + string.Join("; ", st.Select(s => (string)s)) : "") + (rates.Length > 0 ? " | " + rates : ""));
                if (lines.Count >= 60) { lines.Add("…"); break; }
            }
            return lines.Count == 0 ? "no buildings there" : string.Join("\n", lines);
        }

        /// <summary>Known deposits of an element (by name), nearest to (x, y) first.</summary>
        public static string Find(string query, int x, int y)
        {
            var mem = MapMemory.Current;
            if (mem == null) return "(map not ready)";
            var seen = new HashSet<int>();
            var found = new List<(string name, int count, int cx, int cy, int dist)>();
            for (int cy = 0; cy < Grid.HeightInCells; cy++)
                for (int cx = 0; cx < Grid.WidthInCells; cx++)
                {
                    int cell = Grid.XYToCell(cx, cy);
                    if (seen.Contains(cell) || !Matches(mem, cell, query, out string name)) continue;
                    // Flood-fill this deposit.
                    var q = new Queue<int>(); q.Enqueue(cell); seen.Add(cell);
                    int count = 0; long sx = 0, sy = 0;
                    while (q.Count > 0)
                    {
                        int c = q.Dequeue(); count++;
                        var p = Grid.CellToXY(c); sx += p.x; sy += p.y;
                        foreach (int n in new[] { Grid.CellLeft(c), Grid.CellRight(c), Grid.CellAbove(c), Grid.CellBelow(c) })
                            if (Grid.IsValidCell(n) && !seen.Contains(n) && Matches(mem, n, query, out _)) { seen.Add(n); q.Enqueue(n); }
                    }
                    int mx = (int)(sx / count), my = (int)(sy / count);
                    found.Add((name, count, mx, my, Math.Abs(mx - x) + Math.Abs(my - y)));
                }
            if (found.Count == 0) return $"no known '{query}'";
            return string.Join("\n", found.OrderBy(f => f.dist).Take(8).Select(f => $"{f.name}: {f.count} tiles around {f.cx},{f.cy} ({f.dist} tiles away)"));
        }

        static bool Matches(MapMemory mem, int cell, string query, out string name)
        {
            name = null;
            if (!mem.TryElement(cell, out var e, out _) || e == null) return false;
            name = Name(e);
            return name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string Age(float seen)
        {
            if (MapMemory.Current == null || MapMemory.Current.Live) return "";
            float a = MapMemory.AgeCycles(seen);
            return a < 0.5f ? "" : $" (seen {a:0.#} cycles ago)";
        }

        public static string Name(Element e) => Strip(e.name ?? e.id.ToString());
        public static string Strip(string s) => string.IsNullOrEmpty(s) ? s : STRINGS.UI.StripLinkFormatting(s).Trim();
        static string DiseaseName(byte i) { try { return Strip(Db.Get().Diseases[i].Name); } catch { return "germs"; } }
    }
}
