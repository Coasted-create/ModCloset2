using System.Numerics;
using ModCloset2.Core;

namespace ModCloset2.OnYou;

/// <summary> One place on the body in the world (metres), before it is put on the screen. </summary>
public sealed record WorldSpot(string Id, Vector3[] Points, float RadiusMetres);

/// <summary>
/// Which bones make which place on the body, and how a place in the world becomes a <see cref="BodySpot"/> on screen.
/// Pure: the game side reads the bones (<c>Game/Bones.cs</c>) and gives the camera as two functions, so this can be
/// tested with a made-up skeleton. Bone names are the game's (checked against Sculpt+'s bone table and the bone list in
/// docs/reports/detection.md).
/// </summary>
public static class BodyPlaces
{
    /// <summary> Hips (<c>j_kosi</c>) to neck (<c>j_kubi</c>) of a grown Midlander or Miqo'te, for the body's own scale. </summary>
    private const float TorsoMetres = 0.45f;

    /// <summary> The places of a character from its bones (world positions by bone name). Places whose bones are missing are left out. </summary>
    public static List<WorldSpot> FromBones(IReadOnlyDictionary<string, Vector3> bones)
    {
        var spots = new List<WorldSpot>();
        bool Has(string name)
            => bones.ContainsKey(name);

        Vector3 B(string name)
            => bones[name];

        // The body's own size: Lalafell and Roegadyn bones are smaller or larger, and the height slider scales them too.
        // Measured on the skeleton, not taken from the race, so it is right for every body.
        var scale = Has("j_kosi") && Has("j_kubi") ? Math.Clamp(Vector3.Distance(B("j_kosi"), B("j_kubi")) / TorsoMetres, 0.3f, 3f) : 1f;
        float M(float metres)
            => metres * scale;

        // "Up" for the head: along the neck, so a head bent in an emote still gets its top in the right place
        var headUp = Has("j_kao") && Has("j_kubi") ? Dir(B("j_kao") - B("j_kubi"), Vector3.UnitY) : Vector3.UnitY;
        var bodyUp = Has("j_kubi") && Has("j_kosi") ? Dir(B("j_kubi") - B("j_kosi"), Vector3.UnitY) : Vector3.UnitY;

        // the character's left: from a pair of bones that turns with the head if there is one (Miqo'te ears), else the collar
        Vector3? left = null;
        foreach (var (l, r) in new[] { ("j_mimi_l", "j_mimi_r"), ("j_sako_l", "j_sako_r"), ("j_ude_a_l", "j_ude_a_r") })
            if (Has(l) && Has(r) && Vector3.DistanceSquared(B(l), B(r)) > 1e-6f)
            {
                left = Vector3.Normalize(B(l) - B(r));
                break;
            }

        // the face bones sit on the front of the face; their middle is the face (Sculpt+'s HeadCentre uses the same bones)
        var faceSum   = Vector3.Zero;
        var faceCount = 0;
        foreach (var (name, at) in bones)
            if (name.StartsWith("j_f_", StringComparison.Ordinal))
            {
                faceSum += at;
                faceCount++;
            }

        Vector3? faceMid = faceCount >= 3 ? faceSum / faceCount : null;

        // ---- head ----
        if (Has("j_kao"))
        {
            var head = B("j_kao");

            // j_kao is the head's joint at the base of the skull; the top of the head is a hand above it. Miqo'te cat ears
            // (j_mimi_l/r, on top of the head) count to the head top: the line runs from one ear over the crown to the other.
            if (Has("j_mimi_l") && Has("j_mimi_r"))
            {
                var crown = (B("j_mimi_l") + B("j_mimi_r")) / 2 + headUp * M(0.02f);
                spots.Add(new WorldSpot(BodySpots.HeadTop, new[] { B("j_mimi_l"), crown, B("j_mimi_r") }, M(0.05f)));
            }
            else
                spots.Add(new WorldSpot(BodySpots.HeadTop, new[] { head + headUp * M(0.15f) }, M(0.075f)));

            spots.Add(new WorldSpot(BodySpots.Face, new[] { faceMid ?? head + headUp * M(0.07f) }, M(0.06f)));

            // Human ears are not bones of their own: they are beside the head, a little above its joint. Earrings hang there.
            if (left is { } side)
            {
                var ears = head + headUp * M(0.05f);
                spots.Add(new WorldSpot(BodySpots.EarLeft, new[] { ears + side * M(0.075f) }, M(0.03f)));
                spots.Add(new WorldSpot(BodySpots.EarRight, new[] { ears - side * M(0.075f) }, M(0.03f)));
            }

            if (Has("j_kubi"))
                spots.Add(new WorldSpot(BodySpots.Neck, new[] { B("j_kubi"), Vector3.Lerp(B("j_kubi"), head, 0.6f) }, M(0.045f)));
        }

        // ---- trunk: spine low to high is j_sebo_a (waist), b (chest), c (shoulders) ----
        if (Has("j_sebo_b") && Has("j_sebo_c"))
        {
            var below = Has("j_sebo_a") ? Vector3.Lerp(B("j_sebo_b"), B("j_sebo_a"), 0.4f) : B("j_sebo_b");
            spots.Add(new WorldSpot(BodySpots.Chest, new[] { Vector3.Lerp(B("j_sebo_c"), B("j_sebo_b"), 0.3f), below }, M(0.11f)));
        }

        if (Has("j_kosi"))
        {
            var top = Has("j_sebo_a") ? Vector3.Lerp(B("j_sebo_a"), B("j_kosi"), 0.5f) : B("j_kosi") + bodyUp * M(0.06f);
            spots.Add(new WorldSpot(BodySpots.Hips, new[] { top, B("j_kosi") - bodyUp * M(0.06f) }, M(0.12f)));
        }

        // ---- limbs, each side ----
        foreach (var (s, hand, leg, foot) in new[] { ("l", BodySpots.HandLeft, BodySpots.LegLeft, BodySpots.FootLeft), ("r", BodySpots.HandRight, BodySpots.LegRight, BodySpots.FootRight) })
        {
            // the hand: from the wrist (n_hte, where bracelets sit) over the hand joint to the base of the middle finger
            var handLine = new[] { "n_hte_" + s, "j_te_" + s, "j_naka_a_" + s }.Where(Has).Select(B).ToArray();
            if (Has("j_te_" + s))
                spots.Add(new WorldSpot(hand, handLine, M(0.045f)));

            // the leg: thigh (a), knee (b), calf (c) down towards the ankle (d), not into the hips
            if (Has("j_asi_a_" + s) && Has("j_asi_b_" + s))
            {
                var line = new List<Vector3> { Vector3.Lerp(B("j_asi_a_" + s), B("j_asi_b_" + s), 0.2f), B("j_asi_b_" + s) };
                if (Has("j_asi_c_" + s))
                    line.Add(Has("j_asi_d_" + s) ? Vector3.Lerp(B("j_asi_c_" + s), B("j_asi_d_" + s), 0.6f) : B("j_asi_c_" + s));
                spots.Add(new WorldSpot(leg, line.ToArray(), M(0.065f)));
            }

            // the foot: ankle (d) to toes (e)
            if (Has("j_asi_d_" + s))
                spots.Add(new WorldSpot(foot, Has("j_asi_e_" + s) ? new[] { B("j_asi_d_" + s), B("j_asi_e_" + s) } : new[] { B("j_asi_d_" + s) }, M(0.05f)));
        }

        // ---- tail: n_sippo_a… from the root outwards; a character without a tail has none of them ----
        var tail = new[] { "n_sippo_a", "n_sippo_b", "n_sippo_c", "n_sippo_d", "n_sippo_e" }.Where(Has).Select(B).ToArray();
        if (tail.Length > 0)
            spots.Add(new WorldSpot(BodySpots.Tail, tail, M(0.04f)));

        return spots;
    }

    /// <summary>
    /// Puts places on the screen. <paramref name="toScreen"/> gives null for a point behind the camera (such points are
    /// dropped; a place with none left is dropped). <paramref name="pixelsPerMetre"/> says how large a metre looks at a
    /// point, so a place's thickness follows the distance to the camera. The same measure gives the depth: a metre
    /// looks smaller the further away it is, so 1 / (pixels per metre) grows with the distance (in its own unit, which is
    /// all <see cref="BodySpot.Depth"/> asks for). It is taken at the middle of the place's line (a tail's root sits at
    /// the body, its tip may be well behind).
    /// </summary>
    public static List<BodySpot> Project(IReadOnlyList<WorldSpot> spots, Func<Vector3, Vector2?> toScreen, Func<Vector3, float> pixelsPerMetre)
    {
        var result = new List<BodySpot>(spots.Count);
        foreach (var spot in spots)
        {
            var line = new List<(float X, float Y)>(spot.Points.Length);
            foreach (var p in spot.Points)
                if (toScreen(p) is { } s && float.IsFinite(s.X) && float.IsFinite(s.Y))
                    line.Add((s.X, s.Y));
            if (line.Count == 0)
                continue;
            var ppm = pixelsPerMetre(spot.Points[0]);
            if (!float.IsFinite(ppm) || ppm <= 0)
                continue;
            var mid      = pixelsPerMetre(spot.Points[spot.Points.Length / 2]);
            var depth    = float.IsFinite(mid) && mid > 0 ? 1000f / mid : 1000f / ppm;
            result.Add(new BodySpot { Id = spot.Id, Line = line, Radius = MathF.Max(2f, spot.RadiusMetres * ppm), Depth = depth });
        }

        return result;
    }

    /// <summary> The box around all places (their lines and thickness), on screen. </summary>
    public static (float Left, float Top, float Right, float Bottom) BoxOf(IReadOnlyList<BodySpot> spots)
    {
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        foreach (var s in spots)
            foreach (var (x, y) in s.Line)
            {
                l = MathF.Min(l, x - s.Radius);
                t = MathF.Min(t, y - s.Radius);
                r = MathF.Max(r, x + s.Radius);
                b = MathF.Max(b, y + s.Radius);
            }

        return spots.Count == 0 ? (0, 0, 0, 0) : (l, t, r, b);
    }

    /// <summary> Paired places (both hands) share what they remember: "hand.l" and "hand.r" are "hand". </summary>
    public static string Base(string spot)
        => spot.EndsWith(".l", StringComparison.Ordinal) || spot.EndsWith(".r", StringComparison.Ordinal) ? spot[..^2] : spot;

    /// <summary> The other side of a paired place, null for a place in the middle. </summary>
    public static string? Twin(string spot)
        => spot.EndsWith(".l", StringComparison.Ordinal) ? spot[..^2] + ".r" : spot.EndsWith(".r", StringComparison.Ordinal) ? spot[..^2] + ".l" : null;

    private static Vector3 Dir(Vector3 v, Vector3 fallback)
        => v.LengthSquared() < 1e-8f ? fallback : Vector3.Normalize(v);
}
