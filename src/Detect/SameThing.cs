using ModCloset2.Core;

namespace ModCloset2.Detect;

/// <summary>
/// Whether two parts of one mod are the same thing offered through several slots (tights through the legs, ear and ring
/// slots), so the closet can show them as one card with the slot as a choice. Pure, symmetric and cheap: it compares the
/// parts' signatures (where on the body the surface sits, how large it is, where its centre is), nothing else.
/// </summary>
public static class SameThing
{
    /// <summary>
    /// True when both parts have a model and the models look the same: identical always, near-identical when the
    /// signatures are at most <see cref="SameThingTuning.Line"/> apart and the item areas differ by at most
    /// <see cref="SameThingTuning.AreaLine"/> (log10, a factor of about 1.45). Parts of two different mods (one mod
    /// installed twice) are never the same thing here.
    /// </summary>
    public static bool Is(PartInfo a, PartInfo b)
    {
        if (a.Signature.Length != Decision.SignatureLength || b.Signature.Length != Decision.SignatureLength)
            return false;
        if (!string.Equals(a.Mod, b.Mod, StringComparison.Ordinal))
            return false;
        return Distance(a, b) <= Tuning.Same.Line && AreaGap(a, b) <= Tuning.Same.AreaLine;
    }

    /// <summary> The signature distance ("looks the same"); infinity when either part has no model. </summary>
    public static float Distance(PartInfo a, PartInfo b)
        => Decision.Distance(a.Signature, b.Signature);

    /// <summary> How far apart the item areas are, in log10 (0.3 is a factor of two); infinity without a model. </summary>
    public static float AreaGap(PartInfo a, PartInfo b)
        => a.Signature.Length == Decision.SignatureLength && b.Signature.Length == Decision.SignatureLength
            ? MathF.Abs(a.Signature[AreaIndex] - b.Signature[AreaIndex])
            : float.PositiveInfinity;

    /// <summary> Identical models (the same file offered through several slots): the distance is nil. </summary>
    public static bool Identical(PartInfo a, PartInfo b)
        => Is(a, b) && Distance(a, b) <= Tuning.Same.IdenticalMax;

    private const int AreaIndex = 20;
}
