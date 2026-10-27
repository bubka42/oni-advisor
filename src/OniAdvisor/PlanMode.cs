using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OniAdvisor
{
    /// <summary>
    /// Planning mode (Ctrl+Shift+P): pauses the game and switches to a drawing tool.
    ///   drag            area
    ///   Shift + drag    line (shaft, pipe route, tunnel)
    ///   click           point
    ///   right-click     delete the mark under the cursor (or leave the tool if there is none)
    ///   Ctrl+Shift+X    clear all your marks
    ///   Ctrl+Shift+P    done: saves the marks, takes a screenshot and asks the advisor to look
    ///
    /// UNTESTED IN GAME. The tool is a custom InterfaceTool registered with the PlayerController.
    /// If registering it fails, a fallback reads the mouse directly (clicks then also reach the
    /// game's normal select tool, which only selects things - harmless while paused).
    /// </summary>
    internal static class PlanMode
    {
        public static bool Active { get; private set; }
        static bool pausedByUs;
        static bool fallbackDragging;
        static int fallbackStart;
        static readonly MarkRenderer hint = new MarkRenderer("plan-hint");

        public static void Toggle()
        {
            if (Active) Exit(send: true);
            else Enter();
        }

        static void Enter()
        {
            Active = true;
            try
            {
                var speed = SpeedControlScreen.Instance;
                if (speed != null && !speed.IsPaused) { speed.Pause(false, false); pausedByUs = true; }
            }
            catch (Exception e) { Log.Warn("pause failed: " + e.Message); }

            PlanMarks.SetVisible(true);
            ShowHint();
            if (PlanTool.Instance != null && PlayerController.Instance != null)
            {
                try { PlayerController.Instance.ActivateTool(PlanTool.Instance); }
                catch (Exception e) { Log.Warn("plan tool activation failed, using fallback: " + e.Message); }
            }
            AdvisorHud.Show("Planning mode: game paused. Drag = area, Shift+drag = line, click = point. Ctrl+Shift+P = send", 5f);
            Log.Info("planning mode on");
        }

        /// <summary>Leaves planning mode; with send, saves marks and files a "plan" request with a screenshot.</summary>
        public static void Exit(bool send)
        {
            if (!Active) return;
            Active = false;
            hint.Clear();
            PlanMarks.ClearPreview();

            try
            {
                if (PlayerController.Instance != null && PlayerController.Instance.ActiveTool == PlanTool.Instance && SelectTool.Instance != null)
                    PlayerController.Instance.ActivateTool(SelectTool.Instance);
            }
            catch (Exception e) { Log.Warn("plan tool deactivation failed: " + e.Message); }

            if (send)
            {
                try
                {
                    PlanMarks.SavePlayer();
                    AdvisorRunner.Instance?.RequestWithScreenshot("plan");
                    AdvisorHud.Show($"Sketch sent to the advisor ({PlanMarks.Player.Count} mark(s))");
                }
                catch (Exception e) { Log.Warn("plan send failed: " + e.Message); }
            }

            // Back to whatever the overlay setting shows outside planning mode - but when sending, only after the
            // screenshot has been captured, so the advisor sees the marks on it.
            if (send) Overlays.ApplyAt(Time.unscaledTime + 2f);
            else Overlays.Apply();
            if (pausedByUs)
            {
                try { SpeedControlScreen.Instance?.Unpause(false); } catch { }
                pausedByUs = false;
            }
            Log.Info("planning mode off");
        }

        /// <summary>Called by the tool when the game switches away from it (e.g. Escape).</summary>
        public static void OnToolDeactivated()
        {
            if (!Active) return;
            if (PlanMarks.PlayerDirty) { try { PlanMarks.SavePlayer(); } catch { } } // keep the sketch, but don't send it
            Exit(send: false);
            AdvisorHud.Show("Planning mode closed (marks kept, not sent)", 2.5f);
        }

        static void ShowHint()
        {
            hint.Clear();
            try
            {
                var area = GridVisibleArea.GetVisibleArea();
                float x = (area.Min.x + area.Max.x) / 2f;
                float y = area.Max.y - 3f;
                hint.Label(x, y, "PLANNING  drag: area   Shift+drag: line   click: point   right-click: delete   Ctrl+Shift+P: send   Esc: leave", new Color(1f, 0.9f, 0.3f, 1f));
            }
            catch { }
        }

        /// <summary>Fallback input path, used only when the custom tool is not active.</summary>
        public static void FallbackUpdate()
        {
            if (!Active || (PlanTool.Instance != null && PlayerController.Instance != null && PlayerController.Instance.ActiveTool == PlanTool.Instance))
                return;
            var cam = Camera.main;
            if (cam == null) return;
            var world = cam.ScreenToWorldPoint(Input.mousePosition);
            int cell = Grid.PosToCell(world);
            if (Input.GetMouseButtonDown(0)) { fallbackDragging = true; fallbackStart = cell; }
            if (fallbackDragging && Input.GetMouseButton(0)) PlanTool.PreviewDrag(fallbackStart, cell);
            if (fallbackDragging && Input.GetMouseButtonUp(0)) { fallbackDragging = false; PlanTool.FinishDrag(fallbackStart, cell); }
            if (Input.GetMouseButtonDown(1) && Grid.IsValidCell(cell))
            {
                var xy = Grid.CellToXY(cell);
                PlanMarks.RemovePlayerAt(xy.x, xy.y);
            }
        }
    }

    /// <summary>The drawing tool used in planning mode.</summary>
    public sealed class PlanTool : InterfaceTool
    {
        public static PlanTool Instance { get; private set; }
        int dragStart = Grid.InvalidCell;

        protected override void OnPrefabInit()
        {
            base.OnPrefabInit();
            Instance = this;
            if (visualizer == null)
            {
                visualizer = new GameObject("OniAdvisorPlanToolVisualizer");
                visualizer.transform.SetParent(transform, false);
                visualizer.SetActive(false);
            }
        }

        protected override void OnDeactivateTool(InterfaceTool new_tool)
        {
            base.OnDeactivateTool(new_tool);
            dragStart = Grid.InvalidCell;
            PlanMarks.ClearPreview();
            PlanMode.OnToolDeactivated();
        }

        public override void OnLeftClickDown(Vector3 cursor_pos)
        {
            base.OnLeftClickDown(cursor_pos);
            dragStart = Grid.PosToCell(cursor_pos);
        }

        public override void OnMouseMove(Vector3 cursor_pos)
        {
            base.OnMouseMove(cursor_pos);
            if (dragStart != Grid.InvalidCell) PreviewDrag(dragStart, Grid.PosToCell(cursor_pos));
        }

        public override void OnLeftClickUp(Vector3 cursor_pos)
        {
            base.OnLeftClickUp(cursor_pos);
            if (dragStart == Grid.InvalidCell) return;
            FinishDrag(dragStart, Grid.PosToCell(cursor_pos));
            dragStart = Grid.InvalidCell;
        }

        /// <summary>Right-click only deletes marks. (The game's default is "leave this tool", which ended planning
        /// mode and unpaused the game.) Leave with Ctrl+Shift+P (send) or Escape.</summary>
        public override void OnRightClickDown(Vector3 cursor_pos, KButtonEvent e)
        {
            int cell = Grid.PosToCell(cursor_pos);
            if (Grid.IsValidCell(cell))
            {
                var xy = Grid.CellToXY(cell);
                PlanMarks.RemovePlayerAt(xy.x, xy.y);
            }
            e.TryConsume(global::Action.MouseRight);
        }

        static bool ShiftHeld => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        internal static void PreviewDrag(int startCell, int endCell)
        {
            if (!Grid.IsValidCell(startCell) || !Grid.IsValidCell(endCell)) return;
            var a = Grid.CellToXY(startCell); var b = Grid.CellToXY(endCell);
            string kind = a.x == b.x && a.y == b.y ? "point" : ShiftHeld ? "line" : "area";
            PlanMarks.Preview(kind, a.x, a.y, b.x, b.y);
        }

        internal static void FinishDrag(int startCell, int endCell)
        {
            PlanMarks.ClearPreview();
            if (!Grid.IsValidCell(startCell) || !Grid.IsValidCell(endCell)) return;
            var a = Grid.CellToXY(startCell); var b = Grid.CellToXY(endCell);
            string kind = a.x == b.x && a.y == b.y ? "point" : ShiftHeld ? "line" : "area";
            PlanMarks.AddPlayer(kind, a.x, a.y, b.x, b.y);
        }
    }

    /// <summary>Registers the planning tool with the game's tool list.</summary>
    [HarmonyPatch(typeof(PlayerController), "OnPrefabInit")]
    internal static class PlayerController_OnPrefabInit_Patch
    {
        static void Postfix(PlayerController __instance)
        {
            try
            {
                var go = new GameObject("OniAdvisorPlanTool");
                go.transform.SetParent(__instance.transform, false);
                go.AddComponent<PlanTool>();
                go.SetActive(true);
                var tools = new List<InterfaceTool>(__instance.tools) { PlanTool.Instance ?? go.GetComponent<PlanTool>() };
                __instance.tools = tools.ToArray();
            }
            catch (Exception e) { Log.Warn("could not register planning tool (fallback input will be used): " + e.Message); }
        }
    }
}
