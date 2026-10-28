using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// Briefing mode. The advisor writes live/briefing/briefing.json as steps (say / text / focus / zoom /
    /// marks / project). Ctrl+Shift+B (or the ADVISOR button) opens an ONI-style panel, pauses the game and
    /// plays the steps: the camera moves to each step's focus, its marks and project are shown, and the
    /// companion reads its "say" aloud. When the companion reports a step finished
    /// (live/briefing/playback-done.json), the next step starts. Back / Next / Close buttons; closing unpauses.
    /// The panel also lists recent questions and answers (live/advisor-log.json, written by the companion).
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class Briefing
    {
        sealed class Step
        {
            public string Title, Say, Text, Zoom, Project, ProjectAt;
            public int[] Focus;
            public RectInt? Spotlight;     // the area to keep lit (from "spotlight", else the marks, else the focus)
            public bool NoSpotlight;       // "spotlight": false
            public List<Mark> Marks = new List<Mark>();
        }

        static string id, title;
        static int cycle;
        static readonly List<Step> steps = new List<Step>();
        static System.DateTime fileTime, doneTime;
        static bool unseen;

        static bool open, pausedByUs, paused;
        static int current;
        static float advanceAt = -1f;

        static readonly MarkRenderer stepLayer = new MarkRenderer("briefing") { ChipLabels = true };
        static RectTransform panel, list;
        static TextMeshProUGUI headerText, footerText;

        static string Dir => Paths.Sub("briefing");
        static string BriefingFile => Path.Combine(Dir, "briefing.json");
        static string PlaybackFile => Path.Combine(Dir, "playback.json");
        static string DoneFile => Path.Combine(Dir, "playback-done.json");

        public static bool IsOpen => open;

        // ------------------------------------------------------------------ polling

        /// <summary>About once a second: notice a new briefing, and the companion finishing a spoken step.</summary>
        public static void Poll()
        {
            try
            {
                if (File.Exists(BriefingFile))
                {
                    var t = File.GetLastWriteTimeUtc(BriefingFile);
                    if (t != fileTime && (System.DateTime.UtcNow - t).TotalMilliseconds > 500 && Load(BriefingFile))
                    {
                        bool first = fileTime == default;
                        fileTime = t;
                        if (!first || (System.DateTime.UtcNow - t).TotalMinutes < 2)
                        {
                            unseen = true;
                            AdvisorHud.Show($"Briefing ready ({steps.Count} steps): click ADVISOR below, or {Hotkeys.Describe("briefing")}", 6f);
                        }
                        if (open) { current = 0; Rebuild(); PlayStep(); }
                    }
                }
                if (open && File.Exists(DoneFile))
                {
                    var t = File.GetLastWriteTimeUtc(DoneFile);
                    if (t != doneTime)
                    {
                        doneTime = t;
                        var o = JObject.Parse(File.ReadAllText(DoneFile));
                        if ((string)o["id"] == id && (int?)o["step"] == current && current < steps.Count - 1)
                            if (!paused) advanceAt = Time.unscaledTime + 0.8f; // short breath between steps
                    }
                }
            }
            catch (Exception e) { Log.Warn("briefing poll: " + e.Message); }
        }

        public static bool HasUnseen => unseen;

        /// <summary>Every frame: timed auto-advance.</summary>
        public static void Tick(float now)
        {
            if (open) BriefingStage.Tick();
            if (open && !paused) DrawDue();
            if (open && advanceAt >= 0f && now >= advanceAt)
            {
                advanceAt = -1f;
                Go(current + 1);
            }
        }

        static bool Load(string path)
        {
            var root = JObject.Parse(File.ReadAllText(path));
            var newSteps = new List<Step>();
            foreach (var s in root["steps"] ?? new JArray())
            {
                var step = new Step
                {
                    Title = (string)s["title"],
                    Say = (string)s["say"],
                    Text = (string)s["text"],
                    Zoom = ((string)s["zoom"] ?? "base").ToLowerInvariant(),
                    Project = (string)s["project"],
                    ProjectAt = (string)s["projectAt"],
                };
                if (s["focus"] is JArray f && f.Count >= 2) step.Focus = new[] { (int)f[0], (int)f[1] };
                if (s["spotlight"] is JArray sp && sp.Count >= 4)
                    step.Spotlight = new RectInt(Math.Min((int)sp[0], (int)sp[2]), Math.Min((int)sp[1], (int)sp[3]), Math.Abs((int)sp[2] - (int)sp[0]) + 1, Math.Abs((int)sp[3] - (int)sp[1]) + 1);
                else if (s["spotlight"] is JValue off && off.Type == JTokenType.Boolean && !(bool)off) step.NoSpotlight = true;
                foreach (var m in s["marks"] ?? new JArray()) step.Marks.Add(Mark.FromJson(m));
                newSteps.Add(step);
            }
            if (newSteps.Count == 0) return false;
            steps.Clear();
            steps.AddRange(newSteps);
            id = (string)root["id"] ?? File.GetLastWriteTimeUtc(path).Ticks.ToString();
            title = (string)root["title"] ?? "Briefing";
            cycle = (int?)root["cycle"] ?? 0;
            return true;
        }

        // ------------------------------------------------------------------ open / close / steps

        /// <summary>Ctrl+Shift+B: open the latest briefing (or ask for one if there is none), or close.</summary>
        public static void Toggle()
        {
            if (open) { Close(); return; }
            if (steps.Count == 0)
            {
                AdvisorHud.Show("No briefing yet: asking the advisor for one…", 4f);
                AdvisorRunner.Instance?.RequestBriefing();
                return;
            }
            Open();
        }

        /// <summary>Replays a briefing from the archive (the advisor panel's BRIEFINGS tab).</summary>
        public static void OpenFile(string path)
        {
            try
            {
                if (open) Close();
                if (Load(path)) Open();
                else AdvisorHud.Show("That briefing has no steps", 3f);
            }
            catch (Exception e) { Log.Warn("briefing open: " + e.Message); AdvisorHud.Show("Could not open that briefing", 3f); }
        }

        static void Open()
        {
            paused = false;
            if (AdvisorPanel.IsOpen) AdvisorPanel.Close();
            open = true;
            unseen = false;
            try
            {
                var speed = SpeedControlScreen.Instance;
                if (speed != null && !speed.IsPaused) { speed.Pause(false, false); pausedByUs = true; }
            }
            catch { }
            BuildPanel();
            panel.gameObject.SetActive(true);
            current = 0;
            Rebuild();
            PlayStep();
        }

        public static void Close()
        {
            if (!open) return;
            open = false;
            advanceAt = -1f;
            stepLayer.Clear();
            ProjectStore.ForceShow(null);
            BriefingStage.Hide();
            WritePlayback(-1); // tells the companion to stop speaking
            if (panel != null) panel.gameObject.SetActive(false);
            if (pausedByUs)
            {
                try { SpeedControlScreen.Instance?.Unpause(false); } catch { }
                pausedByUs = false;
            }
        }

        /// <summary>Space: stop the voice and the auto-advance; again: play the step from its start.</summary>
        public static void TogglePause()
        {
            if (!open) return;
            paused = !paused;
            if (paused) { advanceAt = -1f; WritePlayback(-1); BriefingStage.Freeze(); Rebuild(); } // -1 stops the voice
            else { Rebuild(); PlayStep(); }
        }

        public static void Next() { if (open && current < steps.Count - 1) Go(current + 1); }

        public static void Back() { if (open && current > 0) Go(current - 1); }

        static void Go(int step)
        {
            paused = false;
            if (steps.Count == 0) return;
            current = Mathf.Clamp(step, 0, steps.Count - 1);
            advanceAt = -1f;
            Rebuild();
            PlayStep();
        }

        static void PlayStep()
        {
            var s = steps[current];
            stepLayer.Clear();
            stepLayer.Visible = true;
            BriefingStage.Show(SpotlightArea(s), s.Title, s.Say);
            ScheduleMarks(s);
            ProjectStore.ForceShow(null);
            DrawDue(); // whatever is due at the very start
            if (s.Focus != null)
            {
                try
                {
                    int cell = Grid.XYToCell(s.Focus[0], s.Focus[1]);
                    var pos = Grid.CellToPosCCC(cell, Grid.SceneLayer.Background);
                    float size = s.Zoom == "near" ? 9f : s.Zoom == "wide" ? 26f : 15f;
                    CameraController.Instance?.SetTargetPos(pos, size, false);
                }
                catch (Exception e) { Log.Warn("briefing camera: " + e.Message); }
            }
            WritePlayback(current);
        }

        /// <summary>
        /// The tiles a step is about: its "spotlight" rectangle if given, else everything its marks touch, else
        /// the area around its camera focus. Null = no spotlight ("spotlight": false, or nothing to go on).
        /// </summary>
        static RectInt? SpotlightArea(Step s)
        {
            if (s.NoSpotlight) return null;
            if (s.Spotlight.HasValue) return s.Spotlight;
            if (s.Marks.Count > 0)
            {
                int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                void Take(int x, int y) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
                foreach (var m in s.Marks)
                {
                    Take(m.X0, m.Y0); Take(m.X1, m.Y1);
                    foreach (var p in m.Points) Take(p.x, p.y);
                    if (m.Kind == "outline") { Take(m.X0 - 2, m.Y0 - 1); Take(m.X0 + 2, m.Y0 + 3); } // the building's size isn't known here
                }
                return new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            }
            if (s.Focus != null)
            {
                int r = s.Zoom == "near" ? 5 : s.Zoom == "wide" ? 14 : 8;
                return new RectInt(s.Focus[0] - r, s.Focus[1] - r, 2 * r + 1, 2 * r + 1);
            }
            return null;
        }

        // ------------------------------------------------------------------ marks in time with the narration

        static readonly List<(int at, Mark mark, int id)> dueMarks = new List<(int, Mark, int)>();
        static int projectDueAt = -1;
        static string projectDue;

        /// <summary>
        /// When each of the step's marks (and its project's ghost) should appear, in characters of the spoken text:
        /// at its "at" phrase if the advisor gave one, otherwise spread through the narration in the listed order,
        /// the first shortly after the step starts. Without spoken text, everything appears at once.
        /// </summary>
        static void ScheduleMarks(Step s)
        {
            dueMarks.Clear();
            string say = s.Say ?? "";
            int n = s.Marks.Count, untimed = 0;
            for (int i = 0; i < n; i++)
            {
                var m = s.Marks[i];
                int at = Find(say, m.At);
                if (at < 0) at = say.Length == 0 ? 0 : (int)(say.Length * (0.08f + 0.8f * untimed++ / Mathf.Max(1, n)));
                dueMarks.Add((at, m, i + 1));
            }
            dueMarks.Sort((a, b) => a.at.CompareTo(b.at));
            projectDue = s.Project;
            projectDueAt = string.IsNullOrEmpty(s.Project) ? -1 : Math.Max(0, Find(say, s.ProjectAt));
        }

        /// <summary>Where a phrase starts in the spoken text (case-insensitive), or -1.</summary>
        static int Find(string say, string phrase) =>
            string.IsNullOrEmpty(phrase) ? -1 : say.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);

        /// <summary>Every frame while playing: draw what the narration has reached.</summary>
        static void DrawDue()
        {
            if (dueMarks.Count == 0 && projectDueAt < 0) return;
            int spoken = BriefingStage.SpokenChars;
            while (dueMarks.Count > 0 && dueMarks[0].at <= spoken)
            {
                var (_, mark, id) = dueMarks[0];
                dueMarks.RemoveAt(0);
                try { PlanMarks.DrawAdvisorMark(stepLayer, mark, id); }
                catch (Exception e) { Log.Warn("briefing mark: " + e.Message); }
            }
            if (projectDueAt >= 0 && projectDueAt <= spoken)
            {
                projectDueAt = -1;
                ProjectStore.ForceShow(projectDue);
            }
        }

        static void WritePlayback(int step)
        {
            try
            {
                var o = new JObject { ["id"] = id, ["step"] = step, ["say"] = step >= 0 && step < steps.Count ? steps[step].Say : null, ["utc"] = System.DateTime.UtcNow.ToString("o") };
                Paths.WriteAtomic(PlaybackFile, o.ToString(Formatting.None));
            }
            catch (Exception e) { Log.Warn("briefing playback write: " + e.Message); }
        }

        // ------------------------------------------------------------------ panel

        static void BuildPanel()
        {
            if (panel != null || AdvisorHud.Canvas == null) return;
            panel = OniStyle.Rect("briefing-panel", AdvisorHud.Canvas);
            panel.anchorMin = panel.anchorMax = new Vector2(1f, 0.5f);
            panel.pivot = new Vector2(1f, 0.5f);
            panel.anchoredPosition = new Vector2(-16, 20);
            panel.sizeDelta = new Vector2(470, 760);
            OniStyle.Fill(panel, OniStyle.Panel, true); // blocks clicks to the world behind the panel

            // Pink header bar with the title.
            var header = OniStyle.Rect("header", panel);
            header.anchorMin = new Vector2(0, 1); header.anchorMax = new Vector2(1, 1);
            header.pivot = new Vector2(0.5f, 1); header.sizeDelta = new Vector2(0, 40); header.anchoredPosition = Vector2.zero;
            OniStyle.Fill(header, OniStyle.Header, true);
            var ht = OniStyle.Rect("title", header);
            OniStyle.Stretch(ht, 14, 4);
            headerText = OniStyle.Label(ht, "ADVISOR BRIEFING", 20, header: true, align: TextAlignmentOptions.MidlineLeft, wrap: false);

            // Scrollable step list.
            var view = OniStyle.Rect("view", panel);
            view.anchorMin = Vector2.zero; view.anchorMax = Vector2.one;
            view.offsetMin = new Vector2(10, 84); view.offsetMax = new Vector2(-10, -48);
            OniStyle.Fill(view, OniStyle.PanelDark, true);
            view.gameObject.AddComponent<RectMask2D>();
            list = OniStyle.Rect("list", view);
            list.anchorMin = new Vector2(0, 1); list.anchorMax = new Vector2(1, 1); list.pivot = new Vector2(0.5f, 1);
            list.anchoredPosition = Vector2.zero; list.sizeDelta = Vector2.zero;
            var vlg = list.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 8, 8); vlg.spacing = 6;
            vlg.childControlHeight = true; vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false; vlg.childForceExpandWidth = true;
            list.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scroll = view.gameObject.AddComponent<ScrollRect>();
            scroll.content = list; scroll.viewport = view; scroll.horizontal = false; scroll.vertical = true; scroll.scrollSensitivity = 30;

            // Buttons and a hint line.
            var bar = OniStyle.Rect("buttons", panel);
            bar.anchorMin = new Vector2(0, 0); bar.anchorMax = new Vector2(1, 0); bar.pivot = new Vector2(0.5f, 0);
            bar.sizeDelta = new Vector2(-20, 34); bar.anchoredPosition = new Vector2(0, 40);
            var hlg = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8; hlg.childAlignment = TextAnchor.MiddleCenter; hlg.childForceExpandWidth = false; hlg.childControlWidth = true; hlg.childControlHeight = true;
            OniStyle.MakeButton(bar, "◀ BACK", () => Go(current - 1), 86);
            OniStyle.MakeButton(bar, "NEXT ▶", () => Go(current + 1), 86);
            OniStyle.MakeButton(bar, "ASK", () => { var t = AdvisorPanel.ThreadForBriefing(id); Close(); if (t != null) AdvisorPanel.OpenThread(t); else AdvisorPanel.Open(); }, 70);
            OniStyle.MakeButton(bar, "ALL", () => { Close(); AdvisorPanel.Open(chat: false); }, 70);
            OniStyle.MakeButton(bar, "CLOSE", Close, 86);
            var ft = OniStyle.Rect("footer", panel);
            ft.anchorMin = new Vector2(0, 0); ft.anchorMax = new Vector2(1, 0); ft.pivot = new Vector2(0.5f, 0);
            ft.sizeDelta = new Vector2(-24, 30); ft.anchoredPosition = new Vector2(0, 6);
            footerText = OniStyle.Label(ft, "", 13, color: OniStyle.TextMuted, align: TextAlignmentOptions.Midline, wrap: false);
        }

        static void Rebuild()
        {
            if (panel == null || list == null) return;
            headerText.text = $"CYCLE {cycle} · " + (string.IsNullOrEmpty(title) ? "BRIEFING" : title.ToUpperInvariant());
            for (int i = list.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(list.GetChild(i).gameObject);

            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                bool active = i == current;
                var row = OniStyle.Rect("step" + i, list);
                OniStyle.Fill(row, active ? OniStyle.RowActive : OniStyle.Row, false);
                var rv = row.gameObject.AddComponent<VerticalLayoutGroup>();
                rv.padding = new RectOffset(10, 10, 6, 8); rv.spacing = 2;
                rv.childControlHeight = true; rv.childControlWidth = true; rv.childForceExpandHeight = false;
                Add(row, $"{i + 1}. {(s.Title ?? "").ToUpperInvariant()}", 16, header: true, color: active ? Color.white : OniStyle.TextMuted);
                if (!string.IsNullOrEmpty(s.Say)) Add(row, s.Say, 15);
                if (!string.IsNullOrEmpty(s.Text) && active) Add(row, s.Text, 13, color: OniStyle.TextMuted);
            }

            footerText.text = steps.Count > 0 ? (paused ? $"Step {current + 1} of {steps.Count} · PAUSED · Space continues" : $"Step {current + 1} of {steps.Count} · Space pause · Left/Right step · Esc close") : "";
        }

        static void Add(RectTransform parent, string text, float size, bool header = false, Color? color = null)
        {
            var rt = OniStyle.Rect("text", parent);
            OniStyle.Label(rt, text, size, header: header, color: color);
        }

        public static void Destroy()
        {
            if (open) Close();
            stepLayer.Clear();
            BriefingStage.Destroy();
            if (panel != null) UnityEngine.Object.Destroy(panel.gameObject);
            panel = null; list = null;
            steps.Clear();
            fileTime = default; doneTime = default; unseen = false;
        }
    }
}
