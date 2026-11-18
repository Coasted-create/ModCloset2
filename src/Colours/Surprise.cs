using ModCloset2.Core;

namespace ModCloset2.Colours;

// Pure code (no game, no Dalamud): linked into tests/ColourTests.

/// <summary>
/// Four outfit colours that go together, on request. Built on the old plugin's schemes (ColourMath.Harmony: matching,
/// one colour, contrast, three colours, pastel, dark), then held to rules a person would check:
/// <list type="bullet">
/// <item> Main and Second can be told apart: at least <see cref="MainSecondDistance"/> apart (OKLab × 100) and
/// <see cref="MainSecondLight"/> apart in lightness. </item>
/// <item> The Accent stands out: chroma at least <see cref="AccentChroma"/>, and <see cref="AccentDistance"/> away from
/// Main and from Second. </item>
/// <item> The Metal is one of gold, silver, rose gold or dark: gold with warm colours, silver with cool ones, rose gold
/// with a pink or red accent, dark metal with a light, soft palette. </item>
/// </list>
/// </summary>
public static class Surprise
{
    public const float MainSecondDistance = 25f;
    public const float MainSecondLight = 0.15f;
    public const float AccentChroma = 0.08f;
    public const float AccentDistance = 20f;

    public const uint Gold = 0xEBC98E, Silver = 0xC9CCD6, RoseGold = 0xD9A49A, DarkMetal = 0x4A4A52;
    public static readonly uint[] Metals = { Gold, Silver, RoseGold, DarkMetal };

    /// <summary> A new set of outfit colours that meets the rules and differs from <paramref name="current"/>. </summary>
    public static OutfitPalette Next(Random random, OutfitPalette? current = null)
    {
        for (var attempt = 0; attempt < 40; ++attempt)
        {
            var seed   = ColourMath.RandomSeed(random);
            var scheme = (Scheme)random.Next(Enum.GetValues<Scheme>().Length);
            var (main, second, accent) = ColourMath.Harmony(seed, scheme);
            second = Contrast(main, second);
            accent = StandOut(accent, main, second);
            var palette = new OutfitPalette
            {
                Main   = main.Hex,
                Second = second.Hex,
                Accent = accent.Hex,
                Metal  = MetalFor(main, second, accent, random),
            };
            if (Meets(palette) && (current is null || !ColourMemory.Near(palette, current)))
                return palette;
        }

        return ColourMemory.Default;
    }

    /// <summary> True when a palette keeps every rule. </summary>
    public static bool Meets(OutfitPalette p)
    {
        var main   = ColourMath.ToOk(Rgb.FromHex(p.Main));
        var second = ColourMath.ToOk(Rgb.FromHex(p.Second));
        var accent = ColourMath.ToOk(Rgb.FromHex(p.Accent));
        return ColourMath.OkDistance(main, second) >= MainSecondDistance
            && MathF.Abs(main.L - second.L) >= MainSecondLight
            && accent.Chroma >= AccentChroma
            && ColourMath.OkDistance(accent, main) >= AccentDistance
            && ColourMath.OkDistance(accent, second) >= AccentDistance
            && Metals.Contains(p.Metal);
    }

    /// <summary> Second moved in lightness away from Main until the two can be told apart. </summary>
    private static Rgb Contrast(Rgb main, Rgb second)
    {
        var m = ColourMath.ToOk(main);
        var s = ColourMath.ToOk(second);
        if (ColourMath.OkDistance(m, s) >= MainSecondDistance + 2 && MathF.Abs(m.L - s.L) >= MainSecondLight + 0.02f)
            return second;
        // a dark main gets a light second and the other way round
        var l = m.L > 0.55f ? Math.Clamp(m.L - 0.4f, 0.12f, 0.5f) : Math.Clamp(m.L + 0.4f, 0.55f, 0.95f);
        return ColourMath.FromOk(s with { L = l });
    }

    /// <summary> The accent made colourful enough and moved away from Main and Second when it sits too near. </summary>
    private static Rgb StandOut(Rgb accent, Rgb main, Rgb second)
    {
        var a = ColourMath.ToOk(accent);
        var m = ColourMath.ToOk(main);
        var s = ColourMath.ToOk(second);
        if (a.Chroma < AccentChroma + 0.04f)
        {
            var h = a.Chroma < 0.01f ? m.Hue + MathF.PI : a.Hue;
            a = OkLab.FromLch(Math.Clamp(a.L, 0.45f, 0.75f), 0.14f, h);
        }

        for (var turn = 0; turn < 6; ++turn)
        {
            var c = ColourMath.ToOk(ColourMath.FromOk(a));
            if (ColourMath.OkDistance(c, m) >= AccentDistance + 2 && ColourMath.OkDistance(c, s) >= AccentDistance + 2 && c.Chroma >= AccentChroma + 0.01f)
                return ColourMath.FromOk(a);
            // turn the hue and sit between the two in lightness, where there is room
            a = OkLab.FromLch(Math.Clamp((m.L + s.L) / 2 + (turn % 2 == 0 ? 0.08f : -0.08f), 0.4f, 0.78f), Math.Max(a.Chroma, 0.13f), a.Hue + MathF.PI * 0.6f);
        }

        return ColourMath.FromOk(a);
    }

    private static uint MetalFor(Rgb main, Rgb second, Rgb accent, Random random)
    {
        var m = ColourMath.ToOk(main);
        var s = ColourMath.ToOk(second);
        var a = ColourMath.ToOk(accent);
        if ((m.L + s.L) / 2 > 0.68f && random.NextDouble() < 0.7)
            return DarkMetal; // soft and light: a dark metal gives it an edge

        var hueDeg = (a.Hue * 180 / MathF.PI + 360) % 360;
        if (a.Chroma > 0.08f && hueDeg is > 340 or < 30 && random.NextDouble() < 0.6)
            return RoseGold;

        // warm (red, orange, yellow, brown) wants gold; cool (green, blue, purple, grey) wants silver
        var warmth = Warmth(m) * 2 + Warmth(s) + Warmth(a);
        return warmth > 0 ? Gold : Silver;
    }

    private static float Warmth(OkLab c)
    {
        if (c.Chroma < 0.03f)
            return 0;
        var hueDeg = (c.Hue * 180 / MathF.PI + 360) % 360;
        return hueDeg is < 110 or > 330 ? 1 : -1;
    }
}
