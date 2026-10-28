using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OniAdvisor
{
    /// <summary>
    /// What a briefing step puts on screen besides its marks:
    ///   - a spotlight: everything is greyed out except a soft-edged oval around the area the step is about
    ///     (it follows the camera as the player pans or zooms); off in the mod options = no spotlight;
    ///   - a caption: the step's spoken sentence, large, at the bottom of the screen, typing itself out at
    ///     about speaking speed, with numbers and quantities picked out.
    /// Both live on the HUD canvas behind the briefing panel and let clicks through.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class BriefingStage
    {
        const float Shade = 0.6f;          // how dark the greyed-out part is
        const float CharsPerSecond = 17f;  // close to the voice's pace
        const float PadTiles = 2.5f;

        static RectTransform root, hole, left, right, top, bottom, caption;
        static CanvasGroup group;
        static TextMeshProUGUI captionTitle, captionText;
        static string fullText;
        static float shownAt, fadeTarget;
        static bool hasArea;
        static float x0, y0, x1, y1; // world rectangle (tiles) kept in the light

        /// <summary>A new step. area: tile rectangle to keep lit, or null for no spotlight.</summary>
        public static void Show(RectInt? area, string title, string say)
        {
            Build();
            if (root == null) return;
            hasArea = area.HasValue && Prefs.Spotlight;
            if (area.HasValue)
            {
                var a = area.Value;
                x0 = a.xMin - PadTiles; y0 = a.yMin - PadTiles; x1 = a.xMax + PadTiles; y1 = a.yMax + PadTiles;
            }
            fadeTarget = hasArea ? 1f : 0f;
            captionTitle.text = (title ?? "").ToUpperInvariant();
            fullText = Highlight(say ?? "");
            captionText.text = fullText;
            captionText.maxVisibleCharacters = 0;
            shownAt = Time.unscaledTime;
            caption.gameObject.SetActive(!string.IsNullOrEmpty(say));
            root.gameObject.SetActive(true);
        }

        public static void Hide()
        {
            if (root == null) return;
            root.gameObject.SetActive(false);
            caption.gameObject.SetActive(false);
            group.alpha = 0f;
        }

        /// <summary>Every frame while a briefing is open.</summary>
        public static void Tick()
        {
            if (root == null || !root.gameObject.activeSelf) return;
            group.alpha = Mathf.MoveTowards(group.alpha, fadeTarget, Time.unscaledDeltaTime * 3f);
            captionText.maxVisibleCharacters = (int)((Time.unscaledTime - shownAt) * CharsPerSecond); // typing out (TMP stops at the end)
            if (group.alpha > 0f) PlaceHole();
        }

        /// <summary>The lit oval around the area, and the four shaded bars around it, in canvas coordinates.</summary>
        static void PlaceHole()
        {
            var cam = CameraController.Instance != null && CameraController.Instance.baseCamera != null ? CameraController.Instance.baseCamera : Camera.main;
            if (cam == null) return;
            var size = root.rect.size;
            Vector2 Local(float wx, float wy)
            {
                var sp = cam.WorldToScreenPoint(new Vector3(wx, wy, 0f));
                RectTransformUtility.ScreenPointToLocalPointInRectangle(root, sp, null, out var local);
                return local; // root's pivot is its bottom-left corner
            }
            var a = Local(x0, y0); var b = Local(x1, y1);
            // An oval that encloses the rectangle (√2 larger), plus room for the soft edge.
            var centre = (a + b) / 2f;
            var half = new Vector2(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y)) * 0.5f * 1.42f;
            half = Vector2.Max(half, new Vector2(60f, 60f));
            float l = centre.x - half.x, r = centre.x + half.x, bt = centre.y - half.y, tp = centre.y + half.y;
            Set(hole, l, bt, r, tp);
            Set(left, -10f, -10f, l, size.y + 10f);
            Set(right, r, -10f, size.x + 10f, size.y + 10f);
            Set(bottom, l, -10f, r, bt);
            Set(top, l, tp, r, size.y + 10f);
        }

        static void Set(RectTransform rt, float l, float b, float r, float t)
        {
            rt.anchoredPosition = new Vector2(l, b);
            rt.sizeDelta = new Vector2(Mathf.Max(0f, r - l), Mathf.Max(0f, t - b));
        }

        /// <summary>Numbers with their units (888 g/s, 12 kg, 30%, 4 cycles) and coordinates stand out.</summary>
        static string Highlight(string s)
        {
            s = s.Replace("<", "‹");
            return Regex.Replace(s, @"\b\d[\d,.]*\s?(%|°C|kg/s|g/s|kg|g|W|kW|J|kJ|tiles?|cycles?|dupes?|duplicants?)?",
                m => $"<b><color=#FFD86B>{m.Value}</color></b>");
        }

        // ------------------------------------------------------------------ building

        static Sprite holeSprite;

        /// <summary>A soft oval hole: transparent in the middle, fading to the shade colour at the edge.</summary>
        static Sprite HoleSprite
        {
            get
            {
                if (holeSprite != null) return holeSprite;
                const int N = 128;
                var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float dx = (x + 0.5f) / N * 2f - 1f, dy = (y + 0.5f) / N * 2f - 1f;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.72f, 1f, d));
                        tex.SetPixel(x, y, new Color(1, 1, 1, alpha));
                    }
                tex.Apply();
                holeSprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f));
                return holeSprite;
            }
        }

        static RectTransform Piece(string name, Sprite sprite)
        {
            var rt = OniStyle.Rect(name, root);
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(0.05f, 0.06f, 0.09f, Shade);
            if (sprite != null) img.sprite = sprite;
            img.raycastTarget = false;
            return rt;
        }

        static void Build()
        {
            if (root != null || AdvisorHud.Canvas == null) return;
            root = OniStyle.Rect("briefing-stage", AdvisorHud.Canvas);
            OniStyle.Stretch(root);
            root.pivot = Vector2.zero;
            root.SetAsFirstSibling(); // behind the briefing panel, toasts and bulb
            group = root.gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false; group.interactable = false; group.alpha = 0f;

            hole = Piece("hole", HoleSprite);
            left = Piece("left", null); right = Piece("right", null); top = Piece("top", null); bottom = Piece("bottom", null);

            // The caption sits on the HUD canvas itself (not inside the faded group), bottom centre, clear of the panel.
            caption = OniStyle.Rect("briefing-caption", AdvisorHud.Canvas);
            caption.anchorMin = caption.anchorMax = new Vector2(0.5f, 0f);
            caption.pivot = new Vector2(0.5f, 0f);
            caption.anchoredPosition = new Vector2(-250f, 70f);
            caption.sizeDelta = new Vector2(1080f, 0f);
            caption.SetSiblingIndex(1);
            OniStyle.Fill(caption, new Color(0.08f, 0.09f, 0.13f, 0.82f), false);
            var v = caption.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(28, 28, 14, 18); v.spacing = 4;
            v.childControlWidth = v.childControlHeight = true; v.childForceExpandHeight = false;
            caption.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var tr = OniStyle.Rect("title", caption);
            captionTitle = OniStyle.Label(tr, "", 16, header: true, color: OniStyle.HeaderLight, align: TextAlignmentOptions.TopLeft, wrap: false);
            var xr = OniStyle.Rect("text", caption);
            captionText = OniStyle.Label(xr, "", 27, color: Color.white, align: TextAlignmentOptions.TopLeft);
            captionText.richText = true;
            captionText.outlineWidth = 0.15f;
            caption.gameObject.SetActive(false);
            root.gameObject.SetActive(false);
        }

        public static void Destroy()
        {
            if (root != null) Object.Destroy(root.gameObject);
            if (caption != null) Object.Destroy(caption.gameObject);
            root = null; caption = null;
        }
    }
}
