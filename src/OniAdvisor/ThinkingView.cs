using System.IO;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// Shows the advisor's thinking while the player waits (option "Show the advisor's thinking"): the companion
    /// writes live/think.json whenever the advisor queries a place; this circles that place, shows the advisor's
    /// latest thought as a caption above the status badge and, with "captions + camera", glides the camera there
    /// and opens the matching overlay. When the turn ends, the camera and overlay go back to where they were,
    /// unless the player moved the camera in the meantime (then it stops following and leaves it alone).
    /// Briefings keep control of the camera while they play.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class ThinkingView
    {
        static readonly MarkRenderer spot = new MarkRenderer("think");
        static string File => Path.Combine(Paths.LiveDir, "think.json");
        static System.DateTime fileTime;
        static float nextCheck, hideAt = -1f;
        static long seq = -1;

        // The camera as it was before the turn, and where we last sent it.
        static bool moved, playerTookOver;
        static Vector3 homePos, ourTarget;
        static float homeSize, movedAt;
        static HashedString homeOverlay;

        static RectTransform box;
        static TextMeshProUGUI text;

        /// <summary>Every frame (cheap: the file is looked at four times a second).</summary>
        public static void Tick(float now)
        {
            if (hideAt >= 0f && now >= hideAt) { hideAt = -1f; Hide(); }
            if (moved && !playerTookOver && now - movedAt > 1.5f && Camera.main != null)
            {
                var p = Camera.main.transform.position;
                if (Vector2.Distance(new Vector2(p.x, p.y), new Vector2(ourTarget.x, ourTarget.y)) > 4f) playerTookOver = true;
            }
            if (now < nextCheck) return;
            nextCheck = now + 0.25f;
            try
            {
                if (!System.IO.File.Exists(File)) return;
                var t = System.IO.File.GetLastWriteTimeUtc(File);
                if (t == fileTime) return;
                fileTime = t;
                var o = JObject.Parse(System.IO.File.ReadAllText(File));
                long s = (long?)o["seq"] ?? 0;
                if (s == seq) return;
                bool first = seq < 0;
                seq = s;
                if (first && (System.DateTime.UtcNow - t).TotalSeconds > 10) return; // left over from before
                if ((bool?)o["active"] == true) Look(o, now);
                else End(now);
            }
            catch { }
        }

        static void Look(JObject o, float now)
        {
            string mode = Prefs.ShowThinking;
            if (mode == "off") return;
            int x0 = (int?)o["x0"] ?? 0, y0 = (int?)o["y0"] ?? 0, x1 = (int?)o["x1"] ?? x0, y1 = (int?)o["y1"] ?? y0;
            float cx = (Mathf.Min(x0, x1) + Mathf.Max(x0, x1) + 1) / 2f, cy = (Mathf.Min(y0, y1) + Mathf.Max(y0, y1) + 1) / 2f;
            float w = Mathf.Abs(x1 - x0) + 1, h = Mathf.Abs(y1 - y0) + 1;

            spot.Clear();
            spot.BeginReveal();
            spot.Stroke(Sketch.Circle(new Vector2(cx, cy), Mathf.Max(1.5f, w / 2f + 1f), Mathf.Max(1.3f, h / 2f + 1f), Sketch.Rng((int)(cx * 31 + cy))), new Color(0.37f, 0.83f, 0.9f, 0.9f), 0.18f);
            ShowCaption((string)o["text"] ?? "");
            hideAt = -1f;

            if (mode != "camera" || Briefing.IsOpen || playerTookOver || CameraController.Instance == null || Camera.main == null) return;
            if (!moved)
            {
                homePos = CameraController.Instance.transform.position;
                homeSize = Camera.main.orthographicSize;
                homeOverlay = OverlayScreen.Instance != null ? OverlayScreen.Instance.GetMode() : OverlayModes.None.ID;
            }
            float size = Mathf.Clamp(Mathf.Max(w, h) * 0.7f, 8f, 22f);
            ourTarget = new Vector3(cx, cy, homePos.z);
            CameraController.Instance.SetTargetPos(ourTarget, size, false);
            SetOverlay((string)o["overlay"]);
            moved = true;
            movedAt = now;
        }

        static void End(float now)
        {
            hideAt = now + 2.5f; // let the last thought linger a moment
            if (moved && !playerTookOver && !Briefing.IsOpen && CameraController.Instance != null)
            {
                CameraController.Instance.SetTargetPos(homePos, homeSize, false);
                try { if (OverlayScreen.Instance != null && OverlayScreen.Instance.GetMode() != homeOverlay) OverlayScreen.Instance.ToggleOverlay(homeOverlay, false); } catch { }
            }
            moved = false;
            playerTookOver = false;
        }

        static void SetOverlay(string name)
        {
            if (OverlayScreen.Instance == null || string.IsNullOrEmpty(name)) return;
            HashedString id;
            switch (name)
            {
                case "liquid": id = OverlayModes.LiquidConduits.ID; break;
                case "gas": id = OverlayModes.GasConduits.ID; break;
                case "power": id = OverlayModes.Power.ID; break;
                case "rail": id = OverlayModes.SolidConveyor.ID; break;
                case "logic": id = OverlayModes.Logic.ID; break;
                case "oxygen": id = OverlayModes.Oxygen.ID; break;
                case "temperature": id = OverlayModes.Temperature.ID; break;
                default: return;
            }
            try { if (OverlayScreen.Instance.GetMode() != id) OverlayScreen.Instance.ToggleOverlay(id, false); } catch { }
        }

        static void ShowCaption(string s)
        {
            Build();
            if (box == null) return;
            text.text = "<i>" + s.Replace("<", "‹") + "</i>";
            box.gameObject.SetActive(s.Length > 0);
        }

        static void Hide()
        {
            spot.Clear();
            if (box != null) box.gameObject.SetActive(false);
        }

        static void Build()
        {
            if (box != null || AdvisorHud.Canvas == null) return;
            box = OniStyle.Rect("advisor-thinking", AdvisorHud.Canvas);
            box.anchorMin = box.anchorMax = new Vector2(0.5f, 0f);
            box.pivot = new Vector2(0.5f, 0f);
            box.anchoredPosition = new Vector2(0f, 52f);
            box.sizeDelta = new Vector2(760f, 0f);
            OniStyle.Fill(box, new Color(OniStyle.PanelDark.r, OniStyle.PanelDark.g, OniStyle.PanelDark.b, 0.9f), false);
            var v = box.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(14, 14, 6, 7);
            v.childControlWidth = v.childControlHeight = true; v.childForceExpandHeight = false;
            box.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var tr = OniStyle.Rect("text", box);
            text = OniStyle.Label(tr, "", 16, color: OniStyle.TextMuted, align: TextAlignmentOptions.Center);
            text.richText = true;
            box.gameObject.SetActive(false);
        }

        public static void Destroy()
        {
            spot.Clear();
            if (box != null) Object.Destroy(box.gameObject);
            box = null; moved = false; playerTookOver = false; seq = -1; fileTime = default;
        }
    }
}
