using ModCloset2.Core;
using ModCloset2.Library;

namespace ModCloset2.Closet;

/// <summary> How far reading the mods has come. </summary>
public sealed class LibraryProgress
{
    public static readonly LibraryProgress Starting = new();

    /// <summary> Every mod went through step 1 (the cards can be shown). </summary>
    public bool Ready { get; init; }
    public int Mods { get; init; }

    /// <summary> Mods through step 1 (what Penumbra says they change). </summary>
    public int Read { get; init; }

    /// <summary> Mods through step 2 (the detector looked at their files), and whether that is still going on. </summary>
    public int Detected { get; init; }
    public bool Detecting { get; init; }

    /// <summary> Why the closet cannot work right now, one translated sentence; "" when fine. </summary>
    public string Problem { get; init; } = string.Empty;
}

/// <summary>
/// The mod library as the session sees it (implemented by <c>Library</c> in the game, by a fake in the tests). Called on
/// the framework / draw thread; never blocks.
/// </summary>
public interface ILibrary
{
    /// <summary> Bumped whenever <see cref="Parts"/> or <see cref="Progress"/> changed. </summary>
    int Version { get; }
    LibraryProgress Progress { get; }

    /// <summary> Every part of every mod read so far (also those that do not fit the player: see <see cref="LibPart.FitsMe"/>). A new list on every change. </summary>
    IReadOnlyList<LibPart> Parts { get; }

    /// <summary> The parts of one mod as of the current <see cref="Version"/> (empty for a mod that is gone). </summary>
    IReadOnlyList<LibPart> PartsOf(string mod);

    /// <summary>
    /// The mods whose parts changed after <paramref name="version"/> (added, changed or gone), or null when that is not
    /// known or too much: then everything is read again.
    /// </summary>
    IReadOnlyCollection<string>? ChangedSince(int version);
    LibMod? Mod(string directory);
    IDetector Detector { get; }

    /// <summary> Forgets what was read and reads every mod again. </summary>
    void Rescan();

    /// <summary> Every mod folder with its hints (for the census). </summary>
    IReadOnlyList<(string Folder, ModHints Hints)> CensusInput();

    /// <summary> The plugin's own folder for caches and the census. </summary>
    string CacheDirectory { get; }
    void OpenInPenumbra(string mod, string name);
}

/// <summary> Penumbra's side of "Make it its own mod" (the game's <c>PenumbraMods</c>, a stand-in in the tests). Framework thread. </summary>
public interface IPenumbraMods
{
    /// <summary> Full path of Penumbra's mod folder ("" when Penumbra does not say). </summary>
    string ModRoot();

    /// <summary> Penumbra takes in a new mod folder (its name inside the mod folder); false when it does not. </summary>
    bool AddMod(string directory);

    /// <summary> Puts the mod at a place in Penumbra's folder tree ("Clothes/Tops/Name"); false when Penumbra does not. </summary>
    bool SetModPath(string directory, string path);
}

/// <summary> Where a wardrobe step stands. </summary>
public enum StepState
{
    /// <summary> No such step (never asked for, or forgotten). </summary>
    Unknown,

    /// <summary> Waiting for the game to finish a redraw. </summary>
    Pending,
    Done,
    Failed,
}

/// <summary> One part to try or keep. <see cref="Nothing"/>: take off what this part shows instead. </summary>
/// <param name="Options"> Options to use (null: the saved ones, else the defaults picked for the player's race and body). </param>
/// <param name="Version"> Which of the part's versions (gear items of different models), 0 = the first. </param>
public sealed record TryRequest(PartKey Key, bool Nothing = false, IReadOnlyDictionary<string, List<string>>? Options = null, int Version = 0);

/// <summary> A sentence from the wardrobe for the status line. <see cref="Serial"/> grows with every new one. </summary>
public sealed record WardrobeMessage(string Text, bool IsError, int Serial, DateTime At)
{
    public static readonly WardrobeMessage None = new(string.Empty, false, 0, DateTime.MinValue);
}

/// <summary>
/// Puts parts on the player and takes them off (implemented by <c>Wardrobe</c> in the game, by a fake in the tests).
/// Commands return at once; the work waits for a redraw when it has to.
/// </summary>
public interface IWardrobe
{
    /// <summary> Bumped whenever something below changed. </summary>
    int Version { get; }

    /// <summary> The game is still redrawing the character, or commands wait for that. </summary>
    bool Busy { get; }

    /// <summary> A try is showing or waiting to be shown (false again when it failed or ended). </summary>
    bool Previewing { get; }

    /// <summary> The mods whose files show in a socket in the kept outfit (frozen while trying on), the main one first. </summary>
    IReadOnlyList<string> ShowingIn(Socket socket);

    /// <summary> The part is on in the kept outfit (its files show; for parts not worn: the mod is on). </summary>
    bool IsOn(PartKey part);

    /// <summary>
    /// The options a mod is (or would be) put on with: what the player's collection has saved, else the defaults with race
    /// and body choices made for the player.
    /// </summary>
    IReadOnlyDictionary<string, List<string>> CurrentOptions(string mod);

    /// <summary> Which version of a gear part is on (index into its versions), -1 when unknown. </summary>
    int VersionShowing(PartKey part);

    /// <summary> Tries these parts on over the kept outfit, replacing what was tried before. Empty: puts back. </summary>
    void Preview(IReadOnlyList<TryRequest> parts);

    /// <summary> Ends trying on; the kept outfit shows again. </summary>
    void PutBack();

    /// <summary> Makes these parts permanent (and takes off the "nothing" ones) as one undo step. </summary>
    void Keep(Guid step, string description, IReadOnlyList<TryRequest> parts);

    /// <summary> Changes a kept mod's options, as one undo step. </summary>
    void SetOptions(Guid step, string description, PartKey part, IReadOnlyDictionary<string, List<string>> options);

    /// <summary> Puts another version of a kept gear part on, as one undo step. </summary>
    void SetVersion(Guid step, string description, PartKey part, int version);

    StepState Step(Guid step);

    /// <summary> Takes back a step, when it is still the newest one. </summary>
    void Undo(Guid step);

    WardrobeMessage Message { get; }
}
