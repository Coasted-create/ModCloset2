namespace ModCloset2.Game;

/// <summary> One hair / face / tail / ears customization a mod replaces (Key is the Glamourer customize key). Clan: 0 any, 1 first clan, 2 second clan (faces). </summary>
public sealed record CustomizationPart(string Race, int Gender, string Key, int Value, int Clan = 0);

/// <summary> Who the player is, as far as fitting mods goes. Plain data, made on the framework thread. </summary>
/// <param name="RaceCode"> The model's race code (801 for a Miqo'te woman), 0 when unknown. </param>
/// <param name="Race"> Model race name as the customization names use it ("Miqote", "Highlander"…), "" when unknown. </param>
/// <param name="Gender"> 0 male, 1 female. </param>
/// <param name="Clan"> 1..16; even clans are a race's second clan. 0 when unknown. </param>
/// <param name="Key"> "name@world", "" when nobody is logged in. </param>
public sealed record PlayerInfo(int RaceCode, string Race, int Gender, int Clan, string Key)
{
    public static readonly PlayerInfo Nobody = new(0, string.Empty, 0, 0, string.Empty);

    public bool Known
        => RaceCode != 0;
}

/// <summary> Race codes and customization matching (port of the race helpers of the old Look.cs). No Dalamud types. </summary>
public static class Races
{
    private static readonly string[] ModelRaces = { "Midlander", "Elezen", "Lalafell", "Miqote", "Roegadyn", "AuRa", "Hrothgar", "Viera" };

    private static readonly Dictionary<string, int> RaceCodeBase = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Midlander"] = 101, ["Highlander"] = 301, ["Elezen"] = 501, ["Miqote"] = 701, ["Roegadyn"] = 901,
        ["Lalafell"] = 1101, ["AuRa"] = 1301, ["Hrothgar"] = 1501, ["Viera"] = 1701,
    };

    /// <summary>
    /// Model race name and gender from the game's customize bytes (byte 0 race 1..8, byte 1 gender, byte 4 clan). Hyur
    /// clans are separate model races (even clan: Highlander). Null when the bytes make no sense.
    /// </summary>
    public static (string Race, int Gender)? ModelOf(ReadOnlySpan<byte> customize)
    {
        if (customize.Length < 5 || customize[0] is < 1 or > 8)
            return null;

        var race = customize[0];
        var clan = customize[4];
        var name = race == 1 ? (clan % 2 == 0 ? "Highlander" : "Midlander") : ModelRaces[race - 1];
        return (name, customize[1]);
    }

    /// <summary> The game's race code of a model (Miqote female → 801), or 0. </summary>
    public static int RaceCode((string Race, int Gender)? model)
        => model is { } m && RaceCodeBase.TryGetValue(m.Race, out var b) ? b + (m.Gender == 1 ? 100 : 0) : 0;

    /// <summary> A player from customize bytes and a key. </summary>
    public static PlayerInfo PlayerFrom(ReadOnlySpan<byte> customize, string key)
    {
        if (ModelOf(customize) is not { } model)
            return PlayerInfo.Nobody with { Key = key };
        return new PlayerInfo(RaceCode(model), model.Race, model.Gender, customize.Length > 4 ? customize[4] : 0, key);
    }

    /// <summary>
    /// Race codes whose gear models this character would load, best first: its own and the game's fallbacks (Hrothgar male
    /// → Roegadyn male, Lalafell female → Lalafell male, other women → Midlander female, others → Midlander male). The
    /// final Midlander male fallback is left out for women so male-only mods don't count as fitting.
    /// </summary>
    public static IReadOnlyList<int> Chain(int code)
    {
        var list = new List<int>();
        if (code == 0)
            return list;

        var female  = (code / 100) % 2 == 0;
        var current = code;
        while (true)
        {
            list.Add(current);
            if (current == 101)
                break;

            current = current switch
            {
                201  => 101,
                1501 => 901,
                1201 => 1101,
                _ when (current / 100) % 2 == 0 => 201,
                _ => 101,
            };

            if (female && current == 101 || list.Contains(current))
                break;
        }

        return list;
    }

    /// <summary> <see cref="Chain"/> as a set (the old CodesFor). </summary>
    public static HashSet<int> CodesFor(int code)
        => Chain(code).ToHashSet();

    /// <summary>
    /// The customize values of a mod's parts that fit the given race, gender and (for faces) clan. A clan of 0 skips the
    /// clan check.
    /// </summary>
    public static Dictionary<string, int> MatchingCustomization(IEnumerable<CustomizationPart> parts, (string Race, int Gender)? model, int clan = 0)
    {
        var values = new Dictionary<string, int>();
        if (model is not { } m)
            return values;

        var clanSlot = clan == 0 ? 0 : clan % 2 == 0 ? 2 : 1;
        foreach (var part in parts)
            if (part.Gender == m.Gender && string.Equals(part.Race, m.Race, StringComparison.OrdinalIgnoreCase)
             && (part.Clan == 0 || clanSlot == 0 || part.Clan == clanSlot))
                values.TryAdd(part.Key, part.Value);
        return values;
    }

    /// <summary> Every value per key that fits the player (a makeup mod with files for every face lists them all), in order. </summary>
    public static Dictionary<string, List<int>> MatchingValues(IEnumerable<CustomizationPart> parts, PlayerInfo player)
    {
        var values = new Dictionary<string, List<int>>();
        if (!player.Known)
            return values;
        var clanSlot = player.Clan == 0 ? 0 : player.Clan % 2 == 0 ? 2 : 1;
        foreach (var part in parts)
            if (part.Gender == player.Gender && string.Equals(part.Race, player.Race, StringComparison.OrdinalIgnoreCase)
             && (part.Clan == 0 || clanSlot == 0 || part.Clan == clanSlot))
            {
                if (!values.TryGetValue(part.Key, out var list))
                    values[part.Key] = list = new List<int>();
                if (!list.Contains(part.Value))
                    list.Add(part.Value);
            }

        return values;
    }

    public static Dictionary<string, int> MatchingCustomization(IEnumerable<CustomizationPart> parts, PlayerInfo player)
        => player.Known ? MatchingCustomization(parts, (player.Race, player.Gender), player.Clan) : new Dictionary<string, int>();

    /// <summary> The customization race name of a race or clan word ("Seeker of the Sun" → "Miqote"). </summary>
    public static string RaceName(string race, string? clan)
    {
        var r = (clan ?? race).Replace(" ", string.Empty).Replace("'", string.Empty).ToLowerInvariant();
        return r switch
        {
            "highlander"                                           => "Highlander",
            "hyur" or "midlander"                                  => "Midlander",
            "elezen" or "wildwood" or "duskwight"                  => "Elezen",
            "lalafell" or "plainsfolk" or "dunesfolk"              => "Lalafell",
            "miqote" or "seekerofthesun" or "seekersofthesun" or "keeperofthemoon" or "keepersofthemoon" => "Miqote",
            "roegadyn" or "seawolf" or "hellsguard"                => "Roegadyn",
            "aura" or "raen" or "xaela"                            => "AuRa",
            "hrothgar" or "helion" or "helions" or "thelost"       => "Hrothgar",
            "viera" or "rava" or "veena"                           => "Viera",
            _                                                      => race,
        };
    }
}
