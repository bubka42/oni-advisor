using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// The advisor's own in-game window (Ctrl+Shift+T), in ONI's style, with two tabs:
    ///   CHAT      - the conversation (live/chat/chat.json from the companion), the answer being written
    ///               (live/chat/stream.json, with what the advisor is checking), and a box to type in.
    ///               Enter sends; while the box has focus the game's keys are blocked (KScreenManager patch).
    ///   BRIEFINGS - every briefing so far (live/briefing/archive/index.json); a click replays it.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class AdvisorPanel
    {
        static RectTransform panel, chatPage, briefPage, chatList, briefList, streamRow;
        static TextMeshProUGUI streamText, tabChatLabel, tabBriefLabel;
        static TMP_InputField input;
        static ScrollRect chatScroll;
        static bool open, chatTab = true;
        static float nextPoll;
        static System.DateTime chatTime, streamTime, indexTime;
        static long shownUpTo;
        static readonly List<GameObject> chatRows = new List<GameObject>();

        static string ChatDir => Paths.Sub("chat");
        static string ArchiveDir => Path.Combine(Paths.Sub("briefing"), "archive");

        /// <summary>True while the player is typing in the chat box: the game must not see the keys.</summary>
        public static bool Typing => open && input != null && input.isFocused;

        /// <summary>True when the mouse is over the panel (scrolling it must not zoom the camera).</summary>
        public static bool PointerOver
        {
            get
            {
                if (!open || panel == null) return false;
                return RectTransformUtility.RectangleContainsScreenPoint(panel, Input.mousePosition, null);
            }
        }

        public static bool IsOpen => open;

        public static void Toggle()
        {
            if (open) Close(); else Open();
        }

        public static void Open(bool chat = true)
        {
            if (Briefing.IsOpen) Briefing.Close();
            Build();
            if (panel == null) return;
            open = true;
            panel.gameObject.SetActive(true);
            ShowTab(chat);
            chatTime = streamTime = indexTime = default; // reload everything
            nextPoll = 0;
            if (chat) Focus();
            TipBulb.AllSeen(); // the tips are in the chat
        }

        public static void Close()
        {
            if (!open) return;
            open = false;
            if (input != null) input.DeactivateInputField();
            if (UnityEngine.EventSystems.EventSystem.current != null) UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
            if (panel != null) panel.gameObject.SetActive(false);
        }

        static void Focus()
        {
            if (input == null) return;
            input.Select();
            input.ActivateInputField();
        }

        // ------------------------------------------------------------------ every frame

        public static void Tick(float now)
        {
            if (!open) return;
            if (Typing && Input.GetKeyDown(KeyCode.Escape)) { input.DeactivateInputField(); UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); }
            else if (!Typing && chatTab && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))) Focus();
            if (now < nextPoll) return;
            nextPoll = now + 0.3f;
            try
            {
                if (chatTab) { PollChat(); PollStream(); }
                else PollArchive();
            }
            catch (Exception e) { Log.Warn("advisor panel: " + e.Message); }
        }

        static void Send()
        {
            string text = input.text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            input.text = "";
            AdvisorRunner.WriteRequest("chat", null, new JObject { ["text"] = text, ["via"] = "panel" });
            // Show it at once; the companion's copy replaces it on the next chat.json update.
            AddRow("you", text, pending: true);
            Focus();
        }

        // ------------------------------------------------------------------ chat

        static void PollChat()
        {
            var path = Path.Combine(ChatDir, "chat.json");
            if (!File.Exists(path)) return;
            var t = File.GetLastWriteTimeUtc(path);
            if (t == chatTime) return;
            chatTime = t;
            var arr = JArray.Parse(File.ReadAllText(path));
            // Simple and robust: rebuild from the last 60 entries.
            foreach (var go in chatRows) if (go != null) UnityEngine.Object.Destroy(go);
            chatRows.Clear();
            foreach (var e in arr.Skip(Math.Max(0, arr.Count - 60)))
                AddRow((string)e["who"] ?? "note", (string)e["text"] ?? "");
            shownUpTo = arr.Count > 0 ? (long?)arr.Last["n"] ?? 0 : 0;
            if (streamRow != null) streamRow.SetAsLastSibling();
            ScrollToBottom();
        }

        static void PollStream()
        {
            var path = Path.Combine(ChatDir, "stream.json");
            if (!File.Exists(path) || streamRow == null) return;
            var t = File.GetLastWriteTimeUtc(path);
            if (t == streamTime) return;
            streamTime = t;
            var o = JObject.Parse(File.ReadAllText(path));
            string state = (string)o["state"] ?? "idle", text = (string)o["text"] ?? "", activity = (string)o["activity"] ?? "";
            bool stale = (System.DateTime.UtcNow - t).TotalMinutes > 6;
            if (state == "idle" || stale) { streamRow.gameObject.SetActive(false); return; }
            streamRow.gameObject.SetActive(true);
            streamRow.SetAsLastSibling();
            streamText.text = state == "writing" && text.Length > 0
                ? Rich(text) + " <color=#F182B7>▌</color>"
                : $"<i><color=#D2D4EB>{(activity.Length > 0 ? Escape(activity) + "…" : "thinking…")}</color></i>";
            ScrollToBottom();
        }

        static void AddRow(string who, string text, bool pending = false)
        {
            if (chatList == null) return;
            var row = OniStyle.Rect("msg-" + who, chatList);
            var v = row.gameObject.AddComponent<VerticalLayoutGroup>();
            v.childControlHeight = true; v.childControlWidth = true; v.childForceExpandHeight = false;
            switch (who)
            {
                case "you":
                    OniStyle.Fill(row, OniStyle.PanelDark, false);
                    v.padding = new RectOffset(40, 10, 5, 6);
                    Text(row, "<b>You</b>  " + Escape(text), 15, OniStyle.TextMuted);
                    break;
                case "advisor":
                    OniStyle.Fill(row, OniStyle.Row, false);
                    v.padding = new RectOffset(10, 10, 6, 8);
                    Text(row, "<color=#F182B7><b>ADVISOR</b></color>\n" + Rich(text), 15, OniStyle.Text);
                    break;
                case "tip-critical":
                case "tip-urgent":
                case "tip-later":
                    string lvl = who.Substring(4);
                    var tipColor = lvl == "critical" ? new Color(0.62f, 0.27f, 0.3f) : lvl == "urgent" ? new Color(0.62f, 0.45f, 0.25f) : OniStyle.Row;
                    OniStyle.Fill(row, tipColor, false);
                    v.padding = new RectOffset(10, 10, 6, 8);
                    string tag = lvl == "critical" ? "<color=#FF8A80><b>CRITICAL</b></color>" : lvl == "urgent" ? "<color=#FFC069><b>CHECK SOON</b></color>" : "<color=#FFE680><b>TIP</b></color>";
                    Text(row, tag + "\n" + Rich(text), 15, OniStyle.Text);
                    break;
                case "thought":
                    v.padding = new RectOffset(14, 10, 1, 1);
                    Text(row, "<i>" + Rich(text) + "</i>", 13, OniStyle.TextMuted);
                    break;
                case "activity":
                    v.padding = new RectOffset(14, 10, 0, 0);
                    Text(row, "· " + Escape(text), 12, OniStyle.TextMuted);
                    break;
                default: // note
                    v.padding = new RectOffset(10, 10, 2, 2);
                    Text(row, "<color=#F182B7>" + Escape(text) + "</color>", 13, OniStyle.TextMuted);
                    break;
            }
            chatRows.Add(row.gameObject);
            if (streamRow != null) streamRow.SetAsLastSibling();
            ScrollToBottom();
        }

        static void ScrollToBottom()
        {
            if (chatScroll == null) return;
            Canvas.ForceUpdateCanvases();
            chatScroll.verticalNormalizedPosition = 0f;
        }

        // ------------------------------------------------------------------ briefings archive

        static void PollArchive()
        {
            var path = Path.Combine(ArchiveDir, "index.json");
            var t = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : System.DateTime.MinValue;
            if (t == indexTime && briefList.childCount > 0) return;
            indexTime = t;
            for (int i = briefList.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(briefList.GetChild(i).gameObject);
            if (!File.Exists(path))
            {
                var none = OniStyle.Rect("none", briefList);
                Text(none, "No briefings yet. They appear here as the advisor writes them.", 14, OniStyle.TextMuted);
                return;
            }
            foreach (var e in JArray.Parse(File.ReadAllText(path)).Reverse().Take(80))
            {
                string file = (string)e["file"], title = (string)e["title"] ?? "Briefing";
                int cycle = (int?)e["cycle"] ?? 0, n = (int?)e["steps"] ?? 0;
                System.DateTime.TryParse((string)e["utc"], out var utc);
                var row = OniStyle.Rect("brief", briefList);
                var img = OniStyle.Fill(row, OniStyle.Row, true);
                var btn = row.gameObject.AddComponent<Button>();
                btn.targetGraphic = img;
                var colors = btn.colors; colors.highlightedColor = new Color(1.15f, 1.15f, 1.2f); btn.colors = colors;
                string full = Path.Combine(ArchiveDir, file);
                btn.onClick.AddListener(() => { Close(); Briefing.OpenFile(full); });
                var v = row.gameObject.AddComponent<VerticalLayoutGroup>();
                v.padding = new RectOffset(10, 10, 6, 7); v.childControlHeight = true; v.childControlWidth = true; v.childForceExpandHeight = false;
                Text(row, $"<b>{Escape(title)}</b>", 15, OniStyle.Text);
                Text(row, $"Cycle {cycle} · {n} step{(n == 1 ? "" : "s")} · {utc.ToLocalTime():ddd HH:mm}", 12, OniStyle.TextMuted);
            }
        }

        // ------------------------------------------------------------------ building the window

        static void ShowTab(bool chat)
        {
            chatTab = chat;
            chatPage.gameObject.SetActive(chat);
            briefPage.gameObject.SetActive(!chat);
            tabChatLabel.color = chat ? Color.white : OniStyle.ButtonText;
            tabBriefLabel.color = chat ? OniStyle.ButtonText : Color.white;
            indexTime = default;
            nextPoll = 0;
        }

        static void Build()
        {
            if (panel != null || AdvisorHud.Canvas == null) return;
            panel = OniStyle.Rect("advisor-panel", AdvisorHud.Canvas);
            panel.anchorMin = panel.anchorMax = new Vector2(1f, 0.5f);
            panel.pivot = new Vector2(1f, 0.5f);
            panel.anchoredPosition = new Vector2(-16, 20);
            panel.sizeDelta = new Vector2(520, 800);
            OniStyle.Fill(panel, OniStyle.Panel, true);

            // Header: title, tabs, close.
            var header = OniStyle.Rect("header", panel);
            header.anchorMin = new Vector2(0, 1); header.anchorMax = new Vector2(1, 1);
            header.pivot = new Vector2(0.5f, 1); header.sizeDelta = new Vector2(0, 42); header.anchoredPosition = Vector2.zero;
            OniStyle.Fill(header, OniStyle.Header, true);
            var hl = header.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.padding = new RectOffset(14, 8, 6, 6); hl.spacing = 8; hl.childAlignment = TextAnchor.MiddleLeft;
            hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false;
            var title = OniStyle.Rect("title", header);
            title.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            OniStyle.Label(title, "ADVISOR", 20, header: true, align: TextAlignmentOptions.MidlineLeft, wrap: false);
            tabChatLabel = Tab(header, "CHAT", () => ShowTab(true));
            tabBriefLabel = Tab(header, "BRIEFINGS", () => ShowTab(false));
            OniStyle.MakeButton(header, "✕", Close, 34);

            chatPage = Page("chat");
            briefPage = Page("briefings");

            // Chat: list, input, buttons.
            chatList = ScrollList(chatPage, new Vector2(10, 92), new Vector2(-10, -8), out chatScroll);
            streamRow = OniStyle.Rect("stream", chatList);
            var sv = streamRow.gameObject.AddComponent<VerticalLayoutGroup>();
            sv.padding = new RectOffset(10, 10, 6, 8); sv.childControlHeight = true; sv.childControlWidth = true; sv.childForceExpandHeight = false;
            OniStyle.Fill(streamRow, OniStyle.Row, false);
            streamText = Text(streamRow, "", 15, OniStyle.Text);
            streamRow.gameObject.SetActive(false);

            var inputRow = OniStyle.Rect("input-row", chatPage);
            inputRow.anchorMin = new Vector2(0, 0); inputRow.anchorMax = new Vector2(1, 0); inputRow.pivot = new Vector2(0.5f, 0);
            inputRow.sizeDelta = new Vector2(-20, 40); inputRow.anchoredPosition = new Vector2(0, 46);
            var il = inputRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            il.spacing = 6; il.childControlWidth = true; il.childControlHeight = true; il.childForceExpandWidth = false;
            input = InputBox(inputRow);
            OniStyle.MakeButton(inputRow, "SEND", Send, 70);

            var bar = OniStyle.Rect("buttons", chatPage);
            bar.anchorMin = new Vector2(0, 0); bar.anchorMax = new Vector2(1, 0); bar.pivot = new Vector2(0.5f, 0);
            bar.sizeDelta = new Vector2(-20, 32); bar.anchoredPosition = new Vector2(0, 8);
            var bl = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
            bl.spacing = 6; bl.childAlignment = TextAnchor.MiddleCenter; bl.childControlWidth = true; bl.childControlHeight = true; bl.childForceExpandWidth = false;
            OniStyle.MakeButton(bar, "LOOK", () => AdvisorRunner.Instance?.RequestWithScreenshot("hotkey"), 80);
            OniStyle.MakeButton(bar, "BRIEF ME", () => { AdvisorHud.Show("Asking the advisor for a briefing…", 3f); AdvisorRunner.Instance?.RequestBriefing(); }, 100);
            OniStyle.MakeButton(bar, "STOP", () => AdvisorRunner.WriteRequest("stop", null), 70);
            OniStyle.MakeButton(bar, "NEW CHAT", () => AdvisorRunner.WriteRequest("new-chat", null), 100);

            // Briefings: list.
            briefList = ScrollList(briefPage, new Vector2(10, 10), new Vector2(-10, -8), out _);

            panel.gameObject.SetActive(false);
        }

        static RectTransform Page(string name)
        {
            var p = OniStyle.Rect(name, panel);
            p.anchorMin = Vector2.zero; p.anchorMax = Vector2.one;
            p.offsetMin = Vector2.zero; p.offsetMax = new Vector2(0, -42);
            return p;
        }

        static TextMeshProUGUI Tab(RectTransform parent, string text, UnityEngine.Events.UnityAction onClick)
        {
            var b = OniStyle.MakeButton(parent, text, onClick, text.Length * 11 + 24);
            b.GetComponent<Image>().color = OniStyle.HeaderLight;
            return b.GetComponentInChildren<TextMeshProUGUI>();
        }

        static RectTransform ScrollList(RectTransform parent, Vector2 offsetMin, Vector2 offsetMax, out ScrollRect scroll)
        {
            var view = OniStyle.Rect("view", parent);
            view.anchorMin = Vector2.zero; view.anchorMax = Vector2.one;
            view.offsetMin = offsetMin; view.offsetMax = offsetMax;
            OniStyle.Fill(view, OniStyle.PanelDark, true);
            view.gameObject.AddComponent<RectMask2D>();
            var list = OniStyle.Rect("list", view);
            list.anchorMin = new Vector2(0, 1); list.anchorMax = new Vector2(1, 1); list.pivot = new Vector2(0.5f, 1);
            list.anchoredPosition = Vector2.zero; list.sizeDelta = Vector2.zero;
            var vlg = list.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(6, 6, 6, 6); vlg.spacing = 5;
            vlg.childControlHeight = true; vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false; vlg.childForceExpandWidth = true;
            list.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll = view.gameObject.AddComponent<ScrollRect>();
            scroll.content = list; scroll.viewport = view; scroll.horizontal = false; scroll.vertical = true; scroll.scrollSensitivity = 30;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            return list;
        }

        static TMP_InputField InputBox(RectTransform parent)
        {
            var box = OniStyle.Rect("input", parent);
            box.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            var bg = OniStyle.Fill(box, OniStyle.Button, true);
            var area = OniStyle.Rect("text-area", box);
            OniStyle.Stretch(area, 8, 4);
            area.gameObject.AddComponent<RectMask2D>();
            var placeholderRt = OniStyle.Rect("placeholder", area);
            OniStyle.Stretch(placeholderRt);
            var placeholder = OniStyle.Label(placeholderRt, "Ask the advisor…  (Enter sends, Esc leaves the box)", 15, color: new Color(0.24f, 0.26f, 0.34f, 0.6f), align: TextAlignmentOptions.MidlineLeft, wrap: false);
            var textRt = OniStyle.Rect("text", area);
            OniStyle.Stretch(textRt);
            var text = OniStyle.Label(textRt, "", 15, color: OniStyle.ButtonText, align: TextAlignmentOptions.MidlineLeft, wrap: false);
            text.richText = false;
            var field = box.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = bg;
            field.textViewport = area;
            field.textComponent = text;
            field.placeholder = placeholder;
            if (OniStyle.BodyFont != null) field.fontAsset = OniStyle.BodyFont;
            field.pointSize = 15;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.caretColor = OniStyle.ButtonText;
            field.customCaretColor = true;
            field.onSubmit.AddListener(_ => Send());
            return field;
        }

        static TextMeshProUGUI Text(RectTransform parent, string text, float size, Color color)
        {
            var rt = OniStyle.Rect("text", parent);
            var t = OniStyle.Label(rt, text, size, color: color);
            t.richText = true;
            return t;
        }

        // ------------------------------------------------------------------ text

        static string Escape(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");

        /// <summary>The advisor's light Markdown as TextMeshPro rich text: **bold**, *italic*, `code`, headings, bullets.</summary>
        static string Rich(string md)
        {
            var lines = Escape(md).Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                var h = Regex.Match(l, @"^\s*#{1,6}\s+(.*)$");
                if (h.Success) l = "<b><color=#F182B7>" + h.Groups[1].Value + "</color></b>";
                l = Regex.Replace(l, @"^(\s*)[-*•]\s+", "$1• ");
                l = Regex.Replace(l, @"\*\*(.+?)\*\*", "<b>$1</b>");
                l = Regex.Replace(l, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", "<i>$1</i>");
                l = Regex.Replace(l, @"`([^`]+)`", "<color=#D2D4EB>$1</color>");
                lines[i] = l;
            }
            return string.Join("\n", lines);
        }

        public static void Destroy()
        {
            Close();
            if (panel != null) UnityEngine.Object.Destroy(panel.gameObject);
            panel = null; chatList = null; briefList = null; streamRow = null; input = null;
            chatRows.Clear();
        }
    }

    /// <summary>While the player types in the advisor's chat box, the game's keyboard shortcuts stay quiet,
    /// and scrolling over the panel doesn't zoom the camera.</summary>
    [HarmonyLib.HarmonyPatch(typeof(KScreenManager), nameof(KScreenManager.OnKeyDown))]
    internal static class KScreenManager_OnKeyDown_Patch
    {
        static bool Prefix(KButtonEvent e)
        {
            if (AdvisorPanel.Typing) { e.Consumed = true; return false; }
            if (AdvisorPanel.PointerOver && (e.IsAction(Action.ZoomIn) || e.IsAction(Action.ZoomOut))) { e.Consumed = true; return false; }
            return true;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(KScreenManager), nameof(KScreenManager.OnKeyUp))]
    internal static class KScreenManager_OnKeyUp_Patch
    {
        static bool Prefix(KButtonEvent e)
        {
            if (AdvisorPanel.Typing) { e.Consumed = true; return false; }
            return true;
        }
    }
}
