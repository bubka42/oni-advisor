using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OniAdvisor
{
    /// <summary>
    /// advisor/config.json, created with defaults on first run and re-read whenever it changes.
    ///
    ///   mapMode     "memory" | "live"  (map mode is fixed when a colony loads; reload to change it)
    ///   help        per-area help level for the advisor: off | advise | plan | draft | place | maintain
    ///               (read by the advisor; the mod itself only uses it for documentation)
    ///   delegation  per-category permission for commands the advisor sends: off | ask | auto
    ///   rules       standing rules the mod runs itself (each needs its delegation category on "auto")
    /// </summary>
    internal static class Config
    {
        public static string MapMode { get; private set; } = "memory";
        public static bool MemoryMode => MapMode == "memory";

        public static JObject Raw { get; private set; } = new JObject();

        static string path;
        static System.DateTime lastWrite;
        static bool mapModeFixed;

        public static readonly string[] DelegationCategories = { "research", "skills", "priorities", "buildings", "storage", "projects" };

        static JObject Defaults() => new JObject
        {
            ["mapMode"] = "memory",
            ["help"] = new JObject
            {
                ["oxygen"] = "advise", ["food"] = "advise", ["power"] = "advise", ["water"] = "advise",
                ["temperature"] = "advise", ["research"] = "advise", ["skills"] = "advise", ["layout"] = "advise",
                ["exploration"] = "advise", ["logistics"] = "advise",
            },
            ["delegation"] = new JObject
            {
                ["research"] = "off", ["skills"] = "off", ["priorities"] = "off",
                ["buildings"] = "off", ["storage"] = "off", ["projects"] = "off",
            },
            ["rules"] = new JObject
            {
                ["researchPlan"] = new JArray(),
                ["skillPlans"] = new JObject(),
            },
            ["_help"] = new JObject
            {
                ["mapMode"] = "\"memory\": the advisor knows what you last saw, per overlay. \"live\": current state of revealed tiles. Reload the colony after changing.",
                ["help"] = "How much the advisor should do per area: off | advise | plan | draft | place | maintain.",
                ["delegation"] = "Commands from the advisor: off (refused) | ask (you approve each) | auto (done immediately). Everything is logged in advisor/live/commands/log.jsonl.",
                ["rules"] = "researchPlan: tech names to research in order (needs delegation.research = auto). skillPlans: {\"Meep\": [\"Farming2\", ...]} skill IDs to learn in order (needs delegation.skills = auto).",
            },
        };

        public static void Load()
        {
            path = Path.Combine(Path.GetDirectoryName(Paths.LiveDir), "config.json");
            try
            {
                if (!File.Exists(path)) Paths.WriteAtomic(path, Defaults().ToString(Formatting.Indented));
                ReadFile();
                // Older config files: add any missing sections (keeps the player's existing values).
                bool changed = false;
                foreach (var p in Defaults().Properties())
                    if (Raw[p.Name] == null) { Raw[p.Name] = p.Value; changed = true; }
                if (changed)
                {
                    Paths.WriteAtomic(path, Raw.ToString(Formatting.Indented));
                    lastWrite = File.GetLastWriteTimeUtc(path);
                }
            }
            catch (Exception e)
            {
                Log.Warn("config read failed, using defaults: " + e.Message);
                Raw = Defaults();
            }
            if (!mapModeFixed)
            {
                var mode = ((string)Raw["mapMode"] ?? "memory").Trim().ToLowerInvariant();
                MapMode = mode == "live" ? "live" : "memory";
                mapModeFixed = true;
            }
            Log.Info("map mode: " + MapMode);
        }

        /// <summary>Called when a colony unloads so the next one re-reads mapMode.</summary>
        public static void Unfix() => mapModeFixed = false;

        /// <summary>Cheap; call every couple of seconds.</summary>
        public static void ReloadIfChanged()
        {
            if (path == null) return;
            try
            {
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != lastWrite) ReadFile();
            }
            catch (Exception e) { Log.Warn("config reload failed: " + e.Message); }
        }

        static void ReadFile()
        {
            var t = File.GetLastWriteTimeUtc(path);
            Raw = JObject.Parse(File.ReadAllText(path)); // on failure keep the old settings and retry next time
            lastWrite = t;
        }

        /// <summary>off | ask | auto for a delegation category (default off).</summary>
        public static string Permission(string category)
        {
            var v = ((string)Raw["delegation"]?[category] ?? "off").Trim().ToLowerInvariant();
            return v == "auto" || v == "ask" ? v : "off";
        }

        public static List<string> ResearchPlan()
        {
            var list = new List<string>();
            if (Raw["rules"]?["researchPlan"] is JArray arr)
                foreach (var t in arr) if (t.Type == JTokenType.String) list.Add((string)t);
            return list;
        }

        public static Dictionary<string, List<string>> SkillPlans()
        {
            var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (Raw["rules"]?["skillPlans"] is JObject o)
                foreach (var p in o.Properties())
                    if (p.Value is JArray arr)
                    {
                        var l = new List<string>();
                        foreach (var t in arr) if (t.Type == JTokenType.String) l.Add((string)t);
                        d[p.Name] = l;
                    }
            return d;
        }
    }
}
