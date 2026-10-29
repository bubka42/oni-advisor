using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace OniAdvisor
{
    /// <summary>
    /// Draws simple world-space marks (tinted tile rectangles, outlines, icons, labels) above the map.
    /// Each named layer can be rebuilt or hidden independently. Uses plain SpriteRenderers and a
    /// TextMeshPro label with the game's own font, so nothing depends on game prefabs.
    ///
    /// UNTESTED IN GAME: sorting/z values and label size may need tuning on first run.
    /// </summary>
    internal sealed class MarkRenderer
    {
        static Sprite white;
        static GameObject root;

        readonly string name;
        readonly List<GameObject> objects = new List<GameObject>();
        bool visible = true;

        // Tunables, in world units (1 unit = 1 tile).
        const float OutlineWidth = 0.12f;
        const float LabelFontSize = 3.2f;
        const int SortingOrder = 30000;

        public MarkRenderer(string name) { this.name = name; }

        /// <summary>Labels as UI chips (MarkChips) instead of world text: readable, styled, and clickable.</summary>
        public bool ChipLabels;

        static float Z => Grid.GetLayerZ(Grid.SceneLayer.FXFront2) - 0.5f;

        static Sprite White
        {
            get
            {
                if (white == null)
                {
                    var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                    var px = new Color32[16];
                    for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
                    tex.SetPixels32(px);
                    tex.Apply();
                    white = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
                }
                return white;
            }
        }

        static Transform Root
        {
            get
            {
                if (root == null) root = new GameObject("OniAdvisorMarks");
                return root.transform;
            }
        }

        public bool Visible
        {
            get => visible;
            set
            {
                visible = value;
                foreach (var go in objects) if (go != null) go.SetActive(value);
                MarkChips.SetVisible(this, value);
            }
        }

        public void Clear()
        {
            MarkChips.Clear(this);
            foreach (var go in objects) if (go != null) Object.Destroy(go);
            objects.Clear();
        }

        /// <summary>Filled rectangle over tiles x0..x1, y0..y1 (inclusive).</summary>
        public void Rect(int x0, int y0, int x1, int y1, Color fill)
        {
            Order(ref x0, ref x1); Order(ref y0, ref y1);
            Quad((x0 + x1 + 1) / 2f, (y0 + y1 + 1) / 2f, x1 - x0 + 1, y1 - y0 + 1, fill, 0);
        }

        /// <summary>Rectangle outline around tiles x0..x1, y0..y1 (inclusive).</summary>
        public void Outline(int x0, int y0, int x1, int y1, Color c)
        {
            Order(ref x0, ref x1); Order(ref y0, ref y1);
            float l = x0, r = x1 + 1, b = y0, t = y1 + 1, w = r - l, h = t - b, o = OutlineWidth;
            Quad(l + w / 2, b + o / 2, w, o, c, 1);
            Quad(l + w / 2, t - o / 2, w, o, c, 1);
            Quad(l + o / 2, b + h / 2, o, h, c, 1);
            Quad(r - o / 2, b + h / 2, o, h, c, 1);
        }

        /// <summary>A straight bar between two world points (tile centres are x + 0.5).</summary>
        public void Segment(float ax, float ay, float bx, float by, Color c, float width = 0.22f)
        {
            float dx = bx - ax, dy = by - ay, len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) return;
            var go = Quad((ax + bx) / 2f, (ay + by) / 2f, len + width, width, c, 1);
            go.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dy, dx) * Mathf.Rad2Deg);
        }

        /// <summary>An arrow head at (bx, by) pointing away from (ax, ay).</summary>
        public void ArrowHead(float ax, float ay, float bx, float by, Color c, float size = 0.9f)
        {
            float ang = Mathf.Atan2(by - ay, bx - ax);
            foreach (float side in new[] { 2.6f, -2.6f })
                Segment(bx, by, bx + Mathf.Cos(ang + side) * size, by + Mathf.Sin(ang + side) * size, c, 0.22f);
        }

        // ------------------------------------------------------------------ hand-drawn strokes (Sketch)

        float revealCursor = -1f;

        /// <summary>Strokes drawn from now on reveal one after another, as if sketched (call once per batch of marks).</summary>
        public void BeginReveal() => revealCursor = Time.unscaledTime;

        /// <summary>No reveal: strokes appear at once (e.g. redraws of marks already shown).</summary>
        public void NoReveal() => revealCursor = -1f;

        /// <summary>
        /// A pen stroke along a polyline: one thin sprite per segment (the same plain sprites as every other mark,
        /// which render reliably; a LineRenderer turned out invisible in the game), tapered at both ends and a
        /// little uneven in the middle, drawn on segment by segment if a reveal is running.
        /// </summary>
        public void Stroke(List<Vector2> points, Color c, float width = 0.2f)
        {
            if (points == null || points.Count < 2) return;
            var go = New("stroke");
            go.transform.position = Vector3.zero;
            float z = Z - 0.025f;
            int n = points.Count - 1;
            float len = 0f;
            var segments = new GameObject[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 a = points[i], b = points[i + 1];
                float d = Vector2.Distance(a, b);
                len += d;
                float t = (i + 0.5f) / n;
                float w = width * Taper(t);
                var seg = new GameObject("seg");
                seg.transform.SetParent(go.transform, false);
                seg.transform.position = new Vector3((a.x + b.x) / 2f, (a.y + b.y) / 2f, z);
                seg.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg);
                seg.transform.localScale = new Vector3(d + w * 0.9f, w, 1f); // overlap, so joints close
                var sr = seg.AddComponent<SpriteRenderer>();
                sr.sprite = White;
                sr.color = c;
                sr.sortingOrder = SortingOrder + 2;
                segments[i] = seg;
            }
            if (revealCursor >= 0f)
            {
                float duration = Mathf.Clamp(len * 0.035f, 0.12f, 0.6f);
                foreach (var seg in segments) seg.SetActive(false);
                var reveal = go.AddComponent<StrokeReveal>();
                reveal.Segments = segments;
                reveal.Start = revealCursor;
                reveal.Duration = duration;
                revealCursor += duration * 0.85f;
            }
        }

        /// <summary>Pen pressure along a stroke (0..1): thin at both ends, a little lighter in the middle.</summary>
        static float Taper(float t) =>
            t < 0.12f ? Mathf.Lerp(0.35f, 1f, t / 0.12f) : t < 0.6f ? Mathf.Lerp(1f, 0.85f, (t - 0.12f) / 0.48f)
            : t < 0.9f ? Mathf.Lerp(0.85f, 1f, (t - 0.6f) / 0.3f) : Mathf.Lerp(1f, 0.45f, (t - 0.9f) / 0.1f);

        /// <summary>Small square centred on a tile.</summary>
        public void Dot(int x, int y, Color c, float size = 0.55f) => Quad(x + 0.5f, y + 0.5f, size, size, c, 2);

        public void Icon(float cx, float cy, Sprite sprite, Color tint, float size)
        {
            if (sprite == null) return;
            var go = New("icon");
            go.transform.position = new Vector3(cx, cy, Z - 0.02f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = tint;
            sr.sortingOrder = SortingOrder + 3;
            var b = sprite.bounds.size;
            float s = size / Mathf.Max(0.01f, Mathf.Max(b.x, b.y));
            go.transform.localScale = new Vector3(s, s, 1f);
        }

        public void Label(float cx, float cy, string text, Color c, float tiltDegrees = 0f, System.Action onClick = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (ChipLabels) { MarkChips.Add(this, cx, cy, text, c, onClick, visible); return; }
            var go = New("label");
            go.transform.position = new Vector3(cx, cy, Z - 0.03f);
            if (tiltDegrees != 0f) go.transform.rotation = Quaternion.Euler(0f, 0f, tiltDegrees);
            var tmp = go.AddComponent<TextMeshPro>();
            var font = Localization.FontAsset;
            if (font != null) tmp.font = font;
            tmp.text = text;
            tmp.fontSize = LabelFontSize;
            tmp.color = c;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.outlineWidth = 0.25f;
            tmp.sortingOrder = SortingOrder + 4;
            tmp.rectTransform.sizeDelta = new Vector2(30f, 3f);
        }

        GameObject Quad(float cx, float cy, float w, float h, Color c, int order)
        {
            var go = New("quad");
            go.transform.position = new Vector3(cx, cy, Z - 0.01f * order);
            go.transform.localScale = new Vector3(w, h, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = White;
            sr.color = c;
            sr.sortingOrder = SortingOrder + order;
            return go;
        }

        GameObject New(string kind)
        {
            var go = new GameObject($"{name}-{kind}");
            go.transform.SetParent(Root, false);
            go.SetActive(visible);
            objects.Add(go);
            return go;
        }

        static void Order(ref int a, ref int b) { if (b < a) { int t = a; a = b; b = t; } }

        public static void DestroyAll()
        {
            if (root != null) Object.Destroy(root);
            root = null;
        }
    }


    /// <summary>Reveals a stroke over a short time, segment by segment, as if it were being drawn (unscaled time: briefings pause the game).</summary>
    internal sealed class StrokeReveal : MonoBehaviour
    {
        public GameObject[] Segments;
        public float Start, Duration;
        int shown;

        void Update()
        {
            if (Segments == null) { enabled = false; return; }
            float t = Duration <= 0 ? 1f : Mathf.Clamp01((Time.unscaledTime - Start) / Duration);
            t = 1f - (1f - t) * (1f - t); // ease out: quick start, gentle finish
            int target = Mathf.CeilToInt(t * Segments.Length);
            while (shown < target && shown < Segments.Length) { if (Segments[shown] != null) Segments[shown].SetActive(true); shown++; }
            if (shown >= Segments.Length) enabled = false;
        }
    }
}
