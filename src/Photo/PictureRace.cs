using ModCloset2.Game;
using ModCloset2.Library;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud, no file): linked into tests/PhotoTests, tests/ClosetTests and tools/photo. The maker
// chooses a picture's model with it, and the closet's photo stamp uses it to know whether a picture depends on the
// character logged in.

/// <summary> Which race a piece's photo is drawn on (0.7.7: never the race of whoever is logged in, unless the mod has it). </summary>
public static class PictureRace
{
    /// <summary>
    /// The race a part with models of <paramref name="races"/> is photographed on, for a player of race
    /// <paramref name="player"/> (0: nobody logged in) and a mod made for the body of race <paramref name="madeFor"/>
    /// (0: none named): one race only, that race; else the body's race when the mod has it; else the player's own or
    /// the nearest along the player's chain; else 0 (the mod's most common, which the maker knows). The closet's stamp
    /// uses the same rule, so a character of another race takes again only the parts whose picture would change.
    /// </summary>
    public static int Of(IReadOnlyCollection<int> races, int player, int madeFor)
    {
        var real = races.Where(r => r > 0).ToList();
        if (real.Count == 1)
            return real[0];
        if (madeFor > 0 && real.Contains(madeFor))
            return madeFor;
        return Races.Chain(player).FirstOrDefault(real.Contains);
    }

    /// <summary>
    /// The race whose body the mod says it is made for, from its name, group and option names ("Top [Rue]", an option
    /// "YAB / Rue"): the women's bodies (Rue, Tre, YAB, Bibo+, …) are the Midlander woman's (c0201), TBSE the Midlander
    /// man's (c0101); 0 when it names none, or bodies of both.
    /// </summary>
    public static int MadeFor(IEnumerable<string> texts)
    {
        var bodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in texts)
            bodies.UnionWith(BodyFit.NamesIn(t));
        if (bodies.Count == 0)
            return 0;
        var men   = bodies.Contains("TBSE");
        var women = bodies.Any(b => !string.Equals(b, "TBSE", StringComparison.OrdinalIgnoreCase));
        return men && women ? 0 : men ? 101 : 201;
    }

}
