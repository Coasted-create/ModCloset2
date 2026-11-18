namespace ModCloset2.Colours;

// Pure code (no game, no Dalamud): linked into tests/ColourTests.

/// <summary>
/// Gives an area a new colour while keeping the shading between its rows (new). Works in OKLab: each row keeps its
/// lightness step from the area's average (scaled to the room the new colour leaves, so the order of shades never flips
/// and nothing runs past black or white), and its hue and chroma step (scaled down when the new colour is greyer). Out of
/// gamut colours lose chroma, never lightness or hue. Finish values (specular, gloss, metalness, sheen…) stay as they
/// are. Metal rows take the colour as a tint: they keep half of their own brightness and get a tinted shine.
/// </summary>
public static class Recolour
{
    /// <summary>
    /// How much a row's lightness step may grow when the new colour leaves more room than the old one. 1: never grows (a
    /// near-black speck in a dark grey part, made white, stays a light grey instead of turning mid grey: build 0.5 had 2).
    /// </summary>
    public const float MaxStretch = 1f;

    /// <summary> Metal keeps this much of its own lightness. </summary>
    public const float MetalOwnLight = 0.5f;

    /// <summary> Chroma of the tint a metal takes (of the new colour's). </summary>
    public const float MetalTint = 0.85f;

    /// <summary> The row's new colour on screen. </summary>
    public static Rgb Colour(Rgb row, OkLab areaMean, Rgb target, bool metal)
    {
        var r = ColourMath.ToOk(row);
        var t = ColourMath.ToOk(target);
        var l = MapLightness(r.L, areaMean.L, t.L);

        var cm = areaMean.Chroma;
        var ct = t.Chroma;
        var s  = cm < 0.02f ? 1f : Math.Clamp(ct / cm, 0f, 1f);
        float a, b;
        if (metal)
        {
            l = r.L * MetalOwnLight + l * (1 - MetalOwnLight);
            a = t.A * MetalTint + (r.A - areaMean.A) * s;
            b = t.B * MetalTint + (r.B - areaMean.B) * s;
        }
        else
        {
            a = t.A + (r.A - areaMean.A) * s;
            b = t.B + (r.B - areaMean.B) * s;
        }

        return ColourMath.FromOk(new OkLab(l, a, b));
    }

    /// <summary>
    /// The row's lightness after the area's average moved from <paramref name="mean"/> to <paramref name="target"/>:
    /// steps above the average share the room above, steps below share the room below. Keeps order, stays in 0..1.
    /// </summary>
    public static float MapLightness(float row, float mean, float target)
    {
        var d = row - mean;
        if (d >= 0)
        {
            var k = Math.Min(MaxStretch, (1 - target) / Math.Max(1 - mean, 0.02f));
            return Math.Clamp(target + d * k, 0, 1);
        }
        else
        {
            var k = Math.Min(MaxStretch, target / Math.Max(mean, 0.02f));
            return Math.Clamp(target + d * k, 0, 1);
        }
    }

    /// <summary> A row's table values with a new colour (diffuse; for metal also a tinted specular). </summary>
    public static float[] Row(RowInfo row, OkLab areaMean, Rgb target, bool metal)
    {
        var values = (float[])row.Values.Clone();
        var c = Colour(row.Colour, areaMean, target, metal);
        (values[RowField.DiffuseR], values[RowField.DiffuseG], values[RowField.DiffuseB]) = ColourMath.ToTable(c);
        if (metal)
        {
            // the shine of a metal has its colour: keep the specular's brightness, give it the tint
            var spec = ColourMath.FromTable(values[RowField.SpecularR], values[RowField.SpecularG], values[RowField.SpecularB]);
            var so   = ColourMath.ToOk(spec);
            var t    = ColourMath.ToOk(target);
            var tinted = ColourMath.FromOk(new OkLab(so.L, t.A * MetalTint, t.B * MetalTint));
            (values[RowField.SpecularR], values[RowField.SpecularG], values[RowField.SpecularB]) = ColourMath.ToTable(tinted);
        }

        return values;
    }

    /// <summary>
    /// The colour that lights a row up on the character while the user points at its area: magenta stands out on almost
    /// anything; on pink or purple a bright green (old plugin, Colourist.Highlight).
    /// </summary>
    public static Rgb Light(Rgb under)
    {
        var (h, s, _) = ColourMath.ToHsl(under);
        return s > 0.25f && h is > 260 and < 345 ? new Rgb(0.2f, 1f, 0.3f) : new Rgb(1f, 0.1f, 0.9f);
    }

    /// <summary> A row's values lit up: the light colour, glowing a little (emissive 0.6 of it, as the old plugin). </summary>
    public static float[] Lit(RowInfo row)
    {
        var values = (float[])row.Values.Clone();
        var light  = Light(row.Colour);
        var (r, g, b) = ColourMath.ToTable(light);
        values[RowField.DiffuseR]  = r;
        values[RowField.DiffuseG]  = g;
        values[RowField.DiffuseB]  = b;
        values[RowField.EmissiveR] = r * 0.6f;
        values[RowField.EmissiveG] = g * 0.6f;
        values[RowField.EmissiveB] = b * 0.6f;
        return values;
    }
}
