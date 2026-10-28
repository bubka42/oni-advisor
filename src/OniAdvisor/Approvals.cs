using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// Advisor commands waiting for the player (delegation "ask", see Commands): a card at the top of the screen
    /// with the oldest one and APPROVE / DECLINE. Deciding writes commands/decisions/&lt;id&gt;.json, which
    /// Commands.Poll carries out - the same file the companion window's buttons write.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class Approvals
    {
        static RectTransform card;
        static TextMeshProUGUI text;
        static string shownId;

        static string Dir(string sub) => Paths.Sub(Path.Combine("commands", sub));

        /// <summary>About once a second.</summary>
        public static void Poll()
        {
            var first = Directory.GetFiles(Dir("pending"), "*.json").OrderBy(f => f)
                .FirstOrDefault(f => !File.Exists(Path.Combine(Dir("decisions"), Path.GetFileName(f))));
            if (first == null) { shownId = null; if (card != null) card.gameObject.SetActive(false); return; }
            string id = Path.GetFileNameWithoutExtension(first);
            if (id == shownId && card != null && card.gameObject.activeSelf) return;
            string summary;
            try { var o = JObject.Parse(File.ReadAllText(first)); summary = (string)o["summary"] ?? (string)o["action"] ?? "a command"; }
            catch { return; }
            int more = Directory.GetFiles(Dir("pending"), "*.json").Length - 1;
            Build();
            if (card == null) return;
            shownId = id;
            text.text = $"<b>THE ADVISOR WANTS TO</b>  {summary.Replace("<", "‹")}" + (more > 0 ? $"  <color=#D2D4EB>(+{more} more)</color>" : "");
            card.gameObject.SetActive(true);
        }

        static void Decide(bool approve)
        {
            if (shownId == null) return;
            Paths.WriteAtomic(Path.Combine(Dir("decisions"), shownId + ".json"), new JObject { ["approve"] = approve }.ToString());
            AdvisorHud.Show(approve ? "Approved" : "Declined", 2f);
            shownId = null;
            card.gameObject.SetActive(false);
        }

        static void Build()
        {
            if (card != null || AdvisorHud.Canvas == null) return;
            card = OniStyle.Rect("advisor-approval", AdvisorHud.Canvas);
            card.anchorMin = card.anchorMax = new Vector2(0.5f, 1f);
            card.pivot = new Vector2(0.5f, 1f);
            card.anchoredPosition = new Vector2(0, -124);
            card.sizeDelta = new Vector2(760, 50);
            OniStyle.Fill(card, OniStyle.PanelDark, true);
            var h = card.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(12, 10, 8, 8); h.spacing = 8;
            h.childAlignment = TextAnchor.MiddleLeft; h.childControlWidth = h.childControlHeight = true; h.childForceExpandWidth = false;

            var tr = OniStyle.Rect("text", card);
            tr.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            text = OniStyle.Label(tr, "", 15, align: TextAlignmentOptions.MidlineLeft, wrap: false);
            text.richText = true;
            OniStyle.MakeButton(card, "APPROVE", () => Decide(true), 110);
            OniStyle.MakeButton(card, "DECLINE", () => Decide(false), 110);
            card.gameObject.SetActive(false);
        }

        public static void Destroy()
        {
            if (card != null) Object.Destroy(card.gameObject);
            card = null; shownId = null;
        }
    }
}
