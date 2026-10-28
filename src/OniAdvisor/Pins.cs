using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// Pins: small round buttons the advisor places on the map (live/pins.json), each at the place it is about
    /// and coloured by tone (red problem, yellow check, green build, cyan info). Clicking one opens a card
    /// right below it: a short explanation and the advisor's suggested actions as buttons ("Turn off these
    /// terrariums", "Cancel the mop jobs here"), plus ASK and DISMISS. An action runs at once (the player's
    /// click is the approval; see Commands.RunByPlayer) and its outcome shows on the card. Outcomes and
    /// dismissals go to live/pins-status.json for the advisor, and done or dismissed pins stay hidden.
    /// Pins follow the camera; they hide during briefings and planning mode.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class Pins
    {
        const int MaxPins = 10;

        sealed class Pin
        {
            public string Id, Tone, Title, Text;
            public int X, Y;
            public int[] Highlight;
            public List<(string label, JObject command)> Actions = new List<(string, JObject)>();
            public RectTransform Button;
            public Image Dot;
        }

        static readonly List<Pin> pins = new List<Pin>();
        static readonly HashSet<string> closed = new HashSet<string>(); // done or dismissed
        static readonly MarkRenderer highlight = new MarkRenderer("pin-highlight");
        static System.DateTime fileTime;
        static RectTransform layer, card;
        static Pin open;

        static string PinsFile => Path.Combine(Paths.LiveDir, "pins.json");
        static string StatusFile => Path.Combine(Paths.LiveDir, "pins-status.json");

        // ------------------------------------------------------------------ loading

        /// <summary>About once a second.</summary>
        public static void Poll()
        {
            try
            {
                if (!File.Exists(PinsFile)) { if (pins.Count > 0) Clear(); return; }
                var t = File.GetLastWriteTimeUtc(PinsFile);
                if (t == fileTime || (System.DateTime.UtcNow - t).TotalMilliseconds < 400) return;
                var root = JToken.Parse(File.ReadAllText(PinsFile));
                fileTime = t;
                LoadClosed();
                Clear();
                var arr = root is JArray a ? a : root["pins"] as JArray ?? new JArray();
                int n = 0;
                foreach (var p in arr)
                {
                    var pin = new Pin
                    {
                        Id = (string)p["id"] ?? $"p{++n}",
                        X = (int?)p["x"] ?? 0, Y = (int?)p["y"] ?? 0,
                        Tone = ((string)p["tone"] ?? "info").ToLowerInvariant(),
                        Title = (string)p["title"] ?? "",
                        Text = (string)p["text"] ?? "",
                        Highlight = p["highlight"] is JArray h && h.Count >= 4 ? h.Select(v => (int)v).ToArray() : null,
                    };
                    foreach (var act in p["actions"] ?? new JArray())
                        if (act["command"] is JObject cmd) pin.Actions.Add(((string)act["label"] ?? "Do it", cmd));
                    if (closed.Contains(pin.Id) || pins.Count >= MaxPins) continue;
                    pins.Add(pin);
                }
                if (pins.Count > 0) AdvisorHud.Show($"The advisor pinned {pins.Count} thing(s) on your map: click a pin to see what to do", 4f);
            }
            catch (Exception e) { Log.Warn("pins load: " + e.Message); }
        }

        // ------------------------------------------------------------------ every frame

        public static void Tick()
        {
            if (pins.Count == 0) return;
            Build();
            if (layer == null) return;
            bool hidden = Briefing.IsOpen || PlanMode.Active;
            layer.gameObject.SetActive(!hidden);
            if (hidden) return;
            var cam = Camera.main;
            if (cam == null) return;
            float pulse = 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * 4f);
            foreach (var p in pins)
            {
                if (p.Button == null) MakeButton(p);
                var sp = cam.WorldToScreenPoint(new Vector3(p.X + 0.5f, p.Y + 1.4f, 0f));
                bool onScreen = sp.z > 0 && sp.x > -20 && sp.y > -20 && sp.x < Screen.width + 20 && sp.y < Screen.height + 20;
                p.Button.gameObject.SetActive(onScreen);
                if (!onScreen) continue;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, sp, null, out var local);
                p.Button.anchoredPosition = local;
                if (p.Tone == "problem") { var c = p.Dot.color; c.a = pulse; p.Dot.color = c; }
                if (p == open && card != null) card.anchoredPosition = local + new Vector2(0f, -22f);
            }
            if (open != null && card != null) card.gameObject.SetActive(open.Button != null && open.Button.gameObject.activeSelf);
        }

        // ------------------------------------------------------------------ pins and the card

        static Sprite circle;
        static Sprite Circle
        {
            get
            {
                if (circle != null) return circle;
                const int N = 48;
                var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(N / 2f, N / 2f)) / (N / 2f);
                        tex.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01((1f - d) * 10f)));
                    }
                tex.Apply();
                circle = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f));
                return circle;
            }
        }

        static Color ToneColor(string tone)
        {
            switch (tone)
            {
                case "problem": return new Color(1f, 0.36f, 0.33f);
                case "build": return new Color(0.49f, 0.86f, 0.42f);
                case "check": return new Color(1f, 0.82f, 0.35f);
                default: return new Color(0.37f, 0.83f, 0.9f);
            }
        }

        static string Glyph(string tone) => tone == "problem" ? "!" : tone == "check" ? "?" : tone == "build" ? "+" : "i";

        static void MakeButton(Pin p)
        {
            var rt = OniStyle.Rect("pin-" + p.Id, layer);
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(30f, 30f);
            var ring = rt.gameObject.AddComponent<Image>();
            ring.sprite = Circle; ring.color = OniStyle.PanelDark;
            var dotRt = OniStyle.Rect("dot", rt);
            OniStyle.Stretch(dotRt, 3f, 3f);
            p.Dot = dotRt.gameObject.AddComponent<Image>();
            p.Dot.sprite = Circle; p.Dot.color = ToneColor(p.Tone); p.Dot.raycastTarget = false;
            var lt = OniStyle.Rect("glyph", rt);
            OniStyle.Stretch(lt);
            OniStyle.Label(lt, Glyph(p.Tone), 17, header: true, color: OniStyle.ButtonText, align: TextAlignmentOptions.Center, wrap: false);
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = ring;
            b.onClick.AddListener(() => Toggle(p));
            p.Button = rt;
        }

        static void Toggle(Pin p)
        {
            if (open == p) { CloseCard(); return; }
            CloseCard();
            open = p;
            if (p.Highlight != null)
            {
                var h = p.Highlight;
                highlight.BeginReveal();
                foreach (var side in Sketch.Box(Math.Min(h[0], h[2]), Math.Min(h[1], h[3]), Math.Max(h[0], h[2]) + 1, Math.Max(h[1], h[3]) + 1, Sketch.Rng(p.Id.GetHashCode())))
                    highlight.Stroke(side, ToneColor(p.Tone), 0.18f);
            }
            card = OniStyle.Rect("pin-card", layer);
            card.anchorMin = card.anchorMax = Vector2.zero;
            card.pivot = new Vector2(0.5f, 1f);
            card.sizeDelta = new Vector2(380f, 0f);
            OniStyle.Fill(card, OniStyle.Panel, true);
            var v = card.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(12, 12, 10, 10); v.spacing = 6;
            v.childControlWidth = v.childControlHeight = true; v.childForceExpandHeight = false;
            card.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            Label(card, p.Title.ToUpperInvariant(), 16, header: true, color: ToneColor(p.Tone));
            if (p.Text.Length > 0) Label(card, p.Text.Replace("<", "‹"), 14, color: OniStyle.Text);
            var outcome = Label(card, "", 13, color: OniStyle.TextMuted);
            outcome.gameObject.SetActive(false);
            foreach (var (label, command) in p.Actions)
            {
                var row = Row(card);
                OniStyle.MakeButton(row, label.ToUpperInvariant(), () =>
                {
                    string result = Commands.RunByPlayer(command, "pin: " + p.Title);
                    outcome.text = result;
                    outcome.gameObject.SetActive(true);
                    Record(p, "done", label, result);
                    AdvisorHud.Show(result, 4f);
                }, 300);
            }
            var bottom = Row(card);
            OniStyle.MakeButton(bottom, "ASK", () => { CloseCard(); AdvisorPanel.OpenWithText($"About the pin \"{p.Title}\": "); }, 80);
            OniStyle.MakeButton(bottom, "DISMISS", () => { Record(p, "dismissed", null, null); Remove(p); }, 110);
            OniStyle.MakeButton(bottom, "CLOSE", () => { var was = p; CloseCard(); if (closed.Contains(was.Id)) Remove(was); }, 90); // a done pin goes away once read
            card.SetAsLastSibling();
        }

        static TextMeshProUGUI Label(RectTransform parent, string text, float size, bool header = false, Color? color = null)
        {
            var rt = OniStyle.Rect("text", parent);
            var t = OniStyle.Label(rt, text, size, header: header, color: color);
            t.richText = true;
            return t;
        }

        static RectTransform Row(RectTransform parent)
        {
            var row = OniStyle.Rect("row", parent);
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 6; h.childAlignment = TextAnchor.MiddleLeft; h.childControlWidth = h.childControlHeight = true; h.childForceExpandWidth = false;
            return row;
        }

        static void CloseCard()
        {
            if (card != null) UnityEngine.Object.Destroy(card.gameObject);
            card = null; open = null;
            highlight.Clear();
        }

        static void Remove(Pin p)
        {
            if (open == p) CloseCard();
            if (p.Button != null) UnityEngine.Object.Destroy(p.Button.gameObject);
            pins.Remove(p);
            closed.Add(p.Id);
        }

        // ------------------------------------------------------------------ outcomes for the advisor

        static void LoadClosed()
        {
            closed.Clear();
            try
            {
                if (!File.Exists(StatusFile)) return;
                var o = JObject.Parse(File.ReadAllText(StatusFile));
                foreach (var e in o["events"] ?? new JArray()) closed.Add((string)e["id"]);
            }
            catch { }
        }

        static void Record(Pin p, string what, string action, string result)
        {
            try
            {
                var o = File.Exists(StatusFile) ? JObject.Parse(File.ReadAllText(StatusFile)) : new JObject();
                var events = o["events"] as JArray ?? new JArray();
                events.Add(new JObject
                {
                    ["id"] = p.Id, ["title"] = p.Title, ["what"] = what, ["action"] = action, ["result"] = result,
                    ["cycle"] = GameClock.Instance != null ? GameClock.Instance.GetCycle() + 1 : 0, ["utc"] = System.DateTime.UtcNow.ToString("o"),
                });
                while (events.Count > 100) events.RemoveAt(0);
                o["events"] = events;
                Paths.WriteAtomic(StatusFile, o.ToString(Formatting.Indented));
            }
            catch (Exception e) { Log.Warn("pin status: " + e.Message); }
            closed.Add(p.Id);
        }

        // ------------------------------------------------------------------ setup

        static void Build()
        {
            if (layer != null || AdvisorHud.Canvas == null) return;
            layer = OniStyle.Rect("advisor-pins", AdvisorHud.Canvas);
            OniStyle.Stretch(layer);
            layer.pivot = Vector2.zero;
            layer.SetSiblingIndex(Math.Min(2, layer.parent.childCount - 1)); // above the briefing stage, below panels and toasts
        }

        static void Clear()
        {
            CloseCard();
            foreach (var p in pins) if (p.Button != null) UnityEngine.Object.Destroy(p.Button.gameObject);
            pins.Clear();
        }

        public static void Destroy()
        {
            Clear();
            if (layer != null) UnityEngine.Object.Destroy(layer.gameObject);
            layer = null; fileTime = default;
        }
    }
}
