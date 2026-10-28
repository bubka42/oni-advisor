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
    ///                           completed, with cycles, and which milestones are ticked.
    /// A quest's status is the player's if they have decided, else the advisor's ("proposed" / "scrapped").
    /// Ticking the last milestone completes it.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class QuestStore
    {
        internal sealed class Milestone
        {
            public string Id, Text;
            public bool Done, AdvisorThinksDone;
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
                        : new Milestone { Id = (string)ms["id"] ?? $"m{++m}", Text = (string)ms["text"] ?? "", AdvisorThinksDone = (bool?)ms["looksDone"] ?? false };
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

        public static void ToggleMilestone(Quest q, Milestone m)
        {
            var s = Entry(q);
            var ms = s["milestones"] as JObject ?? new JObject();
            m.Done = !m.Done;
            ms[m.Id] = m.Done;
            s["milestones"] = ms;
            if (q.Status == "proposed") { s["status"] = "accepted"; q.Status = "accepted"; } // working on it = taking it on
            if (m.Done && q.Milestones.All(x => x.Done))
            {
                s["status"] = "completed";
                s["completedCycle"] = Cycle();
                q.Status = "completed";
                AdvisorHud.Show($"PROJECT COMPLETE: {q.Title}", 6f);
            }
            else if (!m.Done && q.Status == "completed") { s["status"] = "accepted"; q.Status = "accepted"; }
            Save();
            Version++;
        }

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
