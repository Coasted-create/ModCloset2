namespace ModCloset2.Core;

// The character's body as it is on screen right now, for the "On you" face: places to point at, and which shelves
// each place leads to. The game side fills this every frame; the face only draws and hit-tests.

/// <summary> Ids of the places on the body one can point at. </summary>
public static class BodySpots
{
    public const string HeadTop   = "headtop";
    public const string Face      = "face";
    public const string EarLeft   = "ear.l";
    public const string EarRight  = "ear.r";
    public const string Neck      = "neck";
    public const string Chest     = "chest";
    public const string HandLeft  = "hand.l";
    public const string HandRight = "hand.r";
    public const string Hips      = "hips";
    public const string LegLeft   = "leg.l";
    public const string LegRight  = "leg.r";
    public const string FootLeft  = "foot.l";
    public const string FootRight = "foot.r";
    public const string Tail      = "tail";

    /// <summary> The shelves a place leads to, best first. Shelves the closet does not show (no parts) are skipped by the face. </summary>
    public static IReadOnlyList<string> Shelves(string spot)
        => spot switch
        {
            HeadTop                => new[] { Pieces.Hair, Pieces.Head, Pieces.Ears },
            Face                   => new[] { Pieces.Face, Pieces.Makeup, Pieces.Head },
            EarLeft or EarRight    => new[] { Pieces.Earrings, Pieces.Ears },
            Neck                   => new[] { Pieces.Necklace },
            Chest                  => new[] { Pieces.Top, Pieces.Outfits, Pieces.Bodies, Pieces.Skin },
            HandLeft or HandRight  => new[] { Pieces.Gloves, Pieces.Bracelets, Pieces.Rings, Pieces.Nails },
            Hips                   => new[] { Pieces.Bottoms },
            LegLeft or LegRight    => new[] { Pieces.Legwear, Pieces.Bottoms },
            FootLeft or FootRight  => new[] { Pieces.Shoes, Pieces.Legwear },
            Tail                   => new[] { Pieces.Tail },
            _                      => Array.Empty<string>(),
        };
}

/// <summary> One place on the body, in screen pixels. </summary>
public sealed class BodySpot
{
    public string Id { get; init; } = string.Empty;

    /// <summary> Points along the body part (bone positions on screen), at least one. The part is the line through them. </summary>
    public IReadOnlyList<(float X, float Y)> Line { get; init; } = Array.Empty<(float X, float Y)>();

    /// <summary> How thick the part is on screen at this distance (half width in pixels). </summary>
    public float Radius { get; init; }

    /// <summary> Distance from the camera (any unit, smaller is nearer), to tell a hand in front of the body from one behind it; 0 when unknown. </summary>
    public float Depth { get; init; }
}

public interface IBodyOnScreen
{
    /// <summary> False when there is nobody to point at (not logged in, a cutscene, group pose, the character off screen). </summary>
    bool Available { get; }

    /// <summary> The places that are in front of the camera right now. A new list every frame. </summary>
    IReadOnlyList<BodySpot> Spots { get; }

    /// <summary> The character's box on screen (left, top, right, bottom), for scale and for placing things beside it. </summary>
    (float Left, float Top, float Right, float Bottom) Box { get; }
}
