using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OniAdvisor
{
    /// <summary>A planning mark: an area, a line (shaft/route) or a point, in tile coordinates.</summary>
    internal sealed class Mark
    {
        public int Id;
        public string By = "player"; // player | advisor
        public string Kind = "area";  // area | line | point
        public int X0, Y0, X1, Y1;
        public string Label;
        public string Color; // optional "#RRGGBB" for advisor marks

        public JObject ToJson() => new JObject
        {
            ["id"] = Id, ["by"] = By, ["kind"] = Kind,
            ["x0"] = X0, ["y0"] = Y0, ["x1"] = X1, ["y1"] = Y1,
            ["label"] = Label, ["color"] = Color,
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
            Color = (string)t["color"],
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
        static readonly MarkRenderer advisorLayer = new MarkRenderer("advisor-marks");
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

        /// <summary>Cheap; call every second or so.</summary>
        public static void PollAdvisorFile()
        {
            try
            {
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
                Redraw();
                Log.Info($"advisor marks loaded: {Advisor.Count}");
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

        public static void Redraw()
        {
            playerLayer.Clear();
            foreach (var m in Player) Draw(playerLayer, m, PlayerFill, PlayerLine, showLabel: true);
            advisorLayer.Clear();
            foreach (var m in Advisor)
            {
                var line = ParseColor(m.Color, AdvisorLine);
                Draw(advisorLayer, m, new Color(line.r, line.g, line.b, 0.2f), line, showLabel: true);
            }
        }

        /// <summary>Draws marks in the advisor's style (cyan unless a mark has its own colour) onto a layer.</summary>
        public static void DrawAdvisorStyle(MarkRenderer r, IEnumerable<Mark> marks)
        {
            int i = 1;
            foreach (var m in marks)
            {
                if (m.Id == 0) m.Id = i;
                i++;
                m.By = "advisor";
                var line = ParseColor(m.Color, AdvisorLine);
                Draw(r, m, new Color(line.r, line.g, line.b, 0.2f), line, showLabel: true);
            }
        }

        static void Draw(MarkRenderer r, Mark m, Color fill, Color line, bool showLabel)
        {
            string tag = m.By == "advisor" ? $"A{m.Id}" : $"#{m.Id}";
            string label = showLabel ? (string.IsNullOrEmpty(m.Label) ? tag : $"{tag} {m.Label}") : null;
            switch (m.Kind)
            {
                case "point":
                    r.Dot(m.X0, m.Y0, line, 0.7f);
                    if (label != null) r.Label(m.X0 + 0.5f, m.Y0 + 1.6f, label, line);
                    break;
                case "line":
                    foreach (var (x, y) in m.LineCells()) r.Rect(x, y, x, y, new Color(line.r, line.g, line.b, 0.45f));
                    if (label != null) r.Label(m.X0 + 0.5f, Math.Max(m.Y0, m.Y1) + 1.6f, label, line);
                    break;
                default:
                    r.Rect(m.X0, m.Y0, m.X1, m.Y1, fill);
                    r.Outline(m.X0, m.Y0, m.X1, m.Y1, line);
                    if (label != null) r.Label((Math.Min(m.X0, m.X1) + Math.Max(m.X0, m.X1) + 1) / 2f, Math.Max(m.Y0, m.Y1) + 1.6f, label, line);
                    break;
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
