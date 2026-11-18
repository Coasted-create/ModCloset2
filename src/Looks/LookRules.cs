using ModCloset2.Core;

namespace ModCloset2.Looks;

/// <summary> The numbers the looks go by, in one place (docs/reports/looks.md lists them with their reasons). </summary>
public static class LookRules
{
    /// <summary> An outfit counts as worn when nothing was kept for this long after the last Keep. </summary>
    public static readonly TimeSpan QuietTime = TimeSpan.FromMinutes(1);

    /// <summary> Outfits kept under Lately. </summary>
    public const int LatelyMax = 24;

    /// <summary> Tiles on a card (for the collage). </summary>
    public const int CardTiles = 4;

    /// <summary> A wear weighs half after this many days (the order of pinned looks). </summary>
    public const double WearHalfLifeDays = 30;

    /// <summary> File names kept per piece to find its mod again. </summary>
    public const int PrintFiles = 6;

    /// <summary> After a Keep of a look, it stays the outfit's look this long even before what is on was read again. </summary>
    public static readonly TimeSpan KeepGrace = TimeSpan.FromSeconds(10);

    /// <summary> A look stops being the outfit's look when more than this share of its pieces differ. </summary>
    public const double DriftShare = 0.5;

    /// <summary> Finding a mod again: the least score, and how far ahead of the second best it must be. </summary>
    public const double HealScore = 0.6, HealLead = 0.1;

    /// <summary>
    /// A full search for missing pieces (an index of every part) runs at most this often: installing fifty mods one by
    /// one costs one search per pause, not fifty.
    /// </summary>
    public static readonly TimeSpan HealPause = TimeSpan.FromSeconds(5);

    /// <summary> While the looks file cannot be read, it is asked for again this often (nothing is saved meanwhile). </summary>
    public static readonly TimeSpan LoadRetry = TimeSpan.FromSeconds(5);

    /// <summary> A removed pinned look is kept (not shown) this long, so a removal noticed after a restart can be undone. </summary>
    public static readonly TimeSpan RemovedKeep = TimeSpan.FromDays(30);

    /// <summary> Parts offered for a missing piece. </summary>
    public const int Replacements = 24;

    /// <summary> At most this many changes are written per second (a burst of commands is one write). </summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    /// <summary> The clothes and jewellery shelves in outfit (rail) order. </summary>
    public static readonly IReadOnlyList<string> OutfitShelves =
        Pieces.BuiltIn.Where(p => Pieces.IsOutfitPiece(p.Id)).Select(p => p.Id).ToArray();

    /// <summary> The body shelves in rail order. </summary>
    public static readonly IReadOnlyList<string> BodyShelves =
        Pieces.BuiltIn.Where(p => Pieces.IsBodyPiece(p.Id)).Select(p => p.Id).ToArray();

    /// <summary> The most telling pieces first: what a look is named after and what its collage shows. </summary>
    public static readonly IReadOnlyList<string> TellingOrder = new[]
    {
        Pieces.Top, Pieces.Bottoms, Pieces.Shoes, Pieces.Legwear, Pieces.Head, Pieces.Gloves, Pieces.Necklace, Pieces.Earrings,
        Pieces.Bracelets, Pieces.Rings, Pieces.Hair, Pieces.Face, Pieces.Makeup, Pieces.Ears, Pieces.Tail, Pieces.Bodies, Pieces.Skin, Pieces.Nails,
    };

    /// <summary> The main piece, for the name: the top (or dress), else the bottoms, legwear, shoes … </summary>
    public static readonly IReadOnlyList<string> NameOrder = new[]
    {
        Pieces.Top, Pieces.Bottoms, Pieces.Legwear, Pieces.Shoes, Pieces.Head, Pieces.Gloves, Pieces.Necklace, Pieces.Earrings,
        Pieces.Bracelets, Pieces.Rings, Pieces.Hair, Pieces.Face, Pieces.Makeup, Pieces.Ears, Pieces.Tail, Pieces.Bodies, Pieces.Skin, Pieces.Nails,
    };

    /// <summary> The gear sockets a look knows as empty when nothing was on there ("nothing here"). </summary>
    public static readonly IReadOnlyList<Socket> GearSockets = new[]
    {
        Socket.Head, Socket.Body, Socket.Hands, Socket.Legs, Socket.Feet, Socket.Ears, Socket.Neck, Socket.Wrists, Socket.Rings,
    };

    /// <summary> Sockets that are worn (what a look can hold): everything but weapons and things not worn. </summary>
    public static bool Worn(Socket socket)
        => socket is not (Socket.None or Socket.Other or Socket.MainHand or Socket.OffHand);

    /// <summary> A piece on this shelf is body (worn only with "with body"): hair, face, ears, tail, skin, nails, whatever the socket. </summary>
    public static bool IsBody(string shelf)
        => Pieces.IsBodyPiece(shelf);

    /// <summary> Where a shelf comes in outfit order (clothes and jewellery, then the user's own, then body, then the rest). </summary>
    public static int Order(string shelf)
    {
        if (shelf == Pieces.RingsLeft)
            shelf = Pieces.Rings; // a view of Rings
        var at = BuiltInIndex.GetValueOrDefault(shelf, -1);
        if (Pieces.IsOutfitPiece(shelf))
            return at;
        if (Pieces.IsUser(shelf))
            return 100;
        if (Pieces.IsBodyPiece(shelf))
            return 200 + at;
        return 300 + Math.Max(0, at);
    }

    /// <summary> Where a shelf comes for names and collages (the most telling first), else <see cref="Order"/> after them. </summary>
    public static int Telling(string shelf)
        => TellingIndex.TryGetValue(shelf == Pieces.RingsLeft ? Pieces.Rings : shelf, out var at) ? at : 50 + Order(shelf);

    private static readonly Dictionary<string, int> BuiltInIndex =
        Pieces.BuiltIn.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);

    private static readonly Dictionary<string, int> TellingIndex =
        TellingOrder.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i, StringComparer.Ordinal);

    /// <summary>
    /// Shelves that are one place for a piece kept before a shelf was split: body mods sat on Skin until Bodies was split
    /// off from it, makeup on Face until Makeup was; a look saved then knows the piece on the old shelf while the part now
    /// sits on the new one (and the other way round).
    /// </summary>
    public static IReadOnlyList<string> Kin(string shelf)
        => KinOf.TryGetValue(shelf, out var kin) ? kin : new[] { shelf };

    private static readonly Dictionary<string, string[]> KinOf = new(StringComparer.Ordinal)
    {
        [Pieces.Skin]      = new[] { Pieces.Skin, Pieces.Bodies },
        [Pieces.Bodies]    = new[] { Pieces.Bodies, Pieces.Skin },
        [Pieces.Face]      = new[] { Pieces.Face, Pieces.Makeup },
        [Pieces.Makeup]    = new[] { Pieces.Makeup, Pieces.Face },
        // the left-hand rings are a view of Rings: a part is never placed there, but should one be reported so, it is a ring
        [Pieces.Rings]     = new[] { Pieces.Rings, Pieces.RingsLeft },
        [Pieces.RingsLeft] = new[] { Pieces.RingsLeft, Pieces.Rings },
    };

    /// <summary> The same shelf, or kin (<see cref="Kin"/>). </summary>
    public static bool SameShelf(string a, string b)
        => a == b || KinOf.TryGetValue(a, out var kin) && Array.IndexOf(kin, b) >= 0;

    /// <summary>
    /// Sockets where things of different shelves show at once: the body socket (a body mod on Bodies and skin textures
    /// on Skin over it) and face textures (eyes on Face and makeup on Makeup). There, a look holds one part per shelf;
    /// everywhere else one part per socket.
    /// </summary>
    public static bool Layered(Socket socket)
        => socket is Socket.BodyBase or Socket.FaceTextures;

    /// <summary>
    /// Two parts take the same place: the same socket, and in a layered socket also the same shelf (the exact shelf:
    /// a body and skin textures are two layers, not one).
    /// </summary>
    public static bool Clash(Socket a, string shelfA, Socket b, string shelfB)
        => a == b && (!Layered(a) || shelfA == shelfB);

    /// <summary> Where a body shelf comes in rail order (others after them). </summary>
    public static int BodyIndex(string shelf)
    {
        for (var i = 0; i < BodyShelves.Count; ++i)
            if (BodyShelves[i] == shelf)
                return i;
        return BodyShelves.Count;
    }



    /// <summary> The body itself (the body mod every outfit is made for: its socket, on Bodies or Skin) is never taken off by a look. </summary>
    public static bool CanTakeOff(Socket socket)
        => socket != Socket.BodyBase;
}
