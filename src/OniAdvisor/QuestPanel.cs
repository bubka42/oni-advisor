using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// The PROJECTS list on the left edge of the screen: collapsible sections ONGOING, PROPOSED, ON HOLD and
    /// DONE; each project folds open to its reason, the advisor's advice, its milestones (click a box to tick
    /// it) and its buttons (accept / decline, hold / abandon / resume, its blueprint, DISCUSS). Every project
    /// and milestone has its own thread in the advisor panel ("quest:ID", "quest:ID:MILESTONE").
    /// Hidden during briefings and planning mode, and while there is nothing to show.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class QuestPanel
    {
        static RectTransform root, content;
        static TextMeshProUGUI headerText;
        static int drawnVersion = -1;
        static bool collapsed;
        static readonly HashSet<string> openSections = new HashSet<string> { "accepted", "proposed" };
        static readonly HashSet<string> openQuests = new HashSet<string>();

        static readonly (string status, string title)[] Sections =
        {
            ("accepted", "ONGOING"), ("proposed", "PROPOSED"), ("stalled", "ON HOLD"), ("completed", "DONE"),
        };

        public static void Tick()
        {
            var visible = QuestStore.All.Where(q => Sections.Any(s => s.status == q.Status)).ToList();
            bool show = visible.Count > 0 && !Briefing.IsOpen && !PlanMode.Active;
            if (!show) { if (root != null) root.gameObject.SetActive(false); return; }
            Build();
            if (root == null) return;
            root.gameObject.SetActive(true);
            if (drawnVersion == QuestStore.Version) return;
            drawnVersion = QuestStore.Version;
            Redraw(visible);
        }

        static void Redraw(List<QuestStore.Quest> visible)
        {
            for (int i = content.childCount - 1; i >= 0; i--) Object.Destroy(content.GetChild(i).gameObject);
            int ongoing = visible.Count(q => q.Status == "accepted"), proposed = visible.Count(q => q.Status == "proposed");
            headerText.text = $"{(collapsed ? "+" : "-")}  PROJECTS  <size=12><color=#D2D4EB>{ongoing} ongoing · {proposed} proposed</color></size>";
            content.parent.gameObject.SetActive(!collapsed); // the scroll view
            if (collapsed) return;

            foreach (var (status, title) in Sections)
            {
                var list = visible.Where(q => q.Status == status).ToList();
                if (list.Count == 0) continue;
                bool open = openSections.Contains(status);
                string s = status;
                Clickable(content, $"{(open ? "-" : "+")}  {title} ({list.Count})", 14, true, OniStyle.HeaderLight, () => { if (!openSections.Remove(s)) openSections.Add(s); Dirty(); });
                if (!open) continue;
                foreach (var q in list) QuestBlock(q);
            }
        }

        static void QuestBlock(QuestStore.Quest q)
        {
            var block = OniStyle.Rect("quest-" + q.Id, content);
            OniStyle.Fill(block, OniStyle.Row, false);
            var v = block.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(8, 8, 5, 6); v.spacing = 3;
            v.childControlWidth = v.childControlHeight = true; v.childForceExpandHeight = false;

            bool open = openQuests.Contains(q.Id);
            string tag = q.Horizon == "long" ? "LONG" : "SHORT";
            string kind = q.Kind == "colony" ? " · COLONY" : "";
            string progress = q.Milestones.Count > 0 ? $"  {q.DoneCount}/{q.Milestones.Count}" : "";
            string advice = !string.IsNullOrEmpty(q.Advice) ? "  <color=#FFD86B>•</color>" : "";
            Clickable(block, $"{(open ? "-" : "+")} <b>{Esc(q.Title)}</b>{advice}  <size=11><color=#D2D4EB>{tag}{kind}{progress}</color></size>", 14, false, OniStyle.Text,
                () => { if (!openQuests.Remove(q.Id)) openQuests.Add(q.Id); Dirty(); });
            if (!open) return;

            if (!string.IsNullOrEmpty(q.Why)) Text(block, Esc(q.Why), 12, OniStyle.TextMuted);
            if (!string.IsNullOrEmpty(q.Advice)) Text(block, "<color=#FFD86B>Advisor:</color> " + Esc(q.Advice), 12, OniStyle.Text);
            foreach (var m in q.Milestones) MilestoneRow(block, q, m);

            var buttons = Row(block);
            switch (q.Status)
            {
                case "proposed":
                    Small(buttons, "ACCEPT", () => QuestStore.SetStatus(q, "accepted"), 70);
                    Small(buttons, "DECLINE", () => QuestStore.SetStatus(q, "declined"), 74);
                    break;
                case "accepted":
                    Small(buttons, "HOLD", () => QuestStore.SetStatus(q, "stalled"), 54);
                    Small(buttons, "ABANDON", () => QuestStore.SetStatus(q, "abandoned"), 78);
                    break;
                case "stalled":
                    Small(buttons, "RESUME", () => QuestStore.SetStatus(q, "accepted"), 70);
                    Small(buttons, "ABANDON", () => QuestStore.SetStatus(q, "abandoned"), 78);
                    break;
            }
            if (!string.IsNullOrEmpty(q.Project) && q.Status != "completed")
                Small(buttons, "BLUEPRINT", () => AdvisorHud.Show(ProjectStore.Focus(q.Project), 3f), 86);
            Small(buttons, "DISCUSS", () => AdvisorPanel.OpenThread("quest:" + q.Id), 74);
        }

        static void MilestoneRow(RectTransform parent, QuestStore.Quest q, QuestStore.Milestone m)
        {
            var row = Row(parent);
            var box = OniStyle.Rect("check", row);
            var le = box.gameObject.AddComponent<LayoutElement>(); le.preferredWidth = 18; le.preferredHeight = 18;
            var img = OniStyle.Fill(box, m.Done ? OniStyle.Header : OniStyle.Button, true);
            var b = box.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            b.onClick.AddListener(() => QuestStore.ToggleMilestone(q, m));
            if (m.Done)
            {
                var tick = OniStyle.Rect("tick", box);
                OniStyle.Stretch(tick);
                OniStyle.Label(tick, "X", 12, header: true, color: Color.white, align: TextAlignmentOptions.Center, wrap: false);
            }
            var tr = OniStyle.Rect("text", row);
            tr.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            string hint = !m.Done && m.AdvisorThinksDone ? "  <size=11><color=#9FE08C>(looks done: tick it?)</color></size>" : "";
            var t = OniStyle.Label(tr, (m.Done ? "<s>" + Esc(m.Text) + "</s>" : Esc(m.Text)) + hint, 13, color: m.Done ? OniStyle.TextMuted : OniStyle.Text);
            t.richText = true;
            Small(row, "…", () => AdvisorPanel.OpenThread($"quest:{q.Id}:{m.Id}"), 26); // the milestone's own thread
        }

        // ------------------------------------------------------------------ pieces

        static void Dirty() => drawnVersion = -1;

        static string Esc(string s) => (s ?? "").Replace("<", "‹");

        static void Clickable(RectTransform parent, string text, float size, bool header, Color color, UnityEngine.Events.UnityAction onClick)
        {
            var rt = OniStyle.Rect("clickable", parent);
            var img = OniStyle.Fill(rt, new Color(0, 0, 0, 0.001f), true);
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            b.onClick.AddListener(onClick);
            var t = OniStyle.Label(rt, text, size, header: header, color: color, align: TextAlignmentOptions.MidlineLeft);
            t.richText = true;
        }

        static void Text(RectTransform parent, string text, float size, Color color)
        {
            var rt = OniStyle.Rect("text", parent);
            var t = OniStyle.Label(rt, text, size, color: color);
            t.richText = true;
        }

        static RectTransform Row(RectTransform parent)
        {
            var row = OniStyle.Rect("row", parent);
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 5; h.childAlignment = TextAnchor.MiddleLeft; h.childControlWidth = h.childControlHeight = true; h.childForceExpandWidth = false;
            return row;
        }

        static void Small(RectTransform parent, string text, UnityEngine.Events.UnityAction onClick, float width)
        {
            var b = OniStyle.MakeButton(parent, text, onClick, width);
            b.GetComponent<LayoutElement>().preferredHeight = 22;
            var label = b.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.fontSize = 11;
        }

        static void Build()
        {
            if (root != null || AdvisorHud.Canvas == null) return;
            root = OniStyle.Rect("advisor-projects", AdvisorHud.Canvas);
            root.anchorMin = root.anchorMax = new Vector2(0f, 0.5f);
            root.pivot = new Vector2(0f, 0.5f);
            root.anchoredPosition = new Vector2(8f, 40f);
            root.sizeDelta = new Vector2(340f, 560f);
            var v = root.gameObject.AddComponent<VerticalLayoutGroup>();
            v.childControlWidth = v.childControlHeight = true; v.childForceExpandHeight = false; v.childForceExpandWidth = true;
            v.childAlignment = TextAnchor.UpperLeft;

            var header = OniStyle.Rect("header", root);
            header.gameObject.AddComponent<LayoutElement>().preferredHeight = 30;
            var himg = OniStyle.Fill(header, OniStyle.Header, true);
            var hb = header.gameObject.AddComponent<Button>();
            hb.targetGraphic = himg;
            hb.onClick.AddListener(() => { collapsed = !collapsed; Dirty(); });
            var ht = OniStyle.Rect("text", header);
            OniStyle.Stretch(ht, 10, 0);
            headerText = OniStyle.Label(ht, "PROJECTS", 16, header: true, color: Color.white, align: TextAlignmentOptions.MidlineLeft, wrap: false);
            headerText.richText = true;

            var view = OniStyle.Rect("view", root);
            var vle = view.gameObject.AddComponent<LayoutElement>(); vle.flexibleHeight = 1; vle.preferredHeight = 520;
            OniStyle.Fill(view, OniStyle.PanelDark, true);
            view.gameObject.AddComponent<RectMask2D>();
            content = OniStyle.Rect("content", view);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0.5f, 1);
            content.anchoredPosition = Vector2.zero; content.sizeDelta = Vector2.zero;
            var cv = content.gameObject.AddComponent<VerticalLayoutGroup>();
            cv.padding = new RectOffset(6, 6, 6, 6); cv.spacing = 4;
            cv.childControlWidth = cv.childControlHeight = true; cv.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scroll = view.gameObject.AddComponent<ScrollRect>();
            scroll.content = content; scroll.viewport = view; scroll.horizontal = false; scroll.vertical = true; scroll.scrollSensitivity = 25;
        }

        public static bool PointerOver => root != null && root.gameObject.activeSelf && RectTransformUtility.RectangleContainsScreenPoint(root, Input.mousePosition, null);

        public static void Destroy()
        {
            if (root != null) Object.Destroy(root.gameObject);
            root = null; drawnVersion = -1;
        }
    }
}
