namespace ModCloset2.Core;

// What the faces (the window wide or slim, and "On you") see of the closet. A face draws from an IClosetSession and
// calls its commands; it never talks to Penumbra, Glamourer or the detector itself. All three faces share one session,
// so switching face never loses what is being tried on.
//
// Threading: everything here is called on the draw thread. Commands return at once; slow work (redraws, scans) goes on
// in the background and shows up as a new Version. Lists are rebuilt by the session, never changed in place while a
// face reads them. Every text in these classes is already translated and ready to show.

public enum CardState : byte
{
    None,

    /// <summary> Kept and showing on the character. </summary>
    On,

    /// <summary> Being tried on right now. </summary>
    Trying,
}

/// <summary> One row of the rail: a shelf, how many parts it has, and what of it is on the character. </summary>
public sealed class RailRow
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public PieceGroup Group { get; init; }
    public string Icon { get; init; } = string.Empty;
    public int Count { get; init; }

    /// <summary> Name of what is on in this shelf ("" when nothing). </summary>
    public string WornName { get; init; } = string.Empty;

    /// <summary> Picture file of what is on (null when nothing or no picture). </summary>
    public string? WornPicture { get; init; }

    /// <summary>
    /// How many more things are on in this shelf besides the one named (a top, and a jacket worn through another slot):
    /// 0 for none. A face shows it as "+1" after the name.
    /// </summary>
    public int WornMore { get; init; }

    /// <summary> A number to call attention to (To sort: how many wait), 0 for none. </summary>
    public int Badge { get; init; }

    /// <summary> True for a shelf the user made (it can be renamed and removed). </summary>
    public bool IsUser { get; init; }
}

/// <summary>
/// One card in the grid: one part of a mod. On the Outfits shelf a card stands for the whole mod; its key then has
/// <see cref="Socket.None"/> and trying it on tries all clothing parts of the mod.
/// </summary>
public sealed class Card
{
    public PartKey Key { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string? Picture { get; init; }

    /// <summary> The shelf it is on now (after the user's moves). </summary>
    public string Piece { get; init; } = string.Empty;

    /// <summary> Name of that shelf, shown on search results. </summary>
    public string PieceName { get; init; } = string.Empty;

    /// <summary> Icon key for the picture placeholder (the shelf's icon). </summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>
    /// A short label that tells this card apart, "" when there is nothing to tell: on Everything else the kind
    /// ("Animation", "Effect"…); on any other shelf, when the same mod has several cards there, what this one is
    /// ("Jacket", "Top") or, when they are the same kind, how it is worn ("Wrist slot").
    /// </summary>
    public string KindName { get; init; } = string.Empty;
    public CardState State { get; init; }

    /// <summary> Added lately and never tried on. </summary>
    public bool IsNew { get; init; }

    /// <summary> 2 or more: "n versions". </summary>
    public int Versions { get; init; } = 1;

    /// <summary> 2 or more: "n-piece set" (the mod has that many different things to wear; the same thing in several slots counts once). </summary>
    public int PartsInMod { get; init; } = 1;

    /// <summary>
    /// 2 or more: "n slots". The mod offers this same thing through several slots of the game (tights in the legs slot,
    /// the ear slot and the ring slot, so they can be worn with or without a skirt). It is one card; a click wears it
    /// through the slot that fits best now, and <see cref="IClosetSession.SlotsOf"/> lists the choices.
    /// </summary>
    public int Slots { get; init; } = 1;

    /// <summary> The user moved it here (so "Put back where the closet had it" is offered). </summary>
    public bool Moved { get; init; }

    /// <summary>
    /// The closet is unsure about its shelf. The card stays on its best guess (so nothing is ever out of reach), gets a
    /// small mark, and also waits under To sort until the user picks a shelf or confirms this one.
    /// </summary>
    public bool Unsure { get; init; }

    /// <summary>
    /// "Make it its own mod…" is offered for this card: it is gear, and its mod holds more than this thing. What it
    /// would do comes from <see cref="IClosetSession.OwnModOf"/>.
    /// </summary>
    public bool OwnMod { get; init; }

    /// <summary>
    /// This card's mod is a copy the closet made of one thing of another mod: that mod's name ("" otherwise). While the
    /// copy is there, the thing has no card of its own in the mod it came from, unless it is on or tried on from there.
    /// </summary>
    public string CopyOf { get; init; } = string.Empty;
}

/// <summary> An automatic chip above the cards ("Dresses 58"). Id "" is "All". </summary>
public sealed class KindChip
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Count { get; init; }
}

/// <summary> One option group that changes the part that is on, with its choices. </summary>
public sealed class OptionChip
{
    public string Group { get; init; } = string.Empty;
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

    /// <summary> The choices that are on (one for a single-choice group). </summary>
    public IReadOnlyList<string> On { get; init; } = Array.Empty<string>();

    /// <summary> Several choices can be on at once. </summary>
    public bool Multi { get; init; }
}

/// <summary> One slot of the game a thing can be worn through, when its mod offers it in several. </summary>
public sealed class SlotChoice
{
    public Socket Slot { get; init; }

    /// <summary> "Legs slot", "Ring slot". </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary> It is on, or being tried on, through this slot now. </summary>
    public bool On { get; init; }

    /// <summary> The slot a click on the card would use now. </summary>
    public bool Next { get; init; }

    /// <summary> Name of what is on in that slot now and would come off ("" when the slot is free or holds this thing). </summary>
    public string Replaces { get; init; } = string.Empty;
}

/// <summary> One area of a piece's colour bar. </summary>
public sealed class ColourSegment
{
    /// <summary> Index of the area within the piece (what the colour commands take). </summary>
    public int Area { get; init; }

    /// <summary> Share of the piece's surface, 0..1 (the segment's width). </summary>
    public float Share { get; init; }

    /// <summary> Colour as 0xRRGGBB. </summary>
    public uint Rgb { get; init; }
    public string Label { get; init; } = string.Empty;

    /// <summary> Coloured by hand: keeps its own colour when the outfit colours change. </summary>
    public bool Pinned { get; init; }

    /// <summary> Painted texture without colours to change (drawn hatched, not clickable). </summary>
    public bool Painted { get; init; }
}

/// <summary> What is on in the selected shelf. </summary>
public sealed class WearingInfo
{
    public PartKey Key { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Picture { get; init; }
    public string Icon { get; init; } = string.Empty;
    public IReadOnlyList<OptionChip> Options { get; init; } = Array.Empty<OptionChip>();

    /// <summary> Which of its versions is on (0-based) and how many there are (1: no chip). </summary>
    public int Version { get; init; }
    public int Versions { get; init; } = 1;

    /// <summary> The piece's colour bar; empty until colours are supported for it. </summary>
    public IReadOnlyList<ColourSegment> Colours { get; init; } = Array.Empty<ColourSegment>();
}

public sealed class TryItem
{
    public PartKey Key { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Piece { get; init; } = string.Empty;

    /// <summary> This entry is a take-off ("Nothing" was tried for its shelf): Key and Name are what would come off. </summary>
    public bool TakingOff { get; init; }
}

/// <summary> What is being tried on. Several parts can be tried at once (one per socket); Keep keeps them all. </summary>
public sealed class TryState
{
    public static readonly TryState Nothing = new();

    public bool Active { get; init; }

    /// <summary>
    /// The game is still redrawing the character; newer clicks wait for it. Can be true while <see cref="Active"/> is
    /// false: a Keep, Put back or Undo that is still waiting for the game.
    /// </summary>
    public bool Busy { get; init; }

    /// <summary> "Trying on" or "Taking off". </summary>
    public string Lead { get; init; } = string.Empty;

    /// <summary> The thing named after the lead: one name, or "3 things". </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary> A quiet line after the name: "replaces Lace bodysuit", "same slot as Fox tail", "" for none. </summary>
    public string Note { get; init; } = string.Empty;
    public IReadOnlyList<TryItem> Items { get; init; } = Array.Empty<TryItem>();

    /// <summary> Other parts of the newest mod that could be tried on with it ("Also in this mod"). </summary>
    public IReadOnlyList<TryItem> Extras { get; init; } = Array.Empty<TryItem>();
}

/// <summary> A part the closet is unsure about, with what it saw and where it could go. </summary>
public sealed class SortItem
{
    public PartKey Key { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Picture { get; init; }
    public string Icon { get; init; } = string.Empty;

    /// <summary> The three witnesses in a few words each ("earrings", "top of head", "“horns”" or "no hint"). </summary>
    public string SlotText { get; init; } = string.Empty;
    public string SitsText { get; init; } = string.Empty;
    public string WordsText { get; init; } = string.Empty;
    public BodyRegions Covers { get; init; }

    /// <summary> Up to two shelves it could go to, best first. A guess with an id of "" proposes a new shelf by its name. </summary>
    public IReadOnlyList<PieceDef> Guesses { get; init; } = Array.Empty<PieceDef>();
}

/// <summary> After a move: other parts that look the same and could follow it. </summary>
public sealed class MoveSuggestion
{
    /// <summary> "You moved Fluffy fox ears from Tails to Ears." </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary> "3 more mods borrow the tail slot but sit on the head." </summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary> The name of the part that was moved (it also appears inside <see cref="Text"/>, where a face may set it off). </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary> The shelves the move went from and to (ids). </summary>
    public string FromPiece { get; init; } = string.Empty;
    public string ToPiece { get; init; } = string.Empty;

    /// <summary> Icon key of the shelf the parts would move to. </summary>
    public string ToIcon { get; init; } = string.Empty;
    public int Count { get; init; }
    public IReadOnlyList<string?> Pictures { get; init; } = Array.Empty<string?>();
}

public sealed class ScanStatus
{
    /// <summary> The library is read and the cards can be shown (detection may still refine in the background). </summary>
    public bool Ready { get; init; }
    public int Done { get; init; }
    public int Total { get; init; }

    /// <summary> "Getting to know your mods… 412 of 2,300", "" when idle. </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary> Why the closet cannot work right now ("Penumbra is not running."), "" when fine. </summary>
    public string Problem { get; init; } = string.Empty;
}

public sealed class StatusLine
{
    /// <summary> Goes up by one with every new status, so a face can tell a new one from an old one with the same words. </summary>
    public int Id { get; init; }
    public string Text { get; init; } = string.Empty;
    public bool IsError { get; init; }

    /// <summary> Seconds since it was set (faces fade it out after a few). </summary>
    public double Age { get; init; }
}

public interface IClosetSession
{
    /// <summary> Bumped whenever something a face shows has changed. </summary>
    int Version { get; }
    ScanStatus Scan { get; }

    // ---- rail ----

    /// <summary> The shelves to show, in order (shelves without parts are left out). </summary>
    IReadOnlyList<RailRow> Rail { get; }

    /// <summary> Every shelf a part can be moved to (built-in and the user's own), in rail order. </summary>
    IReadOnlyList<PieceDef> MoveTargets { get; }
    string Selected { get; }
    void Select(string piece);

    // ---- cards of the selected shelf, or search results from all shelves ----

    string Search { get; set; }
    IReadOnlyList<KindChip> Kinds { get; }

    /// <summary> The chosen kind chip ("" = all). </summary>
    string Kind { get; set; }
    IReadOnlyList<Card> Cards { get; }

    /// <summary>
    /// A face says every frame whether the mouse is over the cards (or the To sort list) of the selected shelf. While it
    /// is, background detection results that would move a part of that shelf wait, so nothing jumps under the mouse.
    /// Several faces may call it in one frame: one "true" is enough for that frame.
    /// </summary>
    void GridHovered(bool hovered);

    /// <summary> What is on in the selected shelf, null when nothing (or while searching). </summary>
    WearingInfo? Wearing { get; }

    // ---- trying on ----

    TryState Trying { get; }

    /// <summary> Tries a part on. A part already tried in the same socket is swapped out; the kept outfit stays underneath. </summary>
    void Try(PartKey part);

    /// <summary> Takes one part out of what is being tried (the others stay). </summary>
    void StopTrying(PartKey part);

    /// <summary> Tries taking off what is on in a shelf. </summary>
    void TryNothing(string piece);
    void SetOption(PartKey part, string group, IReadOnlyList<string> on);
    void SetVersion(PartKey part, int index);

    /// <summary> Makes everything that is being tried on permanent, as one undo step. </summary>
    void Keep();

    /// <summary> Ends trying on; the character is as before. </summary>
    void PutBack();
    bool CanUndo { get; }
    void Undo();

    // ---- sorting ----

    void Move(PartKey part, string piece);
    void PutBackWhereDetected(PartKey part);
    void Hide(PartKey part);
    void Unhide(PartKey part);

    /// <summary> Makes a shelf of the user's own and returns its id. </summary>
    string AddPiece(string name);
    void RenamePiece(string id, string name);

    /// <summary> Removes a shelf of the user's own; its parts go back to where the closet had them. </summary>
    void RemovePiece(string id);
    IReadOnlyList<SortItem> ToSort { get; }
    MoveSuggestion? Suggestion { get; }
    void AcceptSuggestion();
    void DismissSuggestion();
    IReadOnlyList<Evidence> WhyHere(PartKey part);

    // ---- the rest ----

    StatusLine? Status { get; }
    void OpenInPenumbra(PartKey part);
    void Rescan();

    /// <summary> Writes the census file (what the mods contain, for tuning the detection) and reports where it is in Status. </summary>
    void WriteCensus();

    /// <summary> "" (follow the game), "en" or "de". </summary>
    string Language { get; set; }

    /// <summary> Small things a face wants remembered (window sizes, card size, which face). Unknown keys give "". </summary>
    string Remembered(string key);
    void Remember(string key, string value);

    // ---- second round: each is null (or empty) until the session supports it, and faces then show nothing of it ----

    /// <summary> Colours (src/Core/Colours.cs), or null. </summary>
    IOutfitColours? Colours
        => null;

    /// <summary> Looks (src/Core/Looks.cs), or null. </summary>
    ILooks? Looks
        => null;

    /// <summary>
    /// The cards of one shelf in the closet's order, without changing what is selected, searched or filtered in the
    /// window (the "On you" face flips through a shelf this way).
    /// </summary>
    IReadOnlyList<Card> CardsOf(string piece)
        => Array.Empty<Card>();

    /// <summary>
    /// The slots a card's thing can be worn through, in the game's order, when its mod offers it in several; empty
    /// otherwise. <paramref name="part"/> is the card's key or the key of any of its slots.
    /// </summary>
    IReadOnlyList<SlotChoice> SlotsOf(PartKey part)
        => Array.Empty<SlotChoice>();

    /// <summary>
    /// Wears a thing through one particular slot, and remembers that slot for it. When it is on through another slot it
    /// moves (one step for Undo); otherwise it is tried on there, as a click on its card would.
    /// </summary>
    void WearOn(PartKey part, Socket slot)
    {
    }

    /// <summary>
    /// Everything that is on in the selected shelf, the one <see cref="Wearing"/> names first. Several things can be on
    /// one shelf at once, because a shelf says what a thing is and not which slot wears it (a top in the body slot and a
    /// jacket in the wrist slot are both on Tops). Empty when nothing is on (or while searching).
    /// </summary>
    IReadOnlyList<WearingInfo> WearingAll
        => Wearing is { } one ? new[] { one } : Array.Empty<WearingInfo>();

    /// <summary>
    /// What making a card's thing a mod of its own would do (src/Core/OwnMod.cs), or null when this session cannot do
    /// it at all. Cheap, for every frame while the sheet is open: the first call for a card starts reading its mod in
    /// the background and answers <see cref="OwnModState.Working"/> until that is done. <paramref name="part"/> is the
    /// card's key or the key of any of its slots.
    /// </summary>
    OwnModPlan? OwnModOf(PartKey part)
        => null;

    /// <summary>
    /// Makes the new mod under that name, in the background: <see cref="OwnModOf"/> answers
    /// <see cref="OwnModState.Making"/> meanwhile, and the outcome comes as <see cref="Status"/>. The mod it is copied
    /// from is not changed, and nothing on the character changes: the new mod starts switched off. Does nothing unless
    /// the plan is <see cref="OwnModState.Ready"/>.
    /// </summary>
    void MakeOwnMod(PartKey part, string name)
    {
    }

    /// <summary>
    /// A face calls this for every card (or tile) it draws whose picture is not a photo yet (no picture at all, or the
    /// mod's own picture standing in: <see cref="Pictures.IsPhoto"/>). The closet takes those photos first
    /// (src/Core/Photos.cs). Cheap, for every frame; the same part asked again changes nothing, and an ask for a thing
    /// that gets no photo (an outfit card keeps its mod's picture) is ignored.
    /// </summary>
    void WantPicture(PartKey part)
    {
    }

    /// <summary> How far the closet is with its photos, or null when this session takes none. </summary>
    PhotoStatus? Photos
        => null;

    /// <summary> The user's switch for photos. Off: no new photos are taken; the ones there stay and are shown. </summary>
    void SetPhotos(bool on)
    {
    }
}
