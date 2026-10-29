using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// The advisor's own in-game window (Ctrl+Shift+T), in ONI's style, like a one-on-one Slack conversation:
    ///   CHAT      - the main channel (live/chat/chat.json from the companion). Every tip, briefing and answer can
    ///               get a THREAD underneath it for clarifications: REPLY opens it in place, with its own box;
    ///               it's the same advisor conversation, but the advisor answers within that topic.
    ///               The answer being written and what the advisor is checking come from live/chat/stream.json.
    ///               Enter sends; while the box has focus the game's keys are blocked (KScreenManager patch).
    ///   BRIEFINGS - every briefing so far (live/briefing/archive/index.json): replay it, or discuss it.
    ///   PLAN      - the advisor's long-term colony plan (live/colony-plan.md), with its own thread.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class AdvisorPanel
    {
        enum Page { Chat, Briefings, Plan }

        static RectTransform panel, chatPage, briefPage, planPage, chatList, briefList, planList, streamRow, threadBar;
        static TextMeshProUGUI streamText, threadTitle, planText;
        static RectTransform projectsBox;
        static TextMeshProUGUI eagerLabel;
        static string eagerShown;
        static string projectsKey;
        static readonly Dictionary<Page, TextMeshProUGUI> tabLabels = new Dictionary<Page, TextMeshProUGUI>();
        static TMP_InputField input;
        static ScrollRect chatScroll;
        static bool open;
        static Page page = Page.Chat;
        static string thread; // null = main channel
        static float nextPoll;
        static System.DateTime chatTime, streamTime, indexTime, planTime;
        static readonly List<GameObject> chatRows = new List<GameObject>();

        static string ChatDir => Paths.Sub("chat");
        static string ArchiveDir => Path.Combine(Paths.Sub("briefing"), "archive");
        static string PlanFile => Path.Combine(Paths.LiveDir, "colony-plan.md");

        /// <summary>True while the player is typing in the chat box: the game must not see the keys.</summary>
        public static bool Typing => open && input != null && input.isFocused;

        static int lastTypingFrame = -10;

        /// <summary>The player was typing a frame ago: the Esc that left the box must not also close the panel.</summary>
        public static bool JustLeftTyping => Time.frameCount - lastTypingFrame <= 1;

        /// <summary>True when the mouse is over the panel (scrolling it must not zoom the camera).</summary>
        public static bool PointerOver => open && panel != null && RectTransformUtility.RectangleContainsScreenPoint(panel, Input.mousePosition, null);

        public static bool IsOpen => open;

        public static void Toggle()
        {
            if (open) Close(); else Open();
        }

        public static void Open(bool chat = true) => Show(chat ? Page.Chat : Page.Briefings, null);

        /// <summary>Opens the main chat with the input box already started (e.g. "About the pin ...: ").</summary>
        public static void OpenWithText(string text)
        {
            Show(Page.Chat, null);
            if (input == null) return;
            input.text = text;
            input.caretPosition = text.Length;
        }

        /// <summary>Opens the chat on a thread (a tip's, a briefing's, an answer's, or "plan").</summary>
        public static void OpenThread(string id) => Show(Page.Chat, id);

        static void Show(Page p, string threadId)
        {
            if (Briefing.IsOpen) Briefing.Close();
            Build();
            if (panel == null) return;
            open = true;
            panel.gameObject.SetActive(true);
            thread = threadId;
            ShowPage(p);
            if (p == Page.Chat) Focus();
            TipBulb.AllSeen(); // the tips are in the chat
            if (eagerLabel != null) eagerLabel.text = EagernessLabel(Prefs.Eagerness); // it may have changed in the options
        }

        public static void Close()
        {
            if (!open) return;
            open = false;
            if (input != null) input.DeactivateInputField();
            UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null);
            if (panel != null) panel.gameObject.SetActive(false);
        }

        static void Focus()
        {
            if (input == null) return;
            input.Select();
            input.ActivateInputField();
        }

        /// <summary>The thread of the briefing with this id (its entry in the chat), from the archive index.</summary>
        public static string ThreadForBriefing(string briefingId)
        {
            try
            {
                var path = Path.Combine(ArchiveDir, "index.json");
                if (!File.Exists(path)) return null;
                var e = JArray.Parse(File.ReadAllText(path)).LastOrDefault(x => (string)x["id"] == briefingId && x["chat"] != null);
                return e != null ? ((long)e["chat"]).ToString() : null;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ every frame

        public static void Tick(float now)
        {
            if (!open) return;
            if (Typing) lastTypingFrame = Time.frameCount;
            if (Typing && Input.GetKeyDown(KeyCode.Escape)) { input.DeactivateInputField(); UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null); }
            else if (!Typing && page == Page.Chat && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))) Focus();
            if (now < nextPoll) return;
            nextPoll = now + 0.3f;
            try
            {
                if (page == Page.Chat) { PollChat(); PollStream(); }
                else if (page == Page.Briefings) PollArchive();
                else PollPlan();
            }
            catch (Exception e) { Log.Warn("advisor panel: " + e.Message); }
        }

        static void Send()
        {
            string text = input.text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            input.text = "";
            var extra = new JObject { ["text"] = text, ["via"] = "panel" };
            if (thread != null) extra["thread"] = thread;
            AdvisorRunner.WriteRequest("chat", null, extra);
            AddRow(new JObject { ["who"] = "you", ["text"] = text }, 0, false); // shown at once; replaced on the next chat.json update
            Focus();
        }

        // ------------------------------------------------------------------ chat and threads

        static void PollChat()
        {
            var path = Path.Combine(ChatDir, "chat.json");
            if (!File.Exists(path)) return;
            var t = File.GetLastWriteTimeUtc(path);
            if (t == chatTime) return;
            chatTime = t;
            var all = JArray.Parse(File.ReadAllText(path)).Cast<JObject>().ToList();
            var replies = all.Where(e => e["thread"] != null && ((string)e["who"] == "you" || (string)e["who"] == "advisor"))
                             .GroupBy(e => (string)e["thread"]).ToDictionary(g => g.Key, g => g.Count());

            foreach (var go in chatRows) if (go != null) UnityEngine.Object.Destroy(go);
            chatRows.Clear();

            if (thread == null)
            {
                threadBar.gameObject.SetActive(false);
                foreach (var e in all.Where(e => e["thread"] == null).Skip(Math.Max(0, all.Count(x => x["thread"] == null) - 60)))
                {
                    string id = ((long?)e["n"] ?? 0).ToString();
                    AddRow(e, replies.TryGetValue(id, out int k) ? k : 0, threadable: true);
                }
            }
            else
            {
                threadBar.gameObject.SetActive(true);
                if (thread == "plan")
                {
                    threadTitle.text = "THREAD · COLONY PLAN";
                    AddRow(new JObject { ["who"] = "note", ["text"] = "Discussing the long-term plan (PLAN tab). Ask about goals, order, alternatives." }, 0, false);
                }
                else if (thread.StartsWith("quest:"))
                {
                    threadTitle.text = "THREAD · " + Short(QuestStore.ThreadTitle(thread), 48).ToUpperInvariant();
                    AddRow(new JObject { ["who"] = "note", ["text"] = thread.Split(':').Length > 2
                        ? "Discussing this milestone: how to do it, whether it is done, what it needs."
                        : "Discussing this project: whether to take it on, how to go about it, what is left." }, 0, false);
                }
                else
                {
                    var root = all.FirstOrDefault(e => ((long?)e["n"] ?? 0).ToString() == thread);
                    threadTitle.text = "THREAD · " + (root != null ? Short(Plain((string)root["text"]), 48).ToUpperInvariant() : "EARLIER MESSAGE");
                    if (root != null) AddRow(root, 0, threadable: false);
                }
                foreach (var e in all.Where(e => (string)e["thread"] == thread)) AddRow(e, 0, threadable: false);
            }
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
            string state = (string)o["state"] ?? "idle", text = (string)o["text"] ?? "", activity = (string)o["activity"] ?? "", inThread = (string)o["thread"];
            bool stale = (System.DateTime.UtcNow - t).TotalMinutes > 6;
            if (state == "idle" || stale || inThread != thread) { streamRow.gameObject.SetActive(false); return; }
            streamRow.gameObject.SetActive(true);
            streamRow.SetAsLastSibling();
            streamText.text = state == "writing" && text.Length > 0
                ? Rich(text) + " <color=#F182B7>▌</color>"
                : $"<i><color=#D2D4EB>{(activity.Length > 0 ? Escape(activity) + "…" : "thinking…")}</color></i>";
            ScrollToBottom();
        }

        static void AddRow(JObject e, int replyCount, bool threadable)
        {
            if (chatList == null) return;
            string who = (string)e["who"] ?? "note", text = (string)e["text"] ?? "", id = ((long?)e["n"] ?? 0).ToString(), briefing = (string)e["briefing"];
            var row = OniStyle.Rect("msg-" + who, chatList);
            var v = row.gameObject.AddComponent<VerticalLayoutGroup>();
            v.childControlHeight = true; v.childControlWidth = true; v.childForceExpandHeight = false; v.spacing = 3;
            bool canThread = false;
            switch (who)
            {
                case "you":
                    OniStyle.Fill(row, OniStyle.InkRowLight, false);
                    v.padding = new RectOffset(40, 10, 5, 6);
                    Text(row, "<b>You</b>  " + Escape(text), 15, OniStyle.TextMuted);
                    break;
                case "advisor":
                    OniStyle.Fill(row, OniStyle.InkRow, false);
                    v.padding = new RectOffset(10, 10, 6, 6);
                    Text(row, "<color=#F182B7><b>ADVISOR</b></color>\n" + Rich(text), 15, OniStyle.Text);
                    canThread = true;
                    break;
                case "tip-critical":
                case "tip-urgent":
                case "tip-later":
                    string lvl = who.Substring(4);
                    OniStyle.Fill(row, lvl == "critical" ? new Color(0.45f, 0.17f, 0.19f) : lvl == "urgent" ? new Color(0.44f, 0.3f, 0.14f) : OniStyle.InkRow, false);
                    v.padding = new RectOffset(10, 10, 6, 6);
                    string tag = lvl == "critical" ? "<color=#FF8A80><b>CRITICAL</b></color>" : lvl == "urgent" ? "<color=#FFC069><b>CHECK SOON</b></color>" : "<color=#FFE680><b>TIP</b></color>";
                    Text(row, tag + "\n" + Rich(text), 15, OniStyle.Text);
                    canThread = true;
                    break;
                case "briefing":
                    OniStyle.Fill(row, OniStyle.InkRowLight, false);
                    v.padding = new RectOffset(10, 10, 6, 6);
                    Text(row, "<color=#F182B7><b>BRIEFING</b></color>\n" + Rich(text), 15, OniStyle.Text);
                    canThread = true;
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

            // Buttons under a message: open its briefing, open its thread.
            if ((canThread && threadable) || briefing != null)
            {
                var bar = OniStyle.Rect("actions", row);
                var hl = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
                hl.spacing = 6; hl.childAlignment = TextAnchor.MiddleLeft; hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false;
                if (briefing != null)
                {
                    string full = Path.Combine(ArchiveDir, briefing);
                    SmallButton(bar, "OPEN", () => { Close(); Briefing.OpenFile(full); }, 70);
                }
                if (canThread && threadable)
                    SmallButton(bar, replyCount == 0 ? "REPLY" : replyCount == 1 ? "1 REPLY" : $"{replyCount} REPLIES", () => OpenThread(id), 100);
            }
            chatRows.Add(row.gameObject);
            if (streamRow != null) streamRow.SetAsLastSibling();
        }

        static void SmallButton(RectTransform parent, string text, UnityEngine.Events.UnityAction onClick, float width)
        {
            var b = OniStyle.MakeButton(parent, text, onClick, width);
            var le = b.GetComponent<LayoutElement>();
            le.preferredHeight = 24;
            var label = b.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.fontSize = 13;
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
                long? chatN = (long?)e["chat"];
                System.DateTime.TryParse((string)e["utc"], out var utc);
                var row = OniStyle.Rect("brief", briefList);
                OniStyle.Fill(row, OniStyle.Row, false);
                var v = row.gameObject.AddComponent<VerticalLayoutGroup>();
                v.padding = new RectOffset(10, 10, 6, 7); v.spacing = 3; v.childControlHeight = true; v.childControlWidth = true; v.childForceExpandHeight = false;
                Text(row, $"<b>{Escape(title)}</b>", 15, OniStyle.Text);
                Text(row, $"Cycle {cycle} · {n} step{(n == 1 ? "" : "s")} · {utc.ToLocalTime():ddd HH:mm}", 12, OniStyle.TextMuted);
                var bar = OniStyle.Rect("actions", row);
                var hl = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
                hl.spacing = 6; hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false;
                string full = Path.Combine(ArchiveDir, file);
                SmallButton(bar, "REPLAY", () => { Close(); Briefing.OpenFile(full); }, 90);
                if (chatN != null) SmallButton(bar, "DISCUSS", () => OpenThread(chatN.Value.ToString()), 100);
            }
        }

        // ------------------------------------------------------------------ plan

        // ------------------------------------------------------------------ eagerness

        static readonly string[] EagernessOrder = { "crisis", "normal", "longterm", "asked" };

        static string EagernessLabel(string e) => "SPEAKS UP: " + (e == "crisis" ? "CRISIS" : e == "longterm" ? "LONG-TERM" : e == "asked" ? "WHEN ASKED" : "NORMAL");

        /// <summary>
        /// How eagerly the advisor speaks up unasked, changed in the game: crisis (comments on new events every
        /// minute), normal, long-term (only serious events; works on the plan when calm), when asked (only serious
        /// events). Saved to companion.json; the companion takes it at once.
        /// </summary>
        static void CycleEagerness()
        {
            string cur = Prefs.Eagerness;
            int i = Array.IndexOf(EagernessOrder, cur);
            string next = EagernessOrder[(i + 1) % EagernessOrder.Length];
            ModOptions.WriteSetting("Eagerness", next);
            ModOptions.Request("settings", null);
            eagerShown = next;
            if (eagerLabel != null) eagerLabel.text = EagernessLabel(next);
            AdvisorHud.Show(next == "crisis" ? "Advisor: crisis mode, it comments on new events every minute"
                : next == "longterm" ? "Advisor: long-term mode, only emergencies unasked; it works on the plan"
                : next == "asked" ? "Advisor: speaks when asked (and in emergencies)"
                : "Advisor: normal, a comment on new events at most every 5 minutes", 4f);
        }

        /// <summary>
        /// The advisor's projects above the plan, each with SHOW (its ghost on the map), STAMP (carry it on the
        /// cursor and place copies anywhere) and PLACE (build orders exactly where it was planned).
        /// </summary>
        static void PollProjects()
        {
            if (projectsBox == null) return;
            var list = ProjectStore.List();
            string key = string.Join("|", list.Select(p => p.id + p.progress)) + BuildPlacer.WaitingCount;
            if (key == projectsKey) return;
            projectsKey = key;
            for (int i = projectsBox.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(projectsBox.GetChild(i).gameObject);
            if (list.Count == 0) return;
            Text(projectsBox, "<b>PROJECTS</b>" + (BuildPlacer.WaitingCount > 0 ? $"  <size=12><color=#D2D4EB>{BuildPlacer.WaitingCount} piece(s) waiting to be placed</color></size>" : ""), 14, OniStyle.HeaderLight);
            foreach (var (id, name, progress) in list)
            {
                var row = OniStyle.Rect("project-" + id, projectsBox);
                OniStyle.Fill(row, OniStyle.Row, false);
                var rv = row.gameObject.AddComponent<VerticalLayoutGroup>();
                rv.padding = new RectOffset(8, 8, 5, 6); rv.spacing = 3; rv.childControlHeight = true; rv.childControlWidth = true; rv.childForceExpandHeight = false;
                Text(row, $"<b>{Escape(name)}</b>  <size=12><color=#D2D4EB>{Escape(progress)}</color></size>", 14, OniStyle.Text);
                var buttons = OniStyle.Rect("buttons", row);
                var bh = buttons.gameObject.AddComponent<HorizontalLayoutGroup>();
                bh.spacing = 6; bh.childControlWidth = true; bh.childControlHeight = true; bh.childForceExpandWidth = false;
                string pid = id;
                SmallButton(buttons, "SHOW", () => AdvisorHud.Show(ProjectStore.Focus(pid), 3f), 64);
                SmallButton(buttons, "STAMP", () => { if (StampTool.Begin(pid)) Close(); }, 70);
                SmallButton(buttons, "PLACE AS PLANNED", () => AdvisorHud.Show(ProjectStore.PlaceProject(pid, 5), 6f), 150);
            }
        }

        static void PollPlan()
        {
            PollProjects();
            var t = File.Exists(PlanFile) ? File.GetLastWriteTimeUtc(PlanFile) : System.DateTime.MinValue;
            if (t == planTime && planText != null && planText.text.Length > 0) return;
            planTime = t;
            planText.text = File.Exists(PlanFile)
                ? Rich(File.ReadAllText(PlanFile)) + $"\n\n<size=12><color=#D2D4EB>Updated {t.ToLocalTime():ddd HH:mm}</color></size>"
                : "<color=#D2D4EB>No plan yet. The advisor writes one when things are calm, or press UPDATE PLAN.</color>";
        }

        // ------------------------------------------------------------------ building the window

        static void ShowPage(Page p)
        {
            page = p;
            chatPage.gameObject.SetActive(p == Page.Chat);
            briefPage.gameObject.SetActive(p == Page.Briefings);
            planPage.gameObject.SetActive(p == Page.Plan);
            foreach (var kv in tabLabels) kv.Value.color = kv.Key == p ? Color.white : OniStyle.ButtonText;
            chatTime = streamTime = indexTime = planTime = default; // reload what's shown
            if (input != null) ((TextMeshProUGUI)input.placeholder).text = thread == null ? "Ask the advisor…  (Enter sends, Esc leaves the box)" : "Reply in this thread…";
            nextPoll = 0;
        }

        static void Build()
        {
            if (panel != null || AdvisorHud.Canvas == null) return;
            panel = OniStyle.Rect("advisor-panel", AdvisorHud.Canvas);
            panel.anchorMin = panel.anchorMax = new Vector2(1f, 0.5f);
            panel.pivot = new Vector2(1f, 0.5f);
            panel.anchoredPosition = new Vector2(-16, 20);
            panel.sizeDelta = new Vector2(640, 860); // wider for the larger text
            OniStyle.Fill(panel, OniStyle.Panel, true);

            // Header: title, tabs, close.
            var header = OniStyle.Rect("header", panel);
            header.anchorMin = new Vector2(0, 1); header.anchorMax = new Vector2(1, 1);
            header.pivot = new Vector2(0.5f, 1); header.sizeDelta = new Vector2(0, 42); header.anchoredPosition = Vector2.zero;
            OniStyle.Fill(header, OniStyle.Header, true);
            var hl = header.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.padding = new RectOffset(14, 8, 6, 6); hl.spacing = 6; hl.childAlignment = TextAnchor.MiddleLeft;
            hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false;
            var title = OniStyle.Rect("title", header);
            title.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            OniStyle.Label(title, "ADVISOR", 20, header: true, align: TextAlignmentOptions.MidlineLeft, wrap: false);
            tabLabels[Page.Chat] = Tab(header, "CHAT", () => { thread = null; ShowPage(Page.Chat); });
            tabLabels[Page.Briefings] = Tab(header, "BRIEFINGS", () => ShowPage(Page.Briefings));
            tabLabels[Page.Plan] = Tab(header, "PLAN", () => ShowPage(Page.Plan));
            OniStyle.MakeButton(header, "✕", Close, 34);

            chatPage = MakePage("chat");
            briefPage = MakePage("briefings");
            planPage = MakePage("plan");

            // Chat: thread bar, list, input, buttons.
            threadBar = OniStyle.Rect("thread-bar", chatPage);
            threadBar.anchorMin = new Vector2(0, 1); threadBar.anchorMax = new Vector2(1, 1); threadBar.pivot = new Vector2(0.5f, 1);
            threadBar.sizeDelta = new Vector2(-20, 30); threadBar.anchoredPosition = new Vector2(0, -6);
            var tl = threadBar.gameObject.AddComponent<HorizontalLayoutGroup>();
            tl.spacing = 8; tl.childAlignment = TextAnchor.MiddleLeft; tl.childControlWidth = true; tl.childControlHeight = true; tl.childForceExpandWidth = false;
            SmallButton(threadBar, "◀ BACK", () => { thread = null; ShowPage(Page.Chat); }, 80);
            var tt = OniStyle.Rect("thread-title", threadBar);
            tt.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            threadTitle = OniStyle.Label(tt, "", 14, header: true, color: OniStyle.HeaderLight, align: TextAlignmentOptions.MidlineLeft, wrap: false);
            threadBar.gameObject.SetActive(false);

            chatList = ScrollList(chatPage, new Vector2(10, 92), new Vector2(-10, -40), out chatScroll);
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
            var eager = OniStyle.MakeButton(bar, EagernessLabel(Prefs.Eagerness), () => { }, 230);
            eagerLabel = eager.GetComponentInChildren<TextMeshProUGUI>();
            eager.onClick.AddListener(CycleEagerness);

            // Briefings: list.
            briefList = ScrollList(briefPage, new Vector2(10, 10), new Vector2(-10, -8), out _);

            // Plan: the document, and buttons.
            planList = ScrollList(planPage, new Vector2(10, 48), new Vector2(-10, -8), out _);
            projectsBox = OniStyle.Rect("projects", planList);
            var pjv = projectsBox.gameObject.AddComponent<VerticalLayoutGroup>();
            pjv.padding = new RectOffset(6, 6, 6, 2); pjv.spacing = 4; pjv.childControlHeight = true; pjv.childControlWidth = true; pjv.childForceExpandHeight = false;
            var pr = OniStyle.Rect("plan-text", planList);
            var pv = pr.gameObject.AddComponent<VerticalLayoutGroup>();
            pv.padding = new RectOffset(10, 10, 8, 10); pv.childControlHeight = true; pv.childControlWidth = true; pv.childForceExpandHeight = false;
            planText = Text(pr, "", 15, OniStyle.Text);
            var pbar = OniStyle.Rect("plan-buttons", planPage);
            pbar.anchorMin = new Vector2(0, 0); pbar.anchorMax = new Vector2(1, 0); pbar.pivot = new Vector2(0.5f, 0);
            pbar.sizeDelta = new Vector2(-20, 32); pbar.anchoredPosition = new Vector2(0, 8);
            var pl = pbar.gameObject.AddComponent<HorizontalLayoutGroup>();
            pl.spacing = 8; pl.childAlignment = TextAnchor.MiddleCenter; pl.childControlWidth = true; pl.childControlHeight = true; pl.childForceExpandWidth = false;
            OniStyle.MakeButton(pbar, "DISCUSS", () => OpenThread("plan"), 110);
            OniStyle.MakeButton(pbar, "UPDATE PLAN", () =>
            {
                AdvisorRunner.WriteRequest("chat", null, new JObject { ["text"] = "Please review the colony and update the long-term plan now.", ["via"] = "panel", ["thread"] = "plan" });
                AdvisorHud.Show("Asking the advisor to update the plan…", 3f);
            }, 140);

            panel.gameObject.SetActive(false);
        }

        static RectTransform MakePage(string name)
        {
            var p = OniStyle.Rect(name, panel);
            p.anchorMin = Vector2.zero; p.anchorMax = Vector2.one;
            p.offsetMin = Vector2.zero; p.offsetMax = new Vector2(0, -42);
            return p;
        }

        static TextMeshProUGUI Tab(RectTransform parent, string text, UnityEngine.Events.UnityAction onClick)
        {
            var b = OniStyle.MakeButton(parent, text, onClick, text.Length * 11 + 22);
            b.GetComponent<Image>().color = OniStyle.HeaderLight;
            return b.GetComponentInChildren<TextMeshProUGUI>();
        }

        static RectTransform ScrollList(RectTransform parent, Vector2 offsetMin, Vector2 offsetMax, out ScrollRect scroll)
        {
            var view = OniStyle.Rect("view", parent);
            view.anchorMin = Vector2.zero; view.anchorMax = Vector2.one;
            view.offsetMin = offsetMin; view.offsetMax = offsetMax;
            OniStyle.Fill(view, OniStyle.Ink, true);
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
            var placeholder = OniStyle.Label(placeholderRt, "Ask the advisor…  (Enter sends, Esc leaves the box)", 18, color: new Color(0.24f, 0.26f, 0.34f, 0.6f), align: TextAlignmentOptions.MidlineLeft, wrap: false);
            var textRt = OniStyle.Rect("text", area);
            OniStyle.Stretch(textRt);
            var text = OniStyle.Label(textRt, "", 18, color: OniStyle.ButtonText, align: TextAlignmentOptions.MidlineLeft, wrap: false);
            text.richText = false;
            var field = box.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = bg;
            field.textViewport = area;
            field.textComponent = text;
            field.placeholder = placeholder;
            if (OniStyle.BodyFont != null) field.fontAsset = OniStyle.BodyFont;
            field.pointSize = 18;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.caretColor = OniStyle.ButtonText;
            field.customCaretColor = true;
            field.onSubmit.AddListener(_ => Send());
            return field;
        }

        const float TextScale = 1.3f; // 15 px -> ~19-20 px: close to the advisor card's text, easy to read over the game

        static TextMeshProUGUI Text(RectTransform parent, string text, float size, Color color)
        {
            var rt = OniStyle.Rect("text", parent);
            var t = OniStyle.Label(rt, text, Mathf.Round(size * TextScale), color: color);
            t.richText = true;
            return t;
        }

        // ------------------------------------------------------------------ text

        static string Escape(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");

        static string Plain(string md) => Regex.Replace(md ?? "", @"[*_`#]", "").Replace("\n", " ");

        static string Short(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

        /// <summary>The advisor's light Markdown as TextMeshPro rich text: **bold**, *italic*, `code`, headings, bullets.</summary>
        internal static string Rich(string md)
        {
            var lines = Escape(md).Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                var h = Regex.Match(l, @"^\s*(#{1,6})\s+(.*)$");
                if (h.Success) l = (h.Groups[1].Length <= 2 ? "<size=118%>" : "") + "<b><color=#F182B7>" + h.Groups[2].Value + "</color></b>" + (h.Groups[1].Length <= 2 ? "</size>" : "");
                l = Regex.Replace(l, @"^(\s*)[-*•]\s+\[ \]\s+", "$1☐ ");
                l = Regex.Replace(l, @"^(\s*)[-*•]\s+\[x\]\s+", "$1☑ ", RegexOptions.IgnoreCase);
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
            panel = null; chatList = null; briefList = null; planList = null; streamRow = null; input = null;
            chatRows.Clear(); tabLabels.Clear();
        }
    }

    /// <summary>
    /// Keys the game must not see: everything while the player types in the advisor's text boxes; the advisor's
    /// own hotkeys (single keys in the simple preset would otherwise also trigger the game's tools); Space,
    /// the arrows and Esc while a briefing plays; Esc while the advisor panel is open (it closes the panel,
    /// not the game's menu); and zooming while the pointer is over the panel.
    /// </summary>
    internal static class AdvisorKeys
    {
        internal static bool Swallow(KButtonEvent e, bool down)
        {
            if (AdvisorPanel.Typing || ModOptions.Typing || AdvisorPanel.JustLeftTyping) return true;
            if (Briefing.IsOpen && (e.IsAction(Action.Escape) || e.IsAction(Action.TogglePause) || e.IsAction(Action.PanLeft) || e.IsAction(Action.PanRight))) return true;
            if (AdvisorPanel.IsOpen && e.IsAction(Action.Escape)) return true;
            if (down && (AdvisorPanel.PointerOver || QuestPanel.PointerOver) && (e.IsAction(Action.ZoomIn) || e.IsAction(Action.ZoomOut))) return true;
            return Hotkeys.IsOursNow();
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(KScreenManager), nameof(KScreenManager.OnKeyDown))]
    internal static class KScreenManager_OnKeyDown_Patch
    {
        static bool Prefix(KButtonEvent e)
        {
            if (!AdvisorKeys.Swallow(e, down: true)) return true;
            e.Consumed = true;
            return false;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(KScreenManager), nameof(KScreenManager.OnKeyUp))]
    internal static class KScreenManager_OnKeyUp_Patch
    {
        static bool Prefix(KButtonEvent e)
        {
            if (!AdvisorKeys.Swallow(e, down: false)) return true;
            e.Consumed = true;
            return false;
        }
    }
}
