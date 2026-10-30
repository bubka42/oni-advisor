using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// The PROJECTS button next to the ADVISOR button, and the list it opens on the left of the screen (on top of
    /// the game's notifications, which is why it stays closed until asked for): collapsible sections ONGOING, PROPOSED, ON HOLD and
    /// DONE; each project folds open to its reason, the advisor's advice, its milestones (each box fills with its
    /// progress and ticks itself) and its buttons (accept / decline, hold / abandon / resume, its blueprint, DISCUSS). Every project
    /// and milestone has its own thread in the advisor panel ("quest:ID", "quest:ID:MILESTONE").
    /// The list closes with Esc or the button; it steps aside while the game's build menu or a building's details
    /// are open (same side of the screen). Both hide during briefings and planning mode, and with nothing to show.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class QuestPanel
    {
        static RectTransform root, content;
        static TextMeshProUGUI headerText;
        static int drawnVersion = -1;
        static bool shown;           // the list is open (the PROJECTS button next to ADVISOR toggles it)
        static Button button;
        static TextMeshProUGUI buttonLabel;
        static readonly HashSet<string> openSections = new HashSet<string> { "accepted", "proposed" };
        static readonly HashSet<string> openQuests = new HashSet<string>();

        static readonly (string status, string title)[] Sections =
        {
            ("accepted", "ONGOING"), ("proposed", "PROPOSED"), ("stalled", "ON HOLD"), ("completed", "DONE"),
        };

        public static void Tick()
        {
            var visible = QuestStore.All.Where(q => Sections.Any(s => s.status == q.Status)).ToList();
            bool any = visible.Count > 0 && !Briefing.IsOpen && !PlanMode.Active;
            if (!any && button == null) return;
            Build();
            if (root == null) return;
            button.gameObject.SetActive(any);
            int ongoing = visible.Count(q => q.Status == "accepted"), proposed = visible.Count(q => q.Status == "proposed");
            buttonLabel.text = $"PROJECTS  <size=15>{ongoing}{(proposed > 0 ? $" <color=#{ColorUtility.ToHtmlStringRGB(OniStyle.Header)}>+{proposed} new</color>" : "")}</size>";
            bool show = any && shown && !GamePanelsOpen();
            root.gameObject.SetActive(show);
            if (!show) return;
            if (drawnVersion == QuestStore.Version) return;
            drawnVersion = QuestStore.Version;
            Redraw(visible);
        }

        /// <summary>
        /// The game's own panels on the left of the screen are open: a build menu category (it opens upwards from the
        /// toolbar at the bottom left) or a selected building's details. The list steps aside while they are.
        /// </summary>
        static bool GamePanelsOpen()
        {
            try
            {
                var plan = PlanScreen.Instance;
                if (plan != null && HarmonyLib.Traverse.Create(plan).Field("activeCategoryInfo").GetValue() != null) return true;
                var details = DetailsScreen.Instance;
                return details != null && details.target != null && details.gameObject.activeInHierarchy;
            }
            catch { return false; }
        }

        static void Redraw(List<QuestStore.Quest> visible)
        {
            for (int i = content.childCount - 1; i >= 0; i--) Object.Destroy(content.GetChild(i).gameObject);
            int ongoing = visible.Count(q => q.Status == "accepted"), proposed = visible.Count(q => q.Status == "proposed");
            headerText.text = $"PROJECTS  <size=14><color=#D2D4EB>{ongoing} ongoing · {proposed} proposed</color></size>";

            foreach (var (status, title) in Sections)
            {
                var list = visible.Where(q => q.Status == status).ToList();
                if (list.Count == 0) continue;
                bool open = openSections.Contains(status);
                string s = status;
                Clickable(content, $"{(open ? "-" : "+")}  {title} ({list.Count})", 17, true, OniStyle.HeaderLight, () => { if (!openSections.Remove(s)) openSections.Add(s); Dirty(); });
                if (!open) continue;
                foreach (var q in list) QuestBlock(q);
            }
        }

        static void QuestBlock(QuestStore.Quest q)
        {
            var block = OniStyle.Rect("quest-" + q.Id, content);
            OniStyle.Fill(block, OniStyle.InkRow, false);
            var v = block.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(8, 8, 5, 6); v.spacing = 3;
            v.childControlWidth = v.childControlHeight = true; v.childForceExpandHeight = false;

            bool open = openQuests.Contains(q.Id);
            string tag = q.Horizon == "long" ? "LONG" : "SHORT";
            string kind = q.Kind == "colony" ? " · COLONY" : "";
            string progress = q.Milestones.Count > 0 ? $"  {Mathf.RoundToInt(QuestStore.Progress(q) * 100)}%" : "";
            string advice = !string.IsNullOrEmpty(q.Advice) ? "  <color=#FFD86B>•</color>" : "";
            Clickable(block, $"{(open ? "-" : "+")} <b>{Esc(q.Title)}</b>{advice}  <size=14><color=#D2D4EB>{tag}{kind}{progress}</color></size>", 18, false, OniStyle.Text,
                () => { if (!openQuests.Remove(q.Id)) openQuests.Add(q.Id); Dirty(); });
            if (!open) return;

            if (!string.IsNullOrEmpty(q.Why)) Text(block, Esc(q.Why), 16, OniStyle.TextMuted);
            if (!string.IsNullOrEmpty(q.Advice)) Text(block, "<color=#FFD86B>Advisor:</color> " + Esc(q.Advice), 16, OniStyle.Text);
            foreach (var m in q.Milestones) MilestoneRow(block, q, m);

            var buttons = Row(block);
            if (q.Pitch != null) Small(buttons, "PITCH >", () => Briefing.PlayPitch(q.Id, q.Title, q.Pitch), 80); // the narrated pitch
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
            var le = box.gameObject.AddComponent<LayoutElement>(); le.preferredWidth = 22; le.preferredHeight = 22;
            // The box fills up with the milestone's progress (it ticks itself; nothing to click).
            OniStyle.Fill(box, OniStyle.Button, false);
            if (m.Done || m.Fraction > 0f)
            {
                var fill = OniStyle.Rect("fill", box);
                fill.anchorMin = Vector2.zero; fill.anchorMax = new Vector2(1f, m.Done ? 1f : Mathf.Clamp01(m.Fraction));
                fill.offsetMin = fill.offsetMax = Vector2.zero;
                OniStyle.Fill(fill, OniStyle.Header, false);
            }
            if (m.Done)
            {
                var tick = OniStyle.Rect("tick", box);
                OniStyle.Stretch(tick);
                OniStyle.Label(tick, "X", 14, header: true, color: Color.white, align: TextAlignmentOptions.Center, wrap: false);
            }
            var tr = OniStyle.Rect("text", row);
            tr.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            string hint = m.Done ? ""
                : m.ProgressText != null ? $"  <size=14><color=#D2D4EB>{Esc(m.ProgressText)}</color></size>"
                : m.Check == null ? "  <size=14><color=#D2D4EB>(the advisor ticks this)</color></size>" : "";
            var t = OniStyle.Label(tr, (m.Done ? "<s>" + Esc(m.Text) + "</s>" : Esc(m.Text)) + hint, 17, color: m.Done ? OniStyle.TextMuted : OniStyle.Text);
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
            // One drawable per object: the label lives in a child, and the row's layout takes its height from it.
            var lay = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            lay.childControlWidth = lay.childControlHeight = true; lay.childForceExpandHeight = false; lay.padding = new RectOffset(0, 0, 1, 1);
            var lt = OniStyle.Rect("label", rt);
            var t = OniStyle.Label(lt, text, size, header: header, color: color, align: TextAlignmentOptions.MidlineLeft);
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
            b.GetComponent<LayoutElement>().preferredHeight = 26;
            var label = b.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.fontSize = 13;
        }

        static void Build()
        {
            if (root != null || AdvisorHud.Canvas == null) return;
            root = OniStyle.Rect("advisor-projects", AdvisorHud.Canvas);
            root.anchorMin = root.anchorMax = new Vector2(0f, 0.5f);
            root.pivot = new Vector2(0f, 0.5f);
            root.anchoredPosition = new Vector2(8f, 40f);
            root.sizeDelta = new Vector2(430f, 600f);
            var v = root.gameObject.AddComponent<VerticalLayoutGroup>();
            v.childControlWidth = v.childControlHeight = true; v.childForceExpandHeight = false; v.childForceExpandWidth = true;
            v.childAlignment = TextAnchor.UpperLeft;

            var header = OniStyle.Rect("header", root);
            header.gameObject.AddComponent<LayoutElement>().preferredHeight = 30;
            var himg = OniStyle.Fill(header, OniStyle.Header, true);
            var hb = header.gameObject.AddComponent<Button>();
            hb.targetGraphic = himg;
            hb.onClick.AddListener(Close);
            var ht = OniStyle.Rect("text", header);
            OniStyle.Stretch(ht, 10, 0);
            headerText = OniStyle.Label(ht, "PROJECTS", 18, header: true, color: Color.white, align: TextAlignmentOptions.MidlineLeft, wrap: false);
            headerText.richText = true;

            var view = OniStyle.Rect("view", root);
            var vle = view.gameObject.AddComponent<LayoutElement>(); vle.flexibleHeight = 1; vle.preferredHeight = 520;
            OniStyle.Fill(view, OniStyle.Ink, true);
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
            root.gameObject.SetActive(false);

            // The button, right of the ADVISOR button at the bottom centre (that one is 300 wide).
            button = OniStyle.MakeButton(AdvisorHud.Canvas, "PROJECTS", Toggle, 210);
            var brt = (RectTransform)button.transform;
            brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0f);
            brt.pivot = new Vector2(0f, 0f);
            brt.anchoredPosition = new Vector2(158f, 10f);
            brt.sizeDelta = new Vector2(210f, 36f);
            buttonLabel = button.GetComponentInChildren<TextMeshProUGUI>();
            buttonLabel.richText = true;
        }

        public static bool IsOpen => root != null && root.gameObject.activeSelf;

        public static void Toggle() { shown = !shown; Dirty(); }

        public static void Close() { shown = false; if (root != null) root.gameObject.SetActive(false); }

        public static bool PointerOver => root != null && root.gameObject.activeSelf && RectTransformUtility.RectangleContainsScreenPoint(root, Input.mousePosition, null);

        public static void Destroy()
        {
            if (root != null) Object.Destroy(root.gameObject);
            if (button != null) Object.Destroy(button.gameObject);
            root = null; button = null; drawnVersion = -1;
        }
    }
}
