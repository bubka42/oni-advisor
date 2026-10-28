using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OniAdvisor
{
    /// <summary>
    /// A blueprint on the cursor (Factorio style): one of the advisor's projects follows the mouse, every tile
    /// tinted by whether it fits there (green = buildable, orange = will be dug out first, blue = already
    /// there, red = blocked), and a left click places the whole thing as build orders (BuildPlacer). The tool
    /// stays active for more copies; right-click or Escape puts it away.
    /// UNTESTED IN GAME.
    /// </summary>
    public sealed class StampTool : InterfaceTool
    {
        public static StampTool Instance { get; private set; }

        static List<BuildPlacer.Piece> pieces = new List<BuildPlacer.Piece>();
        static string blueprintName;
        static int centreX, centreY;     // the layout's centre as planned; the cursor holds this point
        static int lastCell = Grid.InvalidCell;
        static readonly MarkRenderer preview = new MarkRenderer("stamp");

        static readonly Color Ok = new Color(0.45f, 0.9f, 0.4f, 0.35f), Dig = new Color(1f, 0.6f, 0.2f, 0.4f),
            There = new Color(0.4f, 0.7f, 1f, 0.3f), Blocked = new Color(1f, 0.25f, 0.2f, 0.45f);

        /// <summary>Picks up a project as a blueprint on the cursor.</summary>
        internal static bool Begin(string projectId)
        {
            pieces = ProjectStore.Pieces(projectId, out blueprintName);
            if (blueprintName == null || pieces.Count == 0) { AdvisorHud.Show("That project has nothing to place", 3f); return false; }
            var cells = pieces.SelectMany(p => p.Cells()).Where(Grid.IsValidCell).Select(Grid.CellToXY).ToList();
            if (cells.Count == 0) return false;
            centreX = (cells.Min(c => c.x) + cells.Max(c => c.x)) / 2;
            centreY = (cells.Min(c => c.y) + cells.Max(c => c.y)) / 2;
            lastCell = Grid.InvalidCell;
            if (Instance == null || PlayerController.Instance == null) { AdvisorHud.Show("The stamp tool isn't available", 3f); return false; }
            PlayerController.Instance.ActivateTool(Instance);
            AdvisorHud.Show($"Blueprint: {blueprintName}. Click to place build orders, right-click or Esc to put it away", 6f);
            return true;
        }

        protected override void OnPrefabInit()
        {
            base.OnPrefabInit();
            Instance = this;
            if (visualizer == null)
            {
                visualizer = new GameObject("OniAdvisorStampToolVisualizer");
                visualizer.transform.SetParent(transform, false);
                visualizer.SetActive(false);
            }
        }

        protected override void OnDeactivateTool(InterfaceTool new_tool)
        {
            base.OnDeactivateTool(new_tool);
            preview.Clear();
            lastCell = Grid.InvalidCell;
        }

        public override void OnMouseMove(Vector3 cursor_pos)
        {
            base.OnMouseMove(cursor_pos);
            int cell = Grid.PosToCell(cursor_pos);
            if (cell == lastCell || !Grid.IsValidCell(cell)) return;
            lastCell = cell;
            Draw(cell);
        }

        public override void OnLeftClickDown(Vector3 cursor_pos)
        {
            base.OnLeftClickDown(cursor_pos);
            int cell = Grid.PosToCell(cursor_pos);
            if (!Grid.IsValidCell(cell)) return;
            try
            {
                var result = BuildPlacer.Place(Shifted(cell), 5);
                AdvisorHud.Show($"{blueprintName}: {result}", 6f);
                Log.Info($"stamped {blueprintName} at {Grid.CellToXY(cell)}: {result}");
                lastCell = Grid.InvalidCell;
                Draw(cell);
            }
            catch (Exception e) { Log.Warn("stamp failed: " + e); AdvisorHud.Show("Placing the blueprint failed (see the log)", 4f); }
        }

        public override void OnRightClickDown(Vector3 cursor_pos, KButtonEvent e)
        {
            e.TryConsume(global::Action.MouseRight);
            if (SelectTool.Instance != null) PlayerController.Instance.ActivateTool(SelectTool.Instance);
        }

        static IEnumerable<BuildPlacer.Piece> Shifted(int cursorCell)
        {
            var c = Grid.CellToXY(cursorCell);
            int dx = c.x - centreX, dy = c.y - centreY;
            return pieces.Select(p => p.Shifted(dx, dy));
        }

        static void Draw(int cursorCell)
        {
            preview.Clear();
            int toDig = 0, blocked = 0;
            float top = float.MinValue, midX = 0;
            foreach (var p in Shifted(cursorCell))
            {
                var checks = BuildPlacer.Check(p);
                foreach (var ch in checks)
                {
                    if (!Grid.IsValidCell(ch.Cell)) continue;
                    var xy = Grid.CellToXY(ch.Cell);
                    var col = ch.Fit == BuildPlacer.Fit.Ok ? Ok : ch.Fit == BuildPlacer.Fit.Dig ? Dig : ch.Fit == BuildPlacer.Fit.Built ? There : Blocked;
                    if (p.IsUtility) preview.Dot(xy.x, xy.y, new Color(col.r, col.g, col.b, 0.85f), 0.45f);
                    else preview.Rect(xy.x, xy.y, xy.x, xy.y, col);
                    if (ch.Fit == BuildPlacer.Fit.Dig) toDig++;
                    if (ch.Fit == BuildPlacer.Fit.Blocked) blocked++;
                    if (xy.y + 1 > top) { top = xy.y + 1; midX = xy.x; }
                }
                if (!p.IsUtility && checks.Count > 0)
                {
                    var pts = checks.Select(ch => Grid.CellToXY(ch.Cell)).ToList();
                    float cx = (pts.Min(q => q.x) + pts.Max(q => q.x) + 1) / 2f, cy = (pts.Min(q => q.y) + pts.Max(q => q.y) + 1) / 2f;
                    Sprite icon = null;
                    try { icon = p.Def.GetUISprite("ui", false); } catch { }
                    preview.Icon(cx, cy, icon, new Color(1, 1, 1, 0.75f), Math.Min(pts.Max(q => q.x) - pts.Min(q => q.x) + 1, pts.Max(q => q.y) - pts.Min(q => q.y) + 1) * 0.8f);
                }
            }
            var c = Grid.CellToXY(cursorCell);
            string status = blocked > 0 ? $"{blocked} tile(s) blocked" : toDig > 0 ? $"{toDig} tile(s) to dig" : "fits";
            preview.Label(c.x + 0.5f, top + 1.5f, $"{blueprintName}: {status}  (click to place, right-click to stop)", blocked > 0 ? new Color(1f, 0.5f, 0.45f) : new Color(0.7f, 1f, 0.6f));
        }
    }
}
