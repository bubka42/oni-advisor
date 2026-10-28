using System.Globalization;
using System.Text;
using OniAdvisor;
using UnityEngine;

// SketchPreview [out.svg]: the advisor's mark shapes on a tile grid, as the mod would draw them.
const float Scale = 24f;     // pixels per tile
const int W = 44, H = 26;    // tiles
var svg = new StringBuilder();
string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
float X(float x) => x * Scale;
float Y(float y) => (H - y) * Scale;
svg.Append($"<svg xmlns='http://www.w3.org/2000/svg' width='{W * Scale}' height='{H * Scale}' viewBox='0 0 {W * Scale} {H * Scale}'>");
svg.Append($"<rect width='100%' height='100%' fill='#2b2f3d'/>");
for (int x = 0; x <= W; x++) svg.Append($"<line x1='{X(x)}' y1='0' x2='{X(x)}' y2='{H * Scale}' stroke='#363b4c' stroke-width='1'/>");
for (int y = 0; y <= H; y++) svg.Append($"<line x1='0' y1='{Y(y)}' x2='{W * Scale}' y2='{Y(y)}' stroke='#363b4c' stroke-width='1'/>");

// The same taper as MarkRenderer.Stroke's width curve.
float Taper(float t) => t < 0.12f ? 0.35f + (1 - 0.35f) * t / 0.12f : t < 0.6f ? 1 - 0.15f * (t - 0.12f) / 0.48f : t < 0.9f ? 0.85f + 0.15f * (t - 0.6f) / 0.3f : 1 - 0.55f * (t - 0.9f) / 0.1f;
void Stroke(List<Vector2> pts, string color, float width, float opacity = 0.95f)
{
    if (opacity < 0.9f) // translucent (highlighter): one piece, as the game draws it, so overlaps don't bead
    {
        svg.Append($"<polyline fill='none' stroke='{color}' stroke-opacity='{F(opacity)}' stroke-width='{F(width * Scale)}' stroke-linecap='round' stroke-linejoin='round' points='{string.Join(" ", pts.Select(p => F(X(p.x)) + "," + F(Y(p.y))))}'/>");
        return;
    }
    for (int i = 1; i < pts.Count; i++)
    {
        float w = width * Taper((i - 0.5f) / (pts.Count - 1)) * Scale;
        svg.Append($"<line x1='{F(X(pts[i - 1].x))}' y1='{F(Y(pts[i - 1].y))}' x2='{F(X(pts[i].x))}' y2='{F(Y(pts[i].y))}' stroke='{color}' stroke-opacity='{F(opacity)}' stroke-width='{F(w)}' stroke-linecap='round'/>");
    }
}
void Label(float x, float y, string text, string color, float tilt) =>
    svg.Append($"<text x='{F(X(x))}' y='{F(Y(y))}' fill='{color}' font-family='Segoe UI, sans-serif' font-size='{F(Scale * 0.9f)}' font-weight='600' text-anchor='middle' transform='rotate({F(-tilt)} {F(X(x))} {F(Y(y))})' stroke='#1b1e27' stroke-width='3' paint-order='stroke'>{text}</text>");
void Box(int x0, int y0, int x1, int y1, string color) =>
    svg.Append($"<rect x='{X(x0)}' y='{Y(y1 + 1)}' width='{(x1 - x0 + 1) * Scale}' height='{(y1 - y0 + 1) * Scale}' fill='{color}' fill-opacity='0.13'/>");

const string Red = "#ff5c54", Green = "#7ddb6b", Yellow = "#ffd159", Cyan = "#5fd3e6";
int id = 1;
System.Random Rng(Mark m) { m.Id = id++; return Sketch.Rng(Sketch.Seed(m)); }
float Tilt(System.Random r) => ((float)r.NextDouble() - 0.5f) * 6f;

// point: circled tile
{ var m = new Mark { Kind = "point", X0 = 5, Y0 = 20 }; var r = Rng(m); var t = Tilt(r);
  svg.Append($"<rect x='{X(5)}' y='{Y(21)}' width='{Scale}' height='{Scale}' fill='#8a6d4b'/>");
  Stroke(Sketch.Circle(new Vector2(5.5f, 20.5f), 1.15f, 1.0f, r), Red, 0.2f); Label(5.5f, 22.6f, "A1 vent blocked", Red, t); }
// outline: a 4x3 building circled
{ var m = new Mark { Kind = "outline", X0 = 15, Y0 = 18 }; var r = Rng(m); var t = Tilt(r);
  svg.Append($"<rect x='{X(15)}' y='{Y(21)}' width='{4 * Scale}' height='{3 * Scale}' fill='#6f7895' rx='3'/>");
  Stroke(Sketch.Circle(new Vector2(17f, 19.5f), 2f + 0.9f, 1.5f + 0.9f, r), Yellow, 0.22f); Label(17f, 23.4f, "A2 check the filter", Yellow, t); }
// area: sketched box over a wash
{ var m = new Mark { Kind = "area", X0 = 27, Y0 = 16, X1 = 38, Y1 = 22 }; var r = Rng(m); var t = Tilt(r);
  Box(27, 16, 38, 22, Green);
  foreach (var side in Sketch.Box(27, 16, 39, 23, r)) Stroke(side, Green, 0.18f);
  Label(33f, 24.7f, "A3 farm here", Green, t); }
// arrow
{ var m = new Mark { Kind = "arrow", X0 = 4, Y0 = 6, X1 = 16, Y1 = 11 }; var r = Rng(m); var t = Tilt(r);
  var s = Sketch.Path(new List<Vector2> { new(4.5f, 6.5f), new(16.5f, 11.5f) }, r); Stroke(s, Cyan, 0.22f);
  foreach (var head in Sketch.ArrowHead(s[Math.Max(0, s.Count - 4)], s[^1], r)) Stroke(head, Cyan, 0.2f);
  Label(4.5f, 8.1f, "A4 hydrogen rises", Cyan, t); }
// path with an arrow
{ var m = new Mark { Kind = "path", X0 = 20, Y0 = 3 }; var r = Rng(m); var t = Tilt(r);
  var s = Sketch.Path(new List<Vector2> { new(20.5f, 3.5f), new(20.5f, 10.5f), new(28.5f, 10.5f), new(28.5f, 6.5f) }, r); Stroke(s, Green, 0.22f);
  foreach (var head in Sketch.ArrowHead(s[Math.Max(0, s.Count - 4)], s[^1], r)) Stroke(head, Green, 0.2f);
  Label(20.5f, 2.0f, "A5 pipe route", Green, t); }
// line (shaft): highlighter
{ var m = new Mark { Kind = "line", X0 = 36, Y0 = 2, X1 = 36, Y1 = 13 }; var r = Rng(m); var t = Tilt(r);
  Stroke(Sketch.Line(new Vector2(36.5f, 2.5f), new Vector2(36.5f, 13.5f), r, bow: false), Yellow, 0.9f, 0.4f);
  Label(36.5f, 15.1f, "A6 ladder shaft", Yellow, t); }

svg.Append("</svg>");
var outPath = args.Length > 0 ? args[0] : "sketch-preview.svg";
File.WriteAllText(outPath, svg.ToString());
Console.WriteLine("wrote " + Path.GetFullPath(outPath));
