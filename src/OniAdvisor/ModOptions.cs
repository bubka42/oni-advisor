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
        static readonly System.Collections.Generic.List<(int id, Image img)> rows = new System.Collections.Generic.List<(int, Image)>();

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
            box.sizeDelta = new Vector2(1120, 600);
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
            body.offsetMax = new Vector2(-18, -52); body.offsetMin = new Vector2(18, 56);
            var cols = body.gameObject.AddComponent<HorizontalLayoutGroup>();
            cols.spacing = 28; cols.childControlWidth = cols.childControlHeight = true; cols.childForceExpandWidth = false; cols.childForceExpandHeight = true;
            var left = Column(body, 600, 0);
            var right = Column(body, 0, 1);

            Text(left, "VOICE", 17, true, 24);
            Text(left, "Click a voice to hear it. Takes effect at once and is remembered.", 14, false, 24, OniStyle.TextMuted);
            foreach (var (group, voices) in Voices)
            {
                Text(left, group, 14, true, 24, OniStyle.TextMuted);
                var grid = OniStyle.Rect("grid", left);
                var g = grid.gameObject.AddComponent<GridLayoutGroup>();
                g.cellSize = new Vector2(141, 28); g.spacing = new Vector2(6, 4); g.constraint = GridLayoutGroup.Constraint.FixedColumnCount; g.constraintCount = 4;
                int lines = (voices.Length + 3) / 4;
                grid.gameObject.AddComponent<LayoutElement>().preferredHeight = lines * 28 + (lines - 1) * 4;
                foreach (var (id, name) in voices) rows.Add((id, VoiceButton(grid, id, name)));
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
            Text(right, "The companion app runs in the background (tray icon) and normally needs no attention; its window has the full log.", 13, false, 44, OniStyle.TextMuted);

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

            var br = OniStyle.Rect("value", row);
            br.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            var img = OniStyle.Fill(br, OniStyle.Row, true);
            var b = br.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            var colors = b.colors; colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f); b.colors = colors;
            var tr = OniStyle.Rect("text", br);
            OniStyle.Stretch(tr, 8, 0);
            var text = OniStyle.Label(tr, "", 15, align: TextAlignmentOptions.MidlineLeft, wrap: false);

            int index = Math.Max(0, Array.FindIndex(c.Values, v => JToken.DeepEquals(Normal(v.value), Normal(ReadSetting(c.Key)))));
            text.text = c.Values[index].text + "  ›";
            b.onClick.AddListener(() =>
            {
                index = (index + 1) % c.Values.Length;
                text.text = c.Values[index].text + "  ›";
                WriteSetting(c.Key, c.Values[index].value);
                Request("settings", null);
            });
        }

        /// <summary>1 and 1.0 compare equal.</summary>
        static JToken Normal(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) ? new JValue((double)t) : t;

        static void Text(Transform parent, string text, float size, bool header, float height, Color? color = null)
        {
            var rt = OniStyle.Rect("text", parent);
            rt.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            OniStyle.Label(rt, text, size, header: header, color: color, align: TextAlignmentOptions.BottomLeft);
        }

        static Image VoiceButton(Transform parent, int id, string name)
        {
            var rt = OniStyle.Rect("voice-" + name, parent);
            var img = OniStyle.Fill(rt, OniStyle.Row, true);
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            var colors = b.colors; colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f); b.colors = colors;
            b.onClick.AddListener(() => Choose(id));
            var lt = OniStyle.Rect("label", rt);
            OniStyle.Stretch(lt, 6, 0);
            OniStyle.Label(lt, name, 15, align: TextAlignmentOptions.Midline, wrap: false);
            return img;
        }

        static void Paint()
        {
            foreach (var (id, img) in rows) img.color = id == selected ? OniStyle.Header : OniStyle.Row;
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
            if (dialog != null) UnityEngine.Object.Destroy(dialog);
            dialog = null;
            rows.Clear();
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
