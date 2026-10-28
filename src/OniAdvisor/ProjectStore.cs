using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OniAdvisor
{
    /// <summary>
    /// Planned infrastructure grouped into projects, written by the advisor to projects/projects.json:
    ///
    /// { "projects": [ { "id": "h2", "name": "Hydrogen power", "purpose": "...", "dependsOn": [],
    ///     "items": [
    ///       { "type": "building", "prefab": "HydrogenGenerator", "x": 60, "y": 110, "rotation": "Neutral", "note": "..." },
    ///       { "type": "gaspipe", "path": [[91,112],[91,110],[64,110]], "note": "pump to generator" },
    ///       { "type": "wire", "path": [[62,109],[62,100]] } ] } ] }
    ///
    /// Pipe/wire paths are waypoints joined by straight segments. Prefab IDs are the game's internal
    /// building IDs (e.g. GasPump, GasFilter, HydrogenGenerator, Electrolyzer, LiquidPump, WaterPurifier).
    ///
    /// The mod checks every item against the map (built / ordered / ready / blocked + reason), sums the
    /// remaining materials, writes projects/projects-status.json and draws the projects as ghosts.
    /// Placing them as real build orders is a separate, optional delegation command (off by default).
    ///
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class ProjectStore
    {
        sealed class Item
        {
            public string Type, Prefab, Rotation, Note;
            public List<string> Materials;      // element ids per ingredient, optional ("material" or "materials")
            public int X, Y;
            public List<(int x, int y)> Path = new List<(int, int)>();
            // computed
            public string Status = "?", Reason;
            public List<int> Cells = new List<int>();
            public int Built, Ordered, Total;
        }

        sealed class Project
        {
            public string Id, Name, Purpose;
            public List<string> DependsOn = new List<string>();
            public List<Item> Items = new List<Item>();
            public Dictionary<string, float> Remaining = new Dictionary<string, float>();
        }

        static readonly List<Project> projects = new List<Project>();
        static readonly MarkRenderer layer = new MarkRenderer("projects");
        static System.DateTime fileTime;
        static string lastDrawKey = "";
        static int focus = -1; // -1 = all projects

        static string Dir => Paths.Sub("projects");
        static string FilePath => Path.Combine(Dir, "projects.json");
        static string StatusPath => Path.Combine(Dir, "projects-status.json");

        static readonly Dictionary<string, ObjectLayer> ConduitLayers = new Dictionary<string, ObjectLayer>(StringComparer.OrdinalIgnoreCase)
        {
            ["gaspipe"] = ObjectLayer.GasConduit,
            ["liquidpipe"] = ObjectLayer.LiquidConduit,
            ["wire"] = ObjectLayer.Wire,
            ["automation"] = ObjectLayer.LogicWire,
            ["heavywire"] = ObjectLayer.Wire,
            ["conveyor"] = ObjectLayer.SolidConduit,
        };

        static readonly Dictionary<string, string> ConduitPrefabs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gaspipe"] = "GasConduit", ["liquidpipe"] = "LiquidConduit", ["wire"] = "Wire", ["automation"] = "LogicWire",
            ["heavywire"] = "HighWattageWire", ["conveyor"] = "SolidConduit",
        };

        // ------------------------------------------------------------------ loading

        public static void Poll()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    if (projects.Count > 0) { projects.Clear(); layer.Clear(); lastDrawKey = ""; }
                    return;
                }
                var t = File.GetLastWriteTimeUtc(FilePath);
                if (t != fileTime && (System.DateTime.UtcNow - t).TotalMilliseconds >= 500)
                {
                    Load();       // throws on a half-written file -> retried next poll
                    fileTime = t;
                }
                Refresh();
            }
            catch (Exception e) { Log.Warn("projects poll failed: " + e.Message); }
        }

        static void Load()
        {
            projects.Clear();
            var root = JObject.Parse(File.ReadAllText(FilePath));
            foreach (var p in root["projects"] ?? new JArray())
            {
                var proj = new Project
                {
                    Id = (string)p["id"] ?? $"p{projects.Count + 1}",
                    Name = (string)p["name"] ?? "Project",
                    Purpose = (string)p["purpose"],
                };
                foreach (var d in p["dependsOn"] ?? new JArray()) proj.DependsOn.Add((string)d);
                foreach (var it in p["items"] ?? new JArray())
                {
                    var item = new Item
                    {
                        Type = ((string)it["type"] ?? "building").ToLowerInvariant(),
                        Prefab = (string)it["prefab"],
                        Rotation = (string)it["rotation"] ?? "Neutral",
                        Note = (string)it["note"],
                        X = (int?)it["x"] ?? 0,
                        Y = (int?)it["y"] ?? 0,
                    };
                    if (it["materials"] is JArray mats) item.Materials = mats.Select(m => (string)m).ToList();
                    else if (it["material"] != null) item.Materials = new List<string> { (string)it["material"] };
                    if (it["path"] is JArray path)
                    {
                        var pts = path.Select(pt => ((int)pt[0], (int)pt[1])).ToList();
                        item.Path = ExpandPath(pts);
                    }
                    proj.Items.Add(item);
                }
                projects.Add(proj);
            }
            if (focus >= projects.Count) focus = -1;
            lastDrawKey = "";
            Log.Info($"projects loaded: {projects.Count}");
        }

        /// <summary>Joins waypoints with straight horizontal/vertical runs (diagonals become an L).</summary>
        static List<(int x, int y)> ExpandPath(List<(int x, int y)> pts)
        {
            var cells = new List<(int, int)>();
            for (int i = 0; i < pts.Count; i++)
            {
                if (i == 0) { cells.Add(pts[0]); continue; }
                var (x, y) = pts[i - 1];
                var (tx, ty) = pts[i];
                while (x != tx) { x += Math.Sign(tx - x); cells.Add((x, y)); }
                while (y != ty) { y += Math.Sign(ty - y); cells.Add((x, y)); }
            }
            return cells.Distinct().ToList();
        }

        // ------------------------------------------------------------------ status

        static void Refresh()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in projects)
            {
                p.Remaining.Clear();
                foreach (var it in p.Items)
                {
                    try { Evaluate(it, p); }
                    catch (Exception e) { it.Status = "error"; it.Reason = e.Message; }
                    sb.Append(it.Status).Append(it.Built).Append(it.Ordered).Append('|');
                }
            }
            string key = sb + "/" + focus + "/" + Overlays.ShowProjects + "/" + forcedId + "/" + forcedCount;
            if (key != lastDrawKey)
            {
                lastDrawKey = key;
                Draw();
                WriteStatus();
            }
        }

        static Orientation ParseOrientation(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "r90": case "90": return Orientation.R90;
                case "r180": case "180": return Orientation.R180;
                case "r270": case "270": return Orientation.R270;
                case "fliph": return Orientation.FlipH;
                case "flipv": return Orientation.FlipV;
                default: return Orientation.Neutral;
            }
        }

        static void Evaluate(Item it, Project p)
        {
            it.Cells.Clear(); it.Built = it.Ordered = 0; it.Reason = null;
            if (it.Type == "building")
            {
                var def = string.IsNullOrEmpty(it.Prefab) ? null : Assets.GetBuildingDef(it.Prefab);
                if (def == null) { it.Status = "unknown"; it.Reason = "unknown prefab " + it.Prefab; it.Total = 1; return; }
                int cell = Grid.XYToCell(it.X, it.Y);
                var orient = ParseOrientation(it.Rotation);
                foreach (var off in def.PlacementOffsets ?? new[] { new CellOffset(0, 0) })
                {
                    int c = Grid.OffsetCell(cell, Rotatable.GetRotatedCellOffset(off, orient));
                    if (Grid.IsValidCell(c)) it.Cells.Add(c);
                }
                it.Total = 1;
                var occ = Grid.IsValidCell(cell) ? Grid.Objects[cell, (int)def.ObjectLayer] : null;
                if (occ != null)
                {
                    var bld = occ.GetComponent<Building>();
                    bool same = bld != null && bld.Def != null && bld.Def.PrefabID == def.PrefabID;
                    if (same && occ.GetComponent<Constructable>() != null) { it.Status = "ordered"; it.Ordered = 1; }
                    else if (same) { it.Status = "built"; it.Built = 1; return; }
                    else { it.Status = "blocked"; it.Reason = "occupied by " + STRINGS.UI.StripLinkFormatting(occ.GetProperName()); }
                }
                else
                {
                    var checks = BuildPlacer.Check(new BuildPlacer.Piece { Def = def, Cell = cell, Orientation = orient });
                    var bad = checks.FirstOrDefault(c => c.Fit == BuildPlacer.Fit.Blocked);
                    if (bad != null) { it.Status = "blocked"; it.Reason = bad.Reason; }
                    else if (checks.Any(c => c.Fit == BuildPlacer.Fit.Dig)) { it.Status = "ready"; it.Reason = "needs digging first (placing it orders the digging)"; }
                    else it.Status = "ready";
                }
                AddMaterials(p, def, 1);
                return;
            }

            if (!ConduitLayers.TryGetValue(it.Type, out var layerId))
            {
                it.Status = "unknown"; it.Reason = "unknown item type " + it.Type; return;
            }
            it.Total = it.Path.Count;
            int missing = 0;
            foreach (var (x, y) in it.Path)
            {
                int c = Grid.XYToCell(x, y);
                if (!Grid.IsValidCell(c)) continue;
                it.Cells.Add(c);
                var occ = Grid.Objects[c, (int)layerId];
                if (occ == null) missing++;
                else if (occ.GetComponent<Constructable>() != null) it.Ordered++;
                else it.Built++;
            }
            it.Status = it.Built == it.Total ? "built" : missing == 0 ? "ordered" : it.Built + it.Ordered > 0 ? "partial" : "ready";
            var cdef = ConduitDef(it);
            if (cdef != null) AddMaterials(p, cdef, missing);
        }

        static void AddMaterials(Project p, BuildingDef def, int count)
        {
            if (count <= 0 || def.Mass == null || def.MaterialCategory == null) return;
            for (int i = 0; i < def.Mass.Length && i < def.MaterialCategory.Length; i++)
            {
                string cat = def.MaterialCategory[i].Replace("&", " or ");
                p.Remaining[cat] = (p.Remaining.TryGetValue(cat, out var v) ? v : 0f) + def.Mass[i] * count;
            }
        }

        static void WriteStatus()
        {
            var arr = new JArray();
            foreach (var p in projects)
            {
                var items = new JArray();
                int n = 0;
                foreach (var it in p.Items)
                {
                    n++;
                    var o = new JObject { ["n"] = n, ["type"] = it.Type, ["status"] = it.Status };
                    if (it.Type == "building") { o["prefab"] = it.Prefab; o["at"] = $"{it.X},{it.Y}"; }
                    else o["tiles"] = $"{it.Built} built, {it.Ordered} ordered, {it.Total} total";
                    if (it.Reason != null) o["reason"] = it.Reason;
                    if (it.Note != null) o["note"] = it.Note;
                    items.Add(o);
                }
                var mats = new JObject();
                foreach (var kv in p.Remaining.OrderByDescending(kv => kv.Value)) mats[kv.Key] = Math.Round(kv.Value);
                arr.Add(new JObject
                {
                    ["id"] = p.Id, ["name"] = p.Name, ["purpose"] = p.Purpose,
                    ["dependsOn"] = new JArray(p.DependsOn),
                    ["progress"] = Progress(p),
                    ["materialsStillNeededKg"] = mats,
                    ["items"] = items,
                });
            }
            Paths.WriteAtomic(StatusPath, new JObject { ["projects"] = arr }.ToString(Formatting.Indented));
        }

        static string Progress(Project p)
        {
            int built = p.Items.Count(i => i.Status == "built");
            int ordered = p.Items.Count(i => i.Status == "ordered" || i.Status == "partial");
            int blocked = p.Items.Count(i => i.Status == "blocked" || i.Status == "unknown" || i.Status == "error");
            return $"{built}/{p.Items.Count} built, {ordered} in progress, {blocked} blocked";
        }

        /// <summary>One line per project for state.json.</summary>
        public static JToken StatusSummary()
        {
            var arr = new JArray();
            foreach (var p in projects) arr.Add($"{p.Name} [{p.Id}]: {Progress(p)}");
            return arr;
        }

        // ------------------------------------------------------------------ drawing

        static string forcedId; // briefing step showing one project
        static int forcedCount = -1; // how many of its pieces (a briefing builds it up as it narrates); -1 = all

        /// <summary>
        /// Shows just this project (even with the overlay off) until called with null; with a count, only its first
        /// pieces, so a briefing can build the ghost up piece by piece as it talks.
        /// </summary>
        public static void ForceShow(string projectId, int count = -1)
        {
            forcedId = string.IsNullOrEmpty(projectId) ? null : projectId;
            forcedCount = forcedId == null ? -1 : count;
            lastDrawKey = "";
            Refresh();
        }

        /// <summary>Shows just this project's ghost (and turns the project overlay on).</summary>
        public static string Focus(string id)
        {
            int i = projects.FindIndex(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return "No such project";
            focus = i;
            if (!Overlays.ShowProjects) Overlays.ShowProjectsNow();
            lastDrawKey = "";
            return "Showing " + projects[i].Name;
        }

        public static string CycleFocus()
        {
            focus = projects.Count == 0 ? -1 : focus + 1 >= projects.Count ? -1 : focus + 1;
            lastDrawKey = "";
            string d = projects.Count == 0 ? "none yet" : focus < 0 ? $"showing all {projects.Count}" : $"showing {projects[focus].Name}";
            Log.Info("project focus: " + d);
            return d;
        }

        static void Draw()
        {
            layer.Clear();
            layer.Visible = Overlays.ShowProjects || forcedId != null;
            for (int pi = 0; pi < projects.Count; pi++)
            {
                if (forcedId != null) { if (!string.Equals(projects[pi].Id, forcedId, StringComparison.OrdinalIgnoreCase)) continue; }
                else if (focus >= 0 && pi != focus) continue;
                var p = projects[pi];
                int n = 0;
                foreach (var it in p.Items)
                {
                    n++;
                    if (forcedId != null && forcedCount >= 0 && n > forcedCount) break; // not narrated yet
                    var col = StatusColor(it.Status);
                    if (it.Type == "building")
                    {
                        foreach (int c in it.Cells)
                        {
                            var xy = Grid.CellToXY(c);
                            layer.Rect(xy.x, xy.y, xy.x, xy.y, col);
                        }
                        if (it.Cells.Count == 0) continue;
                        var pts = it.Cells.Select(Grid.CellToXY).ToList();
                        int x0 = pts.Min(q => q.x), x1 = pts.Max(q => q.x), y0 = pts.Min(q => q.y), y1 = pts.Max(q => q.y);
                        layer.Outline(x0, y0, x1, y1, new Color(col.r, col.g, col.b, 0.9f));
                        var def = Assets.GetBuildingDef(it.Prefab ?? "");
                        if (def != null && it.Status != "built")
                        {
                            Sprite icon = null;
                            try { icon = def.GetUISprite("ui", false); } catch { }
                            layer.Icon((x0 + x1 + 1) / 2f, (y0 + y1 + 1) / 2f, icon, new Color(1, 1, 1, 0.8f), Math.Min(x1 - x0 + 1, y1 - y0 + 1) * 0.8f);
                        }
                        if (it.Status != "built")
                            layer.Label((x0 + x1 + 1) / 2f, y1 + 1.5f, $"{p.Id}.{n} {(def != null ? STRINGS.UI.StripLinkFormatting(def.Name) : it.Prefab)}", new Color(col.r, col.g, col.b, 1f));
                    }
                    else
                    {
                        var typeCol = it.Type == "gaspipe" ? new Color(0.65f, 0.95f, 0.45f, 0.75f)
                            : it.Type == "liquidpipe" ? new Color(0.35f, 0.65f, 1f, 0.75f)
                            : it.Type == "wire" ? new Color(1f, 0.6f, 0.2f, 0.75f)
                            : new Color(0.4f, 1f, 0.7f, 0.75f);
                        foreach (int c in it.Cells)
                        {
                            var xy = Grid.CellToXY(c);
                            var occ = Grid.Objects[c, (int)ConduitLayers[it.Type]];
                            if (occ != null && occ.GetComponent<Constructable>() == null) continue; // already built
                            layer.Dot(xy.x, xy.y, typeCol, 0.42f);
                        }
                        if (it.Cells.Count > 0 && it.Status != "built")
                        {
                            var first = Grid.CellToXY(it.Cells[0]);
                            layer.Label(first.x + 0.5f, first.y + 1.4f, $"{p.Id}.{n} {it.Type}", typeCol);
                        }
                    }
                }
            }
        }

        static Color StatusColor(string s)
        {
            switch (s)
            {
                case "built": return new Color(0.3f, 0.9f, 0.3f, 0.12f);
                case "ordered": case "partial": return new Color(1f, 0.85f, 0.2f, 0.3f);
                case "blocked": return new Color(1f, 0.25f, 0.2f, 0.35f);
                case "unknown": case "error": return new Color(1f, 0.2f, 1f, 0.35f);
                default: return new Color(1f, 1f, 1f, 0.25f); // ready
            }
        }

        // ------------------------------------------------------------------ placement

        static BuildingDef ConduitDef(Item it) =>
            Assets.GetBuildingDef(!string.IsNullOrEmpty(it.Prefab) ? it.Prefab : ConduitPrefabs.TryGetValue(it.Type, out var id) ? id : "");

        /// <summary>A project's items as pieces to check or place (unknown prefabs left out).</summary>
        public static List<BuildPlacer.Piece> Pieces(string id, out string name)
        {
            var p = Find(id);
            name = p?.Name;
            var pieces = new List<BuildPlacer.Piece>();
            if (p == null) return pieces;
            int n = 0;
            foreach (var it in p.Items)
            {
                n++;
                if (it.Type == "building")
                {
                    var def = string.IsNullOrEmpty(it.Prefab) ? null : Assets.GetBuildingDef(it.Prefab);
                    if (def == null) continue;
                    pieces.Add(new BuildPlacer.Piece { Def = def, Cell = Grid.XYToCell(it.X, it.Y), Orientation = ParseOrientation(it.Rotation), Materials = it.Materials, Label = $"{p.Id}.{n}" });
                }
                else if (ConduitLayers.ContainsKey(it.Type))
                {
                    var def = ConduitDef(it);
                    if (def == null || it.Path.Count == 0) continue;
                    pieces.Add(new BuildPlacer.Piece { Def = def, Path = it.Path.Select(c => Grid.XYToCell(c.x, c.y)).ToList(), Materials = it.Materials, Label = $"{p.Id}.{n} {it.Type}" });
                }
            }
            return pieces;
        }

        static Project Find(string id) => projects.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The projects, for the advisor panel: id, name, progress line.</summary>
        public static List<(string id, string name, string progress)> List() => projects.Select(p => (p.Id, p.Name, Progress(p))).ToList();

        /// <summary>How many pieces a project has (briefings reveal them one by one).</summary>
        public static int ItemCount(string id) => Find(id)?.Items.Count ?? 0;

        /// <summary>
        /// Places build orders for a whole project where it was planned: buildings, pipes, wires, rails and
        /// automation (with their connections), dig orders for what is in the way; the rest waits until it fits.
        /// Used by the advisor's placeProject command and the panel's PLACE button. Returns a summary.
        /// </summary>
        public static string PlaceProject(string id, int priority)
        {
            var pieces = Pieces(id, out var name);
            if (name == null) return "no such project: " + id;
            var result = BuildPlacer.Place(pieces, priority);
            lastDrawKey = "";
            return $"{name}: {result}";
        }

        public static void Clear()
        {
            projects.Clear();
            layer.Clear();
            fileTime = default;
            lastDrawKey = "";
            focus = -1;
        }
    }
}
