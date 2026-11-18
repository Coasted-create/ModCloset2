using System.Numerics;
using ModCloset2.Core;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary> A perspective camera: where it stands, where it looks, how wide it sees. Units are the model's (metres). </summary>
public sealed class PhotoCamera
{
    public Vector3 Position { get; init; }
    public Vector3 Right { get; init; }
    public Vector3 Up { get; init; }

    /// <summary> The direction the camera looks in. </summary>
    public Vector3 Forward { get; init; }

    /// <summary> Tangent of half the horizontal and vertical field of view. </summary>
    public float TanX { get; init; }
    public float TanY { get; init; }

    /// <summary> Nothing nearer than this is drawn. </summary>
    public float Near { get; init; } = 0.01f;

    public string Description { get; init; } = string.Empty;

    /// <summary> The point in the camera's frame: x right, y up, z depth (positive in front). </summary>
    public Vector3 ToView(Vector3 p)
    {
        var d = p - Position;
        return new Vector3(Vector3.Dot(d, Right), Vector3.Dot(d, Up), Vector3.Dot(d, Forward));
    }

    /// <summary> Picture coordinates (0..1 across, 0..1 down) of a point in the camera's frame. </summary>
    public Vector2 ToPicture(Vector3 view)
        => new(0.5f + view.X / (view.Z * TanX) * 0.5f, 0.5f - view.Y / (view.Z * TanY) * 0.5f);

    public static PhotoCamera LookAt(Vector3 target, Vector3 fromDirection, float distance, float fovY, float aspect, string description)
    {
        var d       = Vector3.Normalize(fromDirection);
        var forward = -d;
        var right   = Vector3.Cross(forward, Vector3.UnitY);
        right = right.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(right);
        var up   = Vector3.Cross(right, forward);
        var tanY = MathF.Tan(fovY / 2);
        return new PhotoCamera
        {
            Position = target + d * distance, Right = right, Up = up, Forward = forward, TanY = tanY, TanX = tanY * aspect,
            Near = Math.Max(0.002f, distance * 0.05f), Description = description,
        };
    }
}

/// <summary>
/// Aims the camera by the piece itself: at the surface of the piece's own meshes (never the skin it carries), from a
/// direction that suits where it sits on the body, close enough that it fills about four fifths of the picture.
/// <list type="bullet">
/// <item> A piece in two halves far apart (gloves, bracelets, earrings) is framed by one half, the larger one, seen from
/// its own side: a pair of gloves a metre apart would otherwise be two specks. </item>
/// <item> Clothes from the front and a little to the side; shoes and rings a little more from above; earrings from the
/// side they are on; things on the back (capes, wings) from behind. </item>
/// <item> A small piece (a bracelet) shows a little of the skin right around it, so its wrist reads as a wrist. </item>
/// </list>
/// Works in the pools' scratch arrays: nothing per triangle is allocated.
/// </summary>
public static class Framing
{
    /// <summary> The share of the picture the piece fills (in its larger direction). </summary>
    public const float Fill = 0.8f;

    /// <summary> Vertical field of view: a long lens, like a product photo (little distortion, the piece seen whole). </summary>
    public const float FieldOfView = 22f * MathF.PI / 180f;

    /// <summary> A half is framed alone when the gap between the halves is this many times the width of one half... </summary>
    public const float SplitGap = 1.5f;

    /// <summary> ... and at least this far apart (metres): studs and horns on the face are one piece, earrings two. </summary>
    public const float SplitMinGap = 0.1f;

    /// <summary> Pieces smaller than this (metres, across) show a little of the body they sit on. </summary>
    public const float SmallPiece = 0.2f;

    public static PhotoCamera Frame(PhotoScene scene, int width, int height, PhotoPools pools)
    {
        var aspect = width / (float)height;
        var piece  = true;
        var count  = Centres(scene, piece, pools, out var facing, out var total);
        if (count == 0)
        {
            piece = false; // nothing of its own to show: the skin it carries, at least
            count = Centres(scene, piece, pools, out facing, out total);
        }

        if (count == 0)
            return PhotoCamera.LookAt(new Vector3(0, 1, 0), Vector3.UnitZ, 3, FieldOfView, aspect, "nothing to see");

        // One half of a pair, when the halves are far apart for their size.
        var side = Side(pools.X.AsSpan(0, count), pools.Area.AsSpan(0, count), pools);
        var back = total > 0 && facing / total < -0.35;
        var (direction, words) = Direction(scene.Socket, side, back);

        var n = Corners(scene, piece, side, pools);
        n = Context(scene, n, pools);
        return Fit(pools.Points.AsSpan(0, n), direction, aspect, words);
    }

    /// <summary> Each triangle's centre x and area into the pools; the area-weighted facing (normal z) and total area. </summary>
    private static int Centres(PhotoScene scene, bool piece, PhotoPools pools, out double facing, out double total)
    {
        facing = 0;
        total  = 0;
        var n = 0;
        PhotoPools.Ensure(ref pools.X, scene.Arena.IndexCount / 3);
        PhotoPools.Ensure(ref pools.Area, scene.Arena.IndexCount / 3);
        foreach (var item in scene.Items)
        {
            if (item.Surface.Kind == SurfaceKind.Hidden || item.IsPiece != piece)
                continue;
            var m = item.Mesh;
            for (var t = m.IndexStart; t + 2 < m.IndexEnd; t += 3)
            {
                Vector3 a = m.Positions[m.Indices[t]], b = m.Positions[m.Indices[t + 1]], c = m.Positions[m.Indices[t + 2]];
                var cross = Vector3.Cross(b - a, c - a);
                var area  = cross.Length() / 2;
                if (!(area > 0) || !float.IsFinite(area))
                    continue;
                pools.X[n]    = (a.X + b.X + c.X) / 3;
                pools.Area[n] = area;
                ++n;
                facing += cross.Z / 2;
                total  += area;
            }
        }

        return n;
    }

    /// <summary>
    /// The vertices of the triangles framed (one half only when <paramref name="side"/> is not 0), each once, into the
    /// pools (a vertex is marked with the picture's stamp, so nothing has to be cleared).
    /// </summary>
    private static int Corners(PhotoScene scene, bool piece, int side, PhotoPools pools)
    {
        var count = scene.Arena.VertexCount;
        PhotoPools.Ensure(ref pools.Points, count);
        if (pools.Mark.Length < count)
        {
            pools.Mark  = new int[count];
            pools.Stamp = 0;
        }

        var stamp = ++pools.Stamp;
        var n = 0;
        foreach (var item in scene.Items)
        {
            if (item.Surface.Kind == SurfaceKind.Hidden || item.IsPiece != piece)
                continue;
            var m = item.Mesh;
            for (var t = m.IndexStart; t + 2 < m.IndexEnd; t += 3)
            {
                int a = m.Indices[t], b = m.Indices[t + 1], c = m.Indices[t + 2];
                if (side != 0 && MathF.Sign(m.Positions[a].X + m.Positions[b].X + m.Positions[c].X) != side)
                    continue;
                foreach (var v in (ReadOnlySpan<int>)[a, b, c])
                    if (pools.Mark[v] != stamp)
                    {
                        pools.Mark[v]     = stamp;
                        pools.Points[n++] = m.Positions[v];
                    }
            }
        }

        return n;
    }

    /// <summary>
    /// For a small piece, the skin the model carries right around it (within a quarter of its size), added after the
    /// piece's own corners: a bracelet alone at four fifths of the picture fills it with a wall of forearm that reads
    /// as a hip; with the wrist and the start of the hand it reads as a bracelet. Large pieces get none.
    /// </summary>
    private static int Context(PhotoScene scene, int n, PhotoPools pools)
    {
        if (n == 0)
            return n;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < n; ++i)
        {
            min = Vector3.Min(min, pools.Points[i]);
            max = Vector3.Max(max, pools.Points[i]);
        }

        var size = (max - min).Length();
        if (size >= SmallPiece)
            return n;
        var reach = size * 0.25f;
        foreach (var item in scene.Items)
        {
            if (item.IsPiece || item.Surface.Kind == SurfaceKind.Hidden)
                continue;
            var m = item.Mesh;
            for (var v = m.VertexStart; v < m.VertexStart + m.VertexCount && n < pools.Points.Length; ++v)
                if (pools.Mark[v] != pools.Stamp && Vector3.Distance(Vector3.Clamp(m.Positions[v], min, max), m.Positions[v]) <= reach)
                    pools.Points[n++] = m.Positions[v];
        }

        return n;
    }

    /// <summary>
    /// +1 or -1 when the piece is two halves far apart along x (the side with more surface), 0 when it is one piece.
    /// Each half's inner edge and width are taken from area-weighted percentiles, so a stray triangle does not decide.
    /// </summary>
    public static int Side(ReadOnlySpan<float> x, ReadOnlySpan<float> area, PhotoPools pools)
    {
        PhotoPools.Ensure(ref pools.SortX, x.Length);
        PhotoPools.Ensure(ref pools.SortArea, x.Length);
        // the right side is measured before the left one reuses the sort arrays
        var r = Spread(x, area, pools, right: true, out var rn);
        var (rIn, rOut) = (At(pools, rn, r, 0.02), At(pools, rn, r, 0.98));
        var l = Spread(x, area, pools, right: false, out var ln);
        var (lIn, lOut) = (At(pools, ln, l, 0.98), At(pools, ln, l, 0.02));
        double total = r + l;
        if (total <= 0 || r < total * 0.15 || l < total * 0.15)
            return 0;
        var gap  = rIn - lIn;
        var half = Math.Max(rOut - rIn, lIn - lOut);
        if (gap <= Math.Max(SplitGap * half, SplitMinGap))
            return 0;
        return r >= l ? 1 : -1;
    }

    /// <summary> One side's centres and areas, sorted by x, in the pools' sort arrays; its total area and count. </summary>
    private static double Spread(ReadOnlySpan<float> x, ReadOnlySpan<float> area, PhotoPools pools, bool right, out int count)
    {
        count = 0;
        double total = 0;
        for (var i = 0; i < x.Length; ++i)
            if (right ? x[i] > 0 : x[i] < 0)
            {
                pools.SortX[count]    = x[i];
                pools.SortArea[count] = area[i];
                total += area[i];
                ++count;
            }

        Array.Sort(pools.SortX, pools.SortArea, 0, count);
        return total;
    }

    /// <summary> The x below which a share <paramref name="q"/> of the area lies (over the last spread). </summary>
    private static float At(PhotoPools pools, int count, double total, double q)
    {
        double run = 0;
        for (var i = 0; i < count; ++i)
        {
            run += pools.SortArea[i];
            if (run >= total * q)
                return pools.SortX[i];
        }

        return count > 0 ? pools.SortX[count - 1] : 0;
    }

    /// <summary> Where the camera looks from (a direction from the piece), in words for the log. </summary>
    public static (Vector3 Direction, string Words) Direction(Socket socket, int side, bool back)
    {
        float yaw, pitch;
        string words;
        var s = side != 0 ? side : 1;
        if (back)
        {
            (yaw, pitch, words) = (180 - 25, 8, "from behind");
        }
        else if (socket == Socket.Ears && side != 0)
        {
            (yaw, pitch, words) = (s * 75, 5, "from the side it is on");
        }
        else if (side != 0)
        {
            // one glove, one bracelet, one ring: from the front and its own side, a little above
            (yaw, pitch, words) = (s * 50, socket == Socket.Rings ? 25 : 15, "one of the pair, from its side");
        }
        else
        {
            (yaw, pitch, words) = socket switch
            {
                Socket.Feet  => (s * 30f, 22f, "front, a little above"),
                Socket.Rings => (s * 40f, 25f, "front, from above"),
                Socket.Head  => (s * 25f, 10f, "front, a little to the side"),
                Socket.Ears  => (s * 35f, 5f, "front, to the side"),
                _            => (s * 22f, 6f, "front, a little to the side"),
            };
        }

        var y = yaw * MathF.PI / 180;
        var p = pitch * MathF.PI / 180;
        return (new Vector3(MathF.Sin(y) * MathF.Cos(p), MathF.Sin(p), MathF.Cos(y) * MathF.Cos(p)), words);
    }

    /// <summary>
    /// Places the camera along <paramref name="direction"/> so every point is inside the picture and the piece fills
    /// <see cref="Fill"/> of it in its larger direction, centred. Perspective makes the fit depend on the distance, so it
    /// is solved a few times; each pass centres the picture on what the last one saw.
    /// </summary>
    public static PhotoCamera Fit(ReadOnlySpan<Vector3> points, Vector3 direction, float aspect, string words)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        var target   = (min + max) / 2;
        var distance = Math.Max(0.05f, (max - min).Length() * 2);
        var camera   = PhotoCamera.LookAt(target, direction, distance, FieldOfView, aspect, words);
        for (var pass = 0; pass < 4; ++pass)
        {
            // the distance at which the farthest-out point just touches the frame
            var need = 0f;
            foreach (var p in points)
            {
                var d = p - target;
                float x = Vector3.Dot(d, camera.Right), y = Vector3.Dot(d, camera.Up), z = Vector3.Dot(d, -camera.Forward);
                need = Math.Max(need, Math.Max(MathF.Abs(x) / (Fill * camera.TanX), MathF.Abs(y) / (Fill * camera.TanY)) + z);
            }

            distance = Math.Max(need, 0.02f);
            camera   = PhotoCamera.LookAt(target, direction, distance, FieldOfView, aspect, words);

            // centre what is seen
            float lo = float.MaxValue, hi = float.MinValue, bottom = float.MaxValue, top = float.MinValue;
            foreach (var p in points)
            {
                var v = camera.ToView(p);
                if (v.Z <= 1e-4f)
                    continue;
                var sx = v.X / (v.Z * camera.TanX);
                var sy = v.Y / (v.Z * camera.TanY);
                lo = Math.Min(lo, sx);
                hi = Math.Max(hi, sx);
                bottom = Math.Min(bottom, sy);
                top = Math.Max(top, sy);
            }

            if (lo > hi)
                break;
            target += camera.Right * ((lo + hi) / 2 * distance * camera.TanX) + camera.Up * ((bottom + top) / 2 * distance * camera.TanY);
        }

        return PhotoCamera.LookAt(target, direction, distance, FieldOfView, aspect, words);
    }
}
