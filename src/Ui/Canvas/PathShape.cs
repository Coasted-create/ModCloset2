using System.Globalization;
using System.Numerics;

namespace ModCloset2.Ui.Canvas;

/// <summary>
/// A small vector drawing given as SVG path data in its own box (24 × 24 for icons, as in the mockups). The picture
/// backend writes the path as it is; the game backend draws <see cref="Lines"/>, the same path flattened to
/// polylines once, here.
/// </summary>
public sealed class PathShape
{
    public string Data { get; }
    public float BoxW { get; }
    public float BoxH { get; }

    /// <summary> Each sub-path as points in the shape's box, and whether it is closed. </summary>
    public IReadOnlyList<(Vector2[] Points, bool Closed)> Lines { get; }

    public PathShape(string data, float boxW = 24, float boxH = 24)
    {
        Data  = data;
        BoxW  = boxW;
        BoxH  = boxH;
        Lines = Flatten(data);
    }

    /// <summary> A circle as path data (two half arcs), for shapes built in code. </summary>
    public static string Circle(float cx, float cy, float r)
        => string.Create(CultureInfo.InvariantCulture, $"M{cx - r} {cy}a{r} {r} 0 1 0 {2 * r} 0a{r} {r} 0 1 0 {-2 * r} 0z");

    // ---- parsing ----

    private static List<(Vector2[], bool)> Flatten(string d)
    {
        var result = new List<(Vector2[], bool)>();
        var points = new List<Vector2>();
        var pos    = 0;
        var cur    = Vector2.Zero;
        var start  = Vector2.Zero;
        var ctrl   = Vector2.Zero; // last control point, for S and T
        var cmd    = 'M';
        var last   = ' ';

        void Finish(bool closed)
        {
            if (points.Count > 1)
                result.Add((points.ToArray(), closed));
            points.Clear();
        }

        while (true)
        {
            SkipSeparators(d, ref pos);
            if (pos >= d.Length)
                break;
            if (char.IsLetter(d[pos]))
                cmd = d[pos++];
            else if (last == ' ')
                break; // numbers before any command: not a path

            var rel = char.IsLower(cmd);
            var o   = rel ? cur : Vector2.Zero;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    Finish(false);
                    cur   = o + new Vector2(Num(d, ref pos), Num(d, ref pos));
                    start = cur;
                    points.Add(cur);
                    cmd = rel ? 'l' : 'L'; // further pairs are lines
                    break;
                }
                case 'L':
                    cur = o + new Vector2(Num(d, ref pos), Num(d, ref pos));
                    points.Add(cur);
                    break;
                case 'H':
                    cur = new Vector2((rel ? cur.X : 0) + Num(d, ref pos), cur.Y);
                    points.Add(cur);
                    break;
                case 'V':
                    cur = new Vector2(cur.X, (rel ? cur.Y : 0) + Num(d, ref pos));
                    points.Add(cur);
                    break;
                case 'C':
                {
                    var c1 = o + new Vector2(Num(d, ref pos), Num(d, ref pos));
                    var c2 = o + new Vector2(Num(d, ref pos), Num(d, ref pos));
                    var to = o + new Vector2(Num(d, ref pos), Num(d, ref pos));
                    Cubic(points, cur, c1, c2, to);
                    ctrl = c2;
                    cur  = to;
                    break;
                }
                case 'S':
                {
                    var c1 = last is 'C' or 'c' or 'S' or 's' ? 2 * cur - ctrl : cur;
                    var c2 = o + new Vector2(Num(d, ref pos), Num(d, ref pos));
                    var to = o + new Vector2(Num(d, ref pos), Num(d, ref pos));
                    Cubic(points, cur, c1, c2, to);
                    ctrl = c2;
                    cur  = to;
                    break;
                }
                case 'Q':
                {
                    var c  = o + new Vector2(Num(d, ref pos), Num(d, ref pos));
                    var to = o + new Vector2(Num(d, ref pos), Num(d, ref pos));
                    Cubic(points, cur, cur + 2f / 3 * (c - cur), to + 2f / 3 * (c - to), to);
                    ctrl = c;
                    cur  = to;
                    break;
                }
                case 'T':
                {
                    var c  = last is 'Q' or 'q' or 'T' or 't' ? 2 * cur - ctrl : cur;
                    var to = o + new Vector2(Num(d, ref pos), Num(d, ref pos));
                    Cubic(points, cur, cur + 2f / 3 * (c - cur), to + 2f / 3 * (c - to), to);
                    ctrl = c;
                    cur  = to;
                    break;
                }
                case 'A':
                {
                    var rx    = Num(d, ref pos);
                    var ry    = Num(d, ref pos);
                    var rot   = Num(d, ref pos);
                    var large = Flag(d, ref pos);
                    var sweep = Flag(d, ref pos);
                    var to    = o + new Vector2(Num(d, ref pos), Num(d, ref pos));
                    Arc(points, cur, rx, ry, rot, large, sweep, to);
                    cur = to;
                    break;
                }
                case 'Z':
                    cur = start;
                    Finish(true);
                    points.Add(cur);
                    break;
                default:
                    return result; // unknown command: keep what was read
            }

            last = cmd;
        }

        Finish(false);
        return result;
    }

    private static void SkipSeparators(string d, ref int pos)
    {
        while (pos < d.Length && (char.IsWhiteSpace(d[pos]) || d[pos] == ','))
            pos++;
    }

    private static float Num(string d, ref int pos)
    {
        SkipSeparators(d, ref pos);
        var begin = pos;
        if (pos < d.Length && (d[pos] == '-' || d[pos] == '+'))
            pos++;
        var dot = false;
        while (pos < d.Length)
        {
            var ch = d[pos];
            if (char.IsDigit(ch))
                pos++;
            else if (ch == '.' && !dot)
            {
                dot = true;
                pos++;
            }
            else if ((ch == 'e' || ch == 'E') && pos + 1 < d.Length)
            {
                pos++;
                if (d[pos] == '-' || d[pos] == '+')
                    pos++;
            }
            else
                break;
        }

        return float.TryParse(d.AsSpan(begin, pos - begin), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary> Arc flags may be written without separators ("a8 8 0 1 0 16 0" but also "0 1016 0"). </summary>
    private static bool Flag(string d, ref int pos)
    {
        SkipSeparators(d, ref pos);
        if (pos >= d.Length)
            return false;
        return d[pos++] == '1';
    }

    private static void Cubic(List<Vector2> pts, Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3)
    {
        const int steps = 12;
        for (var i = 1; i <= steps; ++i)
        {
            var t = i / (float)steps;
            var u = 1 - t;
            pts.Add(u * u * u * p0 + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * p3);
        }
    }

    /// <summary> SVG's endpoint arc, turned into its centre form (SVG 1.1, appendix F.6) and stepped through. </summary>
    private static void Arc(List<Vector2> pts, Vector2 p1, float rx, float ry, float rotDeg, bool large, bool sweep, Vector2 p2)
    {
        if (rx == 0 || ry == 0 || p1 == p2)
        {
            pts.Add(p2);
            return;
        }

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        var phi  = rotDeg * MathF.PI / 180f;
        var cos  = MathF.Cos(phi);
        var sin  = MathF.Sin(phi);
        var dx   = (p1.X - p2.X) / 2;
        var dy   = (p1.Y - p2.Y) / 2;
        var x1   = cos * dx + sin * dy;
        var y1   = -sin * dx + cos * dy;
        var lam  = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lam > 1)
        {
            var s = MathF.Sqrt(lam);
            rx *= s;
            ry *= s;
        }

        var num  = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        var den  = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        var coef = MathF.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
        var cx1  = coef * rx * y1 / ry;
        var cy1  = -coef * ry * x1 / rx;
        var cx   = cos * cx1 - sin * cy1 + (p1.X + p2.X) / 2;
        var cy   = sin * cx1 + cos * cy1 + (p1.Y + p2.Y) / 2;

        static float Angle(float ux, float uy, float vx, float vy)
        {
            var a = MathF.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            return a;
        }

        var t1 = Angle(1, 0, (x1 - cx1) / rx, (y1 - cy1) / ry);
        var dt = Angle((x1 - cx1) / rx, (y1 - cy1) / ry, (-x1 - cx1) / rx, (-y1 - cy1) / ry);
        if (!sweep && dt > 0)
            dt -= 2 * MathF.PI;
        else if (sweep && dt < 0)
            dt += 2 * MathF.PI;

        var steps = Math.Max(4, (int)MathF.Ceiling(Math.Abs(dt) / (MathF.PI / 16)));
        for (var i = 1; i <= steps; ++i)
        {
            var t  = t1 + dt * i / steps;
            var ex = rx * MathF.Cos(t);
            var ey = ry * MathF.Sin(t);
            pts.Add(i == steps ? p2 : new Vector2(cos * ex - sin * ey + cx, sin * ex + cos * ey + cy));
        }
    }
}
