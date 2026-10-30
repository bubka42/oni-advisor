using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OniAdvisor
{
    /// <summary>
    /// Projects as quests. Two files, so the advisor and the player never overwrite each other:
    ///   live/quests.json        the advisor's: proposals and their content (title, kind, horizon, why,
    ///                           milestones, linked blueprint, advice), and "scrapped" for outdated proposals;
    ///   live/quests-state.json  the player's (written only here): accepted / declined / stalled / abandoned /
    ///                           completed, with cycles, and which milestones are reached (with the cycle).
    /// A quest's status is the player's if they have decided, else the advisor's ("proposed" / "scrapped").
    /// Milestones tick themselves: each has a "check" the game can measure (MilestoneChecks: buildings built,
    /// blueprint pieces, rooms, research, digging, storage), with partial progress shown; one without a check is
    /// ticked when the advisor marks it "looksDone". Once reached, a milestone stays reached. Reaching the last one
    /// completes the project. The player doesn't tick anything.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class QuestStore
    {
        internal sealed class Milestone
        {
            public string Id, Text;
            public bool Done, AdvisorThinksDone;
            public JObject Check;          // what the game measures (MilestoneChecks); null = the advisor judges it
            public float Fraction;         // partial progress 0..1, for display
            public string ProgressText;    // "2/3 built", "40% dug" …
        }

        internal sealed class Quest
        {
            public string Id, Title, Kind, Horizon, Why, Project, Advice;
            public JObject Pitch;   // a briefing ({"steps": [...]}) the briefing player narrates: the project's pitch
            public string Status;   // proposed | accepted | stalled | abandoned | declined | completed | scrapped
            public List<Milestone> Milestones = new List<Milestone>();
            public int DoneCount => Milestones.Count(m => m.Done);
        }

        static readonly List<Quest> quests = new List<Quest>();
        static JObject state = new JObject();
        static System.DateTime questsTime, stateTime;
        public static int Version { get; private set; } // bumps on every change, for the list to redraw

        static string QuestsFile => Path.Combine(Paths.LiveDir, "quests.json");
        static string StateFile => Path.Combine(Paths.LiveDir, "quests-state.json");

        public static IReadOnlyList<Quest> All => quests;

        /// <summary>About once a second.</summary>
        public static void Poll()
        {
            try
            {
                bool changed = false;
                if (File.Exists(StateFile))
                {
                    var t = File.GetLastWriteTimeUtc(StateFile);
                    if (t != stateTime) { state = JObject.Parse(File.ReadAllText(StateFile)); stateTime = t; changed = true; }
                }
                if (File.Exists(QuestsFile))
                {
                    var t = File.GetLastWriteTimeUtc(QuestsFile);
                    if (t != questsTime && (System.DateTime.UtcNow - t).TotalMilliseconds > 400)
                    {
                        int before = quests.Count(q => q.Status == "proposed");
                        Load(JToken.Parse(File.ReadAllText(QuestsFile)));
                        questsTime = t;
                        changed = true;
                        int after = quests.Count(q => q.Status == "proposed");
                        if (after > before) AdvisorHud.Show($"The advisor proposes {after - before} new project(s): open one under PROJECTS on the left and press PITCH to hear it", 5f);
                    }
                }
                if (changed) { Apply(); Version++; }
            }
            catch (Exception e) { Log.Warn("quests poll: " + e.Message); }
        }

        static void Load(JToken root)
        {
            quests.Clear();
            var arr = root is JArray a ? a : root["quests"] as JArray ?? new JArray();
            int n = 0;
            foreach (var q in arr)
            {
                var quest = new Quest
                {
                    Id = (string)q["id"] ?? $"q{++n}",
                    Title = (string)q["title"] ?? "Project",
                    Kind = ((string)q["kind"] ?? "infrastructure").ToLowerInvariant(),
                    Horizon = ((string)q["horizon"] ?? "short").ToLowerInvariant(),
                    Why = (string)q["why"],
                    Project = (string)q["project"],
                    Advice = (string)q["advice"],
                    Status = ((string)q["status"] ?? "proposed").ToLowerInvariant(),
                    Pitch = q["pitch"] is JObject po ? po : q["pitch"] is JArray pa ? new JObject { ["steps"] = pa } : null,
                };
                int m = 0;
                foreach (var ms in q["milestones"] ?? new JArray())
                {
                    var milestone = ms.Type == JTokenType.String
                        ? new Milestone { Id = $"m{++m}", Text = (string)ms }
                        : new Milestone { Id = (string)ms["id"] ?? $"m{++m}", Text = (string)ms["text"] ?? "", AdvisorThinksDone = (bool?)ms["looksDone"] ?? false, Check = ms["check"] as JObject };
                    quest.Milestones.Add(milestone);
                }
                quests.Add(quest);
            }
        }

        /// <summary>The player's decisions over the advisor's statuses.</summary>
        static void Apply()
        {
            foreach (var q in quests)
            {
                var s = state[q.Id] as JObject;
                if (s?["status"] != null) q.Status = (string)s["status"];
                foreach (var m in q.Milestones) m.Done = (bool?)s?["milestones"]?[m.Id] ?? false;
            }
        }

        // ------------------------------------------------------------------ the player's actions

        public static void SetStatus(Quest q, string status)
        {
            var s = Entry(q);
            s["status"] = status;
            s[status + "Cycle"] = Cycle();
            Save();
            q.Status = status;
            string verb = status == "accepted" ? "accepted" : status == "stalled" ? "put on hold" : status == "abandoned" ? "abandoned" : status == "declined" ? "declined" : status;
            AdvisorHud.Show($"Project {verb}: {q.Title}", 3f);
            Version++;
        }

        // ------------------------------------------------------------------ milestones measure themselves

        static float nextMeasure;

        /// <summary>
        /// Every few seconds: measure the open milestones of proposed, accepted and stalled projects. A milestone that
        /// reaches its target (or that the advisor marked looksDone, when it has no check) is reached for good; the
        /// last one completes the project. Progress text changes redraw the list.
        /// </summary>
        public static void Measure(float now)
        {
            if (now < nextMeasure || quests.Count == 0) return;
            nextMeasure = now + 3f;
            bool redraw = false, save = false;
            foreach (var q in quests)
            {
                if (q.Status != "proposed" && q.Status != "accepted" && q.Status != "stalled") continue;
                foreach (var m in q.Milestones)
                {
                    if (m.Done) continue;
                    var measured = MilestoneChecks.Measure(m.Check);
                    string text = measured?.text;
                    float frac = measured == null ? 0f : measured.Value.need <= 0 ? 1f : Math.Min(1f, measured.Value.have / measured.Value.need);
                    if (text != m.ProgressText) { m.ProgressText = text; redraw = true; }
                    m.Fraction = frac;
                    bool reached = measured != null ? measured.Value.have >= measured.Value.need : m.AdvisorThinksDone;
                    if (!reached) continue;
                    m.Done = true;
                    var s = Entry(q);
                    var ms = s["milestones"] as JObject ?? new JObject();
                    ms[m.Id] = true;
                    s["milestones"] = ms;
                    s[m.Id + "Cycle"] = Cycle();
                    save = redraw = true;
                    AdvisorHud.Show($"Milestone reached: {m.Text}", 4f);
                }
                if (q.Milestones.Count > 0 && q.Milestones.All(x => x.Done) && q.Status == "accepted")
                {
                    var s = Entry(q);
                    s["status"] = "completed";
                    s["completedCycle"] = Cycle();
                    q.Status = "completed";
                    save = redraw = true;
                    AdvisorHud.Show($"PROJECT COMPLETE: {q.Title}", 6f);
                }
            }
            if (save) Save();
            if (redraw) Version++;
        }

        /// <summary>A project's progress over its milestones, partial ones included (0..1).</summary>
        public static float Progress(Quest q) => q.Milestones.Count == 0 ? 0f : q.Milestones.Sum(m => m.Done ? 1f : m.Fraction) / q.Milestones.Count;

        static JObject Entry(Quest q)
        {
            if (!(state[q.Id] is JObject s)) { s = new JObject { ["title"] = q.Title }; state[q.Id] = s; }
            return s;
        }

        static int Cycle() => GameClock.Instance != null ? GameClock.Instance.GetCycle() + 1 : 0;

        static void Save()
        {
            try
            {
                state["_utc"] = System.DateTime.UtcNow.ToString("o");
                Paths.WriteAtomic(StateFile, state.ToString(Formatting.Indented));
                stateTime = File.GetLastWriteTimeUtc(StateFile);
            }
            catch (Exception e) { Log.Warn("quests save: " + e.Message); }
        }

        // ------------------------------------------------------------------ for threads and the panel

        public static Quest Find(string id) => quests.FirstOrDefault(q => q.Id == id);

        /// <summary>A thread id ("quest:ID" or "quest:ID:MILESTONE") as a title.</summary>
        public static string ThreadTitle(string thread)
        {
            var parts = thread.Split(':');
            var q = parts.Length > 1 ? Find(parts[1]) : null;
            if (q == null) return "PROJECT";
            if (parts.Length > 2)
            {
                var m = q.Milestones.FirstOrDefault(x => x.Id == parts[2]);
                return $"{q.Title} · {m?.Text ?? "milestone"}";
            }
            return q.Title;
        }

        public static void Clear()
        {
            quests.Clear();
            state = new JObject();
            questsTime = stateTime = default;
            Version++;
        }
    }
}
