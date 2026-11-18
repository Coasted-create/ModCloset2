using System.Text.RegularExpressions;
using ModCloset2.Core;

namespace ModCloset2.Game;

/// <summary> One gear slot as Penumbra and Glamourer name it, Glamourer's slot id and the model suffix of its files. </summary>
public sealed record GearSlot(string Name, byte Id, string Suffix, Socket Socket, bool Accessory);

/// <summary> The gear slots and how they map to sockets. No Dalamud types (the tests use it). </summary>
public static class GearSlots
{
    /// <summary> The ten worn slots. Both rings are the socket <see cref="Socket.Rings"/>. </summary>
    public static readonly GearSlot[] Worn =
    {
        new("Head", 3, "met", Socket.Head, false),
        new("Body", 4, "top", Socket.Body, false),
        new("Hands", 5, "glv", Socket.Hands, false),
        new("Legs", 7, "dwn", Socket.Legs, false),
        new("Feet", 8, "sho", Socket.Feet, false),
        new("Ears", 9, "ear", Socket.Ears, true),
        new("Neck", 10, "nek", Socket.Neck, true),
        new("Wrists", 11, "wrs", Socket.Wrists, true),
        new("RFinger", 12, "rir", Socket.Rings, true),
        new("LFinger", 14, "ril", Socket.Rings, true),
    };

    public const byte MainHandId = 1;
    public const byte OffHandId  = 2;

    public static GearSlot? ByName(string name)
        => Worn.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary> Slot name of a Glamourer slot id (weapons included), or null. </summary>
    public static string? NameOf(byte id)
        => id switch
        {
            MainHandId => "MainHand",
            OffHandId  => "OffHand",
            _          => Worn.FirstOrDefault(s => s.Id == id)?.Name,
        };

    /// <summary> Glamourer slot id of a slot name (weapons included), or null. </summary>
    public static byte? IdOf(string name)
        => name switch
        {
            "MainHand" => MainHandId,
            "OffHand"  => OffHandId,
            _          => ByName(name)?.Id,
        };

    /// <summary> The socket of a slot name ("RFinger" → Rings, "MainHand" → MainHand), or None. </summary>
    public static Socket SocketOf(string slotName)
        => slotName switch
        {
            "MainHand" => Socket.MainHand,
            "OffHand"  => Socket.OffHand,
            _          => ByName(slotName)?.Socket ?? Socket.None,
        };

    /// <summary> Slot names of a gear socket (Rings: both hands). Empty for the other sockets. </summary>
    public static IReadOnlyList<string> NamesOf(Socket socket)
        => socket switch
        {
            Socket.MainHand => new[] { "MainHand" },
            Socket.OffHand  => new[] { "OffHand" },
            _               => Worn.Where(s => s.Socket == socket).Select(s => s.Name).ToArray(),
        };

    public static bool IsGear(Socket socket)
        => socket is >= Socket.Head and <= Socket.OffHand;

    /// <summary> Hairstyle, face, tail and ear shape: picked by a number through Glamourer. </summary>
    public static bool IsCustomization(Socket socket)
        => socket is Socket.Hair or Socket.Face or Socket.Tail or Socket.EarShape;

    /// <summary> Glamourer's customize key for a customization socket ("Hairstyle", "Face", "TailShape"), or null. </summary>
    /// <remarks> Viera and Hrothgar ears and Miqo'te / Au Ra tails are the same customize value in the game ("TailShape"). </remarks>
    public static string? CustomizeKey(Socket socket)
        => socket switch
        {
            Socket.Hair                     => "Hairstyle",
            Socket.Face                     => "Face",
            Socket.Tail or Socket.EarShape  => "TailShape",
            _                               => null,
        };

    /// <summary> Glamourer's "nothing" item of a gear slot (both rings share one). </summary>
    public static ulong NothingItem(byte slotId)
        => uint.MaxValue - 128 - (slotId == 14 ? 12u : slotId);
}

/// <summary> What game paths mean: which slot or socket a file belongs to, race codes, which mod a loaded file is from. </summary>
public static partial class GamePaths
{
    [GeneratedRegex(@"c(\d{4})(?=[eab]\d{4}|/)")]
    private static partial Regex RaceCodeRegex();

    [GeneratedRegex(@"^chara/human/c(\d{4})/obj/(hair|face|tail|zear|body)/[hftzb](\d{4})/")]
    private static partial Regex HumanRegex();

    [GeneratedRegex(@"^chara/(equipment|accessory)/([ea])(\d{4})/model/c(\d{4})\2\3_([a-z]{3})\.mdl$")]
    private static partial Regex GearModelRegex();

    public static string Normalize(string path)
        => path.Replace('\\', '/').ToLowerInvariant();

    /// <summary> The gear slot name a file belongs to ("Legs" for …_dwn.mdl and its materials), or null (port of SlotFiles). </summary>
    public static string? SlotOf(string gamePath)
    {
        var p = Normalize(gamePath);
        if (!p.StartsWith("chara/equipment/") && !p.StartsWith("chara/accessory/"))
            return null;

        var name = p[(p.LastIndexOf('/') + 1)..];
        foreach (var slot in GearSlots.Worn)
            if (name.Contains($"_{slot.Suffix}.") || name.Contains($"_{slot.Suffix}_"))
                return slot.Name;
        return null;
    }

    /// <summary> Parses a gear model path: slot, set id and race code. </summary>
    public static bool TryParseGearModel(string gamePath, out GearSlot slot, out int setId, out int raceCode)
    {
        slot     = GearSlots.Worn[0];
        setId    = 0;
        raceCode = 0;
        var m = GearModelRegex().Match(Normalize(gamePath));
        if (!m.Success || GearSlots.Worn.FirstOrDefault(s => s.Suffix == m.Groups[5].Value) is not { } found)
            return false;

        slot     = found;
        setId    = int.Parse(m.Groups[3].Value);
        raceCode = int.Parse(m.Groups[4].Value);
        return true;
    }

    /// <summary>
    /// The socket a game file belongs to. Gear by its slot suffix, hair / face / tail / ears / body by their human folder
    /// (face files that are not models are <see cref="Socket.FaceTextures"/>), skin textures as <see cref="Socket.BodyBase"/>,
    /// weapons as <see cref="Socket.MainHand"/>, anything else <see cref="Socket.Other"/>.
    /// </summary>
    public static Socket SocketOf(string gamePath)
    {
        var p = Normalize(gamePath);
        if (SlotOf(p) is { } slot)
            return GearSlots.SocketOf(slot);

        var human = HumanRegex().Match(p);
        if (human.Success)
            return human.Groups[2].Value switch
            {
                "hair" => Socket.Hair,
                "face" => p.EndsWith(".mdl") ? Socket.Face : Socket.FaceTextures,
                "tail" => Socket.Tail,
                "zear" => Socket.EarShape,
                _      => Socket.BodyBase,
            };

        if (p.StartsWith("chara/common/texture/skin"))
            return Socket.BodyBase;
        if (p.StartsWith("chara/weapon/"))
            return Socket.MainHand;
        return Socket.Other;
    }

    /// <summary> True when two sockets are one thing for "which option changes this part" (a face's model and its textures). </summary>
    public static bool SameFamily(Socket a, Socket b)
        => a == b || a is Socket.Face or Socket.FaceTextures && b is Socket.Face or Socket.FaceTextures
         || a is Socket.MainHand or Socket.OffHand && b is Socket.MainHand or Socket.OffHand;

    /// <summary> The race code of a character path (c0801 → 801), or 0. Only paths under chara/ count. </summary>
    public static int RaceCodeOf(string gamePath)
    {
        var p = Normalize(gamePath);
        if (!p.StartsWith("chara/"))
            return 0;
        var m = RaceCodeRegex().Match(p);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    public static bool IsModel(string gamePath)
        => gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The mod (its directory name) a loaded file comes from, or null for the game's own files. <paramref name="rootFull"/>
    /// is the Penumbra mod root as a full path ending in a separator.
    /// </summary>
    public static string? ModDirectoryOf(string rootFull, string actual)
    {
        if (rootFull.Length == 0)
            return null;

        // Penumbra may prefix special paths with |...|; the file path follows the last bar.
        var path = actual;
        var bar  = path.LastIndexOf('|');
        if (bar >= 0)
            path = path[(bar + 1)..];
        if (path.Length == 0 || !Path.IsPathRooted(path))
            return null;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }

        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = full[rootFull.Length..];
        var cut  = rest.IndexOfAny(new[] { '\\', '/' });
        return cut <= 0 ? null : rest[..cut];
    }

    /// <summary> A mod root as <see cref="ModDirectoryOf"/> wants it (full path with a trailing separator), "" when invalid. </summary>
    public static string RootFull(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return string.Empty;
        try
        {
            return Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        }
        catch
        {
            return string.Empty;
        }
    }
}
