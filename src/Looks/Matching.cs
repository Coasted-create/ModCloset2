using ModCloset2.Core;

namespace ModCloset2.Looks;

/// <summary> The kept outfit, indexed once per change (by socket and by part). </summary>
public sealed class OutfitNow
{
    public static readonly OutfitNow Empty = new(Array.Empty<WornPart>());

    public IReadOnlyList<WornPart> All { get; }
    public IReadOnlyDictionary<Socket, WornPart> BySocket { get; }
    public IReadOnlyDictionary<PartKey, WornPart> ByKey { get; }

    public OutfitNow(IReadOnlyList<WornPart> kept)
    {
        var all      = new List<WornPart>();
        var bySocket = new Dictionary<Socket, WornPart>();
        var byKey    = new Dictionary<PartKey, WornPart>();
        foreach (var worn in kept)
        {
            // one per socket (in a layered socket one per shelf), worn sockets only: the seam promises it, a stray
            // second one is ignored
            if (!LookRules.Worn(worn.Key.Socket) || byKey.ContainsKey(worn.Key)
             || all.Any(w => LookRules.Clash(w.Key.Socket, w.Shelf, worn.Key.Socket, worn.Shelf)))
                continue;
            bySocket.TryAdd(worn.Key.Socket, worn);
            byKey[worn.Key] = worn;
            all.Add(worn);
        }

        All      = all;
        BySocket = bySocket;
        ByKey    = byKey;
    }

    /// <summary> What is on in a socket (in a layered socket: the first of its layers). </summary>
    public WornPart? In(Socket socket)
        => BySocket.TryGetValue(socket, out var worn) ? worn : null;

    /// <summary> What is on in the place a part of this shelf takes (in a layered socket: on that shelf). </summary>
    public WornPart? At(Socket socket, string shelf)
    {
        if (!LookRules.Layered(socket))
            return In(socket);
        foreach (var worn in All)
            if (worn.Key.Socket == socket && worn.Shelf == shelf)
                return worn;
        return null;
    }

    /// <summary> Anything the closet knows is on (clothes, jewellery, body things). </summary>
    public bool Any
        => All.Count > 0;

    /// <summary> Something is on that is not body (clothes, jewellery, the user's own shelves…). </summary>
    public bool AnyClothes
        => All.Any(w => !LookRules.IsBody(w.Shelf));
}

/// <summary> How a socket of the outfit differs from what a look says about it. </summary>
public enum DiffKind : byte
{
    /// <summary> Something else is on (another part, or the same part with other options or another version). </summary>
    Changed,

    /// <summary> The look knows the socket as empty and something is on. </summary>
    Added,

    /// <summary> The look has a piece there and nothing is on. </summary>
    TookOff,
}

/// <summary> One difference: the socket, the shelf it shows on, and the parts (the look's, and what is on now). </summary>
public readonly record struct PieceDiff(Socket Socket, string Shelf, DiffKind Kind, PartKey? Was, PartKey? Now);

/// <summary>
/// How the looks see the library while comparing: the option groups that count for a part, and the shelf a part is on
/// now (the shelf decides whether a piece is outfit or body).
/// </summary>
public sealed class Lens
{
    /// <summary> The option groups that count for a part (null: the part is unknown, only the key and version count). </summary>
    public delegate IReadOnlyList<string>? GroupsOf(PartKey key);

    public GroupsOf Groups { get; }
    private readonly Func<PartKey, string?> _shelfNow;

    public Lens(GroupsOf groups, Func<PartKey, string?> shelfNow)
    {
        Groups    = groups;
        _shelfNow = shelfNow;
    }

    /// <summary> A lens without a library: pieces keep their stored shelves, every option group counts. </summary>
    public static readonly Lens Plain = new(_ => null, _ => null);

    /// <summary> The shelf a piece is on: the library's now, or the one it was seen on last (when its mod is gone). </summary>
    public string ShelfOf(LookPiece piece)
        => _shelfNow(piece.Key) is { Length: > 0 } shelf ? shelf : piece.Shelf;

    public bool IsBody(LookPiece piece)
        => LookRules.IsBody(ShelfOf(piece));

    /// <summary> The piece is worn with the look: outfit pieces always, body pieces when the look wears their shelf. </summary>
    public bool InScope(LookData look, LookPiece piece)
    {
        var shelf = ShelfOf(piece);
        return !LookRules.IsBody(shelf) || Wears(look, shelf);
    }

    /// <summary>
    /// The look wears this body shelf: it chose it, or it chose a kin shelf none of its pieces is on any more (a body
    /// chosen as Skin before Bodies was split off, its part on Bodies now). Exact otherwise: a body and skin textures
    /// over it are two shelves.
    /// </summary>
    public bool Wears(LookData look, string shelf)
    {
        foreach (var chosen in look.Body)
        {
            if (chosen == shelf)
                return true;
            if (LookRules.SameShelf(chosen, shelf) && !look.Pieces.Any(p => ShelfOf(p) == chosen))
                return true;
        }

        return false;
    }

    /// <summary> What is on in a layered socket on a body shelf the look wears, and the look does not hold (another skin, other makeup). </summary>
    public IEnumerable<WornPart> ExtraLayers(LookData look, OutfitNow outfit)
        => outfit.All.Where(w => LookRules.Layered(w.Key.Socket) && LookRules.IsBody(w.Shelf) && Wears(look, w.Shelf) && !Matching.Holds(look, w.Key));

    /// <summary> The look holds no outfit piece: a look of body things only (it says nothing about clothes). </summary>
    public bool BodyOnly(LookData look)
        => look.Pieces.Count > 0 && look.Pieces.All(IsBody);
}

/// <summary>
/// Looks against the outfit. Pure. The game's truth is one part per socket, so that is what is compared; shelves only
/// decide what is outfit and what is body.
/// <list type="bullet">
/// <item>A piece is on when its part is on (in its socket) with the same version (when both are known) and the same
/// options in the groups shown for that part that the look names.</item>
/// <item>A socket the look knows as empty matches when nothing is on there, or a body piece on a body shelf the look does
/// not wear.</item>
/// <item>Sockets the look says nothing about, and body shelves it does not wear, are free.</item>
/// </list>
/// </summary>
public static class Matching
{
    public static bool PieceOn(LookPiece piece, OutfitNow outfit, Lens lens)
        => outfit.ByKey.TryGetValue(piece.Key, out var worn) && SameWear(piece, worn, lens);

    public static bool SameWear(LookPiece piece, WornPart worn, Lens lens)
    {
        if (piece.Version >= 0 && worn.Version >= 0 && piece.Version != worn.Version)
            return false;
        return SameOptions(piece.Options, worn.Options, lens.Groups(piece.Key));
    }

    /// <summary> Equal in every shown group the look says something about (a group it does not name is left to the user). </summary>
    public static bool SameOptions(IReadOnlyDictionary<string, List<string>> look, IReadOnlyDictionary<string, List<string>> worn, IReadOnlyList<string>? groups)
    {
        if (groups is null || look.Count == 0)
            return true;
        foreach (var group in groups)
        {
            if (!look.TryGetValue(group, out var want))
                continue;
            var have = worn.TryGetValue(group, out var h) ? h : new List<string>();
            if (!SameChoices(want, have))
                return false;
        }

        return true;
    }

    private static bool SameChoices(List<string> a, List<string> b)
        => a.Count == b.Count && a.All(x => b.Contains(x, StringComparer.OrdinalIgnoreCase));

    /// <summary> What is on in an empty socket counts against the look (a body piece on a body shelf the look does not wear is left alone). </summary>
    private static WornPart? Against(LookData look, Socket empty, OutfitNow outfit, Lens lens)
        => outfit.In(empty) is { } worn && (!LookRules.IsBody(worn.Shelf) || lens.Wears(look, worn.Shelf)) && LookRules.CanTakeOff(empty) ? worn : null;

    /// <summary> The pieces worn with the look, in outfit order (by shelf, then socket). </summary>
    public static IEnumerable<LookPiece> Worn(LookData look, Lens lens)
        => look.Pieces.Where(p => lens.InScope(look, p)).OrderBy(p => LookRules.Order(lens.ShelfOf(p))).ThenBy(p => p.Socket);

    /// <summary> Where the outfit differs from the look, piece by piece (and the empty sockets now filled). </summary>
    public static List<PieceDiff> Diff(LookData look, OutfitNow outfit, Lens lens)
    {
        var diffs    = new List<PieceDiff>();
        var replaced = new HashSet<PartKey>();
        foreach (var piece in Worn(look, lens))
        {
            if (PieceOn(piece, outfit, lens))
                continue;
            var shelf = lens.ShelfOf(piece);
            var worn  = outfit.At(piece.Socket, shelf);
            if (worn is not null)
                replaced.Add(worn.Key);
            diffs.Add(new PieceDiff(piece.Socket, shelf, worn is null ? DiffKind.TookOff : DiffKind.Changed, piece.Key, worn?.Key));
        }

        foreach (var socket in look.Empty)
            if (Against(look, socket, outfit, lens) is { } worn)
                diffs.Add(new PieceDiff(socket, worn.Shelf, DiffKind.Added, null, worn.Key));

        // another layer on a body shelf the look wears (a second skin, other makeup), not already counted as a change
        foreach (var worn in lens.ExtraLayers(look, outfit))
            if (!replaced.Contains(worn.Key))
                diffs.Add(new PieceDiff(worn.Key.Socket, worn.Shelf, DiffKind.Added, null, worn.Key));
        return diffs;
    }

    /// <summary>
    /// The look is on: every piece worn with it is on, and every socket it knows as empty is. Same answer as an empty
    /// <see cref="Diff"/>, without making lists (it runs for every look on every change of the outfit).
    /// </summary>
    public static bool Covers(LookData look, OutfitNow outfit, Lens lens)
    {
        foreach (var piece in look.Pieces)
            if (lens.InScope(look, piece) && !PieceOn(piece, outfit, lens))
                return false;
        foreach (var socket in look.Empty)
            if (Against(look, socket, outfit, lens) is not null)
                return false;
        return !lens.ExtraLayers(look, outfit).Any();
    }

    /// <summary>
    /// The outfit is exactly this look: it is on, and nothing else is on that the look would wear (no other outfit part;
    /// and no other body part on a body shelf it wears). For "the same outfit again" and "these pieces are that look".
    /// </summary>
    public static bool Same(LookData look, OutfitNow outfit, Lens lens)
    {
        if (!Covers(look, outfit, lens))
            return false;
        foreach (var worn in outfit.All)
        {
            if (LookRules.IsBody(worn.Shelf) && !lens.Wears(look, worn.Shelf))
                continue;
            if (!Holds(look, worn.Key))
                return false;
        }

        return true;
    }

    /// <summary> Two looks wear the same pieces (as worn: outfit, with body also body). For pinning a look that is already there. </summary>
    public static bool SamePieces(LookData a, LookData b, Lens lens)
    {
        if (!SameBody(a, b))
            return false;
        var pa = a.Pieces.Where(p => lens.InScope(a, p)).ToList();
        var pb = b.Pieces.Where(p => lens.InScope(b, p)).ToList();
        if (pa.Count != pb.Count)
            return false;
        foreach (var x in pa)
        {
            if (pb.FirstOrDefault(y => y.Key == x.Key) is not { } y)
                return false;
            if (x.Version >= 0 && y.Version >= 0 && x.Version != y.Version)
                return false;
            foreach (var (group, choices) in x.Options)
                if (y.Options.TryGetValue(group, out var other) && !SameChoices(choices, other))
                    return false;
        }

        return true;
    }

    /// <summary> Two looks wear the same body shelves (kin counted as one). </summary>
    public static bool SameBody(LookData a, LookData b)
        => a.Body.Count == b.Body.Count && a.Body.All(b.Body.Contains);

    /// <summary> One of the look's pieces is this part. </summary>
    public static bool Holds(LookData look, PartKey key)
    {
        foreach (var piece in look.Pieces)
            if (piece.Key == key)
                return true;
        return false;
    }

    public static HashSet<PartKey> Keys(LookData look)
        => look.Pieces.Select(p => p.Key).ToHashSet();

    /// <summary> How many pieces are worn with the look. </summary>
    public static int Count(LookData look, Lens lens)
        => look.Pieces.Count(p => lens.InScope(look, p));

    /// <summary> The outfit has drifted too far from the look: more than half of the pieces worn with it differ. </summary>
    public static bool TooFar(LookData look, int differences, Lens lens)
        => differences > Math.Max(1, Count(look, lens)) * LookRules.DriftShare;
}
