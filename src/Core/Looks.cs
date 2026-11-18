namespace ModCloset2.Core;

// Looks, as the faces see them. A look is the outfit, kept: every piece that is on, one per slot of the game (and, when
// the user says so, hair and body too). A shelf can hold several pieces of one look, because a shelf says what a thing
// is and not which slot wears it (a top, and a jacket worn through the wrist slot). The closet keeps the outfits the
// user wore under "Lately" on its own; a pin turns one into a look that stays. See docs/LOOKS.md. Threading and texts
// as in Session.cs.

/// <summary> One piece of a look or of the outfit that is on, for collages and the row of small tiles. </summary>
public sealed class LookTile
{
    /// <summary> The shelf. </summary>
    public string Piece { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;

    /// <summary> Name of the thing ("" for a place the look keeps empty). </summary>
    public string Name { get; init; } = string.Empty;
    public string? Picture { get; init; }

    /// <summary> In "wearing now": this piece differs from the look that is on. </summary>
    public bool Changed { get; init; }

    /// <summary> The look holds a mod here that is gone. </summary>
    public bool Missing { get; init; }
}

/// <summary> A look (pinned) or a remembered outfit (lately), as a card. </summary>
public sealed class LookCard
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool Pinned { get; init; }

    /// <summary> A photo of the character in it, when there is one; otherwise the face draws a collage of <see cref="Tiles"/>. </summary>
    public string? Picture { get; init; }

    /// <summary> Its most telling pieces, at most four, biggest first (top, bottoms, shoes, then the rest). </summary>
    public IReadOnlyList<LookTile> Tiles { get; init; } = Array.Empty<LookTile>();

    /// <summary> "6 pieces", "8 pieces, with hair". </summary>
    public string Meta { get; init; } = string.Empty;

    /// <summary> For Lately: "Today", "Yesterday", "Sunday", "Last week". </summary>
    public string When { get; init; } = string.Empty;

    /// <summary> On: every piece of it is what is on (in the colourway <see cref="Colourway"/>). Trying: being tried on. </summary>
    public CardState State { get; init; }

    /// <summary> How many of its pieces hold a mod that is gone (0: none). </summary>
    public int Missing { get; init; }

    /// <summary> The main colour of each of its colourways as 0xRRGGBB (fewer than two: no dots). </summary>
    public IReadOnlyList<uint> Colourways { get; init; } = Array.Empty<uint>();

    /// <summary> The colourway that is on, or was worn last. </summary>
    public int Colourway { get; init; }

    /// <summary> It also changes hair, face, ears, tail, skin and nails. </summary>
    public bool WithBody { get; init; }
}

/// <summary> The outfit that is on, seen as a look. </summary>
public sealed class WearingNow
{
    /// <summary> The pinned look this outfit is, or started from; null when it is an outfit of its own. </summary>
    public Guid? Look { get; init; }

    /// <summary> That look's name, or "" . </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary> "You changed the shoes", "You changed 3 pieces"; "" when it is the look exactly, or no look. </summary>
    public string ChangeText { get; init; } = string.Empty;

    /// <summary> Every clothes and jewellery piece that is on, in outfit order. </summary>
    public IReadOnlyList<LookTile> Tiles { get; init; } = Array.Empty<LookTile>();

    /// <summary> There is something on that is not exactly a pinned look already ("Pin as new" makes sense). </summary>
    public bool CanPin { get; init; }

    /// <summary> It started from a look and differs from it ("Update …" makes sense). </summary>
    public bool CanUpdate { get; init; }
}

public interface ILooks
{
    /// <summary> Bumped whenever something a face shows of the looks has changed. </summary>
    int Version { get; }

    IReadOnlyList<LookCard> Pinned { get; }

    /// <summary> Outfits the closet kept on its own, newest first. </summary>
    IReadOnlyList<LookCard> Lately { get; }
    WearingNow Now { get; }

    /// <summary>
    /// Tries a look on through the shared try-on state (<see cref="IClosetSession.Trying"/> then names the look, and Keep
    /// and Put back work as for anything else). Pieces whose mod is gone are skipped.
    /// </summary>
    void Try(Guid look);

    /// <summary> Tries a look on in one of its colourways. </summary>
    void TryColourway(Guid look, int colourway);

    /// <summary> Turns a remembered outfit into a look that stays. </summary>
    void Pin(Guid look);

    /// <summary> Pins what is on as a new look and returns its id. Same pieces as an existing look: becomes a colourway of it. </summary>
    Guid PinNow();

    /// <summary> Makes a look what is on now. </summary>
    void UpdateFromNow(Guid look);
    void Rename(Guid look, string name);

    /// <summary> Removes a look, or forgets a remembered outfit. One undo step. </summary>
    void Remove(Guid look);
    void SetWithBody(Guid look, bool withBody);
    void RemoveColourway(Guid look, int colourway);

    /// <summary>
    /// For a piece whose mod is gone: the closest parts on that shelf, best first. When several pieces of the look are
    /// gone on one shelf, <see cref="Replace"/> fills the one worn through the same slot as the part given.
    /// </summary>
    IReadOnlyList<Card> ReplacementsFor(Guid look, string piece);
    void Replace(Guid look, string piece, PartKey with);
}
