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
    ///   - toasts at the top centre acknowledging triggers ("Sent to advisor", "Planning mode on", ...);
    ///   - the ADVISOR button at the bottom centre, which also shows the advisor's state (ready, thinking,
    ///     listening, briefing ready) read from live/advisor-status.json (written by the companion); clicking it
    ///     opens a waiting briefing, else the advisor panel;
    ///   - tips and answers to questions not typed in the panel open a card (AdvisorCard).
    /// Its own screen-space canvas; everything except the button and cards lets clicks through.
    /// </summary>
    internal static class AdvisorHud
    {
        static GameObject root;
        static TextMeshProUGUI toastText, statusLabel;
        static Image toastBg, statusBg;
        static readonly Queue<(string text, float seconds)> toasts = new Queue<(string, float)>();
        static float toastUntil;
        static System.DateTime statusTime;
        static string lastReplyShown;
        static bool busy, lastUnseen;

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
            // The button glows gently while the advisor works.
            if (statusBg != null)
                statusBg.color = busy ? Color.Lerp(OniStyle.Button, OniStyle.HeaderLight, 0.35f + 0.35f * Mathf.Sin(now * 4f)) : OniStyle.Button;
        }

        /// <summary>Reads live/advisor-status.json (written by the companion) about once a second.</summary>
        public static void PollStatus()
        {
            if (root == null) return;
            if (Briefing.HasUnseen != lastUnseen && !busy) { lastUnseen = Briefing.HasUnseen; SetIdle(); } // a briefing arrived or was watched
            var path = Path.Combine(Paths.LiveDir, "advisor-status.json");
            try
            {
                if (!File.Exists(path)) { SetStatus("not running", false); return; }
                var t = File.GetLastWriteTimeUtc(path);
                if (t == statusTime) return;
                var o = JObject.Parse(File.ReadAllText(path));
                statusTime = t;
                string state = (string)o["state"] ?? "idle";
                string text = (string)o["text"];
                switch (state)
                {
                    case "thinking": SetStatus("thinking…", true); break;
                    case "notice": Show(text ?? "", 3f); SetIdle(); break;
                    case "listening": SetStatus("listening… (release V)", true); break;
                    case "transcribing": SetStatus("transcribing…", true); break;
                    case "heard": SetStatus("heard you, thinking…", true); Show("Heard: " + Shorten(text, 140), 5f); break;
                    case "error": SetStatus("error", false); Show("The advisor hit an error (Mods > Options > SHOW COMPANION WINDOW for details)", 5f); break;
                    case "replied":
                        SetIdle();
                        string full = (string)o["full"] ?? text ?? "";
                        string key = (string)o["utc"];
                        if (key == lastReplyShown) break;
                        lastReplyShown = key;
                        string level = (string)o["level"];
                        string via = (string)o["via"] ?? "";
                        // Tips, and answers to questions asked outside the panel, get a card; panel answers are in the panel.
                        if (level != "critical" && !AdvisorPanel.IsOpen && (level != null || via != "panel"))
                            AdvisorCard.Show(level, full, (long?)o["chat"] ?? 0);
                        break;
                    default: SetIdle(); break;
                }
            }
            catch { /* half-written file; next poll */ }
        }

        static void SetIdle()
        {
            if (Briefing.HasUnseen) SetStatus("briefing ready", true, pulse: false);
            else SetStatus("ready", false);
        }

        static void SetStatus(string text, bool highlight, bool pulse = true)
        {
            busy = highlight && pulse;
            if (statusLabel == null) return;
            statusLabel.text = $"ADVISOR  <size=13><color=#{ColorUtility.ToHtmlStringRGB(highlight ? OniStyle.Header : new Color(0.35f, 0.38f, 0.48f))}>· {text}</color></size>";
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
                root.AddComponent<GraphicRaycaster>(); // buttons and cards; toasts let clicks through

                (toastBg, toastText) = Box("toast", new Vector2(0.5f, 1f), new Vector2(0, -70), new Vector2(780, 46), 19, TextAlignmentOptions.Center);
                toastBg.gameObject.SetActive(false);
                MakeButton();
                SetStatus("ready", false);
            }
            catch (System.Exception e)
            {
                Log.Warn("HUD could not be created: " + e.Message);
                if (root != null) Object.Destroy(root);
                root = null;
            }
        }

        /// <summary>The ADVISOR button at the bottom centre: shows the state, opens a waiting briefing or the panel.</summary>
        static void MakeButton()
        {
            var b = OniStyle.MakeButton(root.transform, "ADVISOR", () => { if (Briefing.HasUnseen) Briefing.Toggle(); else AdvisorPanel.Toggle(); }, 300);
            var rt = (RectTransform)b.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 10f);
            rt.sizeDelta = new Vector2(300f, 36f);
            statusBg = b.GetComponent<Image>();
            statusLabel = b.GetComponentInChildren<TextMeshProUGUI>();
            statusLabel.richText = true;
            var colors = b.colors; colors.normalColor = colors.selectedColor = Color.white; b.colors = colors; // the image colour carries the glow
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
            AdvisorCard.Destroy();
            if (root != null) Object.Destroy(root);
            root = null;
            toasts.Clear();
            statusTime = default;
        }
    }
}
