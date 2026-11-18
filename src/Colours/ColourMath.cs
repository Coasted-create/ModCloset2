namespace ModCloset2.Colours;

// Pure code (no game, no Dalamud): linked into tests/ColourTests.

/// <summary> A colour as it looks on screen (sRGB, 0..1). Ported from the old plugin's Colours/ColourMath.cs. </summary>
public readonly record struct Rgb(float R, float G, float B)
{
    public static Rgb FromBytes(byte r, byte g, byte b)
        => new(r / 255f, g / 255f, b / 255f);

    public static Rgb FromHex(uint rgb)
        => FromBytes((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary> 0xRRGGBB. </summary>
    public uint Hex
        => (uint)Byte(R) << 16 | (uint)Byte(G) << 8 | Byte(B);

    public Rgb Clamped
        => new(Math.Clamp(R, 0, 1), Math.Clamp(G, 0, 1), Math.Clamp(B, 0, 1));

    private static byte Byte(float v)
        => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);

    public override string ToString()
        => $"#{Hex:X6}";
}

/// <summary> A colour in OKLab (L 0..1, a and b about -0.4..0.4): distances and steps look even to the eye. </summary>
public readonly record struct OkLab(float L, float A, float B)
{
    public float Chroma
        => MathF.Sqrt(A * A + B * B);

    /// <summary> Hue angle in radians. </summary>
    public float Hue
        => MathF.Atan2(B, A);

    public static OkLab FromLch(float l, float c, float h)
        => new(l, c * MathF.Cos(h), c * MathF.Sin(h));
}

/// <summary> Conversions and distances. Ported from the old plugin (Lab, HSL, table values, Glamourer keys); OKLab is new. </summary>
public static class ColourMath
{
    // ---- sRGB <-> linear ----

    public static double ToLinear(double c)
        => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    public static double ToSrgb(double c)
        => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;

    // ---- CIE Lab (old plugin; kept for its distance scale: about 2 barely visible, above 20 clearly another colour) ----

    public readonly record struct Lab(double L, double A, double B);

    public static Lab ToLab(Rgb c)
    {
        double r = ToLinear(Math.Clamp(c.R, 0, 1)), g = ToLinear(Math.Clamp(c.G, 0, 1)), b = ToLinear(Math.Clamp(c.B, 0, 1));
        var x = (r * 0.4124 + g * 0.3576 + b * 0.1805) / 0.95047;
        var y = r * 0.2126 + g * 0.7152 + b * 0.0722;
        var z = (r * 0.0193 + g * 0.1192 + b * 0.9505) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16 / 116.0;
        return new Lab(116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    public static Rgb FromLab(Lab lab)
    {
        var fy = (lab.L + 16) / 116;
        var fx = fy + lab.A / 500;
        var fz = fy - lab.B / 200;
        static double Inv(double t) => t * t * t > 0.008856 ? t * t * t : (t - 16 / 116.0) / 7.787;
        double x = Inv(fx) * 0.95047, y = Inv(fy), z = Inv(fz) * 1.08883;
        var r = x * 3.2406 + y * -1.5372 + z * -0.4986;
        var g = x * -0.9689 + y * 1.8758 + z * 0.0415;
        var b = x * 0.0557 + y * -0.2040 + z * 1.0570;
        return new Rgb((float)ToSrgb(Math.Clamp(r, 0, 1)), (float)ToSrgb(Math.Clamp(g, 0, 1)), (float)ToSrgb(Math.Clamp(b, 0, 1)));
    }

    /// <summary> How different two colours look (CIE76 ΔE: about 2 is barely visible, above 20 clearly another colour). </summary>
    public static double Distance(Rgb a, Rgb b)
    {
        var x = ToLab(a);
        var y = ToLab(b);
        return Math.Sqrt((x.L - y.L) * (x.L - y.L) + (x.A - y.A) * (x.A - y.A) + (x.B - y.B) * (x.B - y.B));
    }

    // ---- HSL (old plugin) ----

    public static (float H, float S, float L) ToHsl(Rgb c)
    {
        float r = c.R, g = c.G, b = c.B;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l   = (max + min) / 2;
        if (max - min < 1e-5f)
            return (0, 0, l);
        var d = max - min;
        var s = l > 0.5f ? d / (2 - max - min) : d / (max + min);
        float h;
        if (max == r)
            h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g)
            h = (b - r) / d + 2;
        else
            h = (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Rgb FromHsl(float h, float s, float l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        if (s < 1e-5f)
            return new Rgb(l, l, l);
        var q = l < 0.5f ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        float Hue(float t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            return t < 1 / 6f ? p + (q - p) * 6 * t : t < 0.5f ? q : t < 2 / 3f ? p + (q - p) * (2 / 3f - t) * 6 : p;
        }

        var hh = h / 360;
        return new Rgb(Hue(hh + 1 / 3f), Hue(hh), Hue(hh - 1 / 3f));
    }

    // ---- OKLab (Björn Ottosson's matrices) ----

    public static OkLab ToOk(Rgb c)
    {
        var r = ToLinear(Math.Clamp(c.R, 0, 1));
        var g = ToLinear(Math.Clamp(c.G, 0, 1));
        var b = ToLinear(Math.Clamp(c.B, 0, 1));
        return ToOkFromLinear(r, g, b);
    }

    public static OkLab ToOkFromLinear(double r, double g, double b)
    {
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return new OkLab(
            (float)(0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s),
            (float)(1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s),
            (float)(0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s));
    }

    /// <summary> Linear sRGB of an OKLab colour, unclamped (values outside 0..1 are out of gamut). </summary>
    public static (double R, double G, double B) OkToLinear(OkLab c)
    {
        var l = c.L + 0.3963377774 * c.A + 0.2158037573 * c.B;
        var m = c.L - 0.1055613458 * c.A - 0.0638541728 * c.B;
        var s = c.L - 0.0894841775 * c.A - 1.2914855480 * c.B;
        l = l * l * l;
        m = m * m * m;
        s = s * s * s;
        return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
               -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
               -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    public static bool InGamut(OkLab c, double eps = 1e-4)
    {
        var (r, g, b) = OkToLinear(c);
        return r >= -eps && r <= 1 + eps && g >= -eps && g <= 1 + eps && b >= -eps && b <= 1 + eps;
    }

    /// <summary>
    /// The colour on screen. Out-of-gamut colours keep their lightness and hue and lose chroma until they fit (no
    /// clipping per channel, which would shift the hue and break the order of shades).
    /// </summary>
    public static Rgb FromOk(OkLab c)
    {
        var l = Math.Clamp(c.L, 0f, 1f);
        c = c with { L = l };
        if (!InGamut(c))
        {
            float lo = 0, hi = 1;
            for (var i = 0; i < 24; ++i)
            {
                var mid = (lo + hi) / 2;
                if (InGamut(new OkLab(l, c.A * mid, c.B * mid)))
                    lo = mid;
                else
                    hi = mid;
            }

            c = new OkLab(l, c.A * lo, c.B * lo);
        }

        var (r, g, b) = OkToLinear(c);
        return new Rgb((float)ToSrgb(Math.Clamp(r, 0, 1)), (float)ToSrgb(Math.Clamp(g, 0, 1)), (float)ToSrgb(Math.Clamp(b, 0, 1)));
    }

    /// <summary> Distance in OKLab, × 100 (about 2 is barely visible, like ΔE). </summary>
    public static float OkDistance(OkLab x, OkLab y)
    {
        float dl = x.L - y.L, da = x.A - y.A, db = x.B - y.B;
        return 100 * MathF.Sqrt(dl * dl + da * da + db * db);
    }

    public static float OkDistance(Rgb x, Rgb y)
        => OkDistance(ToOk(x), ToOk(y));

    /// <summary> The average of colours by weight, taken in OKLab. </summary>
    public static OkLab Mean(IEnumerable<(OkLab Colour, double Weight)> colours)
    {
        double l = 0, a = 0, b = 0, w = 0;
        foreach (var (c, weight) in colours)
        {
            l += c.L * weight;
            a += c.A * weight;
            b += c.B * weight;
            w += weight;
        }

        return w <= 0 ? new OkLab(0, 0, 0) : new OkLab((float)(l / w), (float)(a / w), (float)(b / w));
    }

    // ---- the game's colour tables ----

    /// <summary> Colour tables keep colours squared (roughly linear); this is the value for a colour on screen. </summary>
    public static (float R, float G, float B) ToTable(Rgb c)
        => (c.R * c.R, c.G * c.G, c.B * c.B);

    public static Rgb FromTable(float r, float g, float b)
        => new(MathF.Sqrt(Math.Clamp(r, 0, 1)), MathF.Sqrt(Math.Clamp(g, 0, 1)), MathF.Sqrt(Math.Clamp(b, 0, 1)));

    /// <summary> A colour table row as Glamourer names it: row pairs 1 to 16, A and B (row 30 → "16A"). </summary>
    public static string RowName(int row)
        => $"{row / 2 + 1}{(row % 2 == 0 ? 'A' : 'B')}";

    /// <summary> Glamourer's key of an advanced dye: human (1), slot, material, row, as 16 hex digits. </summary>
    public static string MaterialKey(int slot, int material, int row)
        => ((ulong)(1u << 24 | (uint)slot << 16 | (uint)material << 8 | (uint)row)).ToString("X16");

    public static bool TryParseMaterialKey(string key, out int slot, out int material, out int row)
    {
        slot = material = row = 0;
        if (!ulong.TryParse(key, System.Globalization.NumberStyles.HexNumber, null, out var v) || (v >> 24 & 0xFF) != 1)
            return false;
        slot     = (int)(v >> 16 & 0xFF);
        material = (int)(v >> 8 & 0xFF);
        row      = (int)(v & 0xFF);
        return true;
    }

    // ---- colour sets (old plugin; Surprise builds on these) ----

    /// <summary> The three colours of an outfit built around <paramref name="seed"/> in a scheme. </summary>
    public static (Rgb Main, Rgb Second, Rgb Accent) Harmony(Rgb seed, Scheme scheme)
    {
        var (h, s, l) = ToHsl(seed);
        // a grey seed has no hue to build on: give the partners a soft one
        var grey = s < 0.08f;
        Rgb C(float dh, float ss, float ll)
            => FromHsl(h + dh, grey && dh != 0 ? Math.Max(ss, 0.25f) : ss, ll);

        return scheme switch
        {
            Scheme.Matching     => (seed, C(30, s * 0.85f, Mix(l, 0.42f, 0.4f)), C(-35, Math.Min(1, s * 1.1f), Mix(l, 0.3f, 0.5f))),
            Scheme.OneColour    => (seed, C(0, s * 0.8f, Math.Clamp(l + 0.22f, 0.15f, 0.88f)), C(0, Math.Min(1, s * 1.1f), Math.Clamp(l - 0.25f, 0.08f, 0.8f))),
            Scheme.Contrast     => (seed, C(0, s * 0.25f, l > 0.5f ? 0.2f : 0.85f), C(180, Math.Max(0.55f, s), 0.5f)),
            Scheme.ThreeColours => (seed, C(120, s * 0.8f, l), C(240, Math.Max(0.5f, s), Math.Clamp(l, 0.35f, 0.6f))),
            Scheme.Pastel       => (C(0, Math.Min(s, 0.5f), 0.8f), C(40, Math.Min(s, 0.45f), 0.86f), C(180, Math.Min(s, 0.5f), 0.72f)),
            Scheme.Dark         => (C(0, Math.Min(s, 0.6f), 0.2f), C(0, Math.Min(s, 0.3f), 0.1f), C(0, Math.Max(0.6f, s), 0.45f)),
            _                   => (seed, seed, seed),
        };

        static float Mix(float a, float b, float t) => a + (b - a) * t;
    }

    /// <summary> A pleasant random starting colour (not too grey, not too dark or bright). </summary>
    public static Rgb RandomSeed(Random random)
        => FromHsl(random.Next(360), 0.35f + (float)random.NextDouble() * 0.5f, 0.3f + (float)random.NextDouble() * 0.35f);
}

/// <summary> How the colours of an outfit go together (old plugin). </summary>
public enum Scheme
{
    /// <summary> Neighbouring hues (rose, coral, plum). </summary>
    Matching,

    /// <summary> One hue, light to dark. </summary>
    OneColour,

    /// <summary> A colour, a calm partner and its opposite as the accent. </summary>
    Contrast,

    /// <summary> Three hues spread around the wheel. </summary>
    ThreeColours,

    /// <summary> Soft and light. </summary>
    Pastel,

    /// <summary> Deep and dark, with one stronger accent. </summary>
    Dark,
}
