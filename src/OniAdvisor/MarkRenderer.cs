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

        static Material lineMaterial;
        static Material LineMaterial
        {
            get
            {
                // Sprites/Default ships with every Unity build (SpriteRenderer uses it).
                if (lineMaterial == null) lineMaterial = new Material(Shader.Find("Sprites/Default"));
                return lineMaterial;
            }
        }

        float revealCursor = -1f;

        /// <summary>Strokes drawn from now on reveal one after another, as if sketched (call once per batch of marks).</summary>
        public void BeginReveal() => revealCursor = Time.unscaledTime;

        /// <summary>No reveal: strokes appear at once (e.g. redraws of marks already shown).</summary>
        public void NoReveal() => revealCursor = -1f;

        /// <summary>A pen stroke along a polyline: tapered ends, slightly uneven pressure, drawn on if a reveal is running.</summary>
        public void Stroke(List<Vector2> points, Color c, float width = 0.2f)
        {
            if (points == null || points.Count < 2) return;
            var go = New("stroke");
            float z = Z - 0.025f;
            var pts = new Vector3[points.Count];
            float len = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                pts[i] = new Vector3(points[i].x, points[i].y, z);
                if (i > 0) len += Vector2.Distance(points[i - 1], points[i]);
            }
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.sharedMaterial = LineMaterial;
            line.startColor = line.endColor = c;
            line.numCapVertices = 4;
            line.numCornerVertices = 2;
            line.sortingOrder = SortingOrder + 2;
            line.widthMultiplier = width;
            line.widthCurve = new AnimationCurve(new Keyframe(0f, 0.35f), new Keyframe(0.12f, 1f), new Keyframe(0.6f, 0.85f), new Keyframe(0.9f, 1f), new Keyframe(1f, 0.45f));
            line.positionCount = pts.Length;
            line.SetPositions(pts);
            if (revealCursor >= 0f)
            {
                float duration = Mathf.Clamp(len * 0.035f, 0.12f, 0.6f);
                var reveal = go.AddComponent<StrokeReveal>();
                reveal.Points = pts;
                reveal.Start = revealCursor;
                reveal.Duration = duration;
                line.positionCount = 0;
                revealCursor += duration * 0.85f;
            }
        }

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

    /// <summary>Reveals a stroke over a short time, as if it were being drawn (unscaled time: briefings pause the game).</summary>
    internal sealed class StrokeReveal : MonoBehaviour
    {
        public Vector3[] Points;
        public float Start, Duration;
        LineRenderer line;

        void Awake() => line = GetComponent<LineRenderer>();

        void Update()
        {
            if (line == null || Points == null) { enabled = false; return; }
            float t = Duration <= 0 ? 1f : Mathf.Clamp01((Time.unscaledTime - Start) / Duration);
            t = 1f - (1f - t) * (1f - t); // ease out: quick start, gentle finish
            float exact = t * (Points.Length - 1);
            int whole = Mathf.FloorToInt(exact);
            int count = Mathf.Min(Points.Length, whole + 2);
            line.positionCount = Mathf.Max(count, 0);
            for (int i = 0; i < count && i <= whole; i++) line.SetPosition(i, Points[i]);
            if (whole + 1 < Points.Length && count > whole + 1)
                line.SetPosition(whole + 1, Vector3.Lerp(Points[whole], Points[whole + 1], exact - whole));
            if (t >= 1f) { line.positionCount = Points.Length; line.SetPositions(Points); enabled = false; }
        }
    }
}
