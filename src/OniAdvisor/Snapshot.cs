using System;
using System.Collections.Generic;
using System.Linq;
using Klei.AI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OniAdvisor
{
    /// <summary>
    /// Builds a compact JSON picture of the colony. Each section is collected independently,
    /// so a game update that breaks one section only adds an entry to meta.errors.
    /// Everything here is already known to the player (own duplicants, own buildings,
    /// enclosed rooms); nothing reads unrevealed tiles.
    /// </summary>
    internal static class Snapshot
    {
        // Buildings whose every status line is worth reporting, not just the bad ones.
        static readonly string[] DetailedBuildingKeywords =
        {
            "pump", "generator", "electrolyzer", "diffuser", "sieve", "skimmer", "battery",
            "refrigerator", "deodorizer", "filter", "vent", "printing pod", "super computer", "research station",
        };

        public static JObject Build(string trigger)
        {
            var errors = new JArray();
            var root = new JObject
            {
                ["meta"] = Section(errors, "meta", () => Meta(trigger)),
                ["colony"] = Section(errors, "colony", Colony),
                ["dupes"] = Section(errors, "dupes", Dupes),
                ["rooms"] = Section(errors, "rooms", Rooms),
                ["power"] = Section(errors, "power", Power),
                ["buildings"] = Section(errors, "buildings", Buildings),
                ["construction"] = Section(errors, "construction", Construction),
                ["projects"] = Section(errors, "projects", () => ProjectStore.StatusSummary()),
                ["research"] = Section(errors, "research", ResearchState),
                ["resources"] = Section(errors, "resources", Resources),
                ["pod"] = Section(errors, "pod", Pod),
            };
            ((JObject)root["meta"])?.Add("errors", errors);
            return root;
        }

        static JToken Section(JArray errors, string name, Func<JToken> f)
        {
            try { return f(); }
            catch (Exception e)
            {
                errors.Add($"{name}: {e.GetType().Name}: {e.Message}");
                return JValue.CreateNull();
            }
        }

        // ---------------------------------------------------------------- meta / colony

        static JToken Meta(string trigger)
        {
            var clock = GameClock.Instance;
            return new JObject
            {
                ["mod"] = "OniAdvisor 0.3.0",
                ["trigger"] = trigger,
                ["mapMode"] = Config.MapMode,
                ["colony"] = SaveGame.Instance != null ? SaveGame.Instance.BaseName : null,
                ["cycle"] = clock.GetCycle() + 1,
                ["cycleProgress"] = R(clock.GetTimeSinceStartOfCycle() / 600f, 2),
                ["isNight"] = clock.IsNighttime(),
                ["gameTimeSeconds"] = R(clock.GetTime(), 0),
                ["exportedAtUtc"] = System.DateTime.UtcNow.ToString("o"),
            };
        }

        static JToken Colony()
        {
            float kcal = 0f;
            foreach (var edible in Components.Edibles.Items)
                if (edible != null) kcal += edible.Calories / 1000f;
            return new JObject
            {
                ["dupeCount"] = Components.LiveMinionIdentities.Items.Count,
                ["foodKcal"] = R(kcal, 0),
            };
        }

        // ---------------------------------------------------------------- duplicants

        static JToken Dupes()
        {
            var arr = new JArray();
            var amounts = Db.Get().Amounts;
            var attrs = Db.Get().Attributes;
            foreach (var mi in Components.LiveMinionIdentities.Items)
            {
                if (mi == null) continue;
                var go = mi.gameObject;
                int cell = Grid.PosToCell(go);
                var d = new JObject
                {
                    ["name"] = mi.GetProperName(),
                    ["pos"] = Pos(cell),
                    ["stress"] = R(amounts.Stress.Lookup(go)?.value),
                    ["kcal"] = R(amounts.Calories.Lookup(go)?.value / 1000f, 0),
                    ["breath"] = R(amounts.Breath.Lookup(go)?.value),
                    ["hp"] = R(amounts.HitPoints.Lookup(go)?.value),
                    ["bladder"] = R(amounts.Bladder.Lookup(go)?.value),
                    ["stamina"] = R(amounts.Stamina.Lookup(go)?.value),
                    ["morale"] = R(attrs.QualityOfLife.Lookup(go)?.GetTotalValue()),
                    ["moraleNeed"] = R(attrs.QualityOfLifeExpectation.Lookup(go)?.GetTotalValue()),
                };
                // The exact gas at a duplicant's head is only shown in live mode; in memory mode
                // the player learns about bad air through the duplicant's own status lines.
                if (!Config.MemoryMode) d["air"] = AirAt(Grid.CellAbove(cell));

                var chore = go.GetComponent<ChoreDriver>()?.GetCurrentChore();
                if (chore?.choreType != null) d["doing"] = chore.choreType.Name;

                var resume = go.GetComponent<MinionResume>();
                if (resume != null)
                {
                    d["skillPointsFree"] = resume.AvailableSkillpoints;
                    d["skills"] = new JArray(resume.MasteryBySkillID.Where(kv => kv.Value).Select(kv => SkillName(kv.Key)));
                    d["interests"] = new JArray(resume.AptitudeBySkillGroup.Where(kv => kv.Value > 0f)
                        .Select(kv => SkillGroupName(kv.Key)));
                }

                var pe = go.GetComponent<PrimaryElement>();
                if (pe != null && pe.DiseaseIdx != byte.MaxValue && pe.DiseaseCount > 0)
                    d["germs"] = $"{pe.DiseaseCount} {DiseaseName(pe.DiseaseIdx)}";

                var st = Statuses(go, problemsOnly: true);
                if (st.Count > 0) d["problems"] = st;
                arr.Add(d);
            }
            return arr;
        }

        static string SkillName(string id)
        {
            try { return StripName(Db.Get().Skills.Get(id).Name); } catch { return id; }
        }

        static string SkillGroupName(HashedString id)
        {
            try { return StripName(Db.Get().SkillGroups.Get(id).Name); } catch { return id.ToString(); }
        }

        // ---------------------------------------------------------------- rooms

        static JToken Rooms()
        {
            var arr = new JArray();
            foreach (var room in Game.Instance.roomProber.rooms)
            {
                var cav = room?.cavity;
                if (cav == null || cav.cells == null) continue;
                // The game treats every enclosed pocket of rock as a "room". Keep only spaces with the player's
                // buildings in them, and only when fully explored (pockets under the fog must not leak out).
                if (room.buildings == null || room.buildings.Count == 0) continue;
                if (!cav.cells.All(c => Grid.IsVisible(c))) continue;

                // Gas, temperature and germs come from MapMemory, so in memory mode they reflect
                // what the player last saw in the matching overlay (with its age), not live values.
                var mem = MapMemory.Current;
                var gasMass = new Dictionary<string, float>();
                var germs = new Dictionary<string, long>();
                float tempSum = 0f;
                int gasCells = 0, liquidCells = 0, tempCells = 0, gasKnown = 0, tempKnown = 0, germKnown = 0;
                float gasOldest = 0f, tempOldest = 0f, germOldest = 0f;
                foreach (int cell in cav.cells)
                {
                    if (mem != null && mem.TryGas(cell, out var el, out float mass, out float gs) && el != null)
                    {
                        gasKnown++;
                        gasOldest = Math.Max(gasOldest, MapMemory.AgeCycles(gs));
                        if (el.IsGas || el.IsVacuum)
                        {
                            gasCells++;
                            if (el.IsGas) Add(gasMass, StripName(el.name), mass);
                        }
                        else if (el.IsLiquid) liquidCells++;
                    }
                    if (mem != null && mem.TryTemp(cell, out float k, out float ts))
                    {
                        tempKnown++; tempCells++; tempSum += k;
                        tempOldest = Math.Max(tempOldest, MapMemory.AgeCycles(ts));
                    }
                    if (mem != null && mem.TryGerms(cell, out byte di, out int dc, out float ds))
                    {
                        germKnown++;
                        germOldest = Math.Max(germOldest, MapMemory.AgeCycles(ds));
                        if (di != byte.MaxValue && dc > 0)
                        {
                            string dn = DiseaseName(di);
                            germs[dn] = (germs.TryGetValue(dn, out var g) ? g : 0) + dc;
                        }
                    }
                }

                var gases = new JObject();
                foreach (var kv in gasMass.OrderByDescending(kv => kv.Value))
                    gases[kv.Key] = R(kv.Value * 1000f / Math.Max(1, gasCells), 0); // average grams per tile

                int n = Math.Max(1, cav.cells.Count);
                var r = new JObject
                {
                    ["type"] = StripName(room.roomType?.Name),
                    ["cells"] = cav.NumCells,
                    ["bounds"] = $"x{cav.minX}-{cav.maxX} y{cav.minY}-{cav.maxY}",
                    ["avgTempC"] = tempCells > 0 ? R(tempSum / tempCells - 273.15f) : null,
                    ["gasGramsPerTile"] = gases,
                };
                if (mem != null && !mem.Live)
                {
                    // Share of the room seen in each overlay, and how old the oldest observation is.
                    r["known"] = $"gas {100 * gasKnown / n}% (oldest {R(gasOldest)} cycles), " +
                                 $"temp {100 * tempKnown / n}% (oldest {R(tempOldest)}), germs {100 * germKnown / n}% (oldest {R(germOldest)})";
                }
                if (liquidCells > 0) r["liquidCells"] = liquidCells;
                if (germs.Count > 0) r["germs"] = JObject.FromObject(germs);
                r["buildings"] = CountNames(room.buildings?.Select(b => b != null ? b.GetProperName() : null));
                arr.Add(r);
            }
            return arr;
        }

        // ---------------------------------------------------------------- power

        static JToken Power()
        {
            var cm = Game.Instance.circuitManager;
            var ids = new HashSet<ushort>();
            foreach (var b in Components.BuildingCompletes.Items)
            {
                if (b == null) continue;
                foreach (var cc in b.GetComponents<ICircuitConnected>())
                {
                    ushort id = cm.GetCircuitID(cc);
                    if (id != CircuitManager.INVALID_ID) ids.Add(id);
                }
            }

            var arr = new JArray();
            foreach (ushort id in ids.OrderBy(i => i))
            {
                var batteries = cm.GetBatteriesOnCircuit(id) ?? new List<Battery>();
                var c = new JObject
                {
                    ["id"] = id,
                    ["wattsUsed"] = R(cm.GetWattsUsedByCircuit(id), 0),
                    ["wattsNeededWhenAllActive"] = R(cm.GetWattsNeededWhenActive(id), 0),
                    ["wattsGenerated"] = R(cm.GetWattsGeneratedByCircuit(id), 0),
                    ["wattsPotential"] = R(cm.GetPotentialWattsGeneratedByCircuit(id), 0),
                    ["maxSafeWatts"] = R(cm.GetMaxSafeWattageForCircuit(id), 0),
                    ["joulesStored"] = R(cm.GetJoulesAvailableOnCircuit(id), 0),
                    ["generators"] = CountNames(cm.GetGeneratorsOnCircuit(id)?.Select(g => g != null ? g.GetProperName() : null)),
                    ["consumers"] = CountNames(cm.GetConsumersOnCircuit(id)?.Select(e => StripName(e?.Name))),
                    ["batteries"] = new JArray(batteries.Where(bt => bt != null)
                        .Select(bt => $"{StripName(bt.GetProperName())} {R(bt.PercentFull * 100f, 0)}%")),
                };
                arr.Add(c);
            }
            return arr;
        }

        // ---------------------------------------------------------------- buildings

        static JToken Buildings()
        {
            var counts = new Dictionary<string, int>();
            var notable = new JArray();
            foreach (var b in Components.BuildingCompletes.Items)
            {
                if (b == null) continue;
                string name = StripName(b.GetProperName());
                counts[name] = (counts.TryGetValue(name, out var n) ? n : 0) + 1;

                string lower = name.ToLowerInvariant();
                bool detailed = DetailedBuildingKeywords.Any(k => lower.Contains(k));
                var st = Statuses(b.gameObject, problemsOnly: !detailed);
                if (st.Count == 0) continue;
                notable.Add(new JObject
                {
                    ["name"] = name,
                    ["pos"] = Pos(Grid.PosToCell(b)),
                    ["status"] = st,
                });
            }
            var sortedCounts = new JObject();
            foreach (var kv in counts.OrderBy(kv => kv.Key)) sortedCounts[kv.Key] = kv.Value;
            return new JObject { ["counts"] = sortedCounts, ["notable"] = notable };
        }

        // ---------------------------------------------------------------- construction backlog

        /// <summary>
        /// Every placed-but-unfinished building with why it is waiting, plus what idle duplicants exist.
        /// Building, not research, is usually the bottleneck, so this is what briefings should look at.
        /// </summary>
        static JToken Construction()
        {
            var sites = UnityEngine.Object.FindObjectsByType<Constructable>(FindObjectsSortMode.None);
            var groups = new Dictionary<string, (int count, List<string> where)>();
            var stuck = new JArray();
            foreach (var c in sites)
            {
                if (c == null) continue;
                string name = StripName(c.GetProperName());
                var problems = Statuses(c.gameObject, problemsOnly: true);
                string pos = (string)Pos(Grid.PosToCell(c));
                string key = problems.Count > 0 ? $"{name}: {string.Join("; ", problems.Select(p => (string)p))}" : name + ": queued";
                if (!groups.TryGetValue(key, out var g)) g = (0, new List<string>());
                g.count++;
                if (g.where.Count < 8) g.where.Add(pos);
                groups[key] = g;
            }
            foreach (var kv in groups.OrderByDescending(kv => kv.Value.count))
                stuck.Add($"{kv.Value.count}x {kv.Key} @ {string.Join(" ", kv.Value.where)}");

            var idle = new JArray();
            foreach (var mi in Components.LiveMinionIdentities.Items)
            {
                if (mi == null) continue;
                var chore = mi.GetComponent<ChoreDriver>()?.GetCurrentChore();
                string doing = chore?.choreType?.Name ?? "";
                if (chore == null || doing.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0)
                    idle.Add(mi.GetProperName());
            }

            return new JObject
            {
                ["sitesTotal"] = sites.Length,
                ["sites"] = stuck,
                ["idleDupes"] = idle,
            };
        }

        // ---------------------------------------------------------------- research

        static JToken ResearchState()
        {
            var r = Research.Instance;
            var done = new JArray();
            var available = new JArray();
            foreach (var tech in Db.Get().Techs.resources)
            {
                var ti = r.Get(tech);
                if (ti != null && ti.IsComplete()) { done.Add(StripName(tech.Name)); continue; }
                if (tech.requiredTech.All(t => r.Get(t)?.IsComplete() == true)) available.Add(StripName(tech.Name));
            }
            var queue = new JArray();
            foreach (var ti in r.GetResearchQueue() ?? new List<TechInstance>())
            {
                if (ti?.tech == null) continue;
                var parts = ti.tech.costsByResearchTypeID.Select(kv =>
                {
                    ti.progressInventory.PointsByTypeID.TryGetValue(kv.Key, out var have);
                    return $"{kv.Key} {R(have, 0)}/{R(kv.Value, 0)}";
                });
                queue.Add($"{StripName(ti.tech.Name)} ({string.Join(", ", parts)})");
            }
            return new JObject
            {
                ["active"] = StripName(r.GetActiveResearch()?.tech?.Name),
                ["queue"] = queue,
                ["available"] = available,
                ["done"] = done,
            };
        }

        // ---------------------------------------------------------------- resources

        static JToken Resources()
        {
            var inv = ClusterManager.Instance.activeWorld.worldInventory;
            var list = new List<KeyValuePair<string, float>>();
            foreach (var tag in DiscoveredResources.Instance.GetDiscovered())
            {
                float kg = inv.GetAmount(tag, false);
                if (kg > 0.5f) list.Add(new KeyValuePair<string, float>(TagManager.GetProperName(tag, true), kg));
            }
            var o = new JObject();
            foreach (var kv in list.OrderByDescending(kv => kv.Value))
                if (o.Property(kv.Key) == null) o[kv.Key] = R(kv.Value, 0);
            return o; // kilograms (food items are counted in kg here; see colony.foodKcal)
        }

        // ---------------------------------------------------------------- printing pod

        static JToken Pod()
        {
            var im = Immigration.Instance;
            return new JObject
            {
                ["printablesReady"] = im.ImmigrantsAvailable,
                ["secondsUntilNext"] = im.ImmigrantsAvailable ? 0 : R(im.GetTimeRemaining(), 0),
                ["note"] = "Options are in pod.json, written while the selection screen is open.",
            };
        }

        // ---------------------------------------------------------------- helpers

        public static JArray Statuses(GameObject go, bool problemsOnly)
        {
            var arr = new JArray();
            var grp = go.GetComponent<KSelectable>()?.GetStatusItemGroup();
            if (grp == null) return arr;
            foreach (var e in grp)
            {
                if (e.item == null) continue;
                var t = e.item.notificationType;
                bool bad = t == NotificationType.Bad || t == NotificationType.BadMinor || t == NotificationType.DuplicantThreatening;
                if (problemsOnly && !bad) continue;
                string text = StripName(e.GetName());
                if (string.IsNullOrEmpty(text)) continue;
                arr.Add(bad ? "!" + text : text);
            }
            return arr;
        }

        static JToken AirAt(int cell)
        {
            if (!Grid.IsValidCell(cell)) return null;
            var el = Grid.Element[cell];
            return $"{StripName(el?.name)} {R(Grid.Mass[cell] * 1000f, 0)}g";
        }

        static string DiseaseName(byte idx)
        {
            try { return StripName(Db.Get().Diseases[idx].Name); } catch { return "germ#" + idx; }
        }

        static JToken Pos(int cell)
        {
            if (!Grid.IsValidCell(cell)) return null;
            var xy = Grid.CellToXY(cell);
            return $"{xy.x},{xy.y}";
        }

        static JObject CountNames(IEnumerable<string> names)
        {
            var o = new JObject();
            if (names == null) return o;
            foreach (var g in names.Select(StripName).Where(s => !string.IsNullOrEmpty(s)).GroupBy(s => s).OrderBy(g => g.Key))
                o[g.Key] = g.Count();
            return o;
        }

        static string StripName(string s) => string.IsNullOrEmpty(s) ? s : STRINGS.UI.StripLinkFormatting(s).Trim();

        static void Add(Dictionary<string, float> d, string k, float v) => d[k] = (d.TryGetValue(k, out var x) ? x : 0f) + v;

        static JToken R(float? v, int digits = 1) => v.HasValue ? (JToken)Math.Round(v.Value, digits) : JValue.CreateNull();
    }
}
