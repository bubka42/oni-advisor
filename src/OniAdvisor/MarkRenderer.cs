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
            }
        }

        public void Clear()
        {
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

        public void Label(float cx, float cy, string text, Color c)
        {
            if (string.IsNullOrEmpty(text)) return;
            var go = New("label");
            go.transform.position = new Vector3(cx, cy, Z - 0.03f);
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

        void Quad(float cx, float cy, float w, float h, Color c, int order)
        {
            var go = New("quad");
            go.transform.position = new Vector3(cx, cy, Z - 0.01f * order);
            go.transform.localScale = new Vector3(w, h, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = White;
            sr.color = c;
            sr.sortingOrder = SortingOrder + order;
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
}
