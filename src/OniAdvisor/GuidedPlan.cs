using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// Guided planning: while holding a building in the game's own build tool, Ctrl+Shift+G asks the advisor to
    /// lay out everything it needs around that spot (a hydrogen generator: the pump, filter, pipes, wires…).
    /// The spot is marked while the advisor works (it designs with the real map, so this takes seconds, not a
    /// frame); its answer is the project "guide" in projects.json, shown as ghosts with a card:
    /// STAMP (carry it elsewhere) · PLACE (build orders as shown) · ANOTHER (a different layout) · DISCARD.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class GuidedPlan
    {
        internal const string ProjectId = "guide";

        static readonly MarkRenderer spot = new MarkRenderer("guide-spot");
        static float askedAt = -1f;          // waiting for the advisor since (unscaled time); -1 = not waiting
        static System.DateTime askedUtc;
        static string anchorName, prefab;
        static int ax, ay;
        static string orientation;
        static RectTransform card;
        static TextMeshProUGUI cardText;

        static string ProjectsFile => Path.Combine(Paths.Sub("projects"), "projects.json");

        /// <summary>Ctrl+Shift+G.</summary>
        public static void Ask(bool another = false)
        {
            if (!another)
            {
                var tool = PlayerController.Instance != null ? PlayerController.Instance.ActiveTool : null;
                var def = tool == BuildTool.Instance && BuildTool.Instance != null ? Traverse.Create(BuildTool.Instance).Field("def").GetValue<BuildingDef>() : null;
                if (def == null) { AdvisorHud.Show("Pick a building from the build menu first, hover where you want it, then press Ctrl+Shift+G", 5f); return; }
                var cam = Camera.main;
                int cell = cam != null ? Grid.PosToCell(cam.ScreenToWorldPoint(Input.mousePosition)) : Grid.InvalidCell;
                if (!Grid.IsValidCell(cell)) return;
                var xy = Grid.CellToXY(cell);
                ax = xy.x; ay = xy.y;
                prefab = def.PrefabID;
                anchorName = global::STRINGS.UI.StripLinkFormatting(def.Name);
                orientation = BuildTool.Instance.GetBuildingOrientation.ToString();
            }
            if (prefab == null) return;
            HideCard();
            askedAt = Time.unscaledTime;
            askedUtc = System.DateTime.UtcNow;
            spot.Clear();
            spot.BeginReveal();
            spot.Stroke(Sketch.Circle(new Vector2(ax + 0.5f, ay + 0.5f), 2.2f, 1.8f, Sketch.Rng(ax * 31 + ay)), new Color(0.37f, 0.83f, 0.9f, 0.95f), 0.2f);
            spot.Label(ax + 0.5f, ay + 3.2f, $"planning around the {anchorName}…", new Color(0.37f, 0.83f, 0.9f, 1f));
            AdvisorRunner.WriteRequest("guide", null, new JObject
            {
                ["prefab"] = prefab, ["name"] = anchorName, ["x"] = ax, ["y"] = ay, ["rotation"] = orientation,
                ["another"] = another,
            });
            AdvisorHud.Show(another ? $"Asking for a different layout around the {anchorName}…" : $"Asking the advisor to plan around the {anchorName}…", 4f);
        }

        /// <summary>About once a second: has the advisor's layout arrived?</summary>
        public static void Poll()
        {
            if (askedAt < 0f) return;
            if (Time.unscaledTime - askedAt > 240f) { askedAt = -1f; spot.Clear(); AdvisorHud.Show("The advisor didn't send a layout (see the advisor panel)", 4f); return; }
            if (!File.Exists(ProjectsFile) || File.GetLastWriteTimeUtc(ProjectsFile) <= askedUtc) return;
            if (!HasAnchor()) return; // the file changed, but not (yet) with a layout around this building
            var info = ProjectStore.List().FirstOrDefault(p => p.id == ProjectId);
            if (info.id == null) return; // projects not reloaded yet, or no guide project
            askedAt = -1f;
            spot.Clear();
            ProjectStore.Focus(ProjectId);
            ShowCard($"<b>{info.name}</b>  <color=#D2D4EB>{info.progress}</color>");
        }

        /// <summary>Whether the "guide" project contains the building the player asked about, near where they held it.</summary>
        static bool HasAnchor()
        {
            try
            {
                var root = JObject.Parse(File.ReadAllText(ProjectsFile));
                var guide = (root["projects"] as JArray)?.FirstOrDefault(p => (string)p["id"] == ProjectId);
                return guide?["items"] is JArray items && items.Any(it =>
                    string.Equals((string)it["prefab"], prefab, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(((int?)it["x"] ?? -999) - ax) <= 3 && Math.Abs(((int?)it["y"] ?? -999) - ay) <= 3);
            }
            catch { return false; } // half-written: next poll
        }

        static void Discard()
        {
            HideCard();
            try
            {
                if (!File.Exists(ProjectsFile)) return;
                var root = JObject.Parse(File.ReadAllText(ProjectsFile));
                if (root["projects"] is JArray arr)
                {
                    foreach (var p in arr.Where(p => (string)p["id"] == ProjectId).ToList()) p.Remove();
                    Paths.WriteAtomic(ProjectsFile, root.ToString(Formatting.Indented));
                }
            }
            catch (Exception e) { Log.Warn("discarding the guided layout failed: " + e.Message); }
        }

        static void ShowCard(string text)
        {
            Build();
            if (card == null) return;
            cardText.text = text;
            card.gameObject.SetActive(true);
        }

        static void HideCard() { if (card != null) card.gameObject.SetActive(false); }

        static void Build()
        {
            if (card != null || AdvisorHud.Canvas == null) return;
            card = OniStyle.Rect("advisor-guide-card", AdvisorHud.Canvas);
            card.anchorMin = card.anchorMax = new Vector2(0.5f, 1f);
            card.pivot = new Vector2(0.5f, 1f);
            card.anchoredPosition = new Vector2(0, -180);
            card.sizeDelta = new Vector2(820, 84);
            OniStyle.Fill(card, OniStyle.PanelDark, true);
            var v = card.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(12, 12, 8, 8); v.spacing = 6;
            v.childControlWidth = v.childControlHeight = true; v.childForceExpandHeight = false;
            var tr = OniStyle.Rect("text", card);
            tr.gameObject.AddComponent<LayoutElement>().preferredHeight = 24;
            cardText = OniStyle.Label(tr, "", 15, align: TextAlignmentOptions.MidlineLeft, wrap: false);
            cardText.richText = true;
            var row = OniStyle.Rect("buttons", card);
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 8; h.childAlignment = TextAnchor.MiddleLeft; h.childControlWidth = h.childControlHeight = true; h.childForceExpandWidth = false;
            OniStyle.MakeButton(row, "STAMP", () => { HideCard(); StampTool.Begin(ProjectId); }, 100);
            OniStyle.MakeButton(row, "PLACE AS SHOWN", () => { HideCard(); AdvisorHud.Show(ProjectStore.PlaceProject(ProjectId, 5), 6f); }, 170);
            OniStyle.MakeButton(row, "ANOTHER", () => Ask(another: true), 110);
            OniStyle.MakeButton(row, "ASK", () => { HideCard(); AdvisorPanel.Open(); }, 70);
            OniStyle.MakeButton(row, "DISCARD", Discard, 100);
            card.gameObject.SetActive(false);
        }

        public static void Destroy()
        {
            spot.Clear();
            if (card != null) UnityEngine.Object.Destroy(card.gameObject);
            card = null; askedAt = -1f;
        }
    }
}
