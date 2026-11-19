using ModCloset2.Core;

namespace ModCloset2.Closet;

/// <summary> Names of the detector's kinds for people: plural for the chips ("Dresses"), singular on cards ("Animation"). </summary>
public static class KindNames
{
    private static readonly Dictionary<string, (string One, string Many)> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dress"]           = ("Dress", "Dresses"),
        ["top"]             = ("Top", "Tops"),
        ["jacket"]          = ("Jacket", "Jackets"),
        ["coat"]            = ("Coat", "Coats"),
        ["bodysuit"]        = ("Bodysuit", "Bodysuits"),
        ["bra"]             = ("Bra", "Bras"),
        ["skirt"]           = ("Skirt", "Skirts"),
        ["pants"]           = ("Pants", "Pants"),
        ["shorts"]          = ("Shorts", "Shorts"),
        ["underwear"]       = ("Underwear", "Underwear"),
        ["stockings"]       = ("Stockings", "Stockings"),
        ["socks"]           = ("Socks", "Socks"),
        ["tights"]          = ("Tights", "Tights"),
        ["heels"]           = ("Heels", "Heels"),
        ["boots"]           = ("Boots", "Boots"),
        ["flats"]           = ("Flats", "Flats"),
        ["sandals"]         = ("Sandals", "Sandals"),
        ["animation"]       = ("Animation", "Animations"),
        ["effect"]          = ("Effect", "Effects"),
        ["sound"]           = ("Sound", "Sounds"),
        ["interface"]       = ("Interface", "Interface"),
        ["mount or minion"] = ("Mount or minion", "Mounts and minions"),
        ["world"]           = ("World", "World"),
        ["helper"]          = ("Helper files", "Helper files"),
        ["weapon"]          = ("Weapon", "Weapons"),
        // weapon classes (the detector's kinds on Main hand and Off hand) and emote props (Everything else)
        ["sword"]           = ("Sword", "Swords"),
        ["shield"]          = ("Shield", "Shields"),
        ["fists"]           = ("Fists", "Fists"),
        ["axe"]             = ("Axe", "Axes"),
        ["lance"]           = ("Lance", "Lances"),
        ["bow"]             = ("Bow", "Bows"),
        ["quiver"]          = ("Quiver", "Quivers"),
        ["wand"]            = ("Wand", "Wands"),
        ["staff"]           = ("Staff", "Staves"),
        ["greatsword"]      = ("Greatsword", "Greatswords"),
        ["grimoire"]        = ("Grimoire", "Grimoires"),
        ["daggers"]         = ("Daggers", "Daggers"),
        ["gun"]             = ("Gun", "Guns"),
        ["globe"]           = ("Globe", "Globes"),
        ["katana"]          = ("Katana", "Katanas"),
        ["rapier"]          = ("Rapier", "Rapiers"),
        ["gunblade"]        = ("Gunblade", "Gunblades"),
        ["chakrams"]        = ("Chakrams", "Chakrams"),
        ["nouliths"]        = ("Nouliths", "Nouliths"),
        ["scythe"]          = ("Scythe", "Scythes"),
        ["brush"]           = ("Brush", "Brushes"),
        ["palette"]         = ("Palette", "Palettes"),
        ["twinfangs"]       = ("Twinfangs", "Twinfangs"),
        ["tool"]            = ("Tool", "Tools"),
        ["prop"]            = ("Prop", "Props"),
        ["vest"]            = ("Vest", "Vests"),
        ["corset"]          = ("Corset", "Corsets"),
        ["harness"]         = ("Harness", "Harnesses"),
        ["glasses"]         = ("Glasses", "Glasses"),
        ["mask"]            = ("Mask", "Masks"),
        ["veil"]            = ("Veil", "Veils"),
        ["horns"]           = ("Horns", "Horns"),
        ["halo"]            = ("Halo", "Halos"),
        ["choker"]          = ("Choker", "Chokers"),
        ["scarf"]           = ("Scarf", "Scarves"),
        ["armwarmers"]      = ("Arm warmers", "Arm warmers"),
        ["wings"]           = ("Wings", "Wings"),
        ["piercing"]        = ("Piercing", "Piercings"),
        ["bag"]             = ("Bag", "Bags"),

        // a body mod (Bodies): its bare-skin models and its skin are one part
        ["body"]            = ("Body", "Bodies"),

        // the kinds of the Outfits shelf's own cards (its chips tell them apart)
        ["outfit"]          = ("Outfit", "Outfits"),
        ["jewellery set"]   = ("Jewellery set", "Jewellery sets"),
    };

    /// <summary> Kinds a shelf of the user's cannot belong to: the Outfits shelf's own kinds, and those whose name is a built-in shelf's. </summary>
    /// <summary> ("body" has its built-in shelf, Bodies: a shelf of the user's never takes the body mods from it.) </summary>
    private static readonly HashSet<string> NotForShelves = new(StringComparer.OrdinalIgnoreCase) { "outfit", "jewellery set", "body" };

    /// <summary>
    /// The kind a shelf name stands for ("Piercings", "piercing", "Piercings" in German → "piercing"), or "" when it names
    /// none. A name that is a built-in shelf's ("Tops") stands for no kind: that shelf is already there.
    /// </summary>
    public static string KindOf(string name, IEnumerable<string> builtInNames)
    {
        var n = name.Trim();
        if (n.Length == 0 || builtInNames.Any(b => b.Equals(n, StringComparison.CurrentCultureIgnoreCase)))
            return string.Empty;
        foreach (var (kind, (one, many)) in Names)
        {
            if (NotForShelves.Contains(kind))
                continue;
            if (n.Equals(kind, StringComparison.CurrentCultureIgnoreCase) || n.Equals(one, StringComparison.CurrentCultureIgnoreCase)
             || n.Equals(many, StringComparison.CurrentCultureIgnoreCase) || n.Equals(Loc.T(one), StringComparison.CurrentCultureIgnoreCase)
             || n.Equals(Loc.T(many), StringComparison.CurrentCultureIgnoreCase))
                return kind;
        }

        return string.Empty;
    }

    /// <summary> How a part is worn, for telling two cards of one mod apart ("Wrist slot"). Translated. </summary>
    /// <summary> A gear slot's name in English, what <see cref="SlotLabel"/> translates (the search knows both). </summary>
    public static string SlotLabelEnglish(Socket socket)
        => socket switch
        {
            Socket.Head   => "Head slot",
            Socket.Body   => "Body slot",
            Socket.Hands  => "Hands slot",
            Socket.Legs   => "Legs slot",
            Socket.Feet   => "Feet slot",
            Socket.Ears   => "Ears slot",
            Socket.Neck   => "Neck slot",
            Socket.Wrists => "Wrist slot",
            Socket.Rings  => "Ring slot",
            _             => string.Empty,
        };

    public static string SlotLabel(Socket socket)
        => socket switch
        {
            Socket.Head         => Loc.T("Head slot"),
            Socket.Body         => Loc.T("Body slot"),
            Socket.Hands        => Loc.T("Hands slot"),
            Socket.Legs         => Loc.T("Legs slot"),
            Socket.Feet         => Loc.T("Feet slot"),
            Socket.Ears         => Loc.T("Ears slot"),
            Socket.Neck         => Loc.T("Neck slot"),
            Socket.Wrists       => Loc.T("Wrist slot"),
            Socket.Rings        => Loc.T("Ring slot"),
            Socket.Tail         => Loc.T("Tail slot"),
            Socket.Hair         => Loc.T("Hair"),
            Socket.Face         => Loc.T("Face"),
            Socket.FaceTextures => Loc.T("Face textures"),
            Socket.EarShape     => Loc.T("Ears"),
            Socket.BodyBase     => Loc.T("Body"),
            _                   => string.Empty,
        };

    /// <summary> A kind the closet has names for. </summary>
    public static bool Known(string kind)
        => Names.ContainsKey(kind);

    /// <summary> "Dresses" for "dress"; an unknown kind with a capital letter. Translated. </summary>
    public static string Plural(string kind)
        => Names.TryGetValue(kind, out var n) ? Loc.T(n.Many) : Capital(kind);

    /// <summary> "Animation" for "animation"; "" for none. Translated. </summary>
    public static string Singular(string kind)
        => kind.Length == 0 ? string.Empty : Names.TryGetValue(kind, out var n) ? Loc.T(n.One) : Capital(kind);

    private static string Capital(string s)
        => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
