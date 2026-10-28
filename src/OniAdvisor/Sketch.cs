using System;
using System.Collections.Generic;
using UnityEngine;

namespace OniAdvisor
{
    /// <summary>
    /// Hand-drawn looking strokes for the advisor's marks: circles that overshoot where they started, slightly
    /// bowed lines, quick arrowheads, boxes whose sides run past the corners. All procedural: a smooth wobble
    /// seeded from the mark itself, so a mark always looks the same and doesn't shimmer as the camera moves.
    /// Shapes are polylines in world units (1 = one tile); MarkRenderer.Stroke draws them with a tapered pen.
    /// </summary>
    internal static class Sketch
    {
        /// <summary>
        /// A smooth wobble: a slow drift (about "cycles" waves over the stroke, like a hand moving) plus a faint
        /// faster tremor, with random phases; sampled along the stroke (0..1).
        /// </summary>
        sealed class Wobble
        {
            readonly float f1, f2, p1, p2, amp;
            public Wobble(System.Random rng, float amp, float cycles)
            {
                this.amp = amp;
                f1 = cycles * (0.7f + (float)rng.NextDouble() * 0.6f); f2 = f1 * (2.1f + (float)rng.NextDouble() * 0.8f);
                p1 = (float)rng.NextDouble() * 6.283f; p2 = (float)rng.NextDouble() * 6.283f;
            }
            public float At(float t) => amp * (0.85f * Mathf.Sin(t * f1 * 6.283f + p1) + 0.15f * Mathf.Sin(t * f2 * 6.283f + p2));
        }

        public static System.Random Rng(int seed) => new System.Random(seed);

        /// <summary>A loop around an ellipse, starting at a random angle and overshooting by a bit, like a pen circle.</summary>
        public static List<Vector2> Circle(Vector2 c, float rx, float ry, System.Random rng)
        {
            var pts = new List<Vector2>();
            float start = (float)rng.NextDouble() * 6.283f;
            float overshoot = 0.35f + (float)rng.NextDouble() * 0.45f;
            float tilt = ((float)rng.NextDouble() - 0.5f) * 0.3f;
            float grow = 0.07f + (float)rng.NextDouble() * 0.07f; // the end lands a little outside the start
            var wob = new Wobble(rng, 0.05f, 1.5f);
            int n = 56;
            float sweep = 6.283f + overshoot;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                float a = start + sweep * t;
                float r = 1f + wob.At(t) + grow * t;
                var p = new Vector2(Mathf.Cos(a) * rx * r, Mathf.Sin(a) * ry * r);
                pts.Add(c + Rotate(p, tilt));
            }
            return pts;
        }

        /// <summary>A slightly bowed, wobbly line from a to b.</summary>
        public static List<Vector2> Line(Vector2 a, Vector2 b, System.Random rng, bool bow = true)
        {
            var pts = new List<Vector2>();
            var d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) { pts.Add(a); pts.Add(b); return pts; }
            var normal = new Vector2(-d.y, d.x) / len;
            float bend = bow ? ((float)rng.NextDouble() - 0.5f) * Mathf.Min(1.2f, len * 0.08f) : 0f;
            var wob = new Wobble(rng, Mathf.Min(0.1f, 0.03f + len * 0.005f), Mathf.Min(3f, Mathf.Max(0.5f, len / 7f)));
            int n = Mathf.Clamp((int)(len * 3), 8, 80);
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                float off = bend * 4f * t * (1 - t) + wob.At(t) * Mathf.Sin(t * Mathf.PI); // ends stay put
                pts.Add(a + d * t + normal * off);
            }
            return pts;
        }

        /// <summary>A route through several points, each leg slightly bowed, as one continuous stroke.</summary>
        public static List<Vector2> Path(IList<Vector2> points, System.Random rng)
        {
            var pts = new List<Vector2>();
            for (int i = 1; i < points.Count; i++)
            {
                var leg = Line(points[i - 1], points[i], rng, bow: points.Count == 2);
                if (i > 1) leg.RemoveAt(0);
                pts.AddRange(leg);
            }
            return pts;
        }

        /// <summary>Two quick strokes at the tip, pointing back along the direction of travel.</summary>
        public static List<List<Vector2>> ArrowHead(Vector2 from, Vector2 tip, System.Random rng, float size = 1.1f)
        {
            var dir = (tip - from).normalized;
            var heads = new List<List<Vector2>>();
            foreach (float side in new[] { 1f, -1f })
            {
                float ang = (150f + ((float)rng.NextDouble() - 0.5f) * 20f) * side * Mathf.Deg2Rad;
                float len = size * (0.85f + (float)rng.NextDouble() * 0.3f);
                heads.Add(Line(tip, tip + Rotate(dir, ang) * len, rng, bow: false));
            }
            return heads;
        }

        /// <summary>A quickly sketched box: each side runs a little past the corners. One stroke per side.</summary>
        public static List<List<Vector2>> Box(float l, float b, float r, float t, System.Random rng)
        {
            float over = 0.35f;
            var corners = new[] { new Vector2(l, b), new Vector2(r, b), new Vector2(r, t), new Vector2(l, t) };
            var sides = new List<List<Vector2>>();
            for (int i = 0; i < 4; i++)
            {
                var a = corners[i]; var c = corners[(i + 1) % 4];
                var d = (c - a).normalized;
                float j1 = ((float)rng.NextDouble() - 0.3f) * over, j2 = ((float)rng.NextDouble() - 0.3f) * over;
                sides.Add(Line(a - d * (over * 0.5f + j1), c + d * (over * 0.5f + j2), rng));
            }
            return sides;
        }

        static Vector2 Rotate(Vector2 v, float rad)
        {
            float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        /// <summary>A stable seed for a mark, so it is drawn the same way every time.</summary>
        public static int Seed(Mark m) => unchecked(m.Id * 7919 + m.X0 * 104729 + m.Y0 * 1299709 + m.X1 * 31 + m.Y1 * 17 + (m.Kind ?? "").GetHashCode());
    }
}
