using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// ONI's look for the advisor's in-game UI: pink header bars, slate-blue panels, light lavender buttons,
    /// the game's own fonts (GRAYSTROKE for headers, the localized body font for text). Colours were sampled
    /// from the game's own screens.
    /// </summary>
    internal static class OniStyle
    {
        public static readonly Color Header = Hex(0xE3, 0x74, 0xA9);
        public static readonly Color HeaderLight = Hex(0xF1, 0x82, 0xB7);
        public static readonly Color Panel = Hex(0x5B, 0x62, 0x7C, 0.97f);
        public static readonly Color PanelDark = Hex(0x3E, 0x43, 0x57, 0.97f);
        public static readonly Color Row = Hex(0x6F, 0x78, 0x95);
        public static readonly Color RowActive = Hex(0x85, 0x8E, 0xB0);
        public static readonly Color Button = Hex(0xD2, 0xD4, 0xEB);
        public static readonly Color ButtonText = Hex(0x3E, 0x43, 0x57);
        public static readonly Color Text = Color.white;
        public static readonly Color TextMuted = Hex(0xD2, 0xD4, 0xEB);
        // Reading surfaces (chat rows, lists, chips): dark like the game's tutorial boxes, for white text to stand out.
        public static readonly Color Ink = Hex(0x22, 0x25, 0x32, 0.97f);
        public static readonly Color InkRow = Hex(0x2F, 0x33, 0x44);
        public static readonly Color InkRowLight = Hex(0x3B, 0x40, 0x55);

        static TMP_FontAsset headerFont, bodyFont;

        public static TMP_FontAsset HeaderFont
        {
            get
            {
                if (headerFont == null)
                    headerFont = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
                        .FirstOrDefault(f => f != null && f.name.ToUpperInvariant().Contains("GRAYSTROKE")) ?? BodyFont;
                return headerFont;
            }
        }

        public static TMP_FontAsset BodyFont
        {
            get
            {
                if (bodyFont == null) bodyFont = Localization.FontAsset;
                return bodyFont;
            }
        }

        static Color Hex(int r, int g, int b, float a = 1f) => new Color(r / 255f, g / 255f, b / 255f, a);

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static Image Fill(RectTransform rt, Color c, bool blocksClicks)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.color = c;
            img.raycastTarget = blocksClicks;
            return img;
        }

        public static TextMeshProUGUI Label(RectTransform rt, string text, float size, bool header = false, Color? color = null,
            TextAlignmentOptions align = TextAlignmentOptions.TopLeft, bool wrap = true)
        {
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            var font = header ? HeaderFont : BodyFont;
            if (font != null) t.font = font;
            t.text = text;
            t.fontSize = size;
            t.color = color ?? Text;
            t.alignment = align;
            t.raycastTarget = false;
            t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            if (!wrap) t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        /// <summary>A light ONI-style button with a dark label.</summary>
        public static Button MakeButton(Transform parent, string text, UnityAction onClick, float width)
        {
            var rt = Rect("button-" + text, parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width; le.preferredHeight = 30;
            var img = Fill(rt, Button, true);
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            var colors = b.colors;
            colors.highlightedColor = Color.white;
            colors.pressedColor = HeaderLight;
            b.colors = colors;
            b.onClick.AddListener(onClick);
            var lt = Rect("label", rt);
            Stretch(lt, 6, 2);
            Label(lt, text, 15, header: true, color: ButtonText, align: TextAlignmentOptions.Center, wrap: false);
            return b;
        }

        public static void Stretch(RectTransform rt, float padX = 0, float padY = 0)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padX, padY); rt.offsetMax = new Vector2(-padX, -padY);
        }
    }
}
