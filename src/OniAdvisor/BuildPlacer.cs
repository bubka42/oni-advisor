using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OniAdvisor
{
    /// <summary>
    /// Places build orders for planned layouts (projects, stamped blueprints) the way the player's own tools
    /// would: buildings with the game's placement rules, utility runs (pipes, wires, rails, automation) with
    /// their connections set the way a drag would set them, and dig orders for solid tiles in the way.
    /// Whatever can't go in yet (still to be dug out, or waiting for a floor that is itself only ordered)
    /// waits here and is retried every second until it fits or 30 minutes pass.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class BuildPlacer
    {
        /// <summary>One thing to build: a building at a cell, or a utility run through cells.</summary>
        internal sealed class Piece
        {
            public BuildingDef Def;
            public int Cell = Grid.InvalidCell;            // building: its origin cell
            public Orientation Orientation = Orientation.Neutral;
            public List<int> Path;                         // utility: the cells in order
            public List<string> Materials;                 // element ids, optional
            public string Label;
            public bool IsUtility => Path != null;

            public Piece Shifted(int dx, int dy)
            {
                return new Piece
                {
                    Def = Def, Orientation = Orientation, Materials = Materials, Label = Label,
                    Cell = IsUtility ? Grid.InvalidCell : Grid.OffsetCell(Cell, dx, dy),
                    Path = IsUtility ? Path.Select(c => Grid.OffsetCell(c, dx, dy)).ToList() : null,
                };
            }

            /// <summary>Every cell it covers (a building's footprint, or the run's cells).</summary>
            public IEnumerable<int> Cells()
            {
                if (IsUtility) return Path;
                return (Def.PlacementOffsets ?? new[] { new CellOffset(0, 0) })
                    .Select(off => Grid.OffsetCell(Cell, Rotatable.GetRotatedCellOffset(off, Orientation)));
            }
        }

        /// <summary>How a cell of a piece stands: buildable now, needs digging first, or blocked (with why).</summary>
        internal enum Fit { Ok, Dig, Built, Blocked }

        internal sealed class CellCheck
        {
            public int Cell;
            public Fit Fit;
            public string Reason;
        }

        // ------------------------------------------------------------------ checking

        /// <summary>Checks a piece against the map with the game's own rules, cell by cell.</summary>
        public static List<CellCheck> Check(Piece p)
        {
            var result = new List<CellCheck>();
            if (p.Def == null) return result;
            if (p.IsUtility)
            {
                foreach (int c in p.Path)
                {
                    var cc = new CellCheck { Cell = c };
                    if (!Grid.IsValidCell(c) || !Grid.IsVisible(c)) { cc.Fit = Fit.Blocked; cc.Reason = "outside the explored map"; }
                    else if (Grid.Objects[c, (int)p.Def.ObjectLayer] is GameObject occ && occ != null) cc.Fit = Fit.Built; // joins what is there
                    else if (Grid.Solid[c] && !IsBuiltTile(c)) { cc.Fit = Diggable.IsDiggable(c) ? Fit.Dig : Fit.Blocked; cc.Reason = cc.Fit == Fit.Dig ? "dig first" : "undiggable"; }
                    else cc.Fit = Fit.Ok;
                    result.Add(cc);
                }
                return result;
            }

            var footprint = p.Cells().ToList();
            var existing = Grid.IsValidCell(p.Cell) ? Grid.Objects[p.Cell, (int)p.Def.ObjectLayer] : null;
            var bld = existing != null ? existing.GetComponent<Building>() : null;
            if (bld != null && bld.Def != null && bld.Def.PrefabID == p.Def.PrefabID)
            {
                foreach (int c in footprint) result.Add(new CellCheck { Cell = c, Fit = Fit.Built });
                return result;
            }
            bool needsDig = false;
            foreach (int c in footprint)
            {
                var cc = new CellCheck { Cell = c, Fit = Fit.Ok };
                if (!Grid.IsValidCell(c) || !Grid.IsVisible(c)) { cc.Fit = Fit.Blocked; cc.Reason = "outside the explored map"; }
                else if (Grid.Solid[c] && !IsBuiltTile(c) && !p.Def.IsTilePiece)
                {
                    cc.Fit = Diggable.IsDiggable(c) ? Fit.Dig : Fit.Blocked;
                    cc.Reason = cc.Fit == Fit.Dig ? "dig first" : "undiggable";
                    needsDig |= cc.Fit == Fit.Dig;
                }
                result.Add(cc);
            }
            if (!needsDig && result.All(r => r.Fit == Fit.Ok))
            {
                string reason = null;
                bool ok;
                try { ok = p.Def.IsValidPlaceLocation((GameObject)null, p.Cell, p.Orientation, false, out reason); }
                catch { ok = true; }
                if (!ok)
                {
                    reason = global::STRINGS.UI.StripLinkFormatting(reason ?? "can't go here");
                    foreach (var r in result) { r.Fit = Fit.Blocked; r.Reason = reason; }
                }
            }
            return result;
        }

        static bool IsBuiltTile(int cell)
        {
            var go = Grid.Objects[cell, (int)ObjectLayer.FoundationTile];
            return go != null;
        }

        // ------------------------------------------------------------------ placing

        sealed class Pending
        {
            public Piece Piece;
            public float Until;
            public string Waiting;
        }

        static readonly List<Pending> pending = new List<Pending>();

        public sealed class Result
        {
            public int Placed, Digs, Waiting, Skipped, AlreadyThere;
            public readonly List<string> Problems = new List<string>();
            public override string ToString()
            {
                var parts = new List<string>();
                if (Placed > 0) parts.Add($"{Placed} build order(s) placed");
                if (Digs > 0) parts.Add($"{Digs} tile(s) marked for digging");
                if (Waiting > 0) parts.Add($"{Waiting} waiting (placed once the way is clear)");
                if (AlreadyThere > 0) parts.Add($"{AlreadyThere} already there");
                if (Skipped > 0) parts.Add($"{Skipped} skipped");
                var s = parts.Count > 0 ? string.Join(", ", parts) : "nothing to place";
                if (Problems.Count > 0) s += " (" + string.Join("; ", Problems.Distinct().Take(4)) + ")";
                return s;
            }
        }

        /// <summary>Places everything that can be placed now, digs what is in the way, and queues the rest.</summary>
        public static Result Place(IEnumerable<Piece> pieces, int priority)
        {
            var result = new Result();
            // Floors and walls first (other buildings may stand on them), then buildings, then utility runs.
            var ordered = pieces.Where(p => p.Def != null)
                .OrderBy(p => p.IsUtility ? 2 : p.Def.IsTilePiece ? 0 : 1).ToList();
            foreach (var p in ordered)
            {
                try
                {
                    if (p.IsUtility) PlaceRun(p, priority, result);
                    else PlaceBuilding(p, priority, result, queueIfBlocked: true);
                }
                catch (Exception e) { result.Skipped++; result.Problems.Add(e.Message); }
            }
            return result;
        }

        static void PlaceBuilding(Piece p, int priority, Result result, bool queueIfBlocked)
        {
            var checks = Check(p);
            if (checks.Count > 0 && checks.All(c => c.Fit == Fit.Built)) { result.AlreadyThere++; return; }
            if (checks.Any(c => c.Fit == Fit.Blocked && c.Reason == "undiggable" || c.Reason == "outside the explored map"))
            {
                result.Skipped++; result.Problems.Add($"{Name(p)}: {checks.First(c => c.Fit == Fit.Blocked).Reason}");
                return;
            }
            var toDig = checks.Where(c => c.Fit == Fit.Dig).Select(c => c.Cell).ToList();
            if (toDig.Count > 0)
            {
                foreach (int c in toDig) Dig(c, priority, result);
                Queue(p, "digging", result);
                return;
            }
            if (checks.Any(c => c.Fit == Fit.Blocked))
            {
                if (queueIfBlocked) Queue(p, checks.First(c => c.Fit == Fit.Blocked).Reason, result);
                else { result.Skipped++; result.Problems.Add($"{Name(p)}: {checks.First(c => c.Fit == Fit.Blocked).Reason}"); }
                return;
            }
            var go = p.Def.TryPlace(null, Grid.CellToPosCBC(p.Cell, p.Def.SceneLayer), p.Orientation, Materials(p), 0);
            if (go == null)
            {
                if (queueIfBlocked) Queue(p, "the game refused it", result);
                else result.Skipped++;
                return;
            }
            SetPriority(go, priority);
            result.Placed++;
        }

        /// <summary>A pipe/wire/rail run: each cell placed (or joined if something is there) with connections to its neighbours in the run.</summary>
        static void PlaceRun(Piece p, int priority, Result result)
        {
            var mgr = NetworkManager(p.Def);
            var elements = Materials(p);
            bool waiting = false;
            for (int i = 0; i < p.Path.Count; i++)
            {
                int cell = p.Path[i];
                if (!Grid.IsValidCell(cell)) continue;
                var conn = (UtilityConnections)0;
                if (i > 0 && Adjacent(p.Path[i - 1], cell)) conn |= UtilityConnectionsExtensions.DirectionFromToCell(cell, p.Path[i - 1]);
                if (i < p.Path.Count - 1 && Adjacent(cell, p.Path[i + 1])) conn |= UtilityConnectionsExtensions.DirectionFromToCell(cell, p.Path[i + 1]);

                var existing = Grid.Objects[cell, (int)p.Def.ObjectLayer];
                if (existing != null)
                {
                    Join(existing, conn, cell, mgr);
                    result.AlreadyThere++;
                    continue;
                }
                if (Grid.Solid[cell] && !IsBuiltTile(cell))
                {
                    if (Diggable.IsDiggable(cell)) { Dig(cell, priority, result); waiting = true; }
                    else { result.Skipped++; result.Problems.Add($"{Name(p)}: undiggable tile in the way"); }
                    continue;
                }
                var go = p.Def.TryPlace(null, Grid.CellToPosCBC(cell, p.Def.SceneLayer), Orientation.Neutral, elements, 0);
                if (go == null) { waiting = true; continue; }
                SetConnections(go, conn, cell, mgr, physical: false);
                SetPriority(go, priority);
                result.Placed++;
            }
            // Cells still to be dug: retry the whole run later (cells already placed are joined, not duplicated).
            if (waiting) Queue(p, "digging", result, countIt: false);
        }

        static bool Adjacent(int a, int b)
        {
            var p = Grid.CellToXY(a); var q = Grid.CellToXY(b);
            return Math.Abs(p.x - q.x) + Math.Abs(p.y - q.y) == 1;
        }

        static IUtilityNetworkMgr NetworkManager(BuildingDef def)
        {
            try { return def.BuildingComplete != null ? def.BuildingComplete.GetComponent<IHaveUtilityNetworkMgr>()?.GetNetworkManager() : null; }
            catch { return null; }
        }

        /// <summary>Adds the run's connections to a pipe/wire that is already there (built or ordered).</summary>
        static void Join(GameObject existing, UtilityConnections conn, int cell, IUtilityNetworkMgr mgr)
        {
            var vis = existing.GetComponent<KAnimGraphTileVisualizer>();
            if (vis == null) return;
            var merged = vis.Connections | conn;
            if (merged == vis.Connections) return;
            vis.UpdateConnections(merged);
            vis.Refresh();
        }

        static void SetConnections(GameObject go, UtilityConnections conn, int cell, IUtilityNetworkMgr mgr, bool physical)
        {
            var vis = go.GetComponent<KAnimGraphTileVisualizer>();
            if (vis != null) { vis.UpdateConnections(conn); vis.Refresh(); }
            else mgr?.SetConnections(conn, cell, physical);
        }

        static void Dig(int cell, int priority, Result result)
        {
            if (Diggable.GetDiggable(cell) != null) return; // already marked
            var go = DigTool.PlaceDig(cell, 0);
            if (go == null) return;
            SetPriority(go, priority);
            result.Digs++;
        }

        static void SetPriority(GameObject go, int priority)
        {
            var pr = go.GetComponent<Prioritizable>();
            if (pr != null) pr.SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.basic, Mathf.Clamp(priority, 1, 9)));
        }

        static void Queue(Piece p, string why, Result result, bool countIt = true)
        {
            if (pending.Any(q => q.Piece == p)) return;
            pending.Add(new Pending { Piece = p, Until = Time.unscaledTime + 1800f, Waiting = why });
            if (countIt) result.Waiting++;
        }

        /// <summary>About once a second: places queued pieces whose way has cleared.</summary>
        public static void Poll()
        {
            if (pending.Count == 0) return;
            float now = Time.unscaledTime;
            foreach (var q in pending.ToList())
            {
                if (now > q.Until) { pending.Remove(q); Log.Info($"gave up waiting to place {Name(q.Piece)} ({q.Waiting})"); continue; }
                try
                {
                    var r = new Result();
                    if (q.Piece.IsUtility)
                    {
                        if (q.Piece.Path.Any(c => Grid.Solid[c] && !IsBuiltTile(c))) continue; // still digging
                        pending.Remove(q);
                        PlaceRun(q.Piece, 5, r);
                    }
                    else
                    {
                        var checks = Check(q.Piece);
                        if (checks.Any(c => c.Fit != Fit.Ok && c.Fit != Fit.Built)) continue; // not yet
                        pending.Remove(q);
                        PlaceBuilding(q.Piece, 5, r, queueIfBlocked: false);
                    }
                    if (r.Placed > 0) Log.Info($"placed waiting {Name(q.Piece)}");
                }
                catch (Exception e) { pending.Remove(q); Log.Warn("waiting placement failed: " + e.Message); }
            }
        }

        public static int WaitingCount => pending.Count;

        public static void Clear() => pending.Clear();

        // ------------------------------------------------------------------ materials

        /// <summary>
        /// The materials for each of the building's ingredients: the plan's choice if it names one, otherwise
        /// whichever suitable material the colony has most of (so orders don't wait for something absent).
        /// </summary>
        static IList<Tag> Materials(Piece p)
        {
            var def = p.Def;
            var defaults = def.DefaultElements();
            if (def.MaterialCategory == null) return defaults;
            var chosen = new List<Tag>();
            var inventory = ClusterManager.Instance?.activeWorld?.worldInventory;
            for (int i = 0; i < def.MaterialCategory.Length; i++)
            {
                string wanted = p.Materials != null && i < p.Materials.Count ? p.Materials[i] : null;
                var options = def.MaterialCategory[i].Split('&').Select(t => new Tag(t)).ToList();
                Element best = null;
                float bestAmount = -1f;
                foreach (var e in ElementLoader.elements)
                {
                    if (e == null || e.disabled || !options.Any(t => e.HasTag(t) || e.materialCategory == t)) continue;
                    if (wanted != null && string.Equals(e.tag.Name, wanted, StringComparison.OrdinalIgnoreCase)) { best = e; break; }
                    float amount = inventory != null ? inventory.GetAmount(e.tag, true) : 0f;
                    if (amount > bestAmount) { best = e; bestAmount = amount; }
                }
                chosen.Add(best != null && (wanted != null || bestAmount > 0f) ? best.tag : (i < defaults.Count ? defaults[i] : options[0]));
            }
            return chosen;
        }

        static string Name(Piece p) => p.Label ?? (p.Def != null ? global::STRINGS.UI.StripLinkFormatting(p.Def.Name) : "?");
    }
}
