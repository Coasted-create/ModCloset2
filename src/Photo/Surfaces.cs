using System.Numerics;
using ModCloset2.Colours;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary> What a pixel of a surface is made of (colours linear, as the game's tables keep them). </summary>
public struct SurfaceLook
{
    public Vector3 Base;
    public Vector3 Normal;
    public Vector3 Specular;
    public Vector3 Emissive;
    public float Roughness;
    public float Metalness;
    public float Occlusion;
    public float Alpha;
    public bool Matte;
}

/// <summary>
/// The game's character shaders, as near as the photo gets them (after Penumbra's MaterialExporter, which documents
/// what the textures hold):
/// <list type="bullet">
/// <item> Dawntrail gear (<c>character.shpk</c>): the index map's red picks a pair of table rows, green blends the pair
/// (full green = the pair's first row); the row gives diffuse, specular, emissive, roughness and metalness. The normal
/// map's blue is the opacity (cut out at one half), its red and green the bumps. The mask's blue darkens creases
/// (occlusion), its red scales the shine. A colour picture, when the material has one, multiplies the row's colour. </item>
/// <item> Older gear (<c>characterlegacy.shpk</c>): the normal map's alpha picks the row (16 rows, blended between
/// neighbours), and the colour picture multiplies it. </item>
/// <item> Materials without a table: their colour picture. </item>
/// <item> Skin: one plain mannequin colour. Materials the mod does not ship: a neutral grey. </item>
/// </list>
/// </summary>
public static class Surfaces
{
    /// <summary>
    /// The mannequin: a quiet mid grey with a little of the card's violet, matte. Darker than most cloth so white pieces
    /// stand out from it, lighter than black ones; the body must never draw the eye before the piece.
    /// </summary>
    public static readonly Vector3 Mannequin = Studio.Srgb(0x7C, 0x73, 0x7E);

    /// <summary> Surfaces the mod does not ship (the game's own materials): a neutral grey, a little cooler than the mannequin. </summary>
    public static readonly Vector3 Unknown = Studio.Srgb(0x8E, 0x8C, 0x94);

    /// <summary>
    /// Hair, tails, brows and lashes: their colour comes from the character, so one fixed neutral hair colour (a soft
    /// dark brown that the studio light shows as a mid brown) and a second fixed tone where the hair's highlights mask says (the normal map's blue), as the game
    /// blends its hair and highlight colours.
    /// </summary>
    public static readonly Vector3 HairColour = Studio.Srgb(0x3F, 0x2F, 0x29);
    public static readonly Vector3 HairHighlight = Studio.Srgb(0x8C, 0x74, 0x62);

    /// <summary> Skin that is the piece (a face, makeup, a body): the texture's colour times one fixed neutral tone. </summary>
    public static readonly Vector3 SkinTone = new(1.0f, 0.97f, 0.95f);

    /// <summary> An eye's colour where its mask says, a muted grey-blue; face paint in a fixed deep rose (the player picks it in the game). </summary>
    public static readonly Vector3 EyeColour = Studio.Srgb(0x6E, 0x86, 0x94);
    public static readonly Vector3 DecalColour = Studio.Srgb(0x9A, 0x3E, 0x52);

    /// <summary> How strongly the normal map bends the light (a small picture does not need every thread). </summary>
    public const float BumpStrength = 0.8f;

    internal static float Opacity(Studio.Prepared p, int t, float w0, float w1)
    {
        var m = p.Item.Mesh;
        var uv = m.Uvs[m.Indices[t]] * w0 + m.Uvs[m.Indices[t + 1]] * w1 + m.Uvs[m.Indices[t + 2]] * (1 - w0 - w1);
        var s = p.Item.Surface;
        if (s.Kind == SurfaceKind.Hair)
            return s.Normal is { } hn ? Sample(hn, uv).W : 1; // the hair shader keeps its strands' alpha in the normal map's alpha
        if (s.Normal is { } n)
            return Sample(n, uv).Z;
        if (s.Diffuse is { } d)
            return Sample(d, uv).W;
        return 1;
    }

    internal static SurfaceLook Look(Studio.Prepared p, int a, int b, int c, float w0, float w1, float w2, Vector3 normal, bool flipped)
    {
        var s = p.Item.Surface;
        var look = new SurfaceLook { Normal = normal, Roughness = 0.75f, Occlusion = 1, Alpha = 1, Specular = new Vector3(0.03f) };
        switch (s.Kind)
        {
            case SurfaceKind.Skin:
                look.Base  = Mannequin;
                look.Matte = true;
                return look;
            case SurfaceKind.Missing:
                look.Base = Unknown;
                return look;
        }

        var m  = p.Item.Mesh;
        var uv = m.HasUvs ? m.Uvs[a] * w0 + m.Uvs[b] * w1 + m.Uvs[c] * w2 : Vector2.Zero;
        var nrm = s.Normal is { } nm ? Sample(nm, uv) : new Vector4(0.5f, 0.5f, 1, 1);

        // ---- colour
        if (s.Kind == SurfaceKind.Hair)
        {
            look.Base      = Vector3.Lerp(HairColour, HairHighlight, Math.Clamp(nrm.Z, 0, 1));
            look.Roughness = 0.45f;
            look.Specular  = new Vector3(0.05f);
            look.Alpha     = nrm.W;
            if (s.Mask is { } hm)
                look.Occlusion = 0.45f + 0.55f * Sample(hm, uv).W; // the mask's alpha darkens the strands inside the hair
            Bump(ref look, p, a, b, c, w0, w1, w2, normal, flipped, nrm);
            return look;
        }

        if (s.Kind == SurfaceKind.Iris)
        {
            var colour = s.Diffuse is { } eye ? Linear(Sample(eye, uv)) : EyeColour;
            var tint   = s.Mask is { } em ? Sample(em, uv).Z : 0;
            look.Base      = colour * Vector3.Lerp(Vector3.One, EyeColour * 1.6f, tint);
            look.Roughness = 0.2f;
            look.Specular  = new Vector3(0.05f);
            Bump(ref look, p, a, b, c, w0, w1, w2, normal, flipped, nrm);
            return look;
        }

        if (s.Kind == SurfaceKind.SkinTextured)
        {
            // the skin texture already carries the skin's colour and what is painted on it; the normal map's blue says
            // where the character's skin colour applies (the neutral tone here)
            var colour = s.Diffuse is { } skin ? Linear(Sample(skin, uv)) : Mannequin;
            look.Base      = colour * Vector3.Lerp(Vector3.One, SkinTone, Math.Clamp(nrm.Z, 0, 1));
            look.Roughness = 0.6f;
            look.Specular  = new Vector3(0.025f);
            if (s.IsFace && p.Decal is { } decal)
            {
                var uv2 = m.Uvs2[a] * w0 + m.Uvs2[b] * w1 + m.Uvs2[c] * w2;
                // the paint lies where the face's second coordinates fall inside the texture; outside it there is none
                if (uv2.X is >= 0 and < 1 && uv2.Y is >= 0 and < 1)
                    look.Base = Vector3.Lerp(look.Base, DecalColour, Math.Clamp(Sample(decal, uv2).X, 0, 1));
            }

            Bump(ref look, p, a, b, c, w0, w1, w2, normal, flipped, nrm);
            return look;
        }

        if (s.Kind == SurfaceKind.Table && s.Rows.Length > 0)
        {
            var row = s.RowFromNormalAlpha ? LegacyRow(s.Rows, nrm.W) : s.Index is { } idx ? IndexRow(s.Rows, idx, uv) : s.Rows[0];
            look.Base      = row.Diffuse;
            look.Specular  = row.Specular * row.SpecularStrength;
            look.Emissive  = row.Emissive;
            look.Roughness = row.Roughness;
            look.Metalness = row.Metalness;
            if (s.Diffuse is { } diffuse)
                look.Base *= Linear(Sample(diffuse, uv));
        }
        else
        {
            look.Base = s.Diffuse is { } diffuse ? Linear(Sample(diffuse, uv)) : Unknown;
        }

        if (s.Specular is { } spec)
            look.Specular *= Linear(Sample(spec, uv));

        // the mask: blue darkens creases, red scales the shine (Dawntrail's meaning; older masks are left alone)
        if (s.Mask is { } mask && !s.RowFromNormalAlpha)
        {
            var mk = Sample(mask, uv);
            look.Occlusion = 0.35f + 0.65f * mk.Z;
            look.Specular *= mk.X;
        }

        look.Alpha = s.Normal is not null ? nrm.Z : s.Diffuse is { } d2 ? Sample(d2, uv).W : 1;

        Bump(ref look, p, a, b, c, w0, w1, w2, normal, flipped, nrm);
        return look;
    }

    /// <summary> The normal map bends the normal in the plane the texture coordinates span. </summary>
    private static void Bump(ref SurfaceLook look, Studio.Prepared p, int a, int b, int c, float w0, float w1, float w2, Vector3 normal, bool flipped, Vector4 nrm)
    {
        var s = p.Item.Surface;
        var m = p.Item.Mesh;
        if (s.Normal is not null && m.HasUvs)
        {
            var t  = p.Pools.Tangents[a] * w0 + p.Pools.Tangents[b] * w1 + p.Pools.Tangents[c] * w2;
            var bt = p.Pools.Bitangents[a] * w0 + p.Pools.Bitangents[b] * w1 + p.Pools.Bitangents[c] * w2;
            if (flipped)
            {
                t  = -t;
                bt = -bt;
            }

            // Gram-Schmidt against the normal; a degenerate frame keeps the plain normal
            t  -= normal * Vector3.Dot(normal, t);
            bt -= normal * Vector3.Dot(normal, bt);
            if (t.LengthSquared() > 1e-16f && bt.LengthSquared() > 1e-16f)
            {
                float x = (nrm.X * 2 - 1) * BumpStrength, y = (nrm.Y * 2 - 1) * BumpStrength;
                var z = MathF.Sqrt(Math.Max(0.05f, 1 - x * x - y * y));
                var bent = Vector3.Normalize(t) * x + Vector3.Normalize(bt) * y + normal * z;
                if (bent.LengthSquared() > 1e-12f)
                    look.Normal = Vector3.Normalize(bent);
            }
        }
    }

    /// <summary>
    /// The row of a Dawntrail index map at a point, filtered: each of the four nearest texels gives its own blended row,
    /// and those are mixed by distance. Filtering the index values themselves would mix row numbers (rows 3 and 7 make
    /// row 5, a colour that is not there).
    /// </summary>
    private static TableRow IndexRow(TableRow[] rows, TexImage index, Vector2 uv)
    {
        var (x0, y0, x1, y1, fx, fy) = Taps(index, uv);
        var r00 = TexelRow(rows, index, x0, y0);
        var r10 = TexelRow(rows, index, x1, y0);
        var r01 = TexelRow(rows, index, x0, y1);
        var r11 = TexelRow(rows, index, x1, y1);
        return Mix(Mix(r00, r10, fx), Mix(r01, r11, fx), fy);
    }

    private static TableRow TexelRow(TableRow[] rows, TexImage index, int x, int y)
    {
        var o    = index.At(x, y);
        var pair = Math.Clamp((int)MathF.Round(index.Rgba[o] / 17f), 0, rows.Length / 2 - 1);
        var keep = index.Rgba[o + 1] / 255f; // weight of the pair's first row
        return Mix(rows[pair * 2 + 1], rows[pair * 2], keep);
    }

    /// <summary> Older tables: the normal map's alpha (0..255) points at a row, blended with the next one. </summary>
    private static TableRow LegacyRow(TableRow[] rows, float alpha)
    {
        var t  = Math.Clamp(alpha * 255 / 17f, 0, rows.Length - 1);
        var r0 = (int)t;
        var r1 = Math.Min(r0 + 1, rows.Length - 1);
        return Mix(rows[r0], rows[r1], t - r0);
    }

    private static TableRow Mix(in TableRow a, in TableRow b, float t)
        => new(Vector3.Lerp(a.Diffuse, b.Diffuse, t), Vector3.Lerp(a.Specular, b.Specular, t), Vector3.Lerp(a.Emissive, b.Emissive, t),
            a.Roughness + (b.Roughness - a.Roughness) * t, a.Metalness + (b.Metalness - a.Metalness) * t,
            a.SpecularStrength + (b.SpecularStrength - a.SpecularStrength) * t);

    /// <summary> The four texels around a point (texture coordinates wrap, as the game's samplers do) and the blend between them. </summary>
    private static (int X0, int Y0, int X1, int Y1, float Fx, float Fy) Taps(TexImage img, Vector2 uv)
    {
        var u  = (uv.X - MathF.Floor(uv.X)) * img.Width - 0.5f;
        var v  = (uv.Y - MathF.Floor(uv.Y)) * img.Height - 0.5f;
        var fu = MathF.Floor(u);
        var fv = MathF.Floor(v);
        var x0 = Wrap((int)fu, img.Width);
        var y0 = Wrap((int)fv, img.Height);
        return (x0, y0, (x0 + 1) % img.Width, (y0 + 1) % img.Height, u - fu, v - fv);
    }

    private static int Wrap(int i, int n)
        => (i % n + n) % n;

    /// <summary> A bilinear sample, 0..1 per channel. </summary>
    public static Vector4 Sample(TexImage img, Vector2 uv)
    {
        if (img.Width <= 0 || img.Height <= 0 || !float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
            return Vector4.One;
        var (x0, y0, x1, y1, fx, fy) = Taps(img, uv);
        var top    = Vector4.Lerp(Texel(img, x0, y0), Texel(img, x1, y0), fx);
        var bottom = Vector4.Lerp(Texel(img, x0, y1), Texel(img, x1, y1), fx);
        return Vector4.Lerp(top, bottom, fy);
    }

    /// <summary> One texel; a block-compressed texture decodes its 4 × 4 block the first time one of it is asked for. </summary>
    private static Vector4 Texel(TexImage img, int x, int y)
    {
        var o = img.At(x, y);
        var d = img.Rgba;
        return new Vector4(d[o], d[o + 1], d[o + 2], d[o + 3]) * (1 / 255f);
    }

    /// <summary> A colour picture's sRGB value as linear light. </summary>
    private static Vector3 Linear(Vector4 c)
        => new(Studio.ToLinear(c.X), Studio.ToLinear(c.Y), Studio.ToLinear(c.Z));
}
