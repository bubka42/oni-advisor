using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OniAdvisor
{
    /// <summary>
    /// The advisor's marks grouped by what they belong to, each group shown only while the player has it ticked:
    ///   "msg:N"     the marks drawn with chat message N (the companion keeps a copy per message in plan/sets/);
    ///               the newest message's marks are the live advisor layer (PlanMarks) and show by themselves
    ///   "brief:F"   every mark of the briefing in archive file F
    ///   "quest:ID"  the marks in a project's pitch, plus its blueprint ghost
    /// The ticked groups are remembered per colony (plan/marks-shown.json). Clicking a label says where it is from.
    /// </summary>
    internal static class MarkSets
    {
        static readonly Dictionary<string, MarkRenderer> layers = new Dictionary<string, MarkRenderer>();
        static HashSet<string> shown;
        static bool visible = true;

        static string ShownFile => Path.Combine(Paths.Sub("plan"), "marks-shown.json");
        static string SetsDir => Path.Combine(Paths.Sub("plan"), "sets");
        static string ArchiveDir => Path.Combine(Paths.Sub("briefing"), "archive");

        static HashSet<string> Shown
        {
            get
            {
                if (shown != null) return shown;
                shown = new HashSet<string>();
                try { if (File.Exists(ShownFile)) foreach (var k in JArray.Parse(File.ReadAllText(ShownFile))) shown.Add((string)k); }
                catch (Exception e) { Log.Warn("marks-shown: " + e.Message); }
                foreach (var key in shown.ToList()) Draw(key); // what was ticked last time
                return shown;
            }
        }

        public static bool HasMessageSet(long chat) =>
            chat > 0 && (chat == PlanMarks.LatestSource && PlanMarks.Advisor.Count > 0 || File.Exists(Path.Combine(SetsDir, $"msg-{chat}.json")));

        public static bool IsShown(string key)
        {
            if (TryLatest(key)) return PlanMarks.LatestShown;
            return Shown.Contains(key);
        }

        public static void Toggle(string key) => Set(key, !IsShown(key));

        public static void Set(string key, bool on)
        {
            if (TryLatest(key)) { PlanMarks.SetLatestShown(on); return; }
            if (on) { Shown.Add(key); Draw(key); if (!Overlays.ShowMarks) Overlays.ShowMarksNow(); }
            else { Shown.Remove(key); Erase(key); }
            if (key.StartsWith("quest:")) ProjectStore.SetShown(QuestStore.Find(key.Substring(6))?.Project, on);
            try { Paths.WriteAtomic(ShownFile, new JArray(Shown.ToArray()).ToString(Formatting.None)); } catch { }
        }

        /// <summary>The newest message's marks are PlanMarks' live layer, not a set of their own.</summary>
        static bool TryLatest(string key) => key.StartsWith("msg:") && long.TryParse(key.Substring(4), out long n) && n == PlanMarks.LatestSource;

        public static void SetVisible(bool v)
        {
            visible = v;
            foreach (var l in layers.Values) l.Visible = v;
        }

        // ------------------------------------------------------------------ drawing a group

        static void Draw(string key)
        {
            Erase(key);
            var marks = Load(key, out Action<Mark> onClick);
            if (marks.Count == 0) return;
            var layer = new MarkRenderer("set-" + key) { ChipLabels = true };
            layers[key] = layer;
            int id = 1;
            foreach (var m in marks)
            {
                var mark = m;
                try { PlanMarks.DrawAdvisorMark(layer, mark, id++, () => onClick(mark)); } catch (Exception e) { Log.Warn("mark set draw: " + e.Message); }
            }
            layer.Visible = visible && Overlays.ShowMarks;
        }

        static void Erase(string key)
        {
            if (!layers.TryGetValue(key, out var l)) return;
            l.Clear();
            layers.Remove(key);
        }

        /// <summary>The marks of a group, and what clicking one of its labels shows.</summary>
        static List<Mark> Load(string key, out Action<Mark> onClick)
        {
            var list = new List<Mark>();
            onClick = m => { };
            try
            {
                if (key.StartsWith("msg:"))
                {
                    long chat = long.Parse(key.Substring(4));
                    var f = Path.Combine(SetsDir, $"msg-{chat}.json");
                    if (!File.Exists(f)) return list;
                    var root = JToken.Parse(File.ReadAllText(f));
                    foreach (var t in root is JArray a ? a : root["marks"] as JArray ?? new JArray()) list.Add(Mark.FromJson(t));
                    onClick = m => AdvisorCard.Show(null, Head(m) + (m.Note ?? MessageText(chat) ?? ""), chat, spoken: false);
                }
                else if (key.StartsWith("brief:"))
                {
                    var f = Path.Combine(ArchiveDir, key.Substring(6));
                    if (!File.Exists(f)) return list;
                    var b = JObject.Parse(File.ReadAllText(f));
                    var says = new Dictionary<Mark, string>();
                    int n = 0;
                    foreach (var step in b["steps"] as JArray ?? new JArray())
                    {
                        n++;
                        foreach (var t in step["marks"] as JArray ?? new JArray())
                        {
                            var m = Mark.FromJson(t);
                            list.Add(m);
                            says[m] = $"From the briefing \"{(string)b["title"]}\", step {n}: {(string)step["say"]}";
                        }
                    }
                    onClick = m => AdvisorCard.Show(null, Head(m) + (m.Note ?? (says.TryGetValue(m, out var s) ? s : "")), 0, spoken: false);
                }
                else if (key.StartsWith("quest:"))
                {
                    var q = QuestStore.Find(key.Substring(6));
                    if (q?.Pitch == null) return list;
                    foreach (var step in q.Pitch["steps"] as JArray ?? new JArray())
                        foreach (var t in step["marks"] as JArray ?? new JArray()) list.Add(Mark.FromJson(t));
                    onClick = m => AdvisorCard.Show(null, Head(m) + (m.Note ?? $"From the project \"{q.Title}\": {q.Why}"), 0, spoken: false);
                }
            }
            catch (Exception e) { Log.Warn($"mark set {key}: {e.Message}"); }
            return list;
        }

        static string Head(Mark m) => string.IsNullOrEmpty(m.Label) ? "" : $"**{m.Label}**\n";

        static string MessageText(long chat)
        {
            try
            {
                var f = Path.Combine(Paths.Sub("chat"), "chat.json");
                return File.Exists(f) ? (string)JArray.Parse(File.ReadAllText(f)).FirstOrDefault(e => (long?)e["n"] == chat)?["text"] : null;
            }
            catch { return null; }
        }

        /// <summary>A project's pitch marks may change when the advisor rewrites quests.json: redraw ticked ones.</summary>
        public static void Refresh()
        {
            foreach (var key in Shown.Where(k => k.StartsWith("quest:")).ToList()) Draw(key);
        }

        public static void Clear()
        {
            foreach (var l in layers.Values) l.Clear();
            layers.Clear();
            shown = null;
        }
    }
}
