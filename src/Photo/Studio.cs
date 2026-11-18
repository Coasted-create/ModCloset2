using System.Numerics;
using ModCloset2.Colours;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary> A finished picture: RGBA bytes (sRGB), rows top to bottom (the pools' array: valid until the next picture). </summary>
public sealed class PhotoImage
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Rgba { get; init; } = Array.Empty<byte>();

    /// <summary> Output pixels the scene covers at least half (0: only background). </summary>
    public int Covered { get; init; }

    /// <summary> Output pixels where the piece can be told from the background (<see cref="Studio.SeenDifference"/>). </summary>
    public int Seen { get; init; }

    /// <summary> The piece was dark and thin, and got the lighter backdrop. </summary>
    public bool LightBackdrop { get; init; }

    /// <summary> Why the picture was not finished ("" when it was): <see cref="PhotoProblems.TooMuchDrawing"/>. </summary>
    public string Refused { get; init; } = string.Empty;

    /// <summary> The numbers behind it. </summary>
    public string RefusedDetail { get; init; } = string.Empty;
}

/// <summary>
/// Draws a scene: a depth-tested software renderer at twice the size (or <c>supersample</c>), scaled down at the end
/// for clean edges. Three passes: (1) every opaque or cut-out triangle into a "which triangle is here" buffer, with the
/// depth test, and the alpha test where the material cuts out (lace, holes); (2) each covered pixel is shaded once; (3)
/// see-through surfaces (glass) are blended over, farthest first. Then the light's brightness is mapped to the screen,
/// the result laid over the card background and scaled down. All buffers are the pools'; the stop is looked at inside
/// every pass; a scene that would take far more drawing than any piece needs is refused.
/// </summary>
public static class Studio
{
    /// <summary> The closet's card colour (docs/DESIGN.md "card") at the edges, a little lighter behind the piece. </summary>
    public static readonly Vector3 BackgroundEdge = Srgb(0x26, 0x1D, 0x2D);
    public static readonly Vector3 BackgroundCentre = Srgb(0x3E, 0x31, 0x4A);

    /// <summary>
    /// Behind a thin dark piece (eyelashes, a black choker, a thin chain): the same card colours, lifted, so a dark
    /// outline has something to stand against. Bulky dark clothes keep the normal card: they read by their shape.
    /// </summary>
    public static readonly Vector3 LightEdge = Srgb(0x3A, 0x2D, 0x45);
    public static readonly Vector3 LightCentre = Srgb(0x7A, 0x6A, 0x88);

    /// <summary> A piece darker than this on average (linear light, after the tone curve) counts as dark... </summary>
    public const float DarkPiece = 0.035f;

    /// <summary> ... and thinner than this share of the picture as thin. </summary>
    public const float ThinPiece = 0.06f;

    /// <summary>
    /// A pixel counts as seen when the piece covers at least <see cref="SeenCoverage"/> of it and it differs from the
    /// background by <see cref="SeenDifference"/> (sum over R, G, B, 0..255). See-through glows, which the photo cannot
    /// draw (the scroll shader's discs in the samples' ring slot), cover less and are not counted.
    /// </summary>
    public const int SeenDifference = 36;

    public const float SeenCoverage = 0.35f;

    /// <summary> One mesh made ready (its vertices' view and picture positions and tangents are in the pools). </summary>
    internal sealed class Prepared
    {
        public required SceneItem Item;
        public required PhotoPools Pools;
        public bool Cutout;

        /// <summary> The face paint to lay on a face's skin (null elsewhere). </summary>
        public TexImage? Decal;
    }

    /// <param name="maxOverdraw"> Rasterising work allowed, as a multiple of the pixels (<see cref="PhotoBudget.MaxOverdraw"/>). </param>
    public static PhotoImage Draw(PhotoScene scene, PhotoCamera camera, int width, int height, int supersample, PhotoPools pools, CancellationToken stop,
        long maxOverdraw = 80)
    {
        var ss = Math.Clamp(supersample, 1, 4);
        int w = width * ss, h = height * ss;
        var buffer = new Buffers(w, h, pools);
        var count  = scene.Arena.VertexCount;
        PhotoPools.Ensure(ref pools.View, count);
        PhotoPools.Ensure(ref pools.Screen, count);
        PhotoPools.Ensure(ref pools.Tangents, count);
        PhotoPools.Ensure(ref pools.Bitangents, count);
        var prepared = new List<Prepared>(scene.Items.Count);
        foreach (var item in scene.Items)
            if (item.Surface.Kind != SurfaceKind.Hidden)
                prepared.Add(Prepare(item, camera, w, h, pools, scene.DecalImage));

        // 1. which triangle is in front, per pixel
        var budget = maxOverdraw * w * h;
        long work  = 0;
        for (var i = 0; i < prepared.Count; ++i)
        {
            var p = prepared[i];
            if (p.Item.Surface.Blend)
                continue;
            var sink = new VisibilitySink { Buffer = buffer, Item = i, Prepared = p };
            var mesh = p.Item.Mesh;
            var idx  = mesh.Indices;
            for (var t = mesh.IndexStart; t + 2 < mesh.IndexEnd; t += 3)
            {
                if (((t - mesh.IndexStart) & 0x3FFF) == 0)
                    stop.ThrowIfCancellationRequested();
                if (!Screen(pools, idx[t], idx[t + 1], idx[t + 2], camera, out var tri) || !Faces(p, tri))
                    continue;
                work += Box(tri, w, h);
                if (work > budget)
                    return Refuse(width, height, $"{work:N0} pixels of work for {(long)w * h:N0}");
                sink.Triangle = t;
                Rasteriser.Fill(tri, w, h, ref sink);
            }
        }

        // 2. shade each covered pixel once
        var light = new Lighting(camera);
        for (var y = 0; y < h; ++y)
        {
            if ((y & 31) == 0)
                stop.ThrowIfCancellationRequested();
            for (var x = 0; x < w; ++x)
            {
                var at = y * w + x;
                var i  = buffer.Item[at];
                if (i < 0)
                    continue;
                buffer.Colour[at]   = Shade(prepared[i], buffer.Triangle[at], buffer.W0[at], buffer.W1[at], camera, light, out _);
                buffer.Coverage[at] = 1;
            }
        }

        // 3. see-through surfaces over what is drawn, farthest triangles first
        var blended = pools.Blended;
        blended.Clear();
        for (var i = 0; i < prepared.Count; ++i)
        {
            if (!prepared[i].Item.Surface.Blend)
                continue;
            var mesh = prepared[i].Item.Mesh;
            var idx  = mesh.Indices;
            for (var t = mesh.IndexStart; t + 2 < mesh.IndexEnd; t += 3)
                blended.Add(((pools.View[idx[t]].Z + pools.View[idx[t + 1]].Z + pools.View[idx[t + 2]].Z) / 3, i, t));
        }

        blended.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));
        for (var k = 0; k < blended.Count; ++k)
        {
            if ((k & 0x3FFF) == 0)
                stop.ThrowIfCancellationRequested();
            var (_, i, t) = blended[k];
            var p   = prepared[i];
            var idx = p.Item.Mesh.Indices;
            if (!Screen(pools, idx[t], idx[t + 1], idx[t + 2], camera, out var tri) || !Faces(p, tri))
                continue;
            work += Box(tri, w, h);
            if (work > budget)
                return Refuse(width, height, $"{work:N0} pixels of work for {(long)w * h:N0}");
            var sink = new BlendSink { Buffer = buffer, Prepared = p, Triangle = t, Camera = camera, Light = light };
            Rasteriser.Fill(tri, w, h, ref sink);
        }

        stop.ThrowIfCancellationRequested();
        return Finish(buffer, width, height, ss, pools);
    }

    private static PhotoImage Refuse(int width, int height, string detail)
        => new() { Width = width, Height = height, Refused = PhotoProblems.TooMuchDrawing, RefusedDetail = detail };

    /// <summary> The pixels a triangle's box covers inside the picture: what filling it costs. </summary>
    private static long Box(in ScreenTriangle t, int w, int h)
    {
        var x0 = Math.Clamp(Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)), 0, w);
        var x1 = Math.Clamp(Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)), 0, w);
        var y0 = Math.Clamp(Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y)), 0, h);
        var y1 = Math.Clamp(Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)), 0, h);
        return (long)(x1 - x0 + 1) * (long)(y1 - y0 + 1);
    }

    // ------------------------------------------------------------------ preparing meshes

    private static Prepared Prepare(SceneItem item, PhotoCamera camera, int w, int h, PhotoPools pools, TexImage? decal)
    {
        var mesh = item.Mesh;
        for (var v = mesh.VertexStart; v < mesh.VertexStart + mesh.VertexCount; ++v)
        {
            var view = camera.ToView(mesh.Positions[v]);
            var pic  = view.Z > 1e-6f ? camera.ToPicture(view) : new Vector2(-1, -1);
            pools.View[v]   = view;
            pools.Screen[v] = new Vector2(pic.X * w, pic.Y * h);
        }

        TangentFrame(mesh, pools);
        var s = item.Surface;
        return new Prepared
        {
            Item = item, Pools = pools,
            Cutout = (s.Kind is SurfaceKind.Table or SurfaceKind.Painted && (s.Normal is not null || s.Diffuse is not null)
                   || s.Kind is SurfaceKind.Hair && s.Normal is not null) && mesh.HasUvs,
            Decal = s.Kind == SurfaceKind.SkinTextured && s.IsFace ? decal : null,
        };
    }

    /// <summary>
    /// Per-vertex tangent and bitangent from the texture coordinates (the direction u and v grow along the surface), so
    /// the normal map can bend the normal. Computed rather than read: the model's own tangents come in several packings,
    /// and these are right for any of them.
    /// </summary>
    private static void TangentFrame(PhotoMesh m, PhotoPools pools)
    {
        var t = pools.Tangents;
        var b = pools.Bitangents;
        Array.Clear(t, m.VertexStart, m.VertexCount);
        Array.Clear(b, m.VertexStart, m.VertexCount);
        if (!m.HasUvs)
            return;
        var idx = m.Indices;
        for (var k = m.IndexStart; k + 2 < m.IndexEnd; k += 3)
        {
            int i0 = idx[k], i1 = idx[k + 1], i2 = idx[k + 2];
            var e1 = m.Positions[i1] - m.Positions[i0];
            var e2 = m.Positions[i2] - m.Positions[i0];
            var d1 = m.Uvs[i1] - m.Uvs[i0];
            var d2 = m.Uvs[i2] - m.Uvs[i0];
            var r  = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(r) < 1e-12f)
                continue;
            var f  = 1 / r;
            var tu = (e1 * d2.Y - e2 * d1.Y) * f;
            var tv = (e2 * d1.X - e1 * d2.X) * f;
            if (!float.IsFinite(tu.X + tu.Y + tu.Z + tv.X + tv.Y + tv.Z))
                continue;
            t[i0] += tu; t[i1] += tu; t[i2] += tu;
            b[i0] += tv; b[i1] += tv; b[i2] += tv;
        }
    }

    private static bool Screen(PhotoPools pools, int a, int b, int c, PhotoCamera camera, out ScreenTriangle tri)
    {
        tri = default;
        var near = camera.Near;
        var view = pools.View;
        // a triangle reaching behind the camera is left out (only the skin a model carries can come that close)
        if (view[a].Z <= near || view[b].Z <= near || view[c].Z <= near)
            return false;
        tri = new ScreenTriangle(pools.Screen[a], pools.Screen[b], pools.Screen[c], 1 / view[a].Z, 1 / view[b].Z, 1 / view[c].Z);
        return true;
    }

    /// <summary> False for a triangle seen from behind when its material hides back faces (the game's own culling). </summary>
    private static bool Faces(Prepared p, in ScreenTriangle t)
    {
        if (p.Item.Surface.TwoSided && p.Item.Surface.Kind is not SurfaceKind.Skin)
            return true;
        // picture y grows downwards: a counter-clockwise triangle in the model shows clockwise here
        var area = (t.B.X - t.A.X) * (t.C.Y - t.A.Y) - (t.B.Y - t.A.Y) * (t.C.X - t.A.X);
        return area < 0;
    }

    // ------------------------------------------------------------------ the buffers

    /// <summary> The pools' drawing arrays at the supersampled size, cleared for a new picture. </summary>
    internal readonly struct Buffers
    {
        public readonly int Width, Height;
        public readonly float[] Depth;    // 1 / depth, 0 = nothing
        public readonly int[] Item;       // prepared mesh, -1 = nothing
        public readonly int[] Triangle;   // first index of the triangle
        public readonly float[] W0, W1;   // corner weights
        public readonly Vector3[] Colour; // linear light, premultiplied by coverage
        public readonly float[] Coverage;

        public Buffers(int w, int h, PhotoPools pools)
        {
            var n = w * h;
            PhotoPools.Ensure(ref pools.Depth, n);
            PhotoPools.Ensure(ref pools.Item, n);
            PhotoPools.Ensure(ref pools.Triangle, n);
            PhotoPools.Ensure(ref pools.W0, n);
            PhotoPools.Ensure(ref pools.W1, n);
            PhotoPools.Ensure(ref pools.Colour, n);
            PhotoPools.Ensure(ref pools.Coverage, n);
            Array.Clear(pools.Depth, 0, n);
            Array.Fill(pools.Item, -1, 0, n);
            Array.Clear(pools.Colour, 0, n);
            Array.Clear(pools.Coverage, 0, n);
            (Width, Height, Depth, Item, Triangle, W0, W1, Colour, Coverage) =
                (w, h, pools.Depth, pools.Item, pools.Triangle, pools.W0, pools.W1, pools.Colour, pools.Coverage);
        }
    }

    private struct VisibilitySink : IPixelSink
    {
        public Buffers Buffer;
        public Prepared Prepared;
        public int Item;
        public int Triangle;

        public void Pixel(int x, int y, float invZ, float w0, float w1)
        {
            var at = y * Buffer.Width + x;
            if (invZ <= Buffer.Depth[at])
                return;
            if (Prepared.Cutout && Surfaces.Opacity(Prepared, Triangle, w0, w1) < 0.5f)
                return;
            Buffer.Depth[at]    = invZ;
            Buffer.Item[at]     = Item;
            Buffer.Triangle[at] = Triangle;
            Buffer.W0[at]       = w0;
            Buffer.W1[at]       = w1;
        }
    }

    private struct BlendSink : IPixelSink
    {
        public Buffers Buffer;
        public Prepared Prepared;
        public int Triangle;
        public PhotoCamera Camera;
        public Lighting Light;

        public void Pixel(int x, int y, float invZ, float w0, float w1)
        {
            var at = y * Buffer.Width + x;
            if (invZ <= Buffer.Depth[at])
                return;
            var c = Shade(Prepared, Triangle, w0, w1, Camera, Light, out var alpha);
            alpha = Math.Clamp(alpha, 0, 1);
            Buffer.Colour[at]   = c * alpha + Buffer.Colour[at] * (1 - alpha);
            Buffer.Coverage[at] = alpha + Buffer.Coverage[at] * (1 - alpha);
        }
    }

    // ------------------------------------------------------------------ shading

    /// <summary> The light of one pixel of a triangle, linear (not yet mapped to the screen). </summary>
    private static Vector3 Shade(Prepared p, int t, float w0, float w1, PhotoCamera camera, Lighting light, out float alpha)
    {
        var mesh = p.Item.Mesh;
        int a = mesh.Indices[t], b = mesh.Indices[t + 1], c = mesh.Indices[t + 2];
        var w2 = 1 - w0 - w1;
        var position = mesh.Positions[a] * w0 + mesh.Positions[b] * w1 + mesh.Positions[c] * w2;
        var normal   = mesh.Normals[a] * w0 + mesh.Normals[b] * w1 + mesh.Normals[c] * w2;
        normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
        var toEye = Vector3.Normalize(camera.Position - position);

        // seen from behind (the inside of a sleeve, a skirt from below): light the side we see
        var flip = Vector3.Dot(normal, toEye) < 0;
        if (flip)
            normal = -normal;

        var look = Surfaces.Look(p, a, b, c, w0, w1, w2, normal, flip);
        alpha = look.Alpha;
        return light.Light(look, toEye);
    }

    // ------------------------------------------------------------------ finishing

    /// <summary>
    /// Scales down, then lays the piece over the backdrop: the card's, or the lighter one behind a thin dark piece. Counts
    /// the pixels where the piece can be told from what is behind it, which decides whether there is a picture at all.
    /// </summary>
    private static PhotoImage Finish(Buffers buffer, int w, int h, int ss, PhotoPools pools)
    {
        PhotoPools.Ensure(ref pools.Lit, w * h);
        PhotoPools.Ensure(ref pools.LitCoverage, w * h);
        PhotoPools.Ensure(ref pools.Rgba, w * h * 4);
        var inv = 1f / (ss * ss);
        var covered = 0;
        double luminance = 0, weight = 0;
        for (var y = 0; y < h; ++y)
        for (var x = 0; x < w; ++x)
        {
            var sum      = Vector3.Zero;
            var coverage = 0f;
            for (var sy = 0; sy < ss; ++sy)
            for (var sx = 0; sx < ss; ++sx)
            {
                var at  = (y * ss + sy) * buffer.Width + x * ss + sx;
                var cov = buffer.Coverage[at];
                if (cov <= 0)
                    continue;
                sum      += ToneMap(buffer.Colour[at] / cov) * cov;
                coverage += cov;
            }

            sum      *= inv;
            coverage *= inv;
            pools.Lit[y * w + x]         = sum;
            pools.LitCoverage[y * w + x] = coverage;
            if (coverage >= 0.5f)
                ++covered;
            luminance += 0.2126 * sum.X + 0.7152 * sum.Y + 0.0722 * sum.Z;
            weight    += coverage;
        }

        var dark  = weight > 0 && luminance / weight < DarkPiece && covered < ThinPiece * w * h;
        var (edge, centreColour) = dark ? (LightEdge, LightCentre) : (BackgroundEdge, BackgroundCentre);
        var centre = new Vector2(0.5f, 0.46f);
        var rgba   = pools.Rgba;
        var seen   = 0;
        for (var y = 0; y < h; ++y)
        for (var x = 0; x < w; ++x)
        {
            // the card colour, a soft light behind the middle of the picture
            var d        = Vector2.Distance(new Vector2((x + 0.5f) / w, (y + 0.5f) / h * 1.1f), centre * new Vector2(1, 1.1f));
            var bg       = Vector3.Lerp(centreColour, edge, Math.Clamp(d / 0.62f, 0, 1));
            var coverage = pools.LitCoverage[y * w + x];
            var final    = pools.Lit[y * w + x] + bg * (1 - coverage);
            var o = (y * w + x) * 4;
            rgba[o]     = ToByte(final.X);
            rgba[o + 1] = ToByte(final.Y);
            rgba[o + 2] = ToByte(final.Z);
            rgba[o + 3] = 255;
            if (coverage >= SeenCoverage && Math.Abs(rgba[o] - ToByte(bg.X)) + Math.Abs(rgba[o + 1] - ToByte(bg.Y)) + Math.Abs(rgba[o + 2] - ToByte(bg.Z)) >= SeenDifference)
                ++seen;
        }

        return new PhotoImage { Width = w, Height = h, Rgba = rgba, Covered = covered, Seen = seen, LightBackdrop = dark };
    }

    /// <summary>
    /// A texture shown as it is (brief 13: a texture-only part with nothing to put it on): cropped to the area that is
    /// painted, fitted into four fifths of the picture, over the card colours. A texture of one channel (a face paint, a
    /// mask) is drawn as ink in a light tone, its value as how much ink. "Painted" is where the alpha or, for one channel,
    /// the value is above a sixteenth; a texture painted everywhere shows whole.
    /// </summary>
    public static PhotoImage Flat(TexImage tex, bool oneChannel, int width, int height, PhotoPools pools)
    {
        // the painted area, on a coarse grid so a large texture costs little
        int tw = tex.Width, th = tex.Height;
        int step = Math.Max(1, Math.Max(tw, th) / 256);
        int x0 = tw, y0 = th, x1 = -1, y1 = -1;
        for (var y = 0; y < th; y += step)
        for (var x = 0; x < tw; x += step)
        {
            var o = tex.At(x, y);
            var v = oneChannel ? tex.Rgba[o] : tex.Rgba[o + 3];
            if (v < 16)
                continue;
            x0 = Math.Min(x0, x);
            y0 = Math.Min(y0, y);
            x1 = Math.Max(x1, x);
            y1 = Math.Max(y1, y);
        }

        PhotoPools.Ensure(ref pools.Rgba, width * height * 4);
        var rgba = pools.Rgba;
        var ink  = new Vector3(0.82f, 0.78f, 0.84f);
        var centre = new Vector2(0.5f, 0.46f);
        var seen = 0;
        float cw = 0, ch = 0, scale = 0, ox = 0, oy = 0;
        if (x1 >= x0)
        {
            x1 = Math.Min(tw - 1, x1 + step);
            y1 = Math.Min(th - 1, y1 + step);
            cw = x1 - x0 + 1;
            ch = y1 - y0 + 1;
            scale = Math.Min(width * 0.8f / cw, height * 0.8f / ch);
            ox = (width - cw * scale) / 2;
            oy = (height - ch * scale) / 2;
        }

        for (var y = 0; y < height; ++y)
        for (var x = 0; x < width; ++x)
        {
            var d  = Vector2.Distance(new Vector2((x + 0.5f) / width, (y + 0.5f) / height * 1.1f), centre * new Vector2(1, 1.1f));
            var bg = Vector3.Lerp(BackgroundCentre, BackgroundEdge, Math.Clamp(d / 0.62f, 0, 1));
            var colour = bg;
            if (scale > 0)
            {
                var u = (x + 0.5f - ox) / scale;
                var v = (y + 0.5f - oy) / scale;
                if (u >= 0 && v >= 0 && u < cw && v < ch)
                {
                    var sample = Surfaces.Sample(tex, new Vector2((x0 + u) / tw, (y0 + v) / th));
                    var (c, a) = oneChannel
                        ? (ink, sample.X)
                        : (new Vector3(ToLinear(sample.X), ToLinear(sample.Y), ToLinear(sample.Z)), sample.W);
                    colour = Vector3.Lerp(bg, c, Math.Clamp(a, 0, 1));
                }
            }

            var o = (y * width + x) * 4;
            rgba[o]     = ToByte(colour.X);
            rgba[o + 1] = ToByte(colour.Y);
            rgba[o + 2] = ToByte(colour.Z);
            rgba[o + 3] = 255;
            if (Math.Abs(rgba[o] - ToByte(bg.X)) + Math.Abs(rgba[o + 1] - ToByte(bg.Y)) + Math.Abs(rgba[o + 2] - ToByte(bg.Z)) >= SeenDifference)
                ++seen;
        }

        return new PhotoImage { Width = width, Height = height, Rgba = rgba, Covered = seen, Seen = seen };
    }

    /// <summary> Bright light rolls off softly instead of clipping (a filmic curve, ACES fitted by K. Narkowicz). </summary>
    public static Vector3 ToneMap(Vector3 x)
    {
        static float F(float v)
        {
            v = Math.Max(0, v);
            return Math.Clamp(v * (2.51f * v + 0.03f) / (v * (2.43f * v + 0.59f) + 0.14f), 0, 1);
        }

        return new Vector3(F(x.X), F(x.Y), F(x.Z));
    }

    public static Vector3 Srgb(byte r, byte g, byte b)
        => new(ToLinear(r / 255f), ToLinear(g / 255f), ToLinear(b / 255f));

    public static float ToLinear(float c)
        => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static byte ToByte(float linear)
    {
        var c = Math.Clamp(linear, 0, 1);
        var s = c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(s * 255), 0, 255);
    }
}
