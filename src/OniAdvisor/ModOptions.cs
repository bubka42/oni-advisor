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
    /// An OPTIONS button on the advisor's row in the game's Mods screen, opening a small dialog to pick the
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
            box.sizeDelta = new Vector2(620, 600);
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
            var v = body.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = 4; v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandHeight = false; v.childForceExpandWidth = true;

            Text(body, "VOICE", 17, true, 24);
            Text(body, "Click a voice to hear it (the companion app must be running). Takes effect at once and is remembered.", 14, false, 38, OniStyle.TextMuted);
            foreach (var (group, voices) in Voices)
            {
                Text(body, group, 14, true, 24, OniStyle.TextMuted);
                var grid = OniStyle.Rect("grid", body);
                var g = grid.gameObject.AddComponent<GridLayoutGroup>();
                g.cellSize = new Vector2(141, 28); g.spacing = new Vector2(6, 4); g.constraint = GridLayoutGroup.Constraint.FixedColumnCount; g.constraintCount = 4;
                int lines = (voices.Length + 3) / 4;
                grid.gameObject.AddComponent<LayoutElement>().preferredHeight = lines * 28 + (lines - 1) * 4;
                foreach (var (id, name) in voices) rows.Add((id, VoiceButton(grid, id, name)));
            }
            Paint();

            var footer = OniStyle.Rect("footer", box);
            footer.anchorMin = Vector2.zero; footer.anchorMax = new Vector2(1, 0); footer.pivot = new Vector2(0.5f, 0);
            footer.sizeDelta = new Vector2(0, 44); footer.anchoredPosition = new Vector2(0, 8);
            var h = footer.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.childAlignment = TextAnchor.MiddleCenter; h.childControlWidth = h.childControlHeight = true; h.childForceExpandWidth = false;
            OniStyle.MakeButton(footer, "CLOSE", Close, 120);
        }

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
            WriteVoice(id);
            try
            {
                var req = new JObject { ["trigger"] = "voice", ["speaker"] = id, ["name"] = NameOf(id), ["createdUtc"] = System.DateTime.UtcNow.ToString("o") };
                Paths.WriteAtomic(Path.Combine(Paths.Sub("requests"), $"{Paths.Stamp()}-voice.json"), req.ToString(Formatting.Indented));
            }
            catch (Exception e) { Log.Warn("voice request failed: " + e.Message); }
        }

        static string NameOf(int id) => Voices.SelectMany(g => g.voices).FirstOrDefault(x => x.id == id).name;

        static void Close()
        {
            if (dialog != null) UnityEngine.Object.Destroy(dialog);
            dialog = null;
            rows.Clear();
        }

        static int ReadVoice()
        {
            try { if (File.Exists(CompanionFile)) return (int?)JObject.Parse(File.ReadAllText(CompanionFile))["TtsSpeaker"] ?? DefaultVoice; }
            catch { }
            return DefaultVoice;
        }

        /// <summary>Also written here (not only by the companion) so the choice sticks when the companion isn't running.</summary>
        static void WriteVoice(int id)
        {
            try
            {
                var o = File.Exists(CompanionFile) ? JObject.Parse(File.ReadAllText(CompanionFile)) : new JObject();
                o["TtsSpeaker"] = id;
                Paths.WriteAtomic(CompanionFile, o.ToString(Formatting.Indented));
            }
            catch (Exception e) { Log.Warn("companion.json update failed: " + e.Message); }
        }
    }
}
