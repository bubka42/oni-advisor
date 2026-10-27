using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OniAdvisor
{
    /// <summary>
    /// What the player is looking at, and answers to the advisor's questions about the map.
    ///
    /// live/view.json (every 2 s): visible area, overlay, the tile under the cursor, the selected object, and a
    /// text map around the cursor - so "should I build it here?" has a "here".
    ///
    /// live/queries/req-*.json → live/queries/res-*.json, answered within a fraction of a second:
    ///   {"kind":"region","x0":..,"y0":..,"x1":..,"y1":..,"layer":"terrain|gas|temp"}
    ///   {"kind":"cell","x":..,"y":..}
    ///   {"kind":"buildings","x0":..,"y0":..,"x1":..,"y1":..}
    ///   {"kind":"pipes","x0":..,"y0":..,"x1":..,"y1":..,"layer":"gas|liquid|power|rail"}   network layout + contents
    ///   {"kind":"ports","x0":..,"y0":..,"x1":..,"y1":..}  buildings' pipe/power ports and what reaches them
    ///   {"kind":"rates"}                                   colony production/consumption per element
    ///   {"kind":"find","what":"coal","x":..,"y":..}        nearest known deposits
    ///   {"kind":"view"}                                     a fresh view.json
    /// Everything goes through MapMemory, so memory mode and the fog of war apply. tools/MapQuery's
    /// "live" command writes the request and waits for the answer.
    /// </summary>
    internal static class LiveQueries
    {
        const int ViewW = 40, ViewH = 20;
        static float nextView, nextQuery;

        static string QDir => Paths.Sub("queries");

        public static void Tick(float now)
        {
            if (now >= nextView)
            {
                nextView = now + 2f;
                try { Paths.WriteAtomic(Path.Combine(Paths.LiveDir, "view.json"), View().ToString(Formatting.Indented)); }
                catch (Exception e) { Log.Warn("view: " + e.Message); }
            }
            if (now < nextQuery) return;
            nextQuery = now + 0.15f;
            string[] reqs;
            try { reqs = Directory.GetFiles(QDir, "req-*.json"); } catch { return; }
            foreach (var f in reqs)
            {
                string id = Path.GetFileNameWithoutExtension(f).Substring(4);
                string answer;
                try { answer = Answer(JObject.Parse(File.ReadAllText(f))); }
                catch (IOException) { continue; } // still being written
                catch (Exception e) { answer = "error: " + e.Message; }
                try
                {
                    File.Delete(f);
                    Paths.WriteAtomic(Path.Combine(QDir, "res-" + id + ".txt"), answer);
                }
                catch (Exception e) { Log.Warn("query answer: " + e.Message); }
            }
        }

        static string Answer(JObject q)
        {
            int I(string k, int d = 0) => (int?)q[k] ?? d;
            switch (((string)q["kind"] ?? "").ToLowerInvariant())
            {
                case "region": return MapText.Region(I("x0"), I("y0"), I("x1"), I("y1"), ((string)q["layer"] ?? "terrain").ToLowerInvariant());
                case "cell": return MapText.Cell(I("x"), I("y"));
                case "buildings": return MapText.Buildings(I("x0"), I("y0"), I("x1"), I("y1"));
                case "find": return MapText.Find((string)q["what"] ?? "", I("x", Grid.WidthInCells / 2), I("y", Grid.HeightInCells / 2));
                case "pipes": return Networks.Map(I("x0"), I("y0"), I("x1"), I("y1"), ((string)q["layer"] ?? "gas").ToLowerInvariant());
                case "ports": return Networks.Ports(I("x0"), I("y0"), I("x1"), I("y1"));
                case "rates": return Networks.ColonyRates().ToString(Formatting.Indented);
                case "view": return View().ToString(Formatting.Indented);
                default: return "unknown query kind; use region, cell, buildings, pipes, ports, rates, find or view";
            }
        }

        /// <summary>The player's current view: visible area, overlay, cursor tile, selection, and a map around the cursor.</summary>
        public static JObject View()
        {
            var o = new JObject { ["cycle"] = GameClock.Instance.GetCycle() + 1 };
            var area = GridVisibleArea.GetVisibleArea();
            o["visibleArea"] = $"x{Math.Max(0, area.Min.x)}-{Math.Min(Grid.WidthInCells - 1, area.Max.x)} y{Math.Max(0, area.Min.y)}-{Math.Min(Grid.HeightInCells - 1, area.Max.y)}";
            o["overlay"] = OverlayName();

            int cx, cy;
            var cur = Grid.PosToCell(PlayerController.GetCursorPos(KInputManager.GetMousePos()));
            if (Grid.IsValidCell(cur))
            {
                var xy = Grid.CellToXY(cur); cx = xy.x; cy = xy.y;
                o["cursor"] = MapText.Cell(cx, cy);
            }
            else { cx = (area.Min.x + area.Max.x) / 2; cy = (area.Min.y + area.Max.y) / 2; }

            var sel = SelectTool.Instance != null ? SelectTool.Instance.selected : null;
            if (sel != null)
            {
                var p = Grid.CellToXY(Grid.PosToCell(sel));
                var st = Snapshot.Statuses(sel.gameObject, problemsOnly: false);
                o["selected"] = $"{MapText.Strip(sel.GetProperName())} @{p.x},{p.y}" + (st.Count > 0 ? " [" + string.Join("; ", st.Select(s => (string)s)) + "]" : "");
            }

            int x0 = cx - ViewW / 2, y0 = cy - ViewH / 2;
            o["aroundCursor"] = MapText.Region(x0, y0, x0 + ViewW - 1, y0 + ViewH - 1, "terrain");
            return o;
        }

        static string OverlayName()
        {
            var m = OverlayScreen.Instance != null ? OverlayScreen.Instance.GetMode() : OverlayModes.None.ID;
            if (m == OverlayModes.None.ID) return "none";
            if (m == OverlayModes.Oxygen.ID) return "oxygen";
            if (m == OverlayModes.Power.ID) return "power";
            if (m == OverlayModes.Temperature.ID) return "temperature";
            if (m == OverlayModes.Disease.ID) return "germs";
            if (m == OverlayModes.GasConduits.ID) return "ventilation";
            if (m == OverlayModes.LiquidConduits.ID) return "plumbing";
            if (m == OverlayModes.Rooms.ID) return "rooms";
            return "other";
        }
    }
}
