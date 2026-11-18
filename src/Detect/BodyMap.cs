using System.Text.RegularExpressions;
using ModCloset2.Core;

namespace ModCloset2.Detect;

/// <summary>
/// Where on the body a bone's surface is. Most bones say it by their name (checked against the bone tables of the sample
/// models; see docs/reports/detection.md for the list). A few carry surface in several places and are split by
/// position: the head bone j_kao (hats, glasses, earrings, ears on top), the spine and collar bones (chest or back, neck
/// above the shoulder line), the root bones, and bones the table does not know (custom bones of a mod).
/// </summary>
public static partial class BodyMap
{
    /// <summary> A region, or one of the two markers below. </summary>
    public const BodyRegions ByPosition = (BodyRegions)(1 << 30);

    /// <summary> Surface outside the body (a pet beside you, a floating effect). Counted, never covered. </summary>
    public const BodyRegions Away = (BodyRegions)(1 << 29);

    /// <summary> The regions in signature order (bit order of <see cref="BodyRegions"/>). </summary>
    public static readonly BodyRegions[] Regions = Enum.GetValues<BodyRegions>().Where(r => r != BodyRegions.None).OrderBy(r => (int)r).ToArray();

    // j_oya_a_l, j_hito_b_r, iv_kusu_c_l (third segment added by IVCS / bodies): fingers; "b" and "c" are the tips.
    [GeneratedRegex(@"^(?:j|iv)_(oya|hito|naka|kusu|ko)_([abc])_[lr]$")]
    private static partial Regex Finger();

    // iv_asi_oya_a_l… toe bones some bodies add.
    [GeneratedRegex(@"^iv_asi_")]
    private static partial Regex Toe();

    /// <summary> True for a finger tip bone (j_*_b, iv_*_c). </summary>
    public static bool IsFingerTip(string bone)
    {
        var m = Finger().Match(bone);
        return m.Success && m.Groups[2].Value is "b" or "c";
    }

    /// <summary> True for a finger base bone (j_*_a): rings sit there. </summary>
    public static bool IsFingerBase(string bone)
    {
        var m = Finger().Match(bone);
        return m.Success && m.Groups[2].Value == "a";
    }

    /// <summary> The finger of a finger bone ("kusu_l"), or "" for other bones. </summary>
    public static string FingerOf(string bone)
    {
        var m = Finger().Match(bone);
        return m.Success ? m.Groups[1].Value + bone[^2..] : string.Empty;
    }

    /// <summary> The region a bone's name says, or <see cref="ByPosition"/> when only positions can tell. </summary>
    public static BodyRegions OfBone(string bone)
    {
        var b = bone.ToLowerInvariant();
        if (Finger().IsMatch(b))
            return BodyRegions.Fingers;
        if (Toe().IsMatch(b) || b.StartsWith("j_asi_e", StringComparison.Ordinal))
            return BodyRegions.Toes;

        if (b.StartsWith("j_f_", StringComparison.Ordinal) || b is "j_ago" || b.StartsWith("j_ago_", StringComparison.Ordinal))
            return BodyRegions.Face;
        if (b.StartsWith("j_mimi", StringComparison.Ordinal) || b.StartsWith("n_ear", StringComparison.Ordinal) || b.StartsWith("j_zer", StringComparison.Ordinal))
            return BodyRegions.Ears;
        if (b.StartsWith("j_kami", StringComparison.Ordinal) || b.StartsWith("j_ex_h", StringComparison.Ordinal))
            return BodyRegions.HeadTop;
        if (b.StartsWith("j_kubi", StringComparison.Ordinal))
            return BodyRegions.Neck;
        if (b.StartsWith("j_mune", StringComparison.Ordinal) || b.Contains("_mune", StringComparison.Ordinal))
            return BodyRegions.Chest;
        if (b.StartsWith("j_sebo_a", StringComparison.Ordinal) || b.Contains("fukubu", StringComparison.Ordinal) || b.StartsWith("j_hara", StringComparison.Ordinal))
            return BodyRegions.Waist;
        if (b.StartsWith("n_hkata", StringComparison.Ordinal) || b.StartsWith("j_ude_a", StringComparison.Ordinal) || b.Contains("nitoukin", StringComparison.Ordinal)
         || b.StartsWith("n_hhiji", StringComparison.Ordinal) || b.StartsWith("j_hiji", StringComparison.Ordinal))
            return BodyRegions.UpperArms;
        if (b.StartsWith("j_ude_b", StringComparison.Ordinal) || b.StartsWith("n_hte", StringComparison.Ordinal))
            return BodyRegions.Forearms;
        if (b.StartsWith("j_te_", StringComparison.Ordinal) || b is "j_te")
            return BodyRegions.Hands;
        if (b.StartsWith("j_kosi", StringComparison.Ordinal) || b.Contains("shiri", StringComparison.Ordinal) || b.Contains("kougan", StringComparison.Ordinal)
         || b.Contains("kokan", StringComparison.Ordinal))
            return BodyRegions.Hips;
        if (b.StartsWith("j_asi_a", StringComparison.Ordinal) || b.Contains("daitai", StringComparison.Ordinal))
            return BodyRegions.Thighs;
        if (b.StartsWith("j_asi_b", StringComparison.Ordinal) || b.StartsWith("n_hiza", StringComparison.Ordinal))
            return BodyRegions.Knees;
        if (b.StartsWith("j_asi_c", StringComparison.Ordinal) || b.Contains("fukurahagi", StringComparison.Ordinal))
            return BodyRegions.Calves;
        if (b.StartsWith("j_asi_d", StringComparison.Ordinal))
            return BodyRegions.Feet;
        if (b.StartsWith("n_sippo", StringComparison.Ordinal) || b.StartsWith("j_sippo", StringComparison.Ordinal))
            return BodyRegions.Tail;
        if (b.StartsWith("j_sk_", StringComparison.Ordinal))
            return BodyRegions.Skirt;

        // j_kao (head), j_sebo_b / j_sebo_c (spine), j_sako (collar), roots and bones we do not know.
        return ByPosition;
    }

    /// <summary> Body proportions of a race code: the sample models (c0201, c0801) measure the same, others are scaled. </summary>
    public static float Scale(int race)
    {
        // race code / 100 = race and gender (1 Midlander man, 2 Midlander woman … 18 Viera woman); NPC codes (0104…) share it.
        var key = race / 100;
        return key switch
        {
            1  => 1.07f, 2  => 1.00f, 3  => 1.13f, 4  => 1.05f, 5  => 1.15f, 6  => 1.10f,
            7  => 1.00f, 8  => 1.00f, 9  => 1.25f, 10 => 1.15f, 11 => 0.60f, 12 => 0.60f,
            13 => 1.20f, 14 => 0.97f, 15 => 1.20f, 16 => 1.10f, 17 => 1.12f, 18 => 1.07f,
            _  => 1.00f,
        };
    }

    /// <summary>
    /// The region at a position, for surface whose bone does not say (see <see cref="OfBone"/>). Heights are those of
    /// the c0201 / c0801 rest pose (metres, y up, facing +z), times the race's scale; the numbers are in <see cref="Tuning"/>.
    /// </summary>
    public static BodyRegions OfPosition(string bone, float x, float y, float z, int race)
    {
        var s  = Scale(race);
        var t  = Tuning.Body;
        var ax = MathF.Abs(x) / s;
        var ys = y / s;
        var zs = z / s;

        // Spine and collar bones: never the head (a necklace's chain on j_sebo_c reaches above the chin line).
        if (bone.StartsWith("j_sebo", StringComparison.Ordinal) || bone.StartsWith("j_sako", StringComparison.Ordinal))
        {
            // Well behind the body (wings, capes) is the back, however far it spreads.
            if (zs <= t.BehindZ)
                return BodyRegions.Back;
            if (ys >= t.NeckBaseY)
                return BodyRegions.Neck;
            if (ax >= t.ArmX)
                return BodyRegions.UpperArms;
            return zs >= t.FrontBackZ ? BodyRegions.Chest : BodyRegions.Back;
        }

        // The head: hats and ears on top, glasses and masks in front, earrings at the sides below the eyes. Surface
        // on the head bone counts as head down to the neck base (earrings dangle below the chin).
        if (ys >= t.ChinY || bone == "j_kao" && ys >= t.NeckBaseY)
        {
            var dy = ys - t.HeadCenterY;
            var dz = zs - t.HeadCenterZ;
            if (ax >= t.EarSideX && dy <= t.EarTopDy)
                return BodyRegions.Ears;
            if (dz >= t.FaceFrontDz && dy <= t.BrowDy)
                return BodyRegions.Face;
            if (dy < t.ChinDy)
                return BodyRegions.Neck;
            return BodyRegions.HeadTop;
        }

        // Far from the body (a pet beside you, things floating around): not a place on the body. Below the hands only
        // the legs are there; above, the arms reach out to the sides.
        if (MathF.Abs(zs) > t.AwayZ || ax > (ys < t.HipLowY ? t.AwayXLegs : t.AwayXArms))
            return Away;

        if (ys >= t.NeckBaseY)
            return BodyRegions.Neck;
        if (ys >= t.ChestLowY)
        {
            if (ax >= t.ArmX)
                return BodyRegions.UpperArms;
            return zs >= t.FrontBackZ ? BodyRegions.Chest : BodyRegions.Back;
        }

        if (ys >= t.WaistLowY)
            return ax >= t.ForearmX ? BodyRegions.Forearms : BodyRegions.Waist;
        if (ys >= t.HipLowY)
            return ax >= t.HandX ? BodyRegions.Hands : BodyRegions.Hips;
        if (ys >= t.KneeTopY)
            return BodyRegions.Thighs;
        if (ys >= t.KneeLowY)
            return BodyRegions.Knees;
        if (ys >= t.AnkleY)
            return BodyRegions.Calves;
        return BodyRegions.Feet;
    }
}
