using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OniAdvisor
{
    /// <summary>
    /// Writes map/ under the live folder: one PNG per layer plus map.bin for tools/MapQuery.
    /// Only the bounding box of revealed tiles is exported; what each tile shows comes from
    /// MapMemory (memory or live mode). Buildings and duplicants are the player's own and are
    /// always drawn live.
    /// </summary>
    internal static class MapExport
    {
        const int Scale = 4; // pixels per tile
        const float StaleCycles = 10f; // fully dimmed after this many cycles unseen

        static readonly Color32 Unknown = new Color32(12, 12, 14, 255);

        public static void WriteAll(int cycle)
        {
            var mem = MapMemory.Current;
            if (mem == null) return;
            if (!RevealedBounds(mem, out int ox, out int oy, out int w, out int h)) return;
            string dir = Paths.Sub("map");

            WritePng(Path.Combine(dir, "elements.png"), mem, ox, oy, w, h, ElementColor, overlayOwn: true);
            WritePng(Path.Combine(dir, "oxygen.png"), mem, ox, oy, w, h, OxygenColor, overlayOwn: false);
            WritePng(Path.Combine(dir, "temperature.png"), mem, ox, oy, w, h, TemperatureColor, overlayOwn: false);
            WritePng(Path.Combine(dir, "germs.png"), mem, ox, oy, w, h, GermColor, overlayOwn: false);
            if (!mem.Live) WritePng(Path.Combine(dir, "age.png"), mem, ox, oy, w, h, AgeColor, overlayOwn: false);

            var info = new JObject
            {
                ["mode"] = mem.Live ? "live" : "memory",
                ["cycle"] = cycle,
                ["originX"] = ox,
                ["originY"] = oy,
                ["widthTiles"] = w,
                ["heightTiles"] = h,
                ["pixelsPerTile"] = Scale,
                ["gridLines"] = "thin every 10 tiles, bright every 50 (counted from tile 0)",
                ["pixelToTile"] = $"x = {ox} + floor(px/{Scale});  y = {oy + h - 1} - floor(py/{Scale})  (px,py from top-left)",
                ["legend"] = new JObject
                {
                    ["all"] = "near-black = unrevealed or never seen in this view; in memory mode older observations are dimmer",
                    ["elements"] = "element colours; buildings tinted beige; white dots = duplicants",
                    ["oxygen"] = "cyan very breathable, blue breathable, pink barely; green shades same for polluted oxygen; red other gas; dark blue liquid; grey solid",
                    ["temperature"] = "blue <0C, cyan 10C, green 22C, yellow 35C, orange 50C, red 80C+",
                    ["germs"] = "yellow food poisoning, green slimelung, purple zombie spores, pink floral scent; brighter = more",
                    ["age"] = "memory mode: green just seen, yellow ~5 cycles, red 20+ cycles, black never",
                },
            };
            Paths.WriteAtomic(Path.Combine(dir, "map-info.json"), info.ToString(Formatting.Indented));
            WriteBinary(Path.Combine(dir, "map.bin"), mem, ox, oy, w, h, cycle);
        }

        static bool RevealedBounds(MapMemory mem, out int ox, out int oy, out int w, out int h)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            for (int y = 0; y < mem.H; y++)
                for (int x = 0; x < mem.W; x++)
                {
                    if (!MapMemory.Revealed(Grid.XYToCell(x, y))) continue;
                    if (x < x0) x0 = x; if (x > x1) x1 = x;
                    if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
            ox = x0; oy = y0; w = x1 - x0 + 1; h = y1 - y0 + 1;
            return x1 >= 0;
        }

        // ------------------------------------------------------------------ PNG

        delegate Color32 TileColor(MapMemory mem, int cell);

        static void WritePng(string path, MapMemory mem, int ox, int oy, int w, int h, TileColor color, bool overlayOwn)
        {
            var px = new Color32[w * Scale * h * Scale];
            int pw = w * Scale;
            for (int ty = 0; ty < h; ty++)
            {
                for (int tx = 0; tx < w; tx++)
                {
                    int x = ox + tx, y = oy + ty;
                    int cell = Grid.XYToCell(x, y);
                    Color32 c = MapMemory.Revealed(cell) ? color(mem, cell) : Unknown;
                    if (overlayOwn && MapMemory.Revealed(cell) && Grid.Objects[cell, (int)ObjectLayer.Building] != null)
                        c = Lerp(c, new Color32(225, 210, 180, 255), 0.5f);
                    for (int sy = 0; sy < Scale; sy++)
                    {
                        int py = ty * Scale + sy; // texture rows go bottom-up, like tile y
                        bool line = sy == 0 && y % 10 == 0;
                        for (int sx = 0; sx < Scale; sx++)
                        {
                            bool vline = sx == 0 && x % 10 == 0;
                            var p = c;
                            if (line || vline)
                                p = Lerp(c, new Color32(255, 255, 255, 255), (line && y % 50 == 0) || (vline && x % 50 == 0) ? 0.45f : 0.15f);
                            px[py * pw + tx * Scale + sx] = p;
                        }
                    }
                }
            }

            if (overlayOwn)
            {
                foreach (var mi in Components.LiveMinionIdentities.Items)
                {
                    if (mi == null) continue;
                    var xy = Grid.CellToXY(Grid.PosToCell(mi));
                    int tx = xy.x - ox, ty = xy.y - oy;
                    if (tx < 0 || ty < 0 || tx >= w || ty >= h) continue;
                    for (int sy = 1; sy < Scale - 1; sy++)
                        for (int sx = 1; sx < Scale - 1; sx++)
                            px[(ty * Scale + sy) * pw + tx * Scale + sx] = new Color32(255, 255, 255, 255);
                }
            }

            var tex = new Texture2D(pw, h * Scale, TextureFormat.RGB24, false);
            try
            {
                tex.SetPixels32(px);
                tex.Apply(false);
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            }
            finally { UnityEngine.Object.Destroy(tex); }
        }

        static Color32 ElementColor(MapMemory mem, int cell)
        {
            if (!mem.TryElement(cell, out var el, out float seen) || el == null) return Unknown;
            Color32 c;
            if (el.IsVacuum) c = new Color32(30, 30, 30, 255);
            else
            {
                c = el.substance != null ? el.substance.uiColour : new Color32(128, 128, 128, 255);
                c.a = 255;
                if (el.IsGas) c = Lerp(c, new Color32(235, 235, 235, 255), 0.45f);
            }
            return Dim(c, seen);
        }

        static Color32 OxygenColor(MapMemory mem, int cell)
        {
            if (!mem.TryGas(cell, out var el, out float mass, out float seen) || el == null) return Unknown;
            Color32 c;
            if (el.IsSolid) c = new Color32(70, 70, 70, 255);
            else if (el.IsLiquid) c = new Color32(30, 50, 110, 255);
            else if (el.IsVacuum) c = new Color32(30, 30, 30, 255);
            else if (el.id == SimHashes.Oxygen || el.id == SimHashes.ContaminatedOxygen)
            {
                bool polluted = el.id == SimHashes.ContaminatedOxygen;
                if (mass >= SimDebugView.optimallyBreathable) c = polluted ? new Color32(90, 200, 120, 255) : new Color32(90, 230, 240, 255);
                else if (mass >= SimDebugView.minimumBreathable) c = polluted ? new Color32(60, 150, 80, 255) : new Color32(110, 120, 240, 255);
                else c = polluted ? new Color32(150, 170, 90, 255) : new Color32(240, 120, 220, 255);
            }
            else c = new Color32(230, 70, 70, 255);
            return Dim(c, seen);
        }

        static Color32 TemperatureColor(MapMemory mem, int cell)
        {
            if (!mem.TryTemp(cell, out float k, out float seen)) return Unknown;
            if (mem.TryElement(cell, out var el, out _) && el != null && el.IsVacuum) return new Color32(30, 30, 30, 255);
            float cDeg = k - 273.15f;
            var stops = new (float t, Color32 c)[]
            {
                (-20f, new Color32(40, 60, 200, 255)), (0f, new Color32(60, 120, 255, 255)), (10f, new Color32(60, 220, 230, 255)),
                (22f, new Color32(80, 210, 90, 255)), (35f, new Color32(240, 230, 60, 255)), (50f, new Color32(250, 150, 40, 255)),
                (80f, new Color32(230, 40, 30, 255)), (200f, new Color32(255, 255, 255, 255)),
            };
            Color32 col = stops[0].c;
            if (cDeg >= stops[stops.Length - 1].t) col = stops[stops.Length - 1].c;
            else
                for (int i = 0; i < stops.Length - 1; i++)
                    if (cDeg < stops[i + 1].t)
                    {
                        float f = Mathf.Clamp01((cDeg - stops[i].t) / (stops[i + 1].t - stops[i].t));
                        col = Lerp(stops[i].c, stops[i + 1].c, f);
                        break;
                    }
            return Dim(col, seen);
        }

        static Color32 GermColor(MapMemory mem, int cell)
        {
            if (!mem.TryGerms(cell, out byte idx, out int count, out float seen)) return Unknown;
            var basec = new Color32(45, 45, 50, 255);
            if (idx == byte.MaxValue || count <= 0) return Dim(basec, seen);
            string n = DiseaseName(idx).ToLowerInvariant();
            Color32 g = n.Contains("food") ? new Color32(250, 220, 40, 255)
                : n.Contains("slime") ? new Color32(80, 230, 80, 255)
                : n.Contains("zombie") ? new Color32(180, 80, 230, 255)
                : n.Contains("floral") || n.Contains("pollen") ? new Color32(250, 140, 200, 255)
                : new Color32(240, 240, 240, 255);
            float f = Mathf.Clamp01(Mathf.Log10(count + 1) / 6f) * 0.8f + 0.2f;
            return Dim(Lerp(basec, g, f), seen);
        }

        static Color32 AgeColor(MapMemory mem, int cell)
        {
            float seen = mem.ElemSeen[cell];
            if (seen < 0f) return Unknown;
            float age = MapMemory.AgeCycles(seen);
            if (age <= 5f) return Lerp(new Color32(60, 220, 80, 255), new Color32(240, 220, 50, 255), age / 5f);
            return Lerp(new Color32(240, 220, 50, 255), new Color32(220, 40, 30, 255), Mathf.Clamp01((age - 5f) / 15f));
        }

        static Color32 Dim(Color32 c, float seen)
        {
            if (MapMemory.Current == null || MapMemory.Current.Live) return c;
            float f = Mathf.Clamp01(MapMemory.AgeCycles(seen) / StaleCycles) * 0.6f;
            return Lerp(c, new Color32(10, 10, 10, 255), f);
        }

        static Color32 Lerp(Color32 a, Color32 b, float t) => Color32.Lerp(a, b, t);

        static string DiseaseName(byte idx)
        {
            try { return Db.Get().Diseases[idx].Name; } catch { return "germ"; }
        }

        // ------------------------------------------------------------------ binary dump for tools/MapQuery

        public const int BinVersion = 1;

        static void WriteBinary(string path, MapMemory mem, int ox, int oy, int w, int h, int cycle)
        {
            var buildingNames = new List<string>();
            var buildingIndex = new Dictionary<string, short>();
            var cellBuilding = new short[w * h];
            for (int ty = 0; ty < h; ty++)
                for (int tx = 0; tx < w; tx++)
                {
                    int cell = Grid.XYToCell(ox + tx, oy + ty);
                    short bi = -1;
                    if (MapMemory.Revealed(cell))
                    {
                        var go = Grid.Objects[cell, (int)ObjectLayer.Building];
                        if (go != null)
                        {
                            string n = STRINGS.UI.StripLinkFormatting(go.GetProperName());
                            if (!buildingIndex.TryGetValue(n, out bi))
                            {
                                bi = (short)buildingNames.Count;
                                buildingNames.Add(n);
                                buildingIndex[n] = bi;
                            }
                        }
                    }
                    cellBuilding[ty * w + tx] = bi;
                }

            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionMode.Compress))
            using (var wr = new BinaryWriter(gz))
            {
                wr.Write("OAMB"); wr.Write(BinVersion);
                wr.Write(cycle); wr.Write(GameClock.Instance.GetTime());
                wr.Write(mem.Live ? "live" : "memory");
                wr.Write(ox); wr.Write(oy); wr.Write(w); wr.Write(h);

                var els = ElementLoader.elements;
                wr.Write(els.Count);
                foreach (var e in els)
                {
                    wr.Write(STRINGS.UI.StripLinkFormatting(e.name ?? e.id.ToString()));
                    wr.Write(e.IsSolid ? 'S' : e.IsLiquid ? 'L' : e.IsVacuum ? 'V' : 'G');
                }

                var dis = Db.Get().Diseases;
                wr.Write(dis.Count);
                for (int i = 0; i < dis.Count; i++) wr.Write(STRINGS.UI.StripLinkFormatting(dis[i].Name ?? "germ"));

                wr.Write(buildingNames.Count);
                foreach (var b in buildingNames) wr.Write(b);

                for (int ty = 0; ty < h; ty++)
                    for (int tx = 0; tx < w; tx++)
                    {
                        int cell = Grid.XYToCell(ox + tx, oy + ty);
                        bool rev = MapMemory.Revealed(cell);
                        bool ke = mem.TryElement(cell, out var el, out float es);
                        bool kg = mem.TryGas(cell, out var gel, out float gm, out float gs);
                        bool kt = mem.TryTemp(cell, out float tk, out float ts);
                        bool kd = mem.TryGerms(cell, out byte gi, out int gc, out float ds);
                        byte flags = (byte)((rev ? 1 : 0) | (ke ? 2 : 0) | (kg ? 4 : 0) | (kt ? 8 : 0) | (kd ? 16 : 0));
                        wr.Write(flags);
                        wr.Write(ke && el != null ? el.idx : (ushort)0); wr.Write(MapMemory.AgeCycles(es));
                        wr.Write(kg && gel != null ? gel.idx : (ushort)0); wr.Write(gm); wr.Write(MapMemory.AgeCycles(gs));
                        wr.Write(kt ? tk - 273.15f : 0f); wr.Write(MapMemory.AgeCycles(ts));
                        wr.Write(gi); wr.Write(gc); wr.Write(MapMemory.AgeCycles(ds));
                        wr.Write(cellBuilding[ty * w + tx]);
                    }

                var dupes = new List<(int x, int y, string n)>();
                foreach (var mi in Components.LiveMinionIdentities.Items)
                {
                    if (mi == null) continue;
                    var xy = Grid.CellToXY(Grid.PosToCell(mi));
                    dupes.Add((xy.x, xy.y, mi.GetProperName()));
                }
                wr.Write(dupes.Count);
                foreach (var d in dupes) { wr.Write(d.x); wr.Write(d.y); wr.Write(d.n); }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
