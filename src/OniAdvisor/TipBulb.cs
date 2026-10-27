using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// The advisor's unasked tips as a light bulb hanging from the top of the screen (live/tips.json, written by
    /// the companion). Its colour is the most urgent unseen tip: red = critical (the game pauses at once and the
    /// advisor panel opens), orange = urgent (check soon; the bulb pulses), yellow = for later. Hovering shows
    /// the newest tip; clicking opens the advisor panel and marks them seen (remembered in live/tips-seen.json).
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class TipBulb
    {
        static readonly Color Critical = new Color(1f, 0.33f, 0.3f), Urgent = new Color(1f, 0.62f, 0.2f), Later = new Color(1f, 0.9f, 0.45f);

        static RectTransform root, swing, bulbRt, hoverRt;
        static Image glass, glow;
        static TextMeshProUGUI countText, hoverText;
        static long seenUpTo = -1, alertedUpTo = -1;
        static System.DateTime fileTime;
        static string level, newestText;
        static int unseen;

        static string TipsFile => Path.Combine(Paths.LiveDir, "tips.json");
        static string SeenFile => Path.Combine(Paths.LiveDir, "tips-seen.json");

        /// <summary>About once a second.</summary>
        public static void Poll()
        {
            if (seenUpTo < 0) LoadSeen();
            if (!File.Exists(TipsFile)) return;
            var t = File.GetLastWriteTimeUtc(TipsFile);
            if (t == fileTime) return;
            fileTime = t;
            var tips = JArray.Parse(File.ReadAllText(TipsFile));
            var fresh = tips.Where(x => ((long?)x["n"] ?? 0) > seenUpTo).ToList();
            unseen = fresh.Count;
            level = fresh.Any(x => (string)x["level"] == "critical") ? "critical" : fresh.Any(x => (string)x["level"] == "urgent") ? "urgent" : fresh.Count > 0 ? "later" : null;
            newestText = fresh.Count > 0 ? (string)fresh.Last()["text"] : null;

            // A new critical tip: stop the clock and show it.
            var critical = fresh.LastOrDefault(x => (string)x["level"] == "critical" && ((long?)x["n"] ?? 0) > alertedUpTo);
            long maxN = tips.Count > 0 ? (long?)tips.Last["n"] ?? 0 : 0;
            if (alertedUpTo < 0) alertedUpTo = maxN; // tips from before this session don't pause anything
            else if (critical != null)
            {
                alertedUpTo = maxN;
                try { var speed = SpeedControlScreen.Instance; if (speed != null && !speed.IsPaused) speed.Pause(false, false); } catch { }
                AdvisorHud.Show("ADVISOR: " + Short((string)critical["text"]), 8f);
                AdvisorPanel.Open();
                MarkSeen(maxN);
                return;
            }
            alertedUpTo = Math.Max(alertedUpTo, maxN);
            Refresh();
        }

        /// <summary>Every frame: swing, pulse, hover.</summary>
        public static void Tick(float now)
        {
            if (root == null || !root.gameObject.activeSelf) return;
            swing.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(now * 1.6f) * (level == "critical" ? 9f : 5f));
            float pulse = level == "urgent" || level == "critical" ? 0.5f + 0.5f * Mathf.Sin(now * 5f) : 0.3f;
            var c = glow.color; c.a = 0.15f + 0.35f * pulse; glow.color = c;
            bool hover = RectTransformUtility.RectangleContainsScreenPoint(bulbRt, Input.mousePosition, null);
            if (hoverRt.gameObject.activeSelf != hover) hoverRt.gameObject.SetActive(hover);
        }

        /// <summary>The advisor panel was opened: everything up to now counts as seen.</summary>
        public static void AllSeen()
        {
            try
            {
                if (!File.Exists(TipsFile)) return;
                var tips = JArray.Parse(File.ReadAllText(TipsFile));
                MarkSeen(tips.Count > 0 ? (long?)tips.Last["n"] ?? 0 : 0);
            }
            catch { }
        }

        static void MarkSeen(long n)
        {
            seenUpTo = Math.Max(seenUpTo, n);
            unseen = 0; level = null;
            try { Paths.WriteAtomic(SeenFile, new JObject { ["seenUpTo"] = seenUpTo }.ToString()); } catch { }
            Refresh();
        }

        static void LoadSeen()
        {
            seenUpTo = 0;
            try { if (File.Exists(SeenFile)) seenUpTo = (long?)JObject.Parse(File.ReadAllText(SeenFile))["seenUpTo"] ?? 0; } catch { }
        }

        static void Refresh()
        {
            if (level == null) { if (root != null) root.gameObject.SetActive(false); return; }
            Build();
            if (root == null) return;
            root.gameObject.SetActive(true);
            var col = level == "critical" ? Critical : level == "urgent" ? Urgent : Later;
            glass.color = col;
            glow.color = new Color(col.r, col.g, col.b, 0.3f);
            countText.text = unseen > 1 ? unseen.ToString() : "";
            hoverText.text = $"<b>{(level == "critical" ? "CRITICAL" : level == "urgent" ? "CHECK SOON" : "TIP")}</b>  {Short(newestText, 160)}\n<size=12><color=#D2D4EB>Click to open the advisor</color></size>";
        }

        static string Short(string s, int max = 90)
        {
            s = (s ?? "").Replace("\n", " ").Replace("<", "‹");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        static Sprite circle;
        static Sprite Circle
        {
            get
            {
                if (circle != null) return circle;
                const int N = 64;
                var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(N / 2f, N / 2f)) / (N / 2f);
                        tex.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01((1f - d) * 12f)));
                    }
                tex.Apply();
                circle = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f));
                return circle;
            }
        }

        static void Build()
        {
            if (root != null || AdvisorHud.Canvas == null) return;
            root = OniStyle.Rect("advisor-tip-bulb", AdvisorHud.Canvas);
            root.anchorMin = root.anchorMax = new Vector2(0.62f, 1f);
            root.pivot = new Vector2(0.5f, 1f);
            root.anchoredPosition = Vector2.zero;
            root.sizeDelta = new Vector2(60, 120);

            // Everything below the hook swings.
            swing = OniStyle.Rect("swing", root);
            swing.anchorMin = swing.anchorMax = new Vector2(0.5f, 1f);
            swing.pivot = new Vector2(0.5f, 1f);
            swing.anchoredPosition = Vector2.zero;
            swing.sizeDelta = new Vector2(60, 120);

            var cord = OniStyle.Rect("cord", swing);
            cord.anchorMin = cord.anchorMax = new Vector2(0.5f, 1f); cord.pivot = new Vector2(0.5f, 1f);
            cord.sizeDelta = new Vector2(3, 46); cord.anchoredPosition = Vector2.zero;
            OniStyle.Fill(cord, OniStyle.PanelDark, false);

            var cap = OniStyle.Rect("cap", swing);
            cap.anchorMin = cap.anchorMax = new Vector2(0.5f, 1f); cap.pivot = new Vector2(0.5f, 1f);
            cap.sizeDelta = new Vector2(16, 12); cap.anchoredPosition = new Vector2(0, -44);
            OniStyle.Fill(cap, OniStyle.Row, false);

            var glowRt = OniStyle.Rect("glow", swing);
            glowRt.anchorMin = glowRt.anchorMax = new Vector2(0.5f, 1f); glowRt.pivot = new Vector2(0.5f, 0.5f);
            glowRt.sizeDelta = new Vector2(64, 64); glowRt.anchoredPosition = new Vector2(0, -72);
            glow = glowRt.gameObject.AddComponent<Image>(); glow.sprite = Circle; glow.raycastTarget = false;

            bulbRt = OniStyle.Rect("bulb", swing);
            bulbRt.anchorMin = bulbRt.anchorMax = new Vector2(0.5f, 1f); bulbRt.pivot = new Vector2(0.5f, 0.5f);
            bulbRt.sizeDelta = new Vector2(30, 34); bulbRt.anchoredPosition = new Vector2(0, -70);
            glass = bulbRt.gameObject.AddComponent<Image>(); glass.sprite = Circle; glass.raycastTarget = true;
            var btn = bulbRt.gameObject.AddComponent<Button>();
            btn.targetGraphic = glass;
            btn.onClick.AddListener(() => { AdvisorPanel.Open(); AllSeen(); });

            var ct = OniStyle.Rect("count", bulbRt);
            OniStyle.Stretch(ct);
            countText = OniStyle.Label(ct, "", 14, header: true, color: OniStyle.ButtonText, align: TextAlignmentOptions.Center, wrap: false);

            hoverRt = OniStyle.Rect("hover", root);
            hoverRt.anchorMin = hoverRt.anchorMax = new Vector2(0.5f, 1f); hoverRt.pivot = new Vector2(0.5f, 1f);
            hoverRt.sizeDelta = new Vector2(340, 70); hoverRt.anchoredPosition = new Vector2(0, -96);
            OniStyle.Fill(hoverRt, OniStyle.PanelDark, false);
            var htRt = OniStyle.Rect("text", hoverRt);
            OniStyle.Stretch(htRt, 10, 6);
            hoverText = OniStyle.Label(htRt, "", 14, align: TextAlignmentOptions.TopLeft);
            hoverText.richText = true;
            hoverRt.gameObject.SetActive(false);
            root.gameObject.SetActive(false);
        }

        public static void Destroy()
        {
            if (root != null) UnityEngine.Object.Destroy(root.gameObject);
            root = null;
            fileTime = default; seenUpTo = -1; alertedUpTo = -1; level = null;
        }
    }
}
