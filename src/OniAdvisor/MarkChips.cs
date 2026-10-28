using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// Labels of the advisor's marks as UI chips instead of text floating in the world: a dark slate tag with an
    /// edge in the mark's colour, in the game's font, readable over any terrain, kept on the mark as the camera
    /// moves. A chip can be clickable (the advisor's marks open the message they belong to). MarkRenderer
    /// creates them for layers that ask for chips and clears and hides them with the layer.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class MarkChips
    {
        sealed class Chip
        {
            public MarkRenderer Owner;
            public Vector3 World;
            public RectTransform Rt;
            public bool Visible = true;
        }

        static readonly List<Chip> chips = new List<Chip>();
        static RectTransform layer;

        public static void Add(MarkRenderer owner, float x, float y, string text, Color color, System.Action onClick, bool visible)
        {
            Build();
            if (layer == null) return;
            var rt = OniStyle.Rect("chip", layer);
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0f);
            var bg = OniStyle.Fill(rt, OniStyle.Ink, onClick != null);
            var h = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(0, 11, 4, 4); h.spacing = 8;
            h.childControlWidth = h.childControlHeight = true; h.childForceExpandWidth = false; h.childAlignment = TextAnchor.MiddleLeft;
            var fit = rt.gameObject.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var edge = OniStyle.Rect("edge", rt);
            var le = edge.gameObject.AddComponent<LayoutElement>(); le.preferredWidth = 5; le.minHeight = 22;
            OniStyle.Fill(edge, new Color(color.r, color.g, color.b, 1f), false);
            var lt = OniStyle.Rect("text", rt);
            var label = OniStyle.Label(lt, text.Replace("<", "‹"), 18, color: Color.white, align: TextAlignmentOptions.MidlineLeft, wrap: false);
            label.overflowMode = TextOverflowModes.Overflow;
            if (onClick != null)
            {
                var b = rt.gameObject.AddComponent<Button>();
                b.targetGraphic = bg;
                var colors = b.colors; colors.highlightedColor = new Color(1.25f, 1.25f, 1.4f); b.colors = colors;
                b.onClick.AddListener(() => onClick());
            }
            chips.Add(new Chip { Owner = owner, World = new Vector3(x, y - 0.6f, 0f), Rt = rt, Visible = visible });
        }

        public static void Clear(MarkRenderer owner)
        {
            for (int i = chips.Count - 1; i >= 0; i--)
                if (chips[i].Owner == owner) { if (chips[i].Rt != null) UnityEngine.Object.Destroy(chips[i].Rt.gameObject); chips.RemoveAt(i); }
        }

        public static void SetVisible(MarkRenderer owner, bool visible)
        {
            foreach (var c in chips) if (c.Owner == owner) c.Visible = visible;
        }

        /// <summary>Every frame: keep each chip on its mark.</summary>
        public static void Tick()
        {
            if (chips.Count == 0 || layer == null) return;
            var cam = Camera.main;
            if (cam == null) return;
            foreach (var c in chips)
            {
                if (c.Rt == null) continue;
                var sp = cam.WorldToScreenPoint(c.World);
                bool on = c.Visible && sp.z > 0 && sp.x > -50 && sp.y > -50 && sp.x < Screen.width + 50 && sp.y < Screen.height + 50;
                if (c.Rt.gameObject.activeSelf != on) c.Rt.gameObject.SetActive(on);
                if (!on) continue;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, sp, null, out var local);
                c.Rt.anchoredPosition = local;
            }
        }

        static void Build()
        {
            if (layer != null || AdvisorHud.Canvas == null) return;
            layer = OniStyle.Rect("advisor-mark-chips", AdvisorHud.Canvas);
            OniStyle.Stretch(layer);
            layer.pivot = Vector2.zero;
            layer.SetSiblingIndex(Math.Min(1, layer.parent.childCount - 1)); // low: under pins, cards and panels
        }

        public static void Destroy()
        {
            chips.Clear();
            if (layer != null) UnityEngine.Object.Destroy(layer.gameObject);
            layer = null;
        }
    }
}
