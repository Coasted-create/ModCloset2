using ModCloset2.Core;
using ModCloset2.Library;

namespace ModCloset2.Closet;

// Rings for the right and the left hand: "Rings · right" is the ring parts' home shelf, "Rings · left" a view of it. A ring
// part shows on the shelf of each hand it has files for (both: on both); tried or kept from a shelf, it goes on that
// shelf's finger, a one-hand ring only on its own; the other hand keeps what it wears.

public sealed partial class ClosetSession
{
    private static bool IsRingShelf(string shelf)
        => shelf is Pieces.Rings or Pieces.RingsLeft;

    /// <summary> The finger of a ring shelf; <see cref="Hands.Both"/> for every other shelf. </summary>
    private static Hands HandOfShelf(string shelf)
        => shelf switch
        {
            Pieces.RingsLeft => Hands.Left,
            Pieces.Rings     => Hands.Right,
            _                => Hands.Both,
        };

    private static bool HasHand(Hands hands, Hands hand)
        => hands == Hands.Both || hand == Hands.Both || hands == hand;

    private static Hands Other(Hands hand)
        => hand == Hands.Left ? Hands.Right : hand == Hands.Right ? Hands.Left : Hands.Both;

    /// <summary>
    /// The fingers a ring part has files for (<see cref="PartBuilder.HandsOf"/>); a copy the closet made for one hand counts
    /// as that hand until the detection has read it.
    /// </summary>
    private Hands HandsOf(LibPart part)
    {
        var hands = PartBuilder.HandsOf(part);
        if (hands == Hands.Both && part.Key.Socket == Socket.Rings && _data.Copies.Count > 0 && _data.Copies.TryGetValue(part.Mod.Directory, out var copy))
            return copy.Hand;
        return hands;
    }

    private readonly Dictionary<string, (int Stamp, List<LibPart> Parts)> _ringViews = new();

    /// <summary> The parts of the rings' home shelf for one hand (ring parts with files for it; anything else placed there). </summary>
    private List<LibPart> RingView(string shelf)
    {
        if (!_buckets.TryGetValue(Pieces.Rings, out var bucket))
            return new List<LibPart>();
        if (_ringViews.TryGetValue(shelf, out var made) && made.Stamp == bucket.Stamp)
            return made.Parts;
        var hand = HandOfShelf(shelf);
        var list = bucket.List.Where(p => p.Key.Socket != Socket.Rings || HasHand(HandsOf(p), hand)).ToList();
        _ringViews[shelf] = (bucket.Stamp, list);
        return list;
    }

    /// <summary> The finger a ring goes on when tried now: its own when it has files for one hand only, else the shelf's (right elsewhere). </summary>
    private Hands TryHand(LibPart part)
    {
        if (part.Key.Socket != Socket.Rings)
            return Hands.Both;
        var hands = HandsOf(part);
        if (hands != Hands.Both)
            return hands;
        return _selected == Pieces.RingsLeft && _search.Trim().Length == 0 ? Hands.Left : Hands.Right;
    }

    /// <summary>
    /// A ring of the kept outfit is on that finger: what the wardrobe says is on it, else (when it cannot tell) the ring's
    /// own files, the right hand for a ring with files for both. Any other part: on.
    /// </summary>
    private bool OnFinger(LibPart part, Hands hand)
    {
        if (!_on.Contains(part.Key))
            return false;
        if (part.Key.Socket != Socket.Rings || hand == Hands.Both)
            return true;
        if (_wardrobe.RingOn(hand) is { } on)
            return on.Equals(part.Mod.Directory, StringComparison.OrdinalIgnoreCase);
        if (_wardrobe.RingOn(Other(hand)) is { } other && other.Equals(part.Mod.Directory, StringComparison.OrdinalIgnoreCase))
            return false;
        var hands = HandsOf(part);
        return hands == Hands.Both ? hand == Hands.Right : hands == hand;
    }

    /// <summary> A tried entry shows on a shelf in view: its part's shelf, and for a ring the shelf of the finger it is tried on. </summary>
    private bool TriedOn(TryEntry t, string partShelf, string shelf)
    {
        if (!IsRingShelf(shelf) || t.Key.Socket != Socket.Rings)
            return partShelf == shelf;
        return partShelf == Pieces.Rings && HasHand(t.Hand, HandOfShelf(shelf));
    }

    // ======== a copy for the other hand ========

    public OwnModPlan? OwnModOf(PartKey part, Hands to)
        => to == Hands.Both ? OwnModOf(part) : OwnModOfHand(part, to);

    public void MakeOwnMod(PartKey part, string name, Hands to)
    {
        if (to == Hands.Both)
            MakeOwnMod(part, name);
        else
            MakeOwnModFor(part, name, to);
    }
}
