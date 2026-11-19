using System.Text.RegularExpressions;
using ModCloset2.Core;

namespace ModCloset2.Detect;

/// <summary> What kind of file a game path is. </summary>
public enum FileKind : byte
{
    Other,
    Model,
    Material,
    Texture,
    Imc,
    Skeleton,
    Animation,
    Effect,
    Sound,
}

/// <summary>
/// What one game path says about where it belongs. <see cref="Socket"/> is <see cref="Socket.None"/> when the path only
/// names a gear set (a texture without a slot in its name, a gear effect) or is a file at a custom path: those belong to
/// whichever part has that set or uses the file, and make no part of their own.
/// </summary>
/// <param name="Socket"> The socket, or None when it only attaches to another part. </param>
/// <param name="OtherKind">
/// For <see cref="Socket.Other"/> and weapons: "animation", "effect", "sound", "interface", "mount or minion", "world",
/// "helper", "weapon", "prop" (a model under chara/weapon outside every weapon range: what emotes and poses hold).
/// </param>
/// <param name="SetType"> 'e' equipment, 'a' accessory, 'w' weapon, 'h' human part (hair, face…), '\0' none. </param>
/// <param name="Set"> Gear set id, or the hair / face / tail / body number of a human part. </param>
/// <param name="Race"> Race code from the path (201 for c0201), 0 when none. </param>
/// <param name="Custom"> A file at a custom path (chara/authorname/…): it belongs to the part whose materials name it. </param>
public readonly record struct PathInfo(Socket Socket, string OtherKind, FileKind File, char SetType, int Set, int Race, bool Custom)
{
    public bool IsWearable
        => Socket is not (Socket.None or Socket.Other);
}

/// <summary>
/// Classifies game paths (lower case, forward slashes) into sockets. Rules checked against Penumbra.GameData's
/// GamePathParser / Parser (equipment, accessory, human customisation, common textures) and the sample packs.
/// </summary>
public static partial class GamePaths
{
    /// <summary> Gear slot suffixes as the game writes them in file names. </summary>
    public static readonly IReadOnlyDictionary<string, Socket> SlotSuffix = new Dictionary<string, Socket>
    {
        ["met"] = Socket.Head, ["top"] = Socket.Body, ["glv"] = Socket.Hands, ["dwn"] = Socket.Legs, ["sho"] = Socket.Feet,
        ["ear"] = Socket.Ears, ["nek"] = Socket.Neck, ["wrs"] = Socket.Wrists, ["rir"] = Socket.Rings, ["ril"] = Socket.Rings,
    };

    // chara/equipment/e0123/… or chara/accessory/a0123/…
    [GeneratedRegex(@"^chara/(equipment|accessory)/([ea])(\d{4})/")]
    private static partial Regex GearFolder();

    // The slot in a gear file name: c0201e6157_glv, mt_c0201e6157_glv_a, v01_c0201e6013_top_b_base. Accessory material
    // names inside equipment folders happen ("mt_c1701a5502_met_metal.mtrl" under e5502), so either letter is accepted.
    [GeneratedRegex(@"c(\d{4})[ea](\d{4})_(met|top|glv|dwn|sho|ear|nek|wrs|rir|ril)(?:[_.]|$)")]
    private static partial Regex GearSlotInName();

    // chara/human/c0801/obj/hair/h0145/… (types per Penumbra: hair h, face f, tail t, body b, zear z, met m)
    [GeneratedRegex(@"^chara/human/c(\d{4})/obj/(hair|face|tail|body|zear|met)/[a-z](\d{4})/")]
    private static partial Regex HumanObj();

    [GeneratedRegex(@"^chara/human/c(\d{4})/animation/")]
    private static partial Regex HumanAnimation();

    // chara/human/c0201/skeleton/top/t6126/skl_c0201t6126.sklb: "top" and "met" skeletons belong to a gear set.
    [GeneratedRegex(@"^chara/human/c(\d{4})/skeleton/([a-z]+)/[a-z](\d{4})/")]
    private static partial Regex HumanSkeleton();

    [GeneratedRegex(@"^chara/weapon/w(\d{4})/")]
    private static partial Regex Weapon();

    [GeneratedRegex(@"c(\d{4})")]
    private static partial Regex RaceInName();

    public static PathInfo Classify(string gamePath)
    {
        var p   = gamePath;
        var ext = Extension(p);
        var file = ext switch
        {
            "mdl"                           => FileKind.Model,
            "mtrl"                          => FileKind.Material,
            "tex"                           => FileKind.Texture,
            "imc"                           => FileKind.Imc,
            "sklb" or "skp" or "phyb" or "pbd" or "eid" or "kdb" or "atch" => FileKind.Skeleton,
            "pap" or "tmb"                  => FileKind.Animation,
            "avfx" or "atex"                => FileKind.Effect,
            "scd"                           => FileKind.Sound,
            _                               => FileKind.Other,
        };

        // Gear: equipment and accessories. A gear effect (…/vfx/…) belongs to the set's parts.
        var gear = GearFolder().Match(p);
        if (gear.Success)
        {
            var set = int.Parse(gear.Groups[3].Value);
            var setType = gear.Groups[2].Value[0];
            var name = p[(p.LastIndexOf('/') + 1)..];
            var slot = GearSlotInName().Match(name);
            if (slot.Success && file is not (FileKind.Effect or FileKind.Animation or FileKind.Sound))
                return new PathInfo(SlotSuffix[slot.Groups[3].Value], string.Empty, file, setType, set, int.Parse(slot.Groups[1].Value), false);
            return new PathInfo(Socket.None, file == FileKind.Effect ? "effect" : string.Empty, file, setType, set, 0, false);
        }

        var human = HumanObj().Match(p);
        if (human.Success)
        {
            var race = int.Parse(human.Groups[1].Value);
            var id   = int.Parse(human.Groups[3].Value);
            var socket = human.Groups[2].Value switch
            {
                "hair" => Socket.Hair,
                "face" => Socket.Face, // textures-only faces become FaceTextures when the part is put together
                "tail" => Socket.Tail,
                "zear" => Socket.EarShape,
                "body" => Socket.BodyBase,
                _      => Socket.Face, // "met": head-shape parts of the face, rare
            };
            if (file is FileKind.Animation or FileKind.Effect or FileKind.Sound)
                return Other(file, OtherKindOf(file));
            return new PathInfo(socket, string.Empty, file, 'h', id, race, false);
        }

        if (HumanAnimation().IsMatch(p) || file == FileKind.Animation || p.StartsWith("chara/action/", StringComparison.Ordinal))
            return Other(FileKind.Animation, "animation");

        var skeleton = HumanSkeleton().Match(p);
        if (skeleton.Success)
        {
            // Extra skeletons of gear ("top" / "met" sets) attach to that gear part when the mod has it; else helpers.
            var type = skeleton.Groups[2].Value;
            var set  = int.Parse(skeleton.Groups[3].Value);
            if (type is "top" or "met")
                return new PathInfo(Socket.None, "helper", FileKind.Skeleton, 'e', set, int.Parse(skeleton.Groups[1].Value), false);
            return Other(FileKind.Skeleton, "helper");
        }

        var weapon = Weapon().Match(p);
        if (weapon.Success)
        {
            // A weapon's effects (…/vfx/…, textures too), sounds and animations are not the weapon: files not worn.
            if (file is FileKind.Effect or FileKind.Sound or FileKind.Animation)
                return Other(file, OtherKindOf(file));
            if (p.Contains("/vfx/", StringComparison.Ordinal))
                return Other(file, "effect");
            var set = int.Parse(weapon.Groups[1].Value);
            return new PathInfo(WeaponSocket(set), WeaponClass(set) is null ? "prop" : "weapon", file, 'w', set, 0, false);
        }

        if (p.StartsWith("chara/common/texture/", StringComparison.Ordinal))
        {
            var rest = p["chara/common/texture/".Length..];
            if (rest.StartsWith("skin", StringComparison.Ordinal))
                return new PathInfo(Socket.BodyBase, string.Empty, file, '\0', 0, 0, false);
            if (rest.StartsWith("decal_face/", StringComparison.Ordinal) || rest.StartsWith("catchlight", StringComparison.Ordinal)
             || rest.StartsWith("eye/", StringComparison.Ordinal) || rest.StartsWith("iris", StringComparison.Ordinal))
                return new PathInfo(Socket.FaceTextures, string.Empty, file, '\0', 0, 0, false);
            if (rest.StartsWith("decal_equip/", StringComparison.Ordinal))
                return Other(file, "helper");

            // Shared textures (tiles, spheres) used by gear materials: they go with whoever uses them.
            return new PathInfo(Socket.None, string.Empty, file, '\0', 0, 0, true);
        }

        if (p.StartsWith("chara/monster/", StringComparison.Ordinal) || p.StartsWith("chara/demihuman/", StringComparison.Ordinal))
            return Other(file, file is FileKind.Effect or FileKind.Sound ? OtherKindOf(file) : "mount or minion");

        if (p.StartsWith("chara/xls/", StringComparison.Ordinal))
            return Other(FileKind.Skeleton, "helper");

        if (p.StartsWith("vfx/", StringComparison.Ordinal) || file == FileKind.Effect)
            return Other(FileKind.Effect, "effect");
        if (p.StartsWith("sound/", StringComparison.Ordinal) || file == FileKind.Sound)
            return Other(FileKind.Sound, "sound");
        if (p.StartsWith("ui/", StringComparison.Ordinal) || p.StartsWith("common/font/", StringComparison.Ordinal) || ext is "uld" or "fdt")
            return Other(file, "interface");
        if (p.StartsWith("bg/", StringComparison.Ordinal) || p.StartsWith("bgcommon/", StringComparison.Ordinal))
            return Other(file, "world");
        if (p.StartsWith("shader/", StringComparison.Ordinal) || ext is "shpk" || file == FileKind.Skeleton)
            return Other(file, "helper");

        // A body's skin texture by its name (bibo_mid_base.tex, gen3_raen_n.tex…), wherever the mod put it: the skin.
        if (file == FileKind.Texture && IsSkinTexture(p))
            return new PathInfo(Socket.BodyBase, string.Empty, file, '\0', 0, 0, false);

        // Files at made-up paths (chara/authorname/…, chara/bibo_mid_base.tex): they belong to the part whose materials
        // or models name them.
        if (p.StartsWith("chara/", StringComparison.Ordinal) && file is FileKind.Texture or FileKind.Material or FileKind.Model)
            return new PathInfo(Socket.None, string.Empty, file, '\0', 0, RaceOf(p), true);

        return Other(file, "helper");
    }

    private static PathInfo Other(FileKind file, string kind)
        => new(Socket.Other, kind, file, '\0', 0, 0, false);

    private static string OtherKindOf(FileKind file)
        => file switch
        {
            FileKind.Animation => "animation",
            FileKind.Effect    => "effect",
            FileKind.Sound     => "sound",
            _                  => "helper",
        };

    private static string Extension(string p)
    {
        var slash = p.LastIndexOf('/');
        var dot   = p.LastIndexOf('.');
        return dot > slash ? p[(dot + 1)..] : string.Empty;
    }

    private static int RaceOf(string p)
    {
        var m = RaceInName().Match(p);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary> The socket a meta edit points at, or None for edits that only change attributes, shapes or race sizes. </summary>
    public static Socket SocketOf(MetaEdit m)
    {
        switch (m.Type)
        {
            case "eqp":
            case "eqdp":
                return GearSlot(m.Slot);
            case "gmp":
                return Socket.Head;
            case "est":
                return m.Slot switch
                {
                    "hair" => Socket.Hair,
                    "face" => Socket.Face,
                    "body" => Socket.Body,
                    "head" => Socket.Head,
                    _      => Socket.None,
                };
            case "imc":
                return ImcSocket(m);
            default:
                return Socket.None;
        }
    }

    /// <summary> The socket an IMC entry (or an IMC option group's identifier) belongs to. </summary>
    public static Socket ImcSocket(MetaEdit m)
        => m.ObjectType switch
        {
            "equipment" or "accessory" => GearSlot(m.Slot),
            "weapon"                   => WeaponSocket(m.Set),
            "character" or "human"     => m.BodySlot switch
            {
                "hair" => Socket.Hair,
                "face" => Socket.Face,
                "tail" => Socket.Tail,
                "ear" or "zear" => Socket.EarShape,
                "body" => Socket.BodyBase,
                _      => Socket.None,
            },
            _ => Socket.None,
        };

    /// <summary>
    /// The slot of an attribute or shape edit (ATR, SHP; Penumbra's human slot names, lower case): None for "all slots",
    /// null for a slot that is no socket of ours.
    /// </summary>
    public static Socket? AttributeSocket(string slot)
        => slot switch
        {
            "" or "unknown" => Socket.None,
            "hair"          => Socket.Hair,
            "face"          => Socket.Face,
            "ear"           => Socket.EarShape,
            _               => GearSlot(slot) is var s && s != Socket.None ? s : null,
        };

    // bibo_mid_base.tex, bibop_raen_norm.tex, gen3_xaela_mask.tex, tbse_high_d.tex: a body mod's word, then the race and the role.
    [GeneratedRegex(@"(?:^|/)(?:bibo|bibop|gen3|tbse|eve|otopop)_[a-z0-9_]+\.tex$")]
    private static partial Regex SkinTextureName();

    /// <summary> True for a texture named like a body mod's skin texture, at any path (the folders before it do not matter). </summary>
    public static bool IsSkinTexture(string gamePath)
        => SkinTextureName().IsMatch(gamePath.ToLowerInvariant());

    private static readonly (string[] Words, int Race)[] RaceWords =
    {
        (new[] { "midlander", "mid", "hyur" }, 1), (new[] { "highlander", "high" }, 2), (new[] { "elezen", "elzen", "elf" }, 3),
        (new[] { "miqote", "miqo", "miqo'te" }, 4), (new[] { "roegadyn", "roe" }, 5), (new[] { "lalafell", "lala" }, 6),
        (new[] { "aura", "raen", "xaela" }, 7), (new[] { "hrothgar", "hroth" }, 8), (new[] { "viera", "rava", "veena" }, 9),
    };

    private static readonly HashSet<string> MaleBodies = new(StringComparer.Ordinal) { "tbse", "w25", "thicclander" };

    /// <summary>
    /// The game race codes a skin texture at a made-up path is for, from the race words in its folders and name
    /// ("…/midlander_elzen_miqote/chara/bibo_mid_base.tex" → 201, 601, 801): female codes for female bodies (Bibo+,
    /// Gen3…), male codes for male ones (TBSE). Empty when it names no race.
    /// </summary>
    public static List<int> SkinRaces(string gamePath)
    {
        var tokens = Regex.Split(gamePath.ToLowerInvariant(), "[^a-z0-9']+").Where(t => t.Length > 0).ToList();
        var male = tokens.Any(MaleBodies.Contains);
        var races = new SortedSet<int>();
        foreach (var t in tokens)
            foreach (var (words, race) in RaceWords)
                if (words.Contains(t))
                    races.Add(((race - 1) * 2 + (male ? 1 : 2)) * 100 + 1);
        return races.ToList();
    }

    /// <summary> The ring finger a slot name names: 'r' for "rfinger", 'l' for "lfinger", else null. </summary>
    public static char? Finger(string slot)
        => slot switch
        {
            "rfinger" => 'r',
            "lfinger" => 'l',
            _         => null,
        };

    /// <summary> The ring finger a game path is for, by its file name ("…_rir.mdl", "mt_…_ril_a.mtrl"), else null. </summary>
    public static char? FingerOfPath(string gamePath)
    {
        var name = gamePath[(gamePath.LastIndexOf('/') + 1)..];
        var r = RingSuffix().Match(name);
        return r.Success ? r.Groups[1].Value[0] : null;
    }

    // c0201a0001_rir.mdl, mt_c0201a0001_rir_a.mtrl, v01_c0201a0001_ril_d.tex
    [GeneratedRegex(@"(?:^|_)c\d{4}a\d{4}_ri([rl])[_.]")]
    private static partial Regex RingSuffix();

    /// <summary> Penumbra's equip slot names ("Body", "Hands", "RFinger"…), lower case, to sockets. </summary>
    public static Socket GearSlot(string slot)
        => slot switch
        {
            "head"                          => Socket.Head,
            "body"                          => Socket.Body,
            "hands"                         => Socket.Hands,
            "legs"                          => Socket.Legs,
            "feet"                          => Socket.Feet,
            "ears"                          => Socket.Ears,
            "neck"                          => Socket.Neck,
            "wrists"                        => Socket.Wrists,
            "rfinger" or "lfinger" or "ring" => Socket.Rings,
            "mainhand"                      => Socket.MainHand,
            "offhand"                       => Socket.OffHand,
            _                               => Socket.None,
        };

    // ---- weapons: the class and the hand by the model set's range, as the game numbers them (w0201 a sword, w2251 a
    // katana's sheath). Ranges as in Penumbra.GameData's weapon id table, crafting and gathering tools as Glamourer's
    // starting weapons have them (w5001 / w5041 carpenter, w7201 / w7255 fisher). Index: (set - 1) / 100; a set whose
    // remainder (set - 1) % 100 reaches Off is the off-hand piece of that class.
    private static readonly Dictionary<int, (string Main, string Off, int From)> WeaponRanges = new()
    {
        [1]  = ("shield", "shield", 0),   // shields are all off hand
        [2]  = ("sword", "", 100),
        [3]  = ("fists", "fists", 50),
        [4]  = ("axe", "", 100),
        [5]  = ("lance", "", 100),
        [6]  = ("bow", "quiver", 97),
        [7]  = ("wand", "", 100),
        [8]  = ("staff", "", 100),
        [9]  = ("wand", "", 100),
        [10] = ("staff", "", 100),
        [15] = ("greatsword", "", 100),
        [16] = ("fists", "fists", 50),
        [17] = ("grimoire", "", 100),
        [18] = ("daggers", "daggers", 50),
        [20] = ("gun", "gun", 98),
        [21] = ("globe", "globe", 98),
        [22] = ("katana", "katana", 50),
        [23] = ("rapier", "rapier", 50),
        [24] = ("staff", "", 100),
        [25] = ("gunblade", "", 100),
        [26] = ("chakrams", "chakrams", 50),
        [27] = ("nouliths", "", 100),
        [28] = ("scythe", "", 100),
        [29] = ("brush", "palette", 50),
        [30] = ("twinfangs", "twinfangs", 50),
        [31] = ("twinfangs", "twinfangs", 50),
        [33] = ("axe", "", 100),
        [50] = ("tool", "tool", 40), [51] = ("tool", "tool", 40), [52] = ("tool", "tool", 40), [53] = ("tool", "tool", 40),
        [54] = ("tool", "tool", 40), [55] = ("tool", "tool", 40), [56] = ("tool", "tool", 40), [57] = ("tool", "tool", 40),
        [70] = ("tool", "tool", 50), [71] = ("tool", "tool", 50), [72] = ("tool", "tool", 50),
    };

    /// <summary>
    /// The class of a weapon model set ("sword", "shield", "quiver", "tool"…), or null for a set outside every weapon range:
    /// a prop (what emotes and poses put in the hand, w19xx, w90xx…), never equipped.
    /// </summary>
    public static string? WeaponClass(int set)
        => set > 0 && WeaponRanges.TryGetValue((set - 1) / 100, out var r) ? ((set - 1) % 100 >= r.From ? r.Off : r.Main) : null;

    /// <summary> True for a weapon set the off hand holds: shields, quivers, the second dagger, a katana's sheath, off-hand tools. </summary>
    public static bool IsOffHand(int set)
        => set > 0 && WeaponRanges.TryGetValue((set - 1) / 100, out var r) && (set - 1) % 100 >= r.From;

    /// <summary> The hand a weapon model set is held in (props: the main hand, where the reader always put weapons). </summary>
    public static Socket WeaponSocket(int set)
        => IsOffHand(set) ? Socket.OffHand : Socket.MainHand;

    /// <summary> The weapon set of a game path under chara/weapon (0 for none). </summary>
    public static int WeaponSet(string gamePath)
    {
        var m = Weapon().Match(gamePath);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary> True for sockets whose models are gear (they can carry bare skin that is not the item). </summary>
    public static bool IsGear(Socket s)
        => s is >= Socket.Head and <= Socket.Rings;

    /// <summary> True for the sockets that tie a part to a race (hair, face, tail, ears, body). </summary>
    public static bool TiesRace(Socket s)
        => s is Socket.Hair or Socket.Face or Socket.Tail or Socket.EarShape or Socket.BodyBase or Socket.FaceTextures;
}
