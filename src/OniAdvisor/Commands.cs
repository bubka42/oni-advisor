using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OniAdvisor
{
    /// <summary>
    /// Optional delegation. The advisor drops command files into commands/inbox/; each command's
    /// category is checked against config.json "delegation":
    ///   off  -> refused (result written, nothing changes)
    ///   ask  -> moved to commands/pending/ for the player to approve (the companion app shows them;
    ///           approving writes commands/decisions/&lt;id&gt;.json with {"approve": true|false})
    ///   auto -> done immediately
    /// Every outcome goes to commands/results/&lt;id&gt;.json and one line in commands/log.jsonl.
    ///
    /// Only actions the player could take through the normal UI, at normal cost:
    ///   research      {"action":"research","tech":"Ranching"}
    ///   skills        {"action":"skill","dupe":"Meep","skill":"Farming2"}
    ///   priorities    {"action":"priority","x":40,"y":101,"priority":7}
    ///   buildings     {"action":"enable","x":40,"y":101,"on":false}         (queues the normal toggle errand)
    ///                 priority and enable also take "targets": [[x,y], ...] instead of x, y
    ///   errands       {"action":"cancel","x0":..,"y0":..,"x1":..,"y1":..,"what":["mop","dig","build","deconstruct"]}
    ///   storage       {"action":"storageFilter","x":40,"y":101,"set":["Coal"]} or "add"/"remove"
    ///   projects      {"action":"placeProject","project":"h2","priority":5} (buildings only; pipes/wires stay manual)
    /// Every command may carry "id" (else the file name is used) and "why" (shown to the player).
    ///
    /// Standing rules (run by the mod itself, no advisor wake-up), each only when its category is "auto":
    ///   rules.researchPlan  - when nothing is being researched, start the next unfinished tech in the list
    ///   rules.skillPlans    - when a duplicant has a free skill point, learn the next skill in their list
    ///
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class Commands
    {
        static string Dir(string sub) => Paths.Sub(Path.Combine("commands", sub));

        static readonly Dictionary<string, string> CategoryOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["research"] = "research", ["skill"] = "skills", ["priority"] = "priorities",
            ["enable"] = "buildings", ["storageFilter"] = "storage", ["placeProject"] = "projects",
            ["cancel"] = "errands",
        };

        public static void Poll()
        {
            foreach (var f in SafeFiles(Dir("inbox")))
                if (IsSettled(f)) Intake(f);
            foreach (var f in SafeFiles(Dir("decisions"))) Decide(f);
        }

        static bool IsSettled(string f)
        {
            try { return (System.DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalMilliseconds >= 500; }
            catch { return false; }
        }

        static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir, "*.json").OrderBy(f => f).ToArray(); }
            catch { return Array.Empty<string>(); }
        }

        static void Intake(string file)
        {
            JObject cmd;
            string id = Path.GetFileNameWithoutExtension(file);
            try
            {
                cmd = JObject.Parse(File.ReadAllText(file));
                id = (string)cmd["id"] ?? id;
                cmd["id"] = id;
            }
            catch (IOException) { return; } // still being written; try next poll
            catch (Exception e)
            {
                SafeDelete(file);
                Finish(id, null, "rejected", "unreadable command: " + e.Message);
                return;
            }
            SafeDelete(file);

            string action = (string)cmd["action"] ?? "";
            if (!CategoryOf.TryGetValue(action, out var category)) { Finish(id, cmd, "rejected", "unknown action " + action); return; }
            switch (Config.Permission(category))
            {
                case "auto": Execute(id, cmd); break;
                case "ask":
                    cmd["category"] = category;
                    cmd["summary"] = Describe(cmd);
                    Paths.WriteAtomic(Path.Combine(Dir("pending"), id + ".json"), cmd.ToString(Formatting.Indented));
                    Log.Info("command waiting for approval: " + cmd["summary"]);
                    AdvisorHud.Show("The advisor asks: " + cmd["summary"], 5f);
                    break;
                default: Finish(id, cmd, "refused", $"delegation for '{category}' is off"); break;
            }
        }

        static void Decide(string file)
        {
            string id = Path.GetFileNameWithoutExtension(file);
            bool approve;
            try { approve = (bool?)JObject.Parse(File.ReadAllText(file))["approve"] ?? false; }
            catch (IOException) { return; }
            catch { approve = false; }
            SafeDelete(file);

            var pendingFile = Path.Combine(Dir("pending"), id + ".json");
            if (!File.Exists(pendingFile)) return;
            JObject cmd;
            try { cmd = JObject.Parse(File.ReadAllText(pendingFile)); } catch { cmd = null; }
            SafeDelete(pendingFile);
            if (cmd == null) { Finish(id, null, "rejected", "pending command unreadable"); return; }
            if (approve) Execute(id, cmd);
            else Finish(id, cmd, "declined", "declined by the player");
        }

        static void Execute(string id, JObject cmd)
        {
            try
            {
                string msg = Run(cmd);
                Finish(id, cmd, "done", msg);
            }
            catch (Exception e) { Finish(id, cmd, "failed", e.Message); }
        }

        /// <summary>
        /// A command the player started themselves (a pin's action button): their click is the approval, so it
        /// runs at once whatever the delegation setting. Logged like any other command. Returns the outcome.
        /// </summary>
        public static string RunByPlayer(JObject cmd, string source)
        {
            string id = "player-" + Paths.Stamp();
            cmd = (JObject)cmd.DeepClone();
            cmd["id"] = id;
            cmd["why"] = (string)cmd["why"] ?? source;
            cmd["by"] = "player";
            try
            {
                string msg = Run(cmd);
                Finish(id, cmd, "done", msg);
                return msg;
            }
            catch (Exception e) { Finish(id, cmd, "failed", e.Message); return "Couldn't do it: " + e.Message; }
        }

        /// <summary>"targets": [[x,y], ...] runs the command on each (e.g. switch off four terrariums); else the one x,y.</summary>
        static string ForEachTarget(JObject c, Func<JObject, string> one)
        {
            if (!(c["targets"] is JArray targets) || targets.Count == 0) return one(c);
            var results = new List<string>();
            foreach (var t in targets)
            {
                var single = (JObject)c.DeepClone();
                single.Remove("targets");
                single["x"] = t[0]; single["y"] = t[1];
                try { results.Add(one(single)); }
                catch (Exception e) { results.Add($"({t[0]},{t[1]}): {e.Message}"); }
            }
            return string.Join("; ", results);
        }

        static readonly ObjectLayer[] BuildLayers =
        {
            ObjectLayer.Building, ObjectLayer.FoundationTile, ObjectLayer.GasConduit, ObjectLayer.LiquidConduit,
            ObjectLayer.Wire, ObjectLayer.LogicWire, ObjectLayer.SolidConduit, ObjectLayer.Backwall,
        };

        /// <summary>
        /// Cancels errands in a rectangle, like the game's cancel tool with its filters: "what" any of mop, dig,
        /// build (unbuilt build orders), deconstruct (deconstruction orders), or all.
        /// </summary>
        static string CancelErrands(JObject c)
        {
            int x0 = (int?)c["x0"] ?? (int?)c["x"] ?? 0, y0 = (int?)c["y0"] ?? (int?)c["y"] ?? 0;
            int x1 = (int?)c["x1"] ?? x0, y1 = (int?)c["y1"] ?? y0;
            var what = new HashSet<string>((c["what"] as JArray)?.Select(w => ((string)w).ToLowerInvariant()) ?? new[] { "all" });
            bool all = what.Contains("all");
            int mop = 0, dig = 0, build = 0, decon = 0;
            var seen = new HashSet<UnityEngine.GameObject>();
            for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
                for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
                {
                    int cell = Grid.XYToCell(x, y);
                    if (!Grid.IsValidCell(cell)) continue;
                    if (all || what.Contains("mop")) { var m = Grid.Objects[cell, (int)ObjectLayer.MopPlacer]; if (m != null && seen.Add(m)) { m.Trigger((int)GameHashes.Cancel, null); mop++; } }
                    if (all || what.Contains("dig")) { var d = Grid.Objects[cell, (int)ObjectLayer.DigPlacer]; if (d != null && seen.Add(d)) { d.Trigger((int)GameHashes.Cancel, null); dig++; } }
                    foreach (var layer in BuildLayers)
                    {
                        var go = Grid.Objects[cell, (int)layer];
                        if (go == null || seen.Contains(go)) continue;
                        if ((all || what.Contains("build")) && go.GetComponent<Constructable>() != null) { seen.Add(go); go.Trigger((int)GameHashes.Cancel, null); build++; }
                        else if (all || what.Contains("deconstruct"))
                        {
                            var dc = go.GetComponent<Deconstructable>();
                            if (dc != null && dc.IsMarkedForDeconstruction()) { seen.Add(go); dc.CancelDeconstruction(); decon++; }
                        }
                    }
                }
            var parts = new List<string>();
            if (mop > 0) parts.Add($"{mop} mop job(s)");
            if (dig > 0) parts.Add($"{dig} dig order(s)");
            if (build > 0) parts.Add($"{build} build order(s)");
            if (decon > 0) parts.Add($"{decon} deconstruction order(s)");
            return parts.Count == 0 ? "nothing to cancel there" : "cancelled " + string.Join(", ", parts);
        }

        static string SetPriority(JObject c) { var o = (JObject)c.DeepClone(); o["action"] = "priority-one"; return Run(o); }
        static string Enable(JObject c) { var o = (JObject)c.DeepClone(); o["action"] = "enable-one"; return Run(o); }

        static string Run(JObject c)
        {
            switch ((string)c["action"])
            {
                case "research":
                {
                    var tech = FindTech((string)c["tech"]) ?? throw new Exception("unknown tech " + c["tech"]);
                    if (Research.Instance.Get(tech).IsComplete()) return tech.Name + " is already researched";
                    Research.Instance.SetActiveResearch(tech, true);
                    return "researching " + tech.Name;
                }
                case "skill":
                {
                    var dupe = FindDupe((string)c["dupe"]) ?? throw new Exception("no duplicant named " + c["dupe"]);
                    return LearnSkill(dupe, (string)c["skill"]);
                }
                case "priority":
                    return ForEachTarget(c, one => SetPriority(one));
                case "enable":
                    return ForEachTarget(c, one => Enable(one));
                case "cancel":
                    return CancelErrands(c);
                case "priority-one":
                {
                    var go = BuildingAt(c) ?? throw new Exception("no building at that tile");
                    var pr = go.GetComponent<Prioritizable>() ?? throw new Exception("building has no priority");
                    int v = UnityEngine.Mathf.Clamp((int?)c["priority"] ?? 5, 1, 9);
                    pr.SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.basic, v));
                    return $"{Name(go)} priority set to {v}";
                }
                case "enable-one":
                {
                    var go = BuildingAt(c) ?? throw new Exception("no building at that tile");
                    var btn = go.GetComponent<BuildingEnabledButton>() ?? throw new Exception("building cannot be disabled");
                    bool on = (bool?)c["on"] ?? true;
                    if (btn.IsEnabled == on) return $"{Name(go)} is already {(on ? "enabled" : "disabled")}";
                    // Same as the UI button: queues the toggle errand for a duplicant.
                    Traverse.Create(btn).Method("OnMenuToggle").GetValue();
                    return $"{Name(go)}: toggle to {(on ? "enabled" : "disabled")} queued";
                }
                case "storageFilter":
                {
                    var go = BuildingAt(c) ?? throw new Exception("no building at that tile");
                    var tf = go.GetComponent<TreeFilterable>() ?? throw new Exception("building has no storage filter");
                    var tags = new HashSet<Tag>(tf.GetTags());
                    if (c["set"] is JArray set) tags = new HashSet<Tag>(set.Select(t => FindTag((string)t)));
                    if (c["add"] is JArray add) foreach (var t in add) tags.Add(FindTag((string)t));
                    if (c["remove"] is JArray rem) foreach (var t in rem) tags.Remove(FindTag((string)t));
                    tf.UpdateFilters(tags);
                    return $"{Name(go)} now accepts: {string.Join(", ", tags.Select(t => TagManager.GetProperName(t, true)))}";
                }
                case "placeProject":
                    return ProjectStore.PlaceProject((string)c["project"], (int?)c["priority"] ?? 5);
                default:
                    throw new Exception("unknown action");
            }
        }

        // ------------------------------------------------------------------ standing rules

        public static void RunRules()
        {
            if (Config.Permission("research") == "auto") ResearchRule();
            if (Config.Permission("skills") == "auto") SkillRule();
        }

        static void ResearchRule()
        {
            var r = Research.Instance;
            if (r == null || r.GetActiveResearch() != null) return;
            foreach (var name in Config.ResearchPlan())
            {
                var tech = FindTech(name);
                if (tech == null || r.Get(tech).IsComplete()) continue;
                r.SetActiveResearch(tech, true);
                Finish("rule-research-" + Paths.Stamp(), new JObject { ["action"] = "research", ["tech"] = tech.Name, ["why"] = "standing rule: researchPlan" },
                    "done", "researching " + tech.Name);
                return;
            }
        }

        static void SkillRule()
        {
            foreach (var plan in Config.SkillPlans())
            {
                var dupe = FindDupe(plan.Key);
                var resume = dupe?.GetComponent<MinionResume>();
                if (resume == null || resume.AvailableSkillpoints <= 0) continue;
                var next = plan.Value.FirstOrDefault(s => !resume.HasMasteredSkill(s));
                if (next == null) continue;
                string msg;
                try { msg = LearnSkill(dupe, next); } catch (Exception e) { msg = "could not learn " + next + ": " + e.Message; }
                Finish("rule-skill-" + Paths.Stamp(), new JObject { ["action"] = "skill", ["dupe"] = plan.Key, ["skill"] = next, ["why"] = "standing rule: skillPlans" },
                    msg.StartsWith("could not") ? "failed" : "done", msg);
            }
        }

        // ------------------------------------------------------------------ helpers

        static string LearnSkill(MinionIdentity dupe, string skillId)
        {
            var resume = dupe.GetComponent<MinionResume>();
            var skill = Db.Get().Skills.TryGet(skillId) ?? Db.Get().Skills.resources.FirstOrDefault(s =>
                string.Equals(STRINGS.UI.StripLinkFormatting(s.Name), skillId, StringComparison.OrdinalIgnoreCase));
            if (skill == null) throw new Exception("unknown skill " + skillId);
            if (resume.HasMasteredSkill(skill.Id)) return $"{dupe.GetProperName()} already has {skill.Name}";
            if (!resume.CanMasterSkill(resume.GetSkillMasteryConditions(skill.Id)))
                throw new Exception($"{dupe.GetProperName()} cannot learn {skill.Name} now (needs a free point and the previous skills)");
            resume.MasterSkill(skill.Id);
            return $"{dupe.GetProperName()} learned {skill.Name}";
        }

        static Tech FindTech(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return Db.Get().Techs.resources.FirstOrDefault(t =>
                string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(STRINGS.UI.StripLinkFormatting(t.Name), name, StringComparison.OrdinalIgnoreCase));
        }

        static MinionIdentity FindDupe(string name) =>
            Components.LiveMinionIdentities.Items.FirstOrDefault(m => m != null && string.Equals(m.GetProperName(), name, StringComparison.OrdinalIgnoreCase));

        static UnityEngine.GameObject BuildingAt(JObject c)
        {
            int cell = Grid.XYToCell((int?)c["x"] ?? -1, (int?)c["y"] ?? -1);
            return Grid.IsValidCell(cell) ? Grid.Objects[cell, (int)ObjectLayer.Building] : null;
        }

        static Tag FindTag(string name)
        {
            foreach (var t in DiscoveredResources.Instance.GetDiscovered())
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(TagManager.GetProperName(t, true), name, StringComparison.OrdinalIgnoreCase))
                    return t;
            throw new Exception("unknown item " + name);
        }

        static string Name(UnityEngine.GameObject go) => STRINGS.UI.StripLinkFormatting(go.GetProperName());

        static string Where(JObject c) => c["targets"] is JArray t && t.Count > 0 ? $"{t.Count} buildings" : $"the building at {c["x"]},{c["y"]}";

        static string Describe(JObject c)
        {
            string why = (string)c["why"];
            string what;
            switch ((string)c["action"])
            {
                case "research": what = $"Research {c["tech"]}"; break;
                case "skill": what = $"{c["dupe"]} learns {c["skill"]}"; break;
                case "priority": what = $"Set priority {c["priority"]} on " + Where(c); break;
                case "enable": what = $"{((bool?)c["on"] ?? true ? "Enable" : "Disable")} " + Where(c); break;
                case "cancel": what = $"Cancel {string.Join("/", (c["what"] as JArray)?.Select(w => (string)w) ?? new[] { "all" })} errands in x{c["x0"]}-{c["x1"]}, y{c["y0"]}-{c["y1"]}"; break;
                case "storageFilter": what = $"Change the storage filter at {c["x"]},{c["y"]}"; break;
                case "placeProject": what = $"Place build orders for project {c["project"]}"; break;
                default: what = (string)c["action"]; break;
            }
            return string.IsNullOrEmpty(why) ? what : $"{what} ({why})";
        }

        static void Finish(string id, JObject cmd, string outcome, string message)
        {
            var res = new JObject
            {
                ["id"] = id,
                ["outcome"] = outcome,
                ["message"] = message,
                ["command"] = cmd,
                ["cycle"] = GameClock.Instance != null ? GameClock.Instance.GetCycle() + 1 : 0,
                ["utc"] = System.DateTime.UtcNow.ToString("o"),
            };
            try
            {
                Paths.WriteAtomic(Path.Combine(Dir("results"), id + ".json"), res.ToString(Formatting.Indented));
                File.AppendAllText(Path.Combine(Paths.Sub("commands"), "log.jsonl"), res.ToString(Formatting.None) + "\n");
            }
            catch (Exception e) { Log.Warn("command result write failed: " + e.Message); }
            Log.Info($"command {id}: {outcome} - {message}");
            if (outcome == "done" || outcome == "failed") AdvisorHud.Show($"Advisor {(outcome == "done" ? "did" : "could not do")}: {message}", 4.5f);
        }

        static void SafeDelete(string f)
        {
            try { File.Delete(f); } catch { }
        }
    }
}
