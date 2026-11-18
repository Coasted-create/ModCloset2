using System.Numerics;
using ModCloset2.Ui.Canvas;

namespace ModCloset2.OnYou;

/// <summary> An ellipse on screen, turned by <see cref="Angle"/> (radians; the direction of the <see cref="A"/> axis). </summary>
public readonly record struct Ellipse(Vector2 Centre, float A, float B, float Angle)
{
    private Vector2 AxisA
        => new(MathF.Cos(Angle), MathF.Sin(Angle));

    private Vector2 AxisB
        => new(-MathF.Sin(Angle), MathF.Cos(Angle));

    /// <summary> Half its width on screen (how far it reaches left and right of its centre). </summary>
    public float HalfWidth
    {
        get
        {
            var c = MathF.Cos(Angle);
            var s = MathF.Sin(Angle);
            return MathF.Sqrt(A * A * c * c + B * B * s * s);
        }
    }

    /// <summary> Half its height on screen. </summary>
    public float HalfHeight
    {
        get
        {
            var c = MathF.Cos(Angle);
            var s = MathF.Sin(Angle);
            return MathF.Sqrt(A * A * s * s + B * B * c * c);
        }
    }

    public Rect Bounds
        => new(Centre.X - HalfWidth, Centre.Y - HalfHeight, 2 * HalfWidth, 2 * HalfHeight);

    /// <summary> The outline as <paramref name="n"/> points, going round once. </summary>
    public Vector2[] Points(int n)
    {
        var pts = new Vector2[n];
        var a   = AxisA;
        var b   = AxisB;
        for (var i = 0; i < n; ++i)
        {
            var t = i * MathF.Tau / n;
            pts[i] = Centre + a * (A * MathF.Cos(t)) + b * (B * MathF.Sin(t));
        }

        return pts;
    }

    /// <summary> The same ellipse grown by <paramref name="by"/> pixels on both axes. </summary>
    public Ellipse Grow(float by)
        => this with { A = A + by, B = B + by };
}

/// <summary> Small screen-space helpers for the "On you" face: distances to a body part, the outline around it. </summary>
public static class Geometry
{
    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t  = ab.LengthSquared() < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary> How far a point is from a body part's line (from the point itself when the line is one point). </summary>
    public static float DistanceToLine(Vector2 p, IReadOnlyList<(float X, float Y)> line)
    {
        if (line.Count == 0)
            return float.MaxValue;
        if (line.Count == 1)
            return Vector2.Distance(p, V(line[0]));
        var best = float.MaxValue;
        for (var i = 0; i + 1 < line.Count; ++i)
            best = MathF.Min(best, DistanceToSegment(p, V(line[i]), V(line[i + 1])));
        return best;
    }

    /// <summary> The point halfway along the line (where its quiet ring goes). </summary>
    public static Vector2 Middle(IReadOnlyList<(float X, float Y)> line)
    {
        if (line.Count == 0)
            return Vector2.Zero;
        if (line.Count == 1)
            return V(line[0]);
        var total = 0f;
        for (var i = 0; i + 1 < line.Count; ++i)
            total += Vector2.Distance(V(line[i]), V(line[i + 1]));
        var half = total / 2;
        for (var i = 0; i + 1 < line.Count; ++i)
        {
            var a = V(line[i]);
            var b = V(line[i + 1]);
            var d = Vector2.Distance(a, b);
            if (half <= d && d > 0)
                return Vector2.Lerp(a, b, half / d);
            half -= d;
        }

        return V(line[^1]);
    }

    /// <summary>
    /// An ellipse around a body part. A long straight part (a leg, an arm in an emote) gets a thin one along its own
    /// direction; a bent one (a curled tail) an upright one around the box of its line, as in the mockup, where the tail's
    /// root may leave the outline where it joins the body. <paramref name="upright"/> asks for the upright one whatever the
    /// shape (the tail: it swings and curls, and an outline that turns with every sway would be restless).
    /// </summary>
    public static Ellipse Around(IReadOnlyList<(float X, float Y)> line, float radius, float pad, bool upright = false)
    {
        var r = MathF.Max(0, radius);
        if (line.Count == 0)
            return new Ellipse(Vector2.Zero, r + pad, r + pad, 0);
        if (line.Count == 1)
            return new Ellipse(V(line[0]), r + pad, r + pad, 0);

        // main direction: the principal axis of the points (2 × 2 covariance, closed form)
        var mean = Vector2.Zero;
        foreach (var p in line)
            mean += V(p);
        mean /= line.Count;
        float sxx = 0, syy = 0, sxy = 0;
        foreach (var p in line)
        {
            var d = V(p) - mean;
            sxx += d.X * d.X;
            syy += d.Y * d.Y;
            sxy += d.X * d.Y;
        }

        var angle = 0.5f * MathF.Atan2(2 * sxy, sxx - syy);
        var (centre, hu, hv) = Extent(line, mean, angle);
        if (upright || hv > 0.3f * hu)
        {
            // bent: upright, around the line's box
            angle = 0;
            (centre, hu, hv) = Extent(line, mean, 0);
            if (hv > hu)
            {
                // keep A as the long axis, pointing down
                angle = MathF.PI / 2;
                (hu, hv) = (hv, hu);
            }
        }

        return new Ellipse(centre, hu + r + pad, hv + r + pad, angle);
    }

    /// <summary> The middle of the line's box along a direction, and the box's half length and half width. </summary>
    private static (Vector2 Centre, float HalfU, float HalfV) Extent(IReadOnlyList<(float X, float Y)> line, Vector2 mean, float angle)
    {
        var u = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var v = new Vector2(-u.Y, u.X);
        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        foreach (var p in line)
        {
            var d  = V(p) - mean;
            var pu = Vector2.Dot(d, u);
            var pv = Vector2.Dot(d, v);
            minU = MathF.Min(minU, pu);
            maxU = MathF.Max(maxU, pu);
            minV = MathF.Min(minV, pv);
            maxV = MathF.Max(maxV, pv);
        }

        return (mean + u * ((minU + maxU) / 2) + v * ((minV + maxV) / 2), (maxU - minU) / 2, (maxV - minV) / 2);
    }

    /// <summary> Dashes along a closed outline: the pieces to draw, <paramref name="dash"/> long with <paramref name="gap"/> between. </summary>
    public static List<(Vector2 A, Vector2 B)> Dashes(IReadOnlyList<Vector2> closed, float dash, float gap)
    {
        var result = new List<(Vector2, Vector2)>();
        if (closed.Count < 2 || dash <= 0)
            return result;
        var on   = true;
        var left = dash;
        for (var i = 0; i < closed.Count; ++i)
        {
            var a = closed[i];
            var b = closed[(i + 1) % closed.Count];
            var d = Vector2.Distance(a, b);
            var t = 0f;
            while (d - t > 1e-4f)
            {
                var step = MathF.Min(left, d - t);
                var p0   = Vector2.Lerp(a, b, t / d);
                var p1   = Vector2.Lerp(a, b, (t + step) / d);
                if (on)
                    result.Add((p0, p1));
                t    += step;
                left -= step;
                if (left <= 1e-4f)
                {
                    on   = !on;
                    left = on ? dash : gap;
                }
            }
        }

        return result;
    }

    public static Vector2 V((float X, float Y) p)
        => new(p.X, p.Y);
}
