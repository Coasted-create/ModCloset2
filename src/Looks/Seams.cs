using ModCloset2.Core;

namespace ModCloset2.Looks;

// What the looks engine needs from the rest of the plugin. The foundation implements these when wiring the looks in;
// the tests implement them with fakes. No Dalamud types, so the engine stays testable. Everything here is called on the
// framework / draw thread (from LooksEngine.Tick and the ILooks members), never from a background thread.

/// <summary> One kept part that is on: the part whose files win its socket. </summary>
/// <param name="Key"> The part (its socket is where it is on). </param>
/// <param name="Shelf"> The shelf it is on now, after the user's moves (says whether it is outfit or body, and how it is shown). </param>
/// <param name="Options"> The options its mod is on with now (the wardrobe's <c>CurrentOptions</c>). </param>
/// <param name="Version"> Which of its versions shows (index into its versions), -1 when unknown or not gear. </param>
public sealed record WornPart(PartKey Key, string Shelf, IReadOnlyDictionary<string, List<string>> Options, int Version);

/// <summary> One part of a look handed to the session for trying on. </summary>
/// <param name="Options">
/// The options of the groups the look names (only groups shown for the part, never body or race groups): put them over
/// the options the part would be tried with anyway (saved, else the defaults picked for the player); groups not named
/// stay as they are. Null: the saved ones, as for a click on its card.
/// </param>
/// <param name="Version"> Which of its versions, -1: the one the closet would pick for a click on its card. </param>
public sealed record LookTryPart(PartKey Key, IReadOnlyDictionary<string, List<string>>? Options, int Version);

/// <summary>
/// A look to try on, as one set. It replaces whatever was tried before. Keep keeps it as one step, Put back ends it, as
/// for anything else.
/// </summary>
/// <param name="Look"> The look (the session echoes it in <see cref="ILooksOutfit.TryingLook"/> and <see cref="ILooksOutfit.LastKept"/>). </param>
/// <param name="Colourway"> Which of its colourways is tried (echoed in <see cref="LookKept"/>). </param>
/// <param name="Name"> For the try bar's name ("Beach day"), translated. </param>
/// <param name="Note"> For the try bar's quiet line ("5 pieces · your hair and body stay as they are"), translated. </param>
/// <param name="Parts"> Parts to put on, each in its socket (only those that are not on already as asked). </param>
/// <param name="TakeOff"> Kept parts to take off (as the Nothing tile does for them): what is on in a socket the look wants empty. </param>
/// <param name="Colours"> Colours to show with the try (a colour snapshot of <see cref="ILooksColours"/>), null: leave the colours alone. </param>
/// <param name="Character"> The character's colours and showings to put on with the try (Glamourer), null: none. </param>
public sealed record LookTry(Guid Look, int Colourway, string Name, string Note, IReadOnlyList<LookTryPart> Parts, IReadOnlyList<PartKey> TakeOff,
    string? Colours, CharacterLook? Character = null);

/// <summary>
/// The character's colours (key → 0xRRGGBB: "skin", "hair", "highlights", "eyeLeft", "eyeRight", "lips", "facePaint",
/// "tattoo") and what is shown (key → on: "weapon" drawn, "hat" shown, "visor" down), as Glamourer has them. A key that
/// is not there is not known (read) or not set (put on).
/// </summary>
public sealed record CharacterLook(IReadOnlyDictionary<string, uint> Colours, IReadOnlyDictionary<string, bool> Showing)
{
    public static readonly CharacterLook Empty = new(new Dictionary<string, uint>(), new Dictionary<string, bool>());

    public bool IsEmpty
        => Colours.Count == 0 && Showing.Count == 0;
}

/// <summary> The game, as a look worn by itself needs it: the gearsets and jobs, and which is on now. </summary>
public interface ILooksGame
{
    /// <summary> The character's gearsets in order: number (1-based), name, job abbreviation. Empty when not known. </summary>
    IReadOnlyList<(int Number, string Name, string Job)> Gearsets { get; }

    /// <summary> The jobs in the game's order: abbreviation and name. </summary>
    IReadOnlyList<(string Abbreviation, string Name)> Jobs { get; }

    /// <summary> The gearset in use (1-based), 0 when none or not known. Read every frame: cheap. </summary>
    int CurrentGearset { get; }

    /// <summary> The job now (its abbreviation), "" when not known. Read every frame: cheap. </summary>
    string CurrentJob { get; }
}

/// <summary> Glamourer's designs, as a look kept as a design needs them. </summary>
public interface ILooksDesigns
{
    /// <summary> Why a design cannot be made in Glamourer by the closet ("" when it can). </summary>
    string Problem { get; }

    /// <summary> Makes a design from a Glamourer design text (JSON), replacing <paramref name="old"/>; its id, or null when that failed. </summary>
    Guid? Put(string name, string design, Guid? old);

    /// <summary> Puts a text on the clipboard; false when that is not possible. </summary>
    bool Copy(string text);
}

/// <summary> A Keep of a try that started as a look. <see cref="Serial"/> goes up by one with every such Keep. </summary>
public sealed record LookKept(Guid Look, int Colourway, int Serial);

/// <summary>
/// The outfit and the shared try-on, as the looks see them. Implemented by the foundation from the session and the
/// wardrobe.
/// </summary>
public interface ILooksOutfit
{
    /// <summary> Bumped whenever <see cref="Kept"/>, <see cref="Steps"/>, <see cref="Trying"/>, <see cref="TryingLook"/> or <see cref="LastKept"/> may have changed. </summary>
    int Version { get; }

    /// <summary>
    /// Every kept part that is on, one per socket (any socket but Other, MainHand, OffHand and None). Never what is being
    /// tried (the wardrobe freezes what is on while trying). A new list whenever it changes.
    /// </summary>
    IReadOnlyList<WornPart> Kept { get; }

    /// <summary>
    /// Goes up by one with every step of the user that changes the kept outfit: Keep (also of a take-off), an option or
    /// version change of a kept part, Undo of such a step. Never for a try, never because what is on was read again.
    /// </summary>
    int Steps { get; }

    /// <summary> Something is being tried on (any try, a look's or not). </summary>
    bool Trying { get; }

    /// <summary>
    /// The look the current try started as (from <see cref="TryLook"/>), also after the user swapped pieces in or out of
    /// it; null when nothing is tried, or the try did not start as a look.
    /// </summary>
    Guid? TryingLook { get; }

    /// <summary> The last Keep of a try that started as a look (null: none since the plugin started). </summary>
    LookKept? LastKept { get; }

    /// <summary> Tries a look on (replacing what was tried before). Also a try with no parts and only colours. </summary>
    void TryLook(LookTry look);

    /// <summary> Keeps what is tried (as the Keep button does). For a look worn by itself (a gearset changed). </summary>
    void Keep()
    {
    }

    /// <summary> The character's colours and showings worn now (Glamourer), null when not known. </summary>
    CharacterLook? Character
        => null;

    /// <summary>
    /// Shows one sentence on the closet's status line (what a save did: "Saved as “Gala night”."). Translated already.
    /// The default does nothing, so the plugin builds before the session shows it.
    /// </summary>
    void Say(string text)
    {
    }
}

/// <summary> One part of the library, as the looks need it. </summary>
/// <param name="Key"> The part. </param>
/// <param name="Name"> The mod's name. </param>
/// <param name="Author"> The mod's author ("" when unknown). </param>
/// <param name="Picture"> The part's picture file, or null. </param>
/// <param name="Shelf"> The shelf it is on now (after the user's moves; "hidden" when hidden). </param>
/// <param name="Info"> What the detection (or step 1) knows: socket, signature, kind. </param>
/// <param name="Groups"> The option groups shown for this part (they change it; never body or race groups). </param>
public sealed record LookLibraryPart(PartKey Key, string Name, string Author, string? Picture, string Shelf, PartInfo Info,
    IReadOnlyList<string> Groups);

/// <summary> The mod library, as the looks see it. Implemented by the foundation from the library and the session. </summary>
public interface ILooksLibrary
{
    /// <summary> Bumped whenever anything below may have changed (parts, names, pictures, shelves after moves). </summary>
    int Version { get; }

    /// <summary> Bumped only when mods were added, removed, renamed or moved (what can make a look's piece go missing or come back). </summary>
    int ModsVersion { get; }

    /// <summary>
    /// Every mod has been read once (step 1). Before that a part that cannot be found is not gone yet, and old looks are
    /// not carried over.
    /// </summary>
    bool Ready { get; }

    /// <summary> One part, or null when there is no such part. </summary>
    LookLibraryPart? Part(PartKey key);

    /// <summary> The parts of one mod (empty when the mod is gone). </summary>
    IReadOnlyList<LookLibraryPart> PartsOf(string mod);

    /// <summary> The parts on one shelf, after the user's moves (the cards of that shelf, in any order). </summary>
    IReadOnlyList<LookLibraryPart> PartsOn(string shelf);

    /// <summary> Every part of the library (only read when a look's piece went missing, to find it again). </summary>
    IReadOnlyList<LookLibraryPart> Parts { get; }

    /// <summary>
    /// A few names of the files the part is made of (paths inside the mod), at most 16, the same ones every time. Read
    /// when a look is made or a piece is found again, never every frame. Empty when not known.
    /// </summary>
    IReadOnlyList<string> FilesOf(PartKey key);

    /// <summary> The version (index into the part's versions) a game item belongs to, -1 when none. For the old looks. </summary>
    int VersionOf(PartKey key, ulong item);

    /// <summary> The game item a gear part is worn through in one of its versions, 0 when none (a Glamourer design holds it). </summary>
    ulong ItemOf(PartKey key, int version)
        => 0;

    /// <summary> A shelf's name, translated ("Tops"). </summary>
    string ShelfName(string shelf);

    /// <summary> A shelf's icon key. </summary>
    string ShelfIcon(string shelf);
}

/// <summary>
/// The colours, as the looks need them (implemented by the foundation from the colour engine). Optional: without it the
/// looks have no colourways and leave colours alone.
/// </summary>
public interface ILooksColours
{
    /// <summary> Bumped whenever the colours of what is worn may have changed. </summary>
    int Version { get; }

    /// <summary>
    /// The colours of these parts now: the outfit colours, the switch "dress everything", and every choice the user made
    /// for their areas, as one opaque string (the colour engine's undo snapshot of these parts). Null when the colours
    /// cannot tell now.
    /// </summary>
    string? Snapshot(IReadOnlyCollection<PartKey> parts);

    /// <summary> The colour the biggest area of this worn part shows now (0xRRGGBB), null when not known (yet). </summary>
    uint? ShowingMain(PartKey part);
}

/// <summary> Where the looks are kept: one JSON text in the plugin's own config folder. </summary>
public interface ILooksStore
{
    /// <summary>
    /// What was written last. "" only when there is no file yet; null when the file is there but cannot be read now
    /// (locked by antivirus or cloud sync, a slow disk). On null the looks do not save, do not import and ask again a
    /// few seconds later: a failed read must never lead to the file being written over.
    /// </summary>
    string? Read();

    /// <summary> Keeps the text (the foundation may write the file in the background). </summary>
    void Write(string text);

    /// <summary>
    /// Keeps a looks text that could not be read under another name (looks.damaged.json), so the next
    /// <see cref="Write"/> does not lose it for good.
    /// </summary>
    void SetAside(string text);

    /// <summary>
    /// The old Mod Closet's settings file (pluginConfigs/ModCloset.json) as text, read-only. "" when there is no such
    /// file (nothing to carry over), null when it is there but cannot be read now (then the looks ask again next start).
    /// </summary>
    string? OldSettings();
}

/// <summary>
/// The closet's undo. A looks command hands over one step with how the looks it changed were before and after (opaque
/// strings); when the user takes that step back, the session calls <see cref="LooksEngine.Restore"/> with "before".
/// </summary>
public interface ILooksUndo
{
    void Record(string description, string before, string after);
}

/// <summary> The clock (so the tests can move time). </summary>
public interface ILooksClock
{
    DateTime UtcNow { get; }

    /// <summary> A UTC time in the user's own time zone (for "Today", "Yesterday"). </summary>
    DateTime ToLocal(DateTime utc);
}

/// <summary> The real clock. </summary>
public sealed class SystemLooksClock : ILooksClock
{
    public DateTime UtcNow
        => DateTime.UtcNow;

    public DateTime ToLocal(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
}
