// Just enough of UnityEngine (and the mod's Mark) for Sketch.cs to compile outside the game.
namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.x - b.x, a.y - b.y);
        public static Vector2 operator *(Vector2 a, float s) => new(a.x * s, a.y * s);
        public static Vector2 operator /(Vector2 a, float s) => new(a.x / s, a.y / s);
        public float magnitude => MathF.Sqrt(x * x + y * y);
        public Vector2 normalized => magnitude > 1e-6f ? this / magnitude : new Vector2(0, 0);
        public static float Distance(Vector2 a, Vector2 b) => (a - b).magnitude;
    }

    public static class Mathf
    {
        public const float PI = MathF.PI, Deg2Rad = MathF.PI / 180f;
        public static float Sin(float v) => MathF.Sin(v);
        public static float Cos(float v) => MathF.Cos(v);
        public static float Sqrt(float v) => MathF.Sqrt(v);
        public static float Min(float a, float b) => MathF.Min(a, b);
        public static float Max(float a, float b) => MathF.Max(a, b);
        public static int Clamp(int v, int lo, int hi) => Math.Clamp(v, lo, hi);
    }
}

namespace OniAdvisor
{
    internal sealed class Mark
    {
        public int Id, X0, Y0, X1, Y1;
        public string Kind;
    }
}
