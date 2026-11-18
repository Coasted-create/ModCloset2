using System.Numerics;

namespace ModCloset2.Ui.Canvas;

/// <summary> Points along a rounded rectangle's edge, and dashes along a polyline (for backends without dashed lines). </summary>
public static class Outline
{
    /// <summary> The edge of a rounded rectangle, clockwise from the top-left, as a closed polyline. </summary>
    public static List<Vector2> RoundRect(Rect r, float radius, Corners corners, int stepsPerCorner = 6)
    {
        var rad = Math.Clamp(radius, 0, Math.Min(r.W, r.H) / 2);
        var pts = new List<Vector2>(4 * (stepsPerCorner + 1));
        void Corner(Corners c, Vector2 center, float from)
        {
            if ((corners & c) == 0 || rad <= 0)
            {
                pts.Add(center + new Vector2(MathF.Cos(from + MathF.PI / 4), MathF.Sin(from + MathF.PI / 4)) * rad * MathF.Sqrt(2));
                return;
            }

            for (var i = 0; i <= stepsPerCorner; ++i)
            {
                var a = from + MathF.PI / 2 * i / stepsPerCorner;
                pts.Add(center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * rad);
            }
        }

        Corner(Corners.TopLeft, new Vector2(r.X + rad, r.Y + rad), MathF.PI);
        Corner(Corners.TopRight, new Vector2(r.Right - rad, r.Y + rad), MathF.PI * 1.5f);
        Corner(Corners.BottomRight, new Vector2(r.Right - rad, r.Bottom - rad), 0);
        Corner(Corners.BottomLeft, new Vector2(r.X + rad, r.Bottom - rad), MathF.PI / 2);
        return pts;
    }

    /// <summary> Dash segments (pairs of points) along a closed polyline. </summary>
    public static IEnumerable<(Vector2 A, Vector2 B)> Dashes(IReadOnlyList<Vector2> pts, float dash, float gap)
    {
        if (pts.Count < 2 || dash <= 0)
            yield break;
        var on    = true;
        var left  = dash;
        for (var i = 0; i < pts.Count; ++i)
        {
            var a   = pts[i];
            var b   = pts[(i + 1) % pts.Count];
            var len = Vector2.Distance(a, b);
            var at  = 0f;
            while (len - at > 0.0001f)
            {
                var step = Math.Min(left, len - at);
                var p0   = Vector2.Lerp(a, b, at / len);
                var p1   = Vector2.Lerp(a, b, (at + step) / len);
                if (on)
                    yield return (p0, p1);
                at   += step;
                left -= step;
                if (left <= 0.0001f)
                {
                    on   = !on;
                    left = on ? dash : gap;
                }
            }
        }
    }
}
