namespace ModCloset2.Core;

// Colours, as the faces see them. One bar per worn piece, split into its areas by size; four outfit colours that
// everything can follow. A face draws from IOutfitColours and calls its commands, like IClosetSession.
// Threading and texts as in Session.cs: draw thread, commands return at once, texts are ready to show.

/// <summary> What an area takes its colour from. </summary>
public enum ColourRole : byte
{
    /// <summary> Its own colour: the mod's, or one the user gave it by hand (then it is pinned). </summary>
    Own = 0,
    Main = 1,
    Second = 2,
    Accent = 3,
    Metal = 4,
}

/// <summary> The outfit colours, each as 0xRRGGBB. </summary>
public sealed class OutfitPalette
{
    public uint Main { get; init; }
    public uint Second { get; init; }
    public uint Accent { get; init; }
    public uint Metal { get; init; }

    public uint Of(ColourRole role)
        => role switch
        {
            ColourRole.Main   => Main,
            ColourRole.Second => Second,
            ColourRole.Accent => Accent,
            ColourRole.Metal  => Metal,
            _                 => 0,
        };
}

/// <summary> One area of a piece: what it is, how big, what colour, and what it follows. Drawn as a segment of the bar. </summary>
public sealed class ColourArea
{
    /// <summary> Index of the area within the piece (what the commands take). Stable while the piece is worn. </summary>
    public int Index { get; init; }

    /// <summary> Share of the piece's surface, 0..1. The shares of a piece add up to 1. </summary>
    public float Share { get; init; }

    /// <summary> The colour it shows now, 0xRRGGBB (for a painted area: an average of the paint, only for drawing). </summary>
    public uint Rgb { get; init; }

    /// <summary> "Main area", "Second area", "Accent", "Metal", "Painted part" … </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary> "about half of the top", "a small part" … for the picker's heading. </summary>
    public string ShareText { get; init; } = string.Empty;

    /// <summary> What it follows now. </summary>
    public ColourRole Follows { get; init; }

    /// <summary>
    /// Keeps its own colour on purpose, whatever the outfit colours do: the user coloured it by hand, or chose "its own"
    /// (the mod's colour) for it.
    /// </summary>
    public bool Pinned { get; init; }

    /// <summary> A painted texture with no colours to change (drawn hatched, not clickable). </summary>
    public bool Painted { get; init; }

    /// <summary> Looks like metal (follows the Metal colour by default). </summary>
    public bool Metal { get; init; }
}

/// <summary> The colour bar of one worn piece. </summary>
public sealed class PieceColours
{
    /// <summary>
    /// What the commands take to name this bar. Usually the shelf id; when two different things show on one shelf (a
    /// second bar), something longer. Never shown.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary> The shelf the piece is on. </summary>
    public string Piece { get; init; } = string.Empty;
    public string PieceName { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public PartKey Key { get; init; }

    /// <summary> Name of the thing worn (the mod's name, or the game item's when no mod shows there). </summary>
    public string Name { get; init; } = string.Empty;
    public string? Picture { get; init; }
    public IReadOnlyList<ColourArea> Areas { get; init; } = Array.Empty<ColourArea>();

    /// <summary> At least one area is pinned (the row shows the pin instead of the link). </summary>
    public bool HasOwn { get; init; }

    /// <summary> Still being read (the bar is drawn as a quiet placeholder). </summary>
    public bool Reading { get; init; }
}

public sealed class SavedPalette
{
    public OutfitPalette Colours { get; init; } = new();

    /// <summary> It is the one in use. </summary>
    public bool InUse { get; init; }
}

public interface IOutfitColours
{
    /// <summary>
    /// Gives a part the colours another part has been given, when it has none of its own yet (the closet copied a thing
    /// into a mod of its own: same files, another mod). Does nothing when <paramref name="from"/> has no colours
    /// remembered or <paramref name="to"/> already has some. Nothing changes on the character.
    /// </summary>
    void CopyMemory(PartKey from, PartKey to)
    {
    }

    /// <summary> Bumped whenever something a face shows of the colours has changed. </summary>
    int Version { get; }

    /// <summary> Why colours cannot work right now ("Glamourer is not running."), "" when fine. </summary>
    string Problem { get; }

    OutfitPalette Palette { get; }

    /// <summary>
    /// Changes an outfit colour. Called many times while the user drags in the picker: everything that follows that
    /// colour changes with it on the character, smoothly, and the change counts as one undo step when the drag ends
    /// (<paramref name="done"/>).
    /// </summary>
    void SetPalette(ColourRole role, uint rgb, bool done);

    /// <summary> "Dress everything I try on in these colours." </summary>
    bool DressEverything { get; set; }

    IReadOnlyList<SavedPalette> Saved { get; }
    void UseSaved(int index);
    void SaveCurrent();
    void RemoveSaved(int index);

    /// <summary> A new set of outfit colours that go together. </summary>
    void Surprise();

    /// <summary> The worn pieces that have a bar, in outfit order. </summary>
    IReadOnlyList<PieceColours> Pieces { get; }

    /// <summary> The (first) bar of the piece on one shelf, for the "Wearing" strip, or null. </summary>
    PieceColours? For(string piece);

    // The commands below name a bar by PieceColours.Id.

    /// <summary> Gives an area a colour by hand (it becomes pinned). Called many times while dragging; <paramref name="done"/> ends the step. </summary>
    void SetArea(string bar, int area, uint rgb, bool done);

    /// <summary> Lets an area follow an outfit colour (or, with <see cref="ColourRole.Own"/>, go back to the mod's own colour). </summary>
    void Follow(string bar, int area, ColourRole role);

    /// <summary>
    /// The whole piece in one colour: every area at once, one undo step, as a game dye would do it (shading stays,
    /// metal is tinted). Called many times while the colour field is dragged, with <paramref name="done"/> on release.
    /// </summary>
    void SetPiece(string bar, uint rgb, bool done) { }

    /// <summary> Every area of the piece follows one colour of the outfit. </summary>
    void FollowPiece(string bar, ColourRole role) { }

    /// <summary> Puts a whole piece back to the mod's own colours. </summary>
    void ResetPiece(string bar);

    /// <summary>
    /// Lights an area up on the character. Call it every frame while the mouse is on the area's segment: the light goes
    /// out by itself shortly after the last call, or at once with <see cref="PointNothing"/>.
    /// </summary>
    void Point(string bar, int area);
    void PointNothing();
}
