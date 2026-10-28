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
    /// A planning mark in tile coordinates: area, line (shaft/route), point; advisor marks also path (a route
    /// through several points, optionally ending in an arrow), arrow, and outline (the footprint of the
    /// building at x0,y0). Tone colours advisor marks by meaning: problem, build, check, info.
    /// </summary>
    internal sealed class Mark
    {
        public int Id;
        public string By = "player"; // player | advisor
        public string Kind = "area";  // area | line | point | path | arrow | outline
        public int X0, Y0, X1, Y1;
        public string Label;
        public string At;    // briefing marks: a phrase of the step's spoken text; the mark is drawn when the narration reaches it
        public string Note;  // advisor marks: what the chip shows when clicked (else the message that drew the marks)
        public string Color; // optional "#RRGGBB" for advisor marks
        public string Tone;  // problem | build | check | info (advisor marks)
        public bool Arrow;   // path: arrow head at the last point
        public List<(int x, int y)> Points = new List<(int x, int y)>();

        public JObject ToJson() => new JObject
        {
            ["id"] = Id, ["by"] = By, ["kind"] = Kind,
            ["x0"] = X0, ["y0"] = Y0, ["x1"] = X1, ["y1"] = Y1,
            ["label"] = Label, ["color"] = Color, ["tone"] = Tone,
        };

        public static Mark FromJson(JToken t) => new Mark
        {
            Id = (int?)t["id"] ?? 0,
            By = (string)t["by"] ?? "advisor",
            Kind = ((string)t["kind"] ?? "area").ToLowerInvariant(),
            X0 = (int?)t["x0"] ?? (int?)t["x"] ?? 0,
            Y0 = (int?)t["y0"] ?? (int?)t["y"] ?? 0,
            X1 = (int?)t["x1"] ?? (int?)t["x0"] ?? (int?)t["x"] ?? 0,
            Y1 = (int?)t["y1"] ?? (int?)t["y0"] ?? (int?)t["y"] ?? 0,
            Label = (string)t["label"],
            Note = (string)t["note"],
            At = (string)t["at"],
            Color = (string)t["color"],
            Tone = ((string)t["tone"])?.ToLowerInvariant(),
            Arrow = (bool?)t["arrow"] ?? false,
            Points = (t["points"] as JArray)?.Select(p => ((int)p[0], (int)p[1])).ToList() ?? new List<(int x, int y)>(),
        };

        public bool Contains(int x, int y)
        {
            if (Kind == "line") return LineCells().Any(c => c.x == x && c.y == y);
            return x >= Math.Min(X0, X1) && x <= Math.Max(X0, X1) && y >= Math.Min(Y0, Y1) && y <= Math.Max(Y0, Y1);
        }

        /// <summary>Bresenham line between the endpoints, inclusive.</summary>
        public IEnumerable<(int x, int y)> LineCells()
        {
            int x = X0, y = Y0, dx = Math.Abs(X1 - X0), dy = -Math.Abs(Y1 - Y0);
            int sx = X0 < X1 ? 1 : -1, sy = Y0 < Y1 ? 1 : -1, err = dx + dy;
            for (int guard = 0; guard < 2000; guard++)
            {
                yield return (x, y);
                if (x == X1 && y == Y1) yield break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x += sx; }
                if (e2 <= dx) { err += dx; y += sy; }
            }
        }
    }

    /// <summary>
    /// Player marks (made in planning mode) and advisor marks (written by the advisor to
    /// plan/advisor-marks.json, re-read when the file changes). Both are drawn on the map.
    /// </summary>
    internal static class PlanMarks
    {
        public static readonly List<Mark> Player = new List<Mark>();
        public static readonly List<Mark> Advisor = new List<Mark>();
        public static bool PlayerDirty;

        static readonly MarkRenderer playerLayer = new MarkRenderer("player-marks");
        static readonly MarkRenderer advisorLayer = new MarkRenderer("advisor-marks") { ChipLabels = true };
        static readonly MarkRenderer previewLayer = new MarkRenderer("preview");
        static System.DateTime advisorFileTime;

        static readonly Color PlayerFill = new Color(1f, 0.85f, 0.2f, 0.22f);
        static readonly Color PlayerLine = new Color(1f, 0.85f, 0.2f, 0.95f);
        static readonly Color AdvisorFill = new Color(0.25f, 0.9f, 1f, 0.2f);
        static readonly Color AdvisorLine = new Color(0.25f, 0.9f, 1f, 0.95f);

        static string PlayerFile => Path.Combine(Paths.Sub("plan"), "player-marks.json");
        static string AdvisorFile => Path.Combine(Paths.Sub("plan"), "advisor-marks.json");

        public static void LoadPlayer()
        {
            Player.Clear();
            try
            {
                if (File.Exists(PlayerFile))
                    foreach (var t in JObject.Parse(File.ReadAllText(PlayerFile))["marks"] ?? new JArray())
                        Player.Add(Mark.FromJson(t));
            }
            catch (Exception e) { Log.Warn("player marks load failed: " + e.Message); }
            Redraw();
        }

        public static void SavePlayer()
        {
            var o = new JObject
            {
                ["cycle"] = GameClock.Instance.GetCycle() + 1,
                ["marks"] = new JArray(Player.Select(m => m.ToJson())),
            };
            Paths.WriteAtomic(PlayerFile, o.ToString(Formatting.Indented));
            PlayerDirty = false;
        }

        public static Mark AddPlayer(string kind, int x0, int y0, int x1, int y1)
        {
            var m = new Mark
            {
                Id = Player.Count == 0 ? 1 : Player.Max(p => p.Id) + 1,
                By = "player", Kind = kind, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
            };
            Player.Add(m);
            PlayerDirty = true;
            Redraw();
            return m;
        }

        public static bool RemovePlayerAt(int x, int y)
        {
            var hit = Player.LastOrDefault(m => m.Contains(x, y));
            if (hit == null) return false;
            Player.Remove(hit);
            PlayerDirty = true;
            Redraw();
            return true;
        }

        public static void ClearPlayer()
        {
            Player.Clear();
            PlayerDirty = true;
            Redraw();
        }

        static bool drawnSketch = true;

        /// <summary>Cheap; call every second or so.</summary>
        public static void PollAdvisorFile()
        {
            try
            {
                if (Prefs.SketchMarks != drawnSketch) { drawnSketch = Prefs.SketchMarks; Redraw(); } // mark style changed in the options
                if (!File.Exists(AdvisorFile))
                {
                    if (Advisor.Count > 0) { Advisor.Clear(); Redraw(); }
                    return;
                }
                var t = File.GetLastWriteTimeUtc(AdvisorFile);
                if (t == advisorFileTime) return;
                if ((System.DateTime.UtcNow - t).TotalMilliseconds < 500) return; // may still be being written
                var root = JToken.Parse(File.ReadAllText(AdvisorFile)); // throws -> retried next poll
                advisorFileTime = t;
                Advisor.Clear();
                var arr = root is JArray a ? a : root["marks"] as JArray ?? new JArray();
                int i = 1;
                foreach (var m in arr)
                {
                    var mark = Mark.FromJson(m);
                    mark.By = "advisor";
                    if (mark.Id == 0) mark.Id = i;
                    i++;
                    Advisor.Add(mark);
                }
                Redraw(revealAdvisor: true);
                Log.Info($"advisor marks loaded: {Advisor.Count}");
                // New marks are meant to be seen: show them even if the overlay was off.
                if (Advisor.Count > 0 && !Overlays.ShowMarks)
                {
                    Overlays.ShowMarksNow();
                    AdvisorHud.Show($"The advisor marked {Advisor.Count} thing(s) on your map  ({Hotkeys.Describe("overlay")}: overlay)", 4f);
                }
            }
            catch (Exception e) { Log.Warn("advisor marks load failed: " + e.Message); }
        }

        public static void SetVisible(bool visible)
        {
            playerLayer.Visible = visible;
            advisorLayer.Visible = visible;
        }

        public static void Preview(string kind, int x0, int y0, int x1, int y1)
        {
            previewLayer.Clear();
            Draw(previewLayer, new Mark { Kind = kind, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 }, PlayerFill, PlayerLine, showLabel: false);
        }

        public static void ClearPreview() => previewLayer.Clear();

        public static void Redraw(bool revealAdvisor = false)
        {
            playerLayer.Clear();
            foreach (var m in Player) Draw(playerLayer, m, PlayerFill, PlayerLine, showLabel: true);
            advisorLayer.Clear();
            if (revealAdvisor) advisorLayer.BeginReveal(); else advisorLayer.NoReveal();
            foreach (var m in Advisor)
            {
                var line = ParseColor(m.Color, ToneColor(m.Tone));
                var mark = m;
                Draw(advisorLayer, m, new Color(line.r, line.g, line.b, 0.2f), line, showLabel: true, onClick: () => ShowMarkSource(mark));
            }
        }

        /// <summary>
        /// A click on an advisor mark's label: its own note if it has one, else the advisor's message that drew
        /// the marks (the companion records it in plan/advisor-marks-source.json), in the advisor card with REPLY.
        /// </summary>
        static void ShowMarkSource(Mark m)
        {
            long chat = 0;
            string message = null;
            try
            {
                var src = Path.Combine(Paths.Sub("plan"), "advisor-marks-source.json");
                if (File.Exists(src)) chat = (long?)JObject.Parse(File.ReadAllText(src))["chat"] ?? 0;
                var chatFile = Path.Combine(Paths.Sub("chat"), "chat.json");
                if (chat > 0 && File.Exists(chatFile))
                    message = (string)JArray.Parse(File.ReadAllText(chatFile)).FirstOrDefault(e => (long?)e["n"] == chat)?["text"];
            }
            catch { }
            string head = string.IsNullOrEmpty(m.Label) ? "" : $"**{m.Label}**\n";
            string text = !string.IsNullOrEmpty(m.Note) ? head + m.Note
                : message != null ? head + message
                : head + "The advisor drew this without a message. Ask about it with REPLY.";
            AdvisorCard.Show(null, text, chat, spoken: false);
        }

        /// <summary>Draws marks in the advisor's style (cyan unless a mark has its own colour) onto a layer.</summary>
        /// <summary>One mark in the advisor's style, sketched on now (briefings draw each as the narration reaches it).</summary>
        public static void DrawAdvisorMark(MarkRenderer r, Mark m, int id)
        {
            if (m.Id == 0) m.Id = id;
            m.By = "advisor";
            r.BeginReveal();
            var line = ParseColor(m.Color, ToneColor(m.Tone));
            Draw(r, m, new Color(line.r, line.g, line.b, 0.2f), line, showLabel: true);
        }

        public static void DrawAdvisorStyle(MarkRenderer r, IEnumerable<Mark> marks)
        {
            r.BeginReveal(); // sketched on, one mark after another
            int i = 1;
            foreach (var m in marks)
            {
                if (m.Id == 0) m.Id = i;
                i++;
                m.By = "advisor";
                var line = ParseColor(m.Color, ToneColor(m.Tone));
                Draw(r, m, new Color(line.r, line.g, line.b, 0.2f), line, showLabel: true);
            }
        }

        static void Draw(MarkRenderer r, Mark m, Color fill, Color line, bool showLabel, System.Action onClick = null)
        {
            string tag = m.By == "advisor" ? $"A{m.Id}" : $"#{m.Id}";
            string label = showLabel ? (string.IsNullOrEmpty(m.Label) ? tag : $"{tag} {m.Label}") : null;
            // The advisor's marks look hand-drawn (unless the player chose clean marks); the player's stay geometric.
            if (m.By == "advisor" && Prefs.SketchMarks) { DrawSketch(r, m, line, label, onClick); return; }
            switch (m.Kind)
            {
                case "point":
                    r.Dot(m.X0, m.Y0, line, 0.7f);
                    if (label != null) r.Label(m.X0 + 0.5f, m.Y0 + 1.6f, label, line, 0f, onClick);
                    break;
                case "path":
                case "arrow":
                    var pts = m.Kind == "arrow" || m.Points.Count < 2 ? new List<(int x, int y)> { (m.X0, m.Y0), (m.X1, m.Y1) } : m.Points;
                    for (int i = 1; i < pts.Count; i++)
                        r.Segment(pts[i - 1].x + 0.5f, pts[i - 1].y + 0.5f, pts[i].x + 0.5f, pts[i].y + 0.5f, line);
                    if (m.Kind == "arrow" || m.Arrow)
                        r.ArrowHead(pts[pts.Count - 2].x + 0.5f, pts[pts.Count - 2].y + 0.5f, pts[pts.Count - 1].x + 0.5f, pts[pts.Count - 1].y + 0.5f, line);
                    else foreach (var p in pts) r.Dot(p.x, p.y, line, 0.35f);
                    if (label != null) r.Label(pts[0].x + 0.5f, pts[0].y + 1.6f, label, line, 0f, onClick);
                    break;
                case "outline":
                    int cell = Grid.XYToCell(m.X0, m.Y0);
                    var obj = Grid.IsValidCell(cell) ? Grid.Objects[cell, (int)ObjectLayer.Building] : null;
                    var bld = obj != null ? obj.GetComponent<Building>() : null;
                    int ox0 = m.X0, oy0 = m.Y0, ox1 = m.X0, oy1 = m.Y0;
                    if (bld != null) { var e = bld.GetExtents(); ox0 = e.x; oy0 = e.y; ox1 = e.x + e.width - 1; oy1 = e.y + e.height - 1; }
                    r.Rect(ox0, oy0, ox1, oy1, new Color(line.r, line.g, line.b, 0.12f));
                    r.Outline(ox0, oy0, ox1, oy1, line);
                    if (label != null) r.Label((ox0 + ox1 + 1) / 2f, oy1 + 1.6f, label, line, 0f, onClick);
                    break;
                case "line":
                    foreach (var (x, y) in m.LineCells()) r.Rect(x, y, x, y, new Color(line.r, line.g, line.b, 0.45f));
                    if (label != null) r.Label(m.X0 + 0.5f, Math.Max(m.Y0, m.Y1) + 1.6f, label, line, 0f, onClick);
                    break;
                default:
                    r.Rect(m.X0, m.Y0, m.X1, m.Y1, fill);
                    r.Outline(m.X0, m.Y0, m.X1, m.Y1, line);
                    if (label != null) r.Label((Math.Min(m.X0, m.X1) + Math.Max(m.X0, m.X1) + 1) / 2f, Math.Max(m.Y0, m.Y1) + 1.6f, label, line, 0f, onClick);
                    break;
            }
        }

        /// <summary>
        /// The hand-drawn version: a point or a building gets circled, an area gets a quickly sketched box over a
        /// light wash, a line (shaft/route) a translucent marker stroke, a path or arrow a bowed pen line.
        /// </summary>
        static void DrawSketch(MarkRenderer r, Mark m, Color line, string label, System.Action onClick)
        {
            var rng = Sketch.Rng(Sketch.Seed(m));
            float tilt = ((float)rng.NextDouble() - 0.5f) * 6f;
            var wash = new Color(line.r, line.g, line.b, 0.13f);
            switch (m.Kind)
            {
                case "point":
                    r.Stroke(Sketch.Circle(new Vector2(m.X0 + 0.5f, m.Y0 + 0.5f), 1.15f, 1.0f, rng), line, 0.2f);
                    if (label != null) r.Label(m.X0 + 0.5f, m.Y0 + 2.1f, label, line, tilt, onClick);
                    break;
                case "path":
                case "arrow":
                    {
                        var pts = (m.Kind == "arrow" || m.Points.Count < 2 ? new List<(int x, int y)> { (m.X0, m.Y0), (m.X1, m.Y1) } : m.Points)
                            .Select(p => new Vector2(p.x + 0.5f, p.y + 0.5f)).ToList();
                        var stroke = Sketch.Path(pts, rng);
                        r.Stroke(stroke, line, 0.22f);
                        if (m.Kind == "arrow" || m.Arrow)
                            foreach (var head in Sketch.ArrowHead(stroke[Math.Max(0, stroke.Count - 4)], stroke[stroke.Count - 1], rng)) r.Stroke(head, line, 0.2f);
                        if (label != null) r.Label(pts[0].x, pts[0].y + 1.6f, label, line, tilt, onClick);
                        break;
                    }
                case "outline":
                    {
                        int cell = Grid.XYToCell(m.X0, m.Y0);
                        var obj = Grid.IsValidCell(cell) ? Grid.Objects[cell, (int)ObjectLayer.Building] : null;
                        var bld = obj != null ? obj.GetComponent<Building>() : null;
                        float l = m.X0, b = m.Y0, w = 1, h = 1;
                        if (bld != null) { var e = bld.GetExtents(); l = e.x; b = e.y; w = e.width; h = e.height; }
                        r.Stroke(Sketch.Circle(new Vector2(l + w / 2f, b + h / 2f), w / 2f + 0.9f, h / 2f + 0.9f, rng), line, 0.22f);
                        if (label != null) r.Label(l + w / 2f, b + h + 1.9f, label, line, tilt, onClick);
                        break;
                    }
                case "line":
                    {
                        var a = new Vector2(m.X0 + 0.5f, m.Y0 + 0.5f);
                        var c = new Vector2(m.X1 + 0.5f, m.Y1 + 0.5f);
                        r.Stroke(Sketch.Line(a, c, rng, bow: false), new Color(line.r, line.g, line.b, 0.4f), 0.9f); // highlighter
                        if (label != null) r.Label(m.X0 + 0.5f, Math.Max(m.Y0, m.Y1) + 1.6f, label, line, tilt, onClick);
                        break;
                    }
                default:
                    {
                        int x0 = Math.Min(m.X0, m.X1), x1 = Math.Max(m.X0, m.X1), y0 = Math.Min(m.Y0, m.Y1), y1 = Math.Max(m.Y0, m.Y1);
                        r.Rect(x0, y0, x1, y1, wash);
                        foreach (var side in Sketch.Box(x0, y0, x1 + 1, y1 + 1, rng)) r.Stroke(side, line, 0.18f);
                        if (label != null) r.Label((x0 + x1 + 1) / 2f, y1 + 1.7f, label, line, tilt, onClick);
                        break;
                    }
            }
        }

        /// <summary>Advisor colours by meaning: red problem, green build/do, yellow check, cyan information.</summary>
        static Color ToneColor(string tone)
        {
            switch (tone)
            {
                case "problem": return new Color(1f, 0.36f, 0.33f, 0.95f);
                case "build": return new Color(0.49f, 0.86f, 0.42f, 0.95f);
                case "check": return new Color(1f, 0.82f, 0.35f, 0.95f);
                default: return AdvisorLine;
            }
        }

        static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            return ColorUtility.TryParseHtmlString(hex, out var c) ? new Color(c.r, c.g, c.b, 0.95f) : fallback;
        }

        public static void DestroyAll()
        {
            playerLayer.Clear(); advisorLayer.Clear(); previewLayer.Clear();
            Player.Clear(); Advisor.Clear();
            advisorFileTime = default;
        }
    }
}
