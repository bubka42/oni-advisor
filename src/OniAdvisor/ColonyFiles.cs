using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OniAdvisor
{
    /// <summary>
    /// One set of advisor files per colony. When a save of a different colony loads, the previous colony's
    /// files (its chat, briefings, plan, projects, pins, marks, tips, commands, history, screenshots…) move from
    /// live/ to advisor/colonies/&lt;colony&gt;/, and the loaded colony's come back (or it starts clean).
    /// live/colony.json says which colony live/ belongs to; the companion follows it to keep one advisor
    /// conversation per colony. Shared across colonies: player.md, the custom instructions, the settings and
    /// the map memory (already one file per colony in advisor/memory/).
    /// Runs when a colony has loaded, before anything reads these files.
    /// </summary>
    internal static class ColonyFiles
    {
        static readonly string[] Files =
        {
            "colony-plan.md", "quests.json", "quests-state.json", "pins.json", "pins-status.json", "tips.json",
            "tips-seen.json", "conversation.json", "advisor-log.json", "advisor-status.json", "pod.json", "advisor-memory.md",
        };

        static readonly string[] Dirs = { "chat", "briefing", "projects", "plan", "commands", "history", "screenshots" };

        static string ColonyFile => Path.Combine(Paths.LiveDir, "colony.json");
        static string Store(string key) => Path.Combine(Path.GetDirectoryName(Paths.LiveDir), "colonies", key);

        public static string Key(string name)
        {
            var safe = new string((name ?? "colony").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            return safe.Length > 0 ? safe : "colony";
        }

        public static void Switch()
        {
            string name = SaveGame.Instance != null ? SaveGame.Instance.BaseName : null;
            if (string.IsNullOrEmpty(name)) return;
            string key = Key(name);
            string current = null;
            try { if (File.Exists(ColonyFile)) current = (string)JObject.Parse(File.ReadAllText(ColonyFile))["key"]; } catch { }

            if (current != null && current != key)
            {
                try
                {
                    Move(Paths.LiveDir, Store(current));  // put the previous colony's files away
                    Move(Store(key), Paths.LiveDir);      // and bring this one's back, if it was here before
                    Log.Info($"advisor files switched from colony '{current}' to '{key}'");
                }
                catch (Exception e) { Log.Warn("switching colony files failed: " + e.Message); }
            }
            // First run with per-colony files (no colony.json yet): what is in live/ belongs to this colony.
            Paths.WriteAtomic(ColonyFile, new JObject { ["key"] = key, ["name"] = name, ["utc"] = System.DateTime.UtcNow.ToString("o") }.ToString(Formatting.Indented));
        }

        /// <summary>Moves the per-colony files and folders from one place to another, replacing what is there.</summary>
        static void Move(string from, string to)
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (var f in Files)
            {
                var src = Path.Combine(from, f);
                var dst = Path.Combine(to, f);
                if (File.Exists(dst)) File.Delete(dst);
                if (File.Exists(src)) File.Move(src, dst);
            }
            foreach (var d in Dirs)
            {
                var src = Path.Combine(from, d);
                var dst = Path.Combine(to, d);
                if (Directory.Exists(dst)) Directory.Delete(dst, true);
                if (Directory.Exists(src)) Directory.Move(src, dst);
            }
        }
    }
}
