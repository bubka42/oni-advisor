using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// An OPTIONS button on the advisor's row in the game's Mods screen, opening the advisor's settings (so the
    /// companion window is never needed): the other companion.json settings (model, effort, proactivity, speech,
    /// auto-start; each sends a "settings" request so a running companion reloads them) and a picker for the
    /// advisor's Kokoro voice. The choice is written to advisor/companion.json ("TtsSpeaker") so it holds the
    /// next time the companion starts, and sent as a "voice" request so a running companion switches at once
    /// and speaks a short sample. Speaker ids are those of kokoro-multi-lang-v1_0 (read from the model's
    /// id2speaker metadata); only the English voices are offered.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class ModOptions
    {
        internal static KMod.Mod Self;

        static readonly (string group, (int id, string name)[] voices)[] Voices =
        {
            ("AMERICAN, FEMALE", new[] { (0, "Alloy"), (1, "Aoede"), (2, "Bella"), (3, "Heart"), (4, "Jessica"), (5, "Kore"), (6, "Nicole"), (7, "Nova"), (8, "River"), (9, "Sarah"), (10, "Sky") }),
            ("AMERICAN, MALE", new[] { (11, "Adam"), (12, "Echo"), (13, "Eric"), (14, "Fenrir"), (15, "Liam"), (16, "Michael"), (17, "Onyx"), (18, "Puck"), (19, "Santa") }),
            ("BRITISH, FEMALE", new[] { (20, "Alice"), (21, "Emma"), (22, "Isabella"), (23, "Lily") }),
            ("BRITISH, MALE", new[] { (24, "Daniel"), (25, "Fable"), (26, "George"), (27, "Lewis") }),
        };
        const int DefaultVoice = 3; // af_heart, the companion's default for this model

        static string CompanionFile => Path.Combine(Path.GetDirectoryName(Paths.LiveDir), "companion.json");

        static GameObject dialog;
        static int selected;
        static readonly System.Collections.Generic.List<(int id, Button button, TextMeshProUGUI label, string name)> rows = new System.Collections.Generic.List<(int, Button, TextMeshProUGUI, string)>();
        static TextMeshProUGUI currentVoice;
        static RectTransform popup;   // an open dropdown list (with its click-catcher behind it)
        static GameObject popupBlocker;

        [HarmonyPatch(typeof(ModsScreen), "BuildDisplay")]
        static class AddButton
        {
            static void Postfix(ModsScreen __instance)
            {
                try { Add(__instance); }
                catch (Exception e) { Log.Warn("mod options button failed: " + e); }
            }
        }

        static void Add(ModsScreen screen)
        {
            if (Self == null) return;
            var mods = Global.Instance.modManager.mods;
            foreach (var entry in (IEnumerable)Traverse.Create(screen).Field("displayedMods").GetValue())
            {
                var t = Traverse.Create(entry);
                int index = t.Field("mod_index").GetValue<int>();
                if (index < 0 || index >= mods.Count || mods[index].label.id != Self.label.id) continue;
                var row = t.Field("rect_transform").GetValue<RectTransform>();
                var refs = row.GetComponent<HierarchyReferences>();
                var manage = refs != null && refs.HasReference("ManageButton") ? refs.GetReference<KButton>("ManageButton") : null;
                if (manage == null || row.Find("advisor-options") != null) return;

                // A copy of the row's own MANAGE button, so it looks like the game's.
                var copy = UnityEngine.Object.Instantiate(manage.gameObject, manage.transform.parent, false);
                copy.name = "advisor-options";
                copy.SetActive(true);
                copy.transform.SetSiblingIndex(manage.transform.GetSiblingIndex());
                var button = copy.GetComponent<KButton>();
                button.ClearOnClick();
                button.onClick += () => Open(screen.transform);
                button.isInteractable = true;
                foreach (var label in copy.GetComponentsInChildren<LocText>(true)) { label.key = ""; label.text = "OPTIONS"; }
                var tip = copy.GetComponent<ToolTip>();
                if (tip != null) tip.SetSimpleTooltip("Advisor options: choose the advisor's voice");
                return;
            }
        }

        static void Open(Transform parent)
        {
            if (dialog != null) UnityEngine.Object.Destroy(dialog);
            rows.Clear();
            selected = ReadVoice();

            // A full-screen shade that swallows clicks, with the dialog in the middle.
            var shade = OniStyle.Rect("advisor-options-dialog", parent);
            OniStyle.Stretch(shade);
            OniStyle.Fill(shade, new Color(0, 0, 0, 0.55f), true);
            dialog = shade.gameObject;
            dialog.transform.SetAsLastSibling();

            var box = OniStyle.Rect("box", shade);
            box.anchorMin = box.anchorMax = box.pivot = new Vector2(0.5f, 0.5f);
            box.sizeDelta = new Vector2(1120, 830);
            OniStyle.Fill(box, OniStyle.Panel, true);

            var header = OniStyle.Rect("header", box);
            header.anchorMin = new Vector2(0, 1); header.anchorMax = Vector2.one; header.pivot = new Vector2(0.5f, 1);
            header.sizeDelta = new Vector2(0, 40); header.anchoredPosition = Vector2.zero;
            OniStyle.Fill(header, OniStyle.Header, false);
            var ht = OniStyle.Rect("text", header);
            OniStyle.Stretch(ht, 14, 0);
            OniStyle.Label(ht, "ADVISOR OPTIONS", 20, header: true, align: TextAlignmentOptions.MidlineLeft, wrap: false);

            var body = OniStyle.Rect("body", box);
            OniStyle.Stretch(body, 18, 0);
            body.offsetMax = new Vector2(-18, -52); body.offsetMin = new Vector2(18, 56 + CustomHeight);
            var cols = body.gameObject.AddComponent<HorizontalLayoutGroup>();
            cols.spacing = 28; cols.childControlWidth = cols.childControlHeight = true; cols.childForceExpandWidth = false; cols.childForceExpandHeight = true;
            var left = Column(body, 600, 0);
            var right = Column(body, 0, 1);

            Text(left, "VOICE", 17, true, 24);
            currentVoice = Text(left, "", 15, false, 24);
            Text(left, "Click a voice to hear it. Takes effect at once and is remembered.", 14, false, 22, OniStyle.TextMuted);
            foreach (var (group, voices) in Voices)
            {
                Text(left, group, 14, true, 24, OniStyle.TextMuted);
                var grid = OniStyle.Rect("grid", left);
                var g = grid.gameObject.AddComponent<GridLayoutGroup>();
                g.cellSize = new Vector2(141, 28); g.spacing = new Vector2(6, 4); g.constraint = GridLayoutGroup.Constraint.FixedColumnCount; g.constraintCount = 4;
                int lines = (voices.Length + 3) / 4;
                grid.gameObject.AddComponent<LayoutElement>().preferredHeight = lines * 28 + (lines - 1) * 4;
                foreach (var (id, name) in voices) { var (b, l) = VoiceButton(grid, id, name); rows.Add((id, b, l, name)); }
            }
            Paint();

            Text(right, "ADVISOR", 17, true, 24);
            Text(right, "Click a setting to change it. Applies at once.", 14, false, 24, OniStyle.TextMuted);
            foreach (var s in Choices) ChoiceRow(right, s);
            Text(right, "", 14, false, 10);
            var show = OniStyle.Rect("show", right);
            show.gameObject.AddComponent<LayoutElement>().preferredHeight = 30;
            var sh = show.gameObject.AddComponent<HorizontalLayoutGroup>();
            sh.childControlWidth = sh.childControlHeight = true; sh.childForceExpandWidth = false;
            OniStyle.MakeButton(show, "SHOW COMPANION WINDOW", () => Request("companion", null), 260);

            CustomInstructions(box);

            var footer = OniStyle.Rect("footer", box);
            footer.anchorMin = Vector2.zero; footer.anchorMax = new Vector2(1, 0); footer.pivot = new Vector2(0.5f, 0);
            footer.sizeDelta = new Vector2(0, 44); footer.anchoredPosition = new Vector2(0, 8);
            var h = footer.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.childAlignment = TextAnchor.MiddleCenter; h.childControlWidth = h.childControlHeight = true; h.childForceExpandWidth = false;
            OniStyle.MakeButton(footer, "CLOSE", Close, 120);
        }

        static RectTransform Column(Transform parent, float width, float flexible)
        {
            var rt = OniStyle.Rect("column", parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            if (width > 0) le.preferredWidth = width;
            le.flexibleWidth = flexible;
            var v = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = 4; v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandHeight = false; v.childForceExpandWidth = true;
            return rt;
        }

        /// <summary>A companion.json setting with the values offered for it (the first is the default).</summary>
        sealed class Choice
        {
            public string Key, Label;
            public (JToken value, string text)[] Values;
            public Choice(string key, string label, params (JToken, string)[] values) { Key = key; Label = label; Values = values; }
        }

        static readonly Choice[] Choices =
        {
            new Choice("AdviceLevel", "Advice level", ("standard", "Standard"), ("light", "Light (the important bits)"), ("minimal", "Minimal (critical only)"), ("teacher", "Teacher (explains the why)")),
            new Choice("Model", "Model", ("sonnet", "Sonnet (answers in ~5-15 s)"), ("opus", "Opus (deeper, slower)")),
            new Choice("Effort", "Thinking", ("medium", "Medium"), ("high", "High"), ("low", "Low")),
            new Choice("Proactive", "Speaks up unasked", ("freely", "Freely"), ("serious", "Only when serious"), ("never", "Never")),
            new Choice("PlanReviewMinutes", "Long-term plan review", (12, "Every 12 min when calm"), (20, "Every 20 min"), (30, "Every 30 min"), (6, "Every 6 min")),
            new Choice("Speak", "Read replies aloud", (false, "Off"), (true, "On")),
            new Choice("SpeakBriefings", "Read briefings aloud", (true, "On"), (false, "Off")),
            new Choice("TtsSpeed", "Voice speed", (1.0, "Normal"), (1.1, "A bit faster"), (1.2, "Faster"), (1.3, "Fastest"), (0.9, "Slower")),
            new Choice("AutoStart", "Start with the game", (true, "On"), (false, "Off (start it by hand)")),
            new Choice("MarkStyle", "Advisor's marks", ("sketch", "Hand-drawn"), ("clean", "Clean shapes")),
            new Choice("Spotlight", "Briefing spotlight", (true, "On (grey out the rest)"), (false, "Off")),
            new Choice("ShowThinking", "Advisor's thinking", ("captions", "Captions while you wait"), ("camera", "Captions + camera follows"), ("off", "Off")),
        };

        static void ChoiceRow(Transform parent, Choice c)
        {
            var row = OniStyle.Rect("setting-" + c.Key, parent);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 30;
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 8; h.childControlWidth = h.childControlHeight = true; h.childForceExpandWidth = false; h.childAlignment = TextAnchor.MiddleLeft;
            var lr = OniStyle.Rect("label", row);
            lr.gameObject.AddComponent<LayoutElement>().preferredWidth = 170;
            OniStyle.Label(lr, c.Label, 15, align: TextAlignmentOptions.MidlineLeft, wrap: false);

            // A dropdown: the current value with a ▼; clicking opens the list of values under it.
            var br = OniStyle.Rect("value", row);
            br.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            var img = OniStyle.Fill(br, Color.white, true);
            var b = br.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            var colors = b.colors; colors.normalColor = colors.selectedColor = OniStyle.Row; colors.highlightedColor = OniStyle.RowActive; b.colors = colors;
            var tr = OniStyle.Rect("text", br);
            OniStyle.Stretch(tr, 8, 0);
            var text = OniStyle.Label(tr, "", 15, align: TextAlignmentOptions.MidlineLeft, wrap: false);
            var ar = OniStyle.Rect("arrow", br);
            ar.anchorMin = new Vector2(1, 0); ar.anchorMax = Vector2.one; ar.pivot = new Vector2(1, 0.5f);
            ar.sizeDelta = new Vector2(24, 0); ar.anchoredPosition = new Vector2(-4, 0);
            OniStyle.Label(ar, "V", 13, header: true, color: OniStyle.TextMuted, align: TextAlignmentOptions.Center, wrap: false); // the game font may lack ▼

            int index = Math.Max(0, Array.FindIndex(c.Values, v => JToken.DeepEquals(Normal(v.value), Normal(ReadSetting(c.Key)))));
            text.text = c.Values[index].text;
            b.onClick.AddListener(() => OpenDropdown(br, c, index, picked =>
            {
                index = picked;
                text.text = c.Values[index].text;
                WriteSetting(c.Key, c.Values[index].value);
                Request("settings", null);
            }));
        }

        /// <summary>The list of a setting's values, opened under its button; a click outside closes it.</summary>
        static void OpenDropdown(RectTransform anchor, Choice c, int current, Action<int> onPick)
        {
            CloseDropdown();
            if (dialog == null) return;
            var shade = (RectTransform)dialog.transform;

            var blocker = OniStyle.Rect("dropdown-blocker", shade);
            OniStyle.Stretch(blocker);
            var bimg = OniStyle.Fill(blocker, new Color(0, 0, 0, 0.001f), true);
            blocker.gameObject.AddComponent<Button>().onClick.AddListener(CloseDropdown);
            popupBlocker = blocker.gameObject;

            popup = OniStyle.Rect("dropdown", shade);
            popup.pivot = new Vector2(0, 1);
            var corners = new Vector3[4];
            anchor.GetWorldCorners(corners); // [0] bottom-left
            popup.position = corners[0];
            popup.sizeDelta = new Vector2(anchor.rect.width, 0);
            OniStyle.Fill(popup, OniStyle.PanelDark, true);
            var v = popup.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(2, 2, 2, 2); v.spacing = 2;
            v.childControlWidth = v.childControlHeight = true; v.childForceExpandHeight = false;
            popup.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            for (int i = 0; i < c.Values.Length; i++)
            {
                int pick = i;
                var item = OniStyle.Rect("option", popup);
                item.gameObject.AddComponent<LayoutElement>().preferredHeight = 28;
                var iimg = OniStyle.Fill(item, Color.white, true);
                var ib = item.gameObject.AddComponent<Button>();
                ib.targetGraphic = iimg;
                var lt = OniStyle.Rect("label", item);
                OniStyle.Stretch(lt, 8, 0);
                var label = OniStyle.Label(lt, "", 15, align: TextAlignmentOptions.MidlineLeft, wrap: false);
                label.richText = true;
                Tint(ib, label, i == current, c.Values[i].text);
                ib.onClick.AddListener(() => { CloseDropdown(); onPick(pick); });
            }
            popup.SetAsLastSibling();
        }

        static void CloseDropdown()
        {
            if (popup != null) UnityEngine.Object.Destroy(popup.gameObject);
            if (popupBlocker != null) UnityEngine.Object.Destroy(popupBlocker);
            popup = null; popupBlocker = null;
        }

        /// <summary>1 and 1.0 compare equal.</summary>
        static JToken Normal(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? new JValue((double)t) : t;

        static TextMeshProUGUI Text(Transform parent, string text, float size, bool header, float height, Color? color = null)
        {
            var rt = OniStyle.Rect("text", parent);
            rt.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            var t = OniStyle.Label(rt, text, size, header: header, color: color, align: TextAlignmentOptions.BottomLeft);
            t.richText = true;
            return t;
        }

        static (Button, TextMeshProUGUI) VoiceButton(Transform parent, int id, string name)
        {
            var rt = OniStyle.Rect("voice-" + name, parent);
            var img = OniStyle.Fill(rt, Color.white, true); // white base: the button's own colours (below) do the tinting
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            b.onClick.AddListener(() => Choose(id));
            var lt = OniStyle.Rect("label", rt);
            OniStyle.Stretch(lt, 6, 0);
            var label = OniStyle.Label(lt, name, 15, align: TextAlignmentOptions.Midline, wrap: false);
            label.richText = true;
            return (b, label);
        }

        /// <summary>
        /// The chosen voice is shown inverted (light button, dark bold text) and named above the list. Colours go
        /// through the button's own colour block: it tints over the image, so setting the image colour alone
        /// could be overridden by the button's state transitions.
        /// </summary>
        static void Paint()
        {
            foreach (var (id, button, label, name) in rows) Tint(button, label, id == selected, name);
            var group = Voices.FirstOrDefault(g => g.voices.Any(v => v.id == selected)).group;
            if (currentVoice != null) currentVoice.text = $"Current voice: <b>{NameOf(selected) ?? "?"}</b>" + (group != null ? $"  <color=#D2D4EB>({group.ToLowerInvariant()})</color>" : "");
        }

        static void Tint(Button button, TextMeshProUGUI label, bool on, string text)
        {
            var colors = button.colors;
            var normal = on ? OniStyle.Button : OniStyle.Row;
            colors.normalColor = normal;
            colors.selectedColor = normal;
            colors.highlightedColor = on ? Color.white : OniStyle.RowActive;
            colors.pressedColor = OniStyle.HeaderLight;
            button.colors = colors;
            label.color = on ? OniStyle.ButtonText : OniStyle.Text;
            label.text = on ? $"<b>{text}</b>" : text;
        }

        static void Choose(int id)
        {
            selected = id;
            Paint();
            WriteSetting("TtsSpeaker", id);
            Request("voice", new JObject { ["speaker"] = id, ["name"] = NameOf(id) });
        }

        /// <summary>Tells a running companion (GameClock-free, unlike AdvisorRunner.WriteRequest: this works on the main menu).</summary>
        static void Request(string trigger, JObject extra)
        {
            try
            {
                var req = new JObject { ["trigger"] = trigger, ["createdUtc"] = System.DateTime.UtcNow.ToString("o") };
                if (extra != null) req.Merge(extra);
                Paths.WriteAtomic(Path.Combine(Paths.Sub("requests"), $"{Paths.Stamp()}-{trigger}.json"), req.ToString(Formatting.Indented));
            }
            catch (Exception e) { Log.Warn(trigger + " request failed: " + e.Message); }
        }

        static string NameOf(int id) => Voices.SelectMany(g => g.voices).FirstOrDefault(x => x.id == id).name;

        static void Close()
        {
            CloseDropdown();
            SaveCustom();
            if (dialog != null) UnityEngine.Object.Destroy(dialog);
            dialog = null;
            customField = null;
            rows.Clear();
        }

        // ------------------------------------------------------------------ the player's own instructions

        const float CustomHeight = 190;
        const int CustomLimit = 2000;
        static TMP_InputField customField;
        static TextMeshProUGUI customCount;

        /// <summary>
        /// Optional instructions in the player's own words, saved to advisor/custom-prompt.md. The companion hands
        /// them to the advisor when a conversation starts and whenever they change.
        /// </summary>
        static string CustomFile => Path.Combine(Path.GetDirectoryName(Paths.LiveDir), "custom-prompt.md");

        /// <summary>While the player types here, the game's shortcuts (and Esc closing the Mods screen) stay quiet.</summary>
        public static bool Typing => dialog != null && customField != null && customField.isFocused;

        static void CustomInstructions(RectTransform box)
        {
            var area = OniStyle.Rect("custom", box);
            area.anchorMin = Vector2.zero; area.anchorMax = new Vector2(1, 0); area.pivot = new Vector2(0.5f, 0);
            area.sizeDelta = new Vector2(-36, CustomHeight - 8); area.anchoredPosition = new Vector2(0, 58);
            var v = area.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = 4; v.childControlWidth = v.childControlHeight = true; v.childForceExpandHeight = false; v.childForceExpandWidth = true;

            var head = OniStyle.Rect("head", area);
            head.gameObject.AddComponent<LayoutElement>().preferredHeight = 24;
            var hh = head.gameObject.AddComponent<HorizontalLayoutGroup>();
            hh.childControlWidth = hh.childControlHeight = true; hh.childForceExpandWidth = false;
            var title = OniStyle.Rect("title", head);
            title.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            OniStyle.Label(title, "YOUR INSTRUCTIONS FOR THE ADVISOR <size=13><color=#D2D4EB>(optional)</color></size>", 17, header: true, align: TextAlignmentOptions.BottomLeft, wrap: false).richText = true;
            var count = OniStyle.Rect("count", head);
            count.gameObject.AddComponent<LayoutElement>().preferredWidth = 120;
            customCount = OniStyle.Label(count, "", 13, color: OniStyle.TextMuted, align: TextAlignmentOptions.BottomRight, wrap: false);

            var box2 = OniStyle.Rect("input", area);
            box2.gameObject.AddComponent<LayoutElement>().preferredHeight = CustomHeight - 40;
            var bg = OniStyle.Fill(box2, OniStyle.Button, true);
            var viewport = OniStyle.Rect("text-area", box2);
            OniStyle.Stretch(viewport, 10, 6);
            viewport.gameObject.AddComponent<RectMask2D>();
            var placeholderRt = OniStyle.Rect("placeholder", viewport);
            OniStyle.Stretch(placeholderRt);
            var placeholder = OniStyle.Label(placeholderRt,
                "Anything you want the advisor to keep in mind, in your own words. For example: \"Call me Captain.\" \"Answer in German.\" " +
                "\"I'm going for a no-power-plant run.\" \"Don't mention research.\" Saved when you close this window.",
                15, color: new Color(0.24f, 0.26f, 0.34f, 0.6f), align: TextAlignmentOptions.TopLeft);
            var textRt = OniStyle.Rect("text", viewport);
            OniStyle.Stretch(textRt);
            var text = OniStyle.Label(textRt, "", 15, color: OniStyle.ButtonText, align: TextAlignmentOptions.TopLeft);
            text.richText = false;

            customField = box2.gameObject.AddComponent<TMP_InputField>();
            customField.targetGraphic = bg;
            customField.textViewport = viewport;
            customField.textComponent = text;
            customField.placeholder = placeholder;
            if (OniStyle.BodyFont != null) customField.fontAsset = OniStyle.BodyFont;
            customField.pointSize = 15;
            customField.lineType = TMP_InputField.LineType.MultiLineNewline;
            customField.characterLimit = CustomLimit;
            customField.caretColor = OniStyle.ButtonText;
            customField.customCaretColor = true;
            try { if (File.Exists(CustomFile)) customField.text = File.ReadAllText(CustomFile).TrimEnd(); } catch { }
            customField.onValueChanged.AddListener(_ => Count());
            customField.onEndEdit.AddListener(_ => SaveCustom());
            Count();
        }

        static void Count()
        {
            if (customField != null && customCount != null) customCount.text = $"{customField.text.Length} / {CustomLimit}";
        }

        static void SaveCustom()
        {
            if (customField == null) return;
            try
            {
                string text = customField.text.Trim();
                string old = File.Exists(CustomFile) ? File.ReadAllText(CustomFile).Trim() : "";
                if (text == old) return;
                if (text.Length == 0) File.Delete(CustomFile);
                else Paths.WriteAtomic(CustomFile, text + "\n");
                AdvisorHud.Show(text.Length == 0 ? "Advisor instructions cleared" : "Advisor instructions saved", 2.5f);
            }
            catch (Exception e) { Log.Warn("custom instructions not saved: " + e.Message); }
        }

        static int ReadVoice() => (int?)ReadSetting("TtsSpeaker") ?? DefaultVoice;

        static JToken ReadSetting(string key)
        {
            try { if (File.Exists(CompanionFile)) { var v = JObject.Parse(File.ReadAllText(CompanionFile))[key]; if (v != null && v.Type != JTokenType.Null) return v; } }
            catch { }
            return Choices.FirstOrDefault(c => c.Key == key)?.Values[0].value;
        }

        /// <summary>Written here (not only by the companion) so a choice sticks when the companion isn't running.</summary>
        static void WriteSetting(string key, JToken value)
        {
            try
            {
                var o = File.Exists(CompanionFile) ? JObject.Parse(File.ReadAllText(CompanionFile)) : new JObject();
                o[key] = value;
                Paths.WriteAtomic(CompanionFile, o.ToString(Formatting.Indented));
            }
            catch (Exception e) { Log.Warn("companion.json update failed: " + e.Message); }
        }
    }
}
