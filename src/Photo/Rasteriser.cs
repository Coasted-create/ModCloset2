using System.Numerics;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary> A triangle on the picture: its corners in pixels, and 1 / depth at each corner (for perspective). </summary>
public readonly record struct ScreenTriangle(Vector2 A, Vector2 B, Vector2 C, float InvZA, float InvZB, float InvZC);

/// <summary> Called for every pixel a triangle covers, with perspective-correct weights of its corners. </summary>
public interface IPixelSink
{
    /// <param name="invZ"> 1 / depth at the pixel (larger is nearer). </param>
    /// <param name="w0"> Weight of corner A; <paramref name="w1"/> of B; C has 1 - w0 - w1. </param>
    void Pixel(int x, int y, float invZ, float w0, float w1);
}

/// <summary>
/// Fills triangles pixel by pixel: every pixel whose centre is inside (edges shared by two triangles go to one of them,
/// so nothing is drawn twice or left as a crack). The corner weights are corrected for perspective: interpolated as
/// weight / depth on the picture and divided by the interpolated 1 / depth, so textures do not swim on slanted
/// surfaces. Depth testing and shading are the sink's business.
/// </summary>
public static class Rasteriser
{
    /// <summary> Returns the number of pixels visited. </summary>
    public static int Fill<T>(in ScreenTriangle t, int width, int height, ref T sink) where T : struct, IPixelSink
    {
        var area = Edge(t.A, t.B, t.C);
        if (!(MathF.Abs(area) > 1e-9f))
            return 0;

        var minX = Math.Max(0, (int)MathF.Floor(Math.Min(t.A.X, Math.Min(t.B.X, t.C.X))));
        var maxX = Math.Min(width - 1, (int)MathF.Ceiling(Math.Max(t.A.X, Math.Max(t.B.X, t.C.X))));
        var minY = Math.Max(0, (int)MathF.Floor(Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y))));
        var maxY = Math.Min(height - 1, (int)MathF.Ceiling(Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y))));
        if (minX > maxX || minY > maxY)
            return 0;

        var inv  = 1 / area;
        var sign = MathF.Sign(area);
        var n    = 0;
        for (var y = minY; y <= maxY; ++y)
        for (var x = minX; x <= maxX; ++x)
        {
            // Each edge is measured the same way whichever triangle it belongs to (Shared), so two triangles that share
            // it get exactly opposite values and the tie rule gives a pixel on it to one of them only.
            var p  = new Vector2(x + 0.5f, y + 0.5f);
            var e0 = Shared(t.B, t.C, p) * sign;
            var e1 = Shared(t.C, t.A, p) * sign;
            var e2 = Shared(t.A, t.B, p) * sign;
            if (!Inside(e0, t.B, t.C, sign) || !Inside(e1, t.C, t.A, sign) || !Inside(e2, t.A, t.B, sign))
                continue;
            float l0 = e0 * sign * inv, l1 = e1 * sign * inv, l2 = 1 - l0 - l1;
            var invZ = l0 * t.InvZA + l1 * t.InvZB + l2 * t.InvZC;
            if (!(invZ > 0))
                continue;
            sink.Pixel(x, y, invZ, l0 * t.InvZA / invZ, l1 * t.InvZB / invZ);
            ++n;
        }

        return n;
    }

    /// <summary> <see cref="Edge"/> with the edge's two ends always taken in the same order, so (a, b) and (b, a) give exact negatives. </summary>
    private static float Shared(Vector2 a, Vector2 b, Vector2 p)
        => a.X < b.X || a.X == b.X && a.Y < b.Y ? Edge(a, b, p) : -Edge(b, a, p);

    /// <summary> Twice the signed area of (a, b, p). </summary>
    private static float Edge(Vector2 a, Vector2 b, Vector2 p)
        => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    /// <summary>
    /// Inside, or on an edge that belongs to this triangle (the "top-left" rule, stated for either winding): a pixel
    /// centre exactly on an edge two triangles share is drawn by one of them only.
    /// </summary>
    private static bool Inside(float edge, Vector2 from, Vector2 to, float sign)
    {
        if (edge > 0)
            return true;
        if (edge < 0)
            return false;
        var dy = (to.Y - from.Y) * sign;
        var dx = (to.X - from.X) * sign;
        return dy < 0 || dy == 0 && dx > 0;
    }
}
