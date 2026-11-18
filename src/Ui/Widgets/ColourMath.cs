namespace ModCloset2.Ui.Widgets;

/// <summary> Colour conversions for the picker (hue, saturation, value; all 0..1) and a brightness test. </summary>
public static class ColourMath
{
    public static uint FromHsv(float h, float s, float v)
    {
        h = (h % 1 + 1) % 1 * 6;
        var i = (int)MathF.Floor(h) % 6;
        var f = h - MathF.Floor(h);
        var p = v * (1 - s);
        var q = v * (1 - s * f);
        var t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return ((uint)MathF.Round(r * 255) << 16) | ((uint)MathF.Round(g * 255) << 8) | (uint)MathF.Round(b * 255);
    }

    public static (float H, float S, float V) ToHsv(uint rgb)
    {
        var r = ((rgb >> 16) & 0xFF) / 255f;
        var g = ((rgb >> 8) & 0xFF) / 255f;
        var b = (rgb & 0xFF) / 255f;
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var d   = max - min;
        var h   = 0f;
        if (d > 0)
        {
            if (max == r)
                h = (g - b) / d % 6;
            else if (max == g)
                h = (b - r) / d + 2;
            else
                h = (r - g) / d + 4;
            h /= 6;
            if (h < 0)
                h += 1;
        }

        return (h, max <= 0 ? 0 : d / max, max);
    }

    /// <summary> Perceived lightness 0..1 (dark swatches and segments get a thin outline so they stay visible). </summary>
    public static float Lightness(uint rgb)
        => (0.299f * ((rgb >> 16) & 0xFF) + 0.587f * ((rgb >> 8) & 0xFF) + 0.114f * (rgb & 0xFF)) / 255f;
}
