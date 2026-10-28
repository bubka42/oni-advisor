using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// A tip, or an answer to a question asked outside the panel (voice, the look hotkey, guided planning), in
    /// exactly the briefing caption's style (BriefingStage): the pink title bar in the game's header font, a
    /// slate panel with large text that types itself out (at the voice's pace when it is read aloud). Urgency
    /// is a small coloured square in the title (orange: check soon, yellow: a tip for later). REPLY (its thread
    /// in the panel) and CLOSE are quiet text links in the bar. It closes by itself after a reading time unless
    /// the pointer is on it. Critical tips don't use it: they pause the game and open the panel (TipBulb).
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class AdvisorCard
    {
        static RectTransform card;
        static TextMeshProUGUI title, body;
        static Image square; // urgency: a small box before the title (the game font may lack ■)
        static long chatEntry;
        static float closeAt = -1f, shownAt, charsPerSecond;

        public static void Show(string level, string text, long chat, bool spoken = true)
        {
            Build();
            if (card == null) return;
            chatEntry = chat;
            string what = level == "urgent" ? "CHECK SOON" : level == "later" ? "TIP" : "ADVISOR";
            title.text = what;
            square.gameObject.SetActive(level == "urgent" || level == "later");
            square.color = level == "urgent" ? new Color(1f, 0.6f, 0.24f) : new Color(1f, 0.85f, 0.42f);
            body.text = AdvisorPanel.Rich(text ?? "");
            body.maxVisibleCharacters = 0;
            shownAt = Time.unscaledTime;
            charsPerSecond = spoken ? 17f : 60f; // read aloud: keep pace with the voice
            card.gameObject.SetActive(true);
            int words = (text ?? "").Split(' ').Length;
            closeAt = Time.unscaledTime + Mathf.Clamp(10f + words / 2.2f, 14f, 75f); // roughly reading (or listening) time
        }

        /// <summary>Every frame: type out, and close after the reading time (never under the pointer).</summary>
        public static void Tick()
        {
            if (card == null || !card.gameObject.activeSelf) return;
            body.maxVisibleCharacters = (int)((Time.unscaledTime - shownAt) * charsPerSecond);
            if (closeAt < 0f) return;
            if (RectTransformUtility.RectangleContainsScreenPoint(card, Input.mousePosition, null)) { closeAt = Mathf.Max(closeAt, Time.unscaledTime + 4f); return; }
            if (Time.unscaledTime >= closeAt || Briefing.IsOpen || AdvisorPanel.IsOpen) Close();
        }

        static void Close()
        {
            closeAt = -1f;
            if (card != null) card.gameObject.SetActive(false);
        }

        static void Build()
        {
            if (card != null || AdvisorHud.Canvas == null) return;
            card = OniStyle.Rect("advisor-card", AdvisorHud.Canvas);
            card.anchorMin = card.anchorMax = new Vector2(0.5f, 0f);
            card.pivot = new Vector2(0.5f, 0f);
            card.anchoredPosition = new Vector2(0f, 60f);
            card.sizeDelta = new Vector2(1000f, 0f);
            var v = card.gameObject.AddComponent<VerticalLayoutGroup>();
            v.childControlWidth = v.childControlHeight = true; v.childForceExpandHeight = false; v.childForceExpandWidth = true;
            card.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Title bar: as the briefing caption's, with two quiet links on the right.
            var bar = OniStyle.Rect("title-bar", card);
            OniStyle.Fill(bar, OniStyle.Header, true);
            var bl = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
            bl.padding = new RectOffset(18, 14, 5, 4); bl.spacing = 18;
            bl.childControlWidth = bl.childControlHeight = true; bl.childForceExpandWidth = false; bl.childAlignment = TextAnchor.MiddleLeft;
            var sq = OniStyle.Rect("urgency", bar);
            var sle = sq.gameObject.AddComponent<LayoutElement>(); sle.preferredWidth = 12; sle.preferredHeight = 12;
            square = OniStyle.Fill(sq, Color.white, false);
            var tr = OniStyle.Rect("title", bar);
            tr.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            title = OniStyle.Label(tr, "", 20, header: true, color: Color.white, align: TextAlignmentOptions.MidlineLeft, wrap: false);
            title.richText = true;
            Link(bar, "REPLY", () => { Close(); if (chatEntry > 0) AdvisorPanel.OpenThread(chatEntry.ToString()); else AdvisorPanel.Open(); });
            Link(bar, "CLOSE", Close);

            // Body: as the caption's.
            var bodyRt = OniStyle.Rect("body", card);
            OniStyle.Fill(bodyRt, OniStyle.Panel, true);
            var bv = bodyRt.gameObject.AddComponent<VerticalLayoutGroup>();
            bv.padding = new RectOffset(22, 22, 12, 16);
            bv.childControlWidth = bv.childControlHeight = true; bv.childForceExpandHeight = false;
            var xr = OniStyle.Rect("text", bodyRt);
            body = OniStyle.Label(xr, "", 24, color: Color.white, align: TextAlignmentOptions.TopLeft);
            body.richText = true;
            card.gameObject.SetActive(false);
        }

        /// <summary>A text link in the title bar: white, a little transparent, brighter on hover.</summary>
        static void Link(RectTransform parent, string text, UnityEngine.Events.UnityAction onClick)
        {
            var rt = OniStyle.Rect("link-" + text, parent);
            var img = OniStyle.Fill(rt, new Color(1, 1, 1, 0.001f), true);
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            b.onClick.AddListener(onClick);
            var lay = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            lay.childControlWidth = lay.childControlHeight = true; lay.padding = new RectOffset(4, 4, 0, 0);
            var lt = OniStyle.Rect("label", rt);
            var label = OniStyle.Label(lt, text, 14, header: true, color: new Color(1f, 1f, 1f, 0.75f), align: TextAlignmentOptions.Midline, wrap: false);
            var colors = b.colors; colors.highlightedColor = new Color(1f, 1f, 1f, 0.25f); b.colors = colors;
        }

        public static void Destroy()
        {
            if (card != null) Object.Destroy(card.gameObject);
            card = null; closeAt = -1f;
        }
    }
}
