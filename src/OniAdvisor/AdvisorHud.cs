using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// The advisor's on-screen presence:
    ///   - toasts at the top centre acknowledging triggers ("Sent to advisor", "Planning mode on", ...)
    ///   - a small status badge at the bottom left: idle / thinking / first line of the latest reply,
    ///     read from live/advisor-status.json, which the companion app writes.
    /// Its own screen-space canvas; clicks pass through to the game.
    /// UNTESTED IN GAME: position and size may need tuning.
    /// </summary>
    internal static class AdvisorHud
    {
        static GameObject root;
        static TextMeshProUGUI toastText, badgeText;
        static Image toastBg, badgeBg;
        static readonly Queue<(string text, float seconds)> toasts = new Queue<(string, float)>();
        static float toastUntil;
        static System.DateTime statusTime;
        static string lastReplyShown;

        static Color Accent => OniStyle.HeaderLight;

        public static void Show(string text, float seconds = 3.5f)
        {
            toasts.Enqueue((text, seconds));
        }

        public static void Tick()
        {
            if (root == null) Build();
            if (root == null) return;

            float now = Time.unscaledTime;
            if (now >= toastUntil)
            {
                if (toasts.Count > 0)
                {
                    var (text, secs) = toasts.Dequeue();
                    toastText.text = text;
                    toastBg.gameObject.SetActive(true);
                    toastUntil = now + secs;
                }
                else if (toastBg.gameObject.activeSelf) toastBg.gameObject.SetActive(false);
            }
        }

        /// <summary>Reads live/advisor-status.json (written by the companion) about once a second.</summary>
        public static void PollStatus()
        {
            if (root == null) return;
            var path = Path.Combine(Paths.LiveDir, "advisor-status.json");
            try
            {
                if (!File.Exists(path)) { SetBadge("Advisor: companion not running", false); return; }
                var t = File.GetLastWriteTimeUtc(path);
                if (t == statusTime) return;
                var o = JObject.Parse(File.ReadAllText(path));
                statusTime = t;
                string state = (string)o["state"] ?? "idle";
                string text = (string)o["text"];
                switch (state)
                {
                    case "thinking": SetBadge("Advisor: thinking…", true); break;
                    case "notice": Show(text ?? "", 3f); SetBadge(Briefing.HasUnseen ? "Advisor: briefing ready (Ctrl+Shift+B)" : "Advisor: ready (Ctrl+Shift+A to ask)", Briefing.HasUnseen); break;
                    case "listening": SetBadge("Advisor: listening… (release V to send)", true); break;
                    case "transcribing": SetBadge("Advisor: transcribing…", true); break;
                    case "heard": SetBadge("Advisor: heard you, thinking…", true); Show("Heard: " + Shorten(text, 140), 5f); break;
                    case "error": SetBadge("Advisor: error — see the companion window", false); break;
                    case "replied":
                        SetBadge("Advisor: " + Shorten(text, 110), true);
                        if (text != lastReplyShown) { lastReplyShown = text; Show("Advisor replied — see the companion window", 3f); }
                        break;
                    default: SetBadge(Briefing.HasUnseen ? "Advisor: briefing ready (Ctrl+Shift+B)" : "Advisor: ready (Ctrl+Shift+A to ask)", Briefing.HasUnseen); break;
                }
            }
            catch { /* half-written file; next poll */ }
        }

        static void SetBadge(string text, bool highlight)
        {
            if (badgeText == null) return;
            badgeText.text = text;
            badgeText.color = highlight ? Accent : OniStyle.TextMuted;
        }

        static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("**", "").Trim();
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        /// <summary>The HUD canvas; the briefing panel lives on it too.</summary>
        public static Transform Canvas => root != null ? root.transform : null;

        static void Build()
        {
            try
            {
                root = new GameObject("OniAdvisorHud");
                Object.DontDestroyOnLoad(root);
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.overrideSorting = true;
                canvas.sortingOrder = 30000;
                var scaler = root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
                root.AddComponent<GraphicRaycaster>(); // briefing panel buttons; toast and badge let clicks through

                (toastBg, toastText) = Box("toast", new Vector2(0.5f, 1f), new Vector2(0, -70), new Vector2(780, 46), 19, TextAlignmentOptions.Center);
                toastBg.gameObject.SetActive(false);
                (badgeBg, badgeText) = Box("badge", new Vector2(0.5f, 0f), new Vector2(0, 10), new Vector2(660, 34), 16, TextAlignmentOptions.Center);
                SetBadge("Advisor: ready (Ctrl+Shift+A to ask)", false);
            }
            catch (System.Exception e)
            {
                Log.Warn("HUD could not be created: " + e.Message);
                if (root != null) Object.Destroy(root);
                root = null;
            }
        }

        /// <summary>ONI-style strip: slate panel with a pink accent bar on the left.</summary>
        static (Image, TextMeshProUGUI) Box(string name, Vector2 anchor, Vector2 pos, Vector2 size, float fontSize, TextAlignmentOptions align)
        {
            var rt = OniStyle.Rect(name, root.transform);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var bg = OniStyle.Fill(rt, OniStyle.PanelDark, false);

            var accent = OniStyle.Rect(name + "-accent", rt);
            accent.anchorMin = new Vector2(0, 0); accent.anchorMax = new Vector2(0, 1);
            accent.pivot = new Vector2(0, 0.5f);
            accent.sizeDelta = new Vector2(6, 0);
            OniStyle.Fill(accent, OniStyle.Header, false);

            var trt = OniStyle.Rect(name + "-text", rt);
            OniStyle.Stretch(trt, 16, 4);
            var text = OniStyle.Label(trt, "", fontSize, align: align | TextAlignmentOptions.Midline, wrap: false);
            text.alignment = align == TextAlignmentOptions.Center ? TextAlignmentOptions.Midline : TextAlignmentOptions.MidlineLeft;
            return (bg, text);
        }
        public static void Destroy()
        {
            if (root != null) Object.Destroy(root);
            root = null;
            toasts.Clear();
            statusTime = default;
        }
    }
}
