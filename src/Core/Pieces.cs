namespace ModCloset2.Core;

/// <summary> Where a shelf sits in the rail. </summary>
public enum PieceGroup
{
    /// <summary> Above the groups (Outfits). </summary>
    Top,
    Clothes,
    Jewellery,
    Body,

    /// <summary> The user's own shelves. </summary>
    Mine,

    /// <summary> Below the groups (To sort, Everything else, Hidden). </summary>
    Bottom,
}

/// <summary>
/// One shelf of the closet. Built-in shelves have fixed ids; the user's own have ids starting with "user:".
/// <see cref="Name"/> is English; show it through Loc.T. <see cref="Icon"/> is an icon key the UI knows (for built-in
/// shelves it equals the id).
/// </summary>
public sealed record PieceDef(string Id, string Name, PieceGroup Group, string Icon);

/// <summary> The built-in shelves and their ids. A "piece" is what a thing looks like on you, not the slot it borrows. </summary>
public static class Pieces
{
    /// <summary> Virtual: every mod with two or more clothing parts; clicking a card tries all of them on. </summary>
    public const string Outfits = "outfits";

    public const string Head     = "head";
    public const string Top      = "top";
    public const string Gloves   = "gloves";
    public const string Bottoms  = "bottoms";
    public const string Legwear  = "legwear";
    public const string Shoes    = "shoes";

    public const string Earrings  = "earrings";
    public const string Necklace  = "necklace";
    public const string Bracelets = "bracelets";
    /// <summary> Rings for the right hand: ring-slot parts with files for the right finger (or both). </summary>
    public const string Rings     = "rings";

    /// <summary>
    /// Virtual: rings for the left hand, the ring-slot parts whose home is <see cref="Rings"/> and that have files for the
    /// left finger (or both). A part is never placed here; moving a card here means moving it to <see cref="Rings"/>.
    /// </summary>
    public const string RingsLeft = "ringsleft";

    public const string Hair  = "hair";
    public const string Face  = "face";

    /// <summary>
    /// Makeup and other paint on the face: makeup, face paint and decals, eyes, brows, lashes, freckles of the face.
    /// Sculpts (a face model of its own) stay on <see cref="Face"/>.
    /// </summary>
    public const string Makeup = "makeup";
    public const string Ears  = "ears";
    public const string Tail  = "tail";
    public const string Skin  = "skin";

    /// <summary>
    /// Body mods: the bare body's own models for one or more races (a body base and what is made on top of it); also
    /// what shows of the body where nothing is worn. Skin textures, tattoos and markings without a body model of their
    /// own stay on <see cref="Skin"/>.
    /// </summary>
    public const string Bodies = "bodies";
    public const string Nails = "nails";

    /// <summary> Virtual: parts the detector is unsure about. A part leaves it when the user picks a shelf. </summary>
    public const string ToSort = "tosort";

    /// <summary> Everything that is not worn: animations, effects, sounds, interface, mounts, weapons, helper files. </summary>
    public const string Other = "other";

    /// <summary> Virtual: parts the user hid. </summary>
    public const string Hidden = "hidden";

    public const string UserPrefix = "user:";

    public static readonly IReadOnlyList<PieceDef> BuiltIn = new PieceDef[]
    {
        new(Outfits, "Outfits", PieceGroup.Top, Outfits),

        new(Head, "Headwear", PieceGroup.Clothes, Head),
        new(Top, "Tops", PieceGroup.Clothes, Top),
        new(Gloves, "Gloves", PieceGroup.Clothes, Gloves),
        new(Bottoms, "Bottoms", PieceGroup.Clothes, Bottoms),
        new(Legwear, "Legwear", PieceGroup.Clothes, Legwear),
        new(Shoes, "Shoes", PieceGroup.Clothes, Shoes),

        new(Earrings, "Earrings", PieceGroup.Jewellery, Earrings),
        new(Necklace, "Necklaces", PieceGroup.Jewellery, Necklace),
        new(Bracelets, "Bracelets", PieceGroup.Jewellery, Bracelets),
        new(Rings, "Rings · right", PieceGroup.Jewellery, Rings),
        new(RingsLeft, "Rings · left", PieceGroup.Jewellery, RingsLeft),

        new(Hair, "Hair", PieceGroup.Body, Hair),
        new(Face, "Face", PieceGroup.Body, Face),
        new(Makeup, "Makeup", PieceGroup.Body, Makeup),
        new(Ears, "Ears", PieceGroup.Body, Ears),
        new(Tail, "Tails", PieceGroup.Body, Tail),
        new(Bodies, "Bodies", PieceGroup.Body, Bodies),
        new(Skin, "Skin", PieceGroup.Body, Skin),
        new(Nails, "Nails", PieceGroup.Body, Nails),

        new(ToSort, "To sort", PieceGroup.Bottom, ToSort),
        new(Other, "Everything else", PieceGroup.Bottom, Other),
        new(Hidden, "Hidden", PieceGroup.Bottom, Hidden),
    };

    public static bool IsUser(string id)
        => id.StartsWith(UserPrefix, StringComparison.Ordinal);

    /// <summary> Shelves a part cannot be detected into or dragged onto as its home (they are views). </summary>
    public static bool IsVirtual(string id)
        => id is Outfits or ToSort or RingsLeft;

    /// <summary> True for the clothes and jewellery shelves (what a Look holds by default). </summary>
    public static bool IsOutfitPiece(string id)
        => id is Head or Top or Gloves or Bottoms or Legwear or Shoes or Earrings or Necklace or Bracelets or Rings;

    /// <summary> True for the body shelves (a Look only touches these when the user says so). </summary>
    public static bool IsBodyPiece(string id)
        => id is Hair or Face or Makeup or Ears or Tail or Bodies or Skin or Nails;
}
