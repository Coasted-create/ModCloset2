namespace ModCloset2.Core;

/// <summary>
/// The technical place a part takes on the character. Only one thing can show per socket, so two parts with the same
/// socket replace each other even when they sit on different shelves (ears that borrow the tail slot take your tail).
/// </summary>
public enum Socket : byte
{
    None = 0,

    // gear
    Head   = 1,
    Body   = 2,
    Hands  = 3,
    Legs   = 4,
    Feet   = 5,
    Ears   = 6,
    Neck   = 7,
    Wrists = 8,

    /// <summary> Right and left ring together. </summary>
    Rings = 9,
    MainHand = 10,
    OffHand  = 11,

    // character parts picked by a number (hairstyle 115, tail 3…)
    Hair = 20,
    Face = 21,
    Tail = 22,

    /// <summary> Ear models of races that have them as their own part (Viera). </summary>
    EarShape = 23,

    // files that show as long as the mod is on, without wearing anything
    /// <summary> Body models and skin textures. </summary>
    BodyBase = 30,

    /// <summary> Textures of a face only (makeup, eyes, brows). </summary>
    FaceTextures = 31,

    /// <summary> Not worn (animations, effects, sounds, interface, mounts, helper files). </summary>
    Other = 40,
}

/// <summary> One wearable part of a mod: the mod's Penumbra directory name and the socket the part takes. </summary>
public readonly record struct PartKey(string Mod, Socket Socket)
{
    public override string ToString()
        => $"{(int)Socket}|{Mod}";

    public static bool TryParse(string? text, out PartKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(text))
            return false;

        var cut = text.IndexOf('|');
        if (cut <= 0 || !int.TryParse(text.AsSpan(0, cut), out var socket) || !Enum.IsDefined(typeof(Socket), (byte)socket))
            return false;

        key = new PartKey(text[(cut + 1)..], (Socket)socket);
        return key.Mod.Length > 0;
    }
}

/// <summary> Places on the body a model covers. </summary>
[Flags]
public enum BodyRegions
{
    None      = 0,
    HeadTop   = 1 << 0,
    Face      = 1 << 1,
    Ears      = 1 << 2,
    Neck      = 1 << 3,
    Chest     = 1 << 4,
    Back      = 1 << 5,
    Waist     = 1 << 6,
    Hips      = 1 << 7,
    UpperArms = 1 << 8,
    Forearms  = 1 << 9,
    Hands     = 1 << 10,
    Fingers   = 1 << 11,
    Thighs    = 1 << 12,
    Knees     = 1 << 13,
    Calves    = 1 << 14,
    Feet      = 1 << 15,
    Toes      = 1 << 16,
    Tail      = 1 << 17,

    /// <summary> Hangs from the skirt bones (a skirt, the lower half of a dress or coat). </summary>
    Skirt = 1 << 18,
}

/// <summary> Which witness said something about a part. </summary>
public enum EvidenceKind : byte
{
    /// <summary> The game file it replaces. </summary>
    Slot,

    /// <summary> Where its model sits on the body. </summary>
    Body,

    /// <summary> Words in the mod, option, folder or file names. </summary>
    Words,

    /// <summary> Shaders and textures (skin, hair, eyes; textures only). </summary>
    Material,

    /// <summary> The user's own Penumbra folder names. </summary>
    Folder,

    /// <summary> A move the user made on a part that looks the same. </summary>
    Learned,
}

/// <summary>
/// One thing the detector saw, for "Why is it here?". <see cref="Value"/> is a short English phrase ("earrings",
/// "top of head", "says “horns”"); fixed phrases go through Loc.T when shown.
/// </summary>
public sealed class Evidence
{
    public EvidenceKind Kind { get; set; }
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// The word or folder name that a "says “…”" / "your folder “…”" phrase quotes, "" for fixed phrases. With it the
    /// phrase can be translated around the word instead of taking <see cref="Value"/> apart.
    /// </summary>
    public string Word { get; set; } = string.Empty;

    /// <summary> The shelf this points to ("" when it points nowhere, e.g. "no hint"). </summary>
    public string Piece { get; set; } = string.Empty;
    public float Weight { get; set; }
}

public sealed class PieceGuess
{
    public string Piece { get; set; } = string.Empty;
    public float Score { get; set; }
}

/// <summary> What the detector found out about one part of a mod. Plain data, kept in a cache file. </summary>
public sealed class PartInfo
{
    public string Mod { get; set; } = string.Empty;
    public Socket Socket { get; set; }

    /// <summary> The shelf the detector puts it on (never a virtual shelf). The user's moves are stored elsewhere. </summary>
    public string Piece { get; set; } = Pieces.Other;

    /// <summary>
    /// The kind within the shelf as a lower-case English id ("dress", "jacket", "bodysuit", "skirt", "pants", "stockings",
    /// "boots", "animation", "effect"…), "" when unknown. Kinds become the automatic chips above the cards.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary> 0..1. Below <see cref="DetectorThresholds.Unsure"/> the part waits under To sort. </summary>
    public float Confidence { get; set; }

    /// <summary> Shelves it could be, best first (at least one). </summary>
    public List<PieceGuess> Guesses { get; set; } = new();
    public List<Evidence> Evidence { get; set; } = new();
    public BodyRegions Covers { get; set; }

    /// <summary> Fixed-length numbers describing where the model sits, for "looks the same as" (empty: no model read). </summary>
    public float[] Signature { get; set; } = Array.Empty<float>();
    public bool TextureOnly { get; set; }

    /// <summary> Game race codes it has files for (201, 801…); empty: not tied to a race. </summary>
    public List<int> RaceCodes { get; set; } = new();

    /// <summary> Option groups whose options change files of this part (the only ones worth showing for it). </summary>
    public List<string> Groups { get; set; } = new();

    /// <summary> How many different models it replaces in this socket (2 or more: "n versions"). </summary>
    public int Models { get; set; } = 1;

    public PartKey Key()
        => new(Mod, Socket);
}

public static class DetectorThresholds
{
    /// <summary> Parts with less confidence than this wait under To sort. </summary>
    public const float Unsure = 0.55f;
}

/// <summary> Everything the detector found in one mod. </summary>
public sealed class ModDetection
{
    public int DetectorVersion { get; set; }
    public List<PartInfo> Parts { get; set; } = new();

    /// <summary> Files that could not be read (for the log, never shown as an error to the user). </summary>
    public List<string> Problems { get; set; } = new();
}

/// <summary> What the library knows about a mod besides its files. </summary>
/// <param name="Name"> The mod's display name. </param>
/// <param name="SortFolder"> The user's Penumbra folder path of the mod ("Clothes/Female/Top"), "" when none. </param>
/// <param name="Tags"> Mod tags from Penumbra. </param>
/// <param name="PreferRaceCodes"> The player's race code and its fallbacks, best first (which model to look at when there are several). </param>
/// <param name="PreferBody"> The player's body mod ("Rue"), "" when unknown (which size option to look at). </param>
public sealed record ModHints(string Name, string SortFolder, IReadOnlyList<string> Tags, IReadOnlyList<int> PreferRaceCodes, string PreferBody);

/// <summary>
/// The smart detection. Reads a mod's files (never changes them, never throws) and says which parts it has and where
/// each belongs. Safe to call from background threads.
/// </summary>
public interface IDetector
{
    /// <summary> Bumped when results may differ from before, so caches are redone. </summary>
    int Version { get; }

    /// <summary>
    /// Looks at one mod (a mod folder, or a .pmp file). When cancelled it returns no parts, the problem "cancelled" and
    /// <see cref="ModDetection.DetectorVersion"/> 0: such a result must be thrown away, never cached.
    /// </summary>
    ModDetection Detect(string modFolder, ModHints hints, CancellationToken cancel = default);

    /// <summary>
    /// The parts among <paramref name="candidates"/> that look the same as <paramref name="moved"/> (same socket, model in
    /// the same place), closest first. Used after the user moved a part: "3 more look the same".
    /// </summary>
    IReadOnlyList<PartInfo> LookAlikes(PartInfo moved, IEnumerable<PartInfo> candidates);

    /// <summary>
    /// Writes the census: for every mod, the raw facts the detector looked at and what it decided, as one gzip'd
    /// JSON-lines file (lists and numbers only, never file contents). The facts are complete enough to run the decision
    /// again from the file alone. Returns how many mods were written. Slow: call it on a background thread.
    /// </summary>
    int WriteCensus(string file, IEnumerable<(string Folder, ModHints Hints)> mods, Action<int>? progress = null, CancellationToken cancel = default);
}
