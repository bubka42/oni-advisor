using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// A tip, or an answer to a question asked outside the panel (voice, the look hotkey, guided planning), in
    /// the briefing caption's style just above the ADVISOR button: a title bar coloured by urgency (TIP /
    /// CHECK SOON / ADVISOR) over a slate panel with the whole text, and REPLY (its thread in the panel) /
    /// CLOSE. It closes by itself after a reading time unless the pointer is on it. Critical tips don't use it:
    /// they pause the game and open the panel (TipBulb).
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class AdvisorCard
    {
        static RectTransform card;
        static Image bar;
        static TextMeshProUGUI title, body;
        static long chatEntry;
        static float closeAt = -1f;

        public static void Show(string level, string text, long chat)
        {
            Build();
            if (card == null) return;
            chatEntry = chat;
            string heading = level == "urgent" ? "CHECK SOON" : level == "later" ? "TIP" : "ADVISOR";
            bar.color = level == "urgent" ? new Color(1f, 0.55f, 0.2f) : level == "later" ? new Color(0.85f, 0.7f, 0.25f) : OniStyle.Header;
            title.text = heading;
            body.text = AdvisorPanel.Rich(text ?? "");
            card.gameObject.SetActive(true);
            int words = (text ?? "").Split(' ').Length;
            closeAt = Time.unscaledTime + Mathf.Clamp(8f + words / 2.5f, 12f, 60f); // roughly reading time
        }

        /// <summary>Every frame: close after the reading time, but never under the pointer.</summary>
        public static void Tick()
        {
            if (card == null || !card.gameObject.activeSelf || closeAt < 0f) return;
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
            card.anchoredPosition = new Vector2(0f, 56f);
            card.sizeDelta = new Vector2(820f, 0f);
            var v = card.gameObject.AddComponent<VerticalLayoutGroup>();
            v.childControlWidth = v.childControlHeight = true; v.childForceExpandHeight = false; v.childForceExpandWidth = true;
            card.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var barRt = OniStyle.Rect("title-bar", card);
            bar = OniStyle.Fill(barRt, OniStyle.Header, true);
            var bl = barRt.gameObject.AddComponent<HorizontalLayoutGroup>();
            bl.padding = new RectOffset(16, 8, 4, 4); bl.spacing = 6;
            bl.childControlWidth = bl.childControlHeight = true; bl.childForceExpandWidth = false; bl.childAlignment = TextAnchor.MiddleLeft;
            var tr = OniStyle.Rect("title", barRt);
            tr.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            title = OniStyle.Label(tr, "", 18, header: true, color: Color.white, align: TextAlignmentOptions.MidlineLeft, wrap: false);
            Small(barRt, "REPLY", () => { Close(); if (chatEntry > 0) AdvisorPanel.OpenThread(chatEntry.ToString()); else AdvisorPanel.Open(); }, 80);
            Small(barRt, "CLOSE", Close, 80);

            var bodyRt = OniStyle.Rect("body", card);
            OniStyle.Fill(bodyRt, OniStyle.Panel, true);
            var bv = bodyRt.gameObject.AddComponent<VerticalLayoutGroup>();
            bv.padding = new RectOffset(18, 18, 10, 14);
            bv.childControlWidth = bv.childControlHeight = true; bv.childForceExpandHeight = false;
            var xr = OniStyle.Rect("text", bodyRt);
            body = OniStyle.Label(xr, "", 19, color: Color.white, align: TextAlignmentOptions.TopLeft);
            body.richText = true;
            card.gameObject.SetActive(false);
        }

        static void Small(RectTransform parent, string text, UnityEngine.Events.UnityAction onClick, float width)
        {
            var b = OniStyle.MakeButton(parent, text, onClick, width);
            b.GetComponent<LayoutElement>().preferredHeight = 26;
            var label = b.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.fontSize = 13;
        }

        public static void Destroy()
        {
            if (card != null) Object.Destroy(card.gameObject);
            card = null; closeAt = -1f;
        }
    }
}
