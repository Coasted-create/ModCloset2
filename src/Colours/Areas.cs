using ModCloset2.Core;

namespace ModCloset2.Colours;

// Pure code (no game, no Dalamud): linked into tests/ColourTests.

/// <summary> What an area is, by size and finish (decides its label and the outfit colour it follows by default). </summary>
public enum AreaKind
{
    Main,
    Second,
    Accent,
    Details,
    Metal,
    Painted,

    /// <summary> Materials that could not be read: no colours to change, shown like a painted part, labelled for what it is. </summary>
    Unreadable,
}

/// <summary> One area of a worn piece as grouped from its rows (no colour choices in here; those come from memory). </summary>
public sealed class AreaDef
{
    /// <summary>
    /// Stable identity for memory: the id of the area's heaviest row ("material:file:row"), "painted" for the painted
    /// part. Derived from the material and its rows, never from a list position.
    /// </summary>
    public string Id { get; init; } = string.Empty;
    public AreaKind Kind { get; init; }
    public IReadOnlyList<RowInfo> Rows { get; init; } = Array.Empty<RowInfo>();

    /// <summary> Share of the piece's surface, 0..1 (the shares of a piece add up to 1). </summary>
    public float Share { get; init; }

    /// <summary> The mod's own colour of the area (average of its rows by surface, in OKLab). </summary>
    public OkLab Mean { get; init; }
    public Rgb Colour { get; init; }

    public bool Metal
        => Kind == AreaKind.Metal;

    /// <summary> No colours to change here (a painted look, or a material that could not be read): hatched, not clickable. </summary>
    public bool Painted
        => Kind is AreaKind.Painted or AreaKind.Unreadable;

    /// <summary> The outfit colour the area follows when it follows by default. </summary>
    public ColourRole DefaultRole
        => Kind switch
        {
            AreaKind.Main    => ColourRole.Main,
            AreaKind.Second  => ColourRole.Second,
            AreaKind.Metal   => ColourRole.Metal,
            AreaKind.Painted or AreaKind.Unreadable => ColourRole.Own,
            _                => ColourRole.Accent,
        };

    /// <summary> True when a memory entry made for <paramref name="rowId"/> belongs to this area. </summary>
    public bool Has(string rowId)
        => Id == rowId || Rows.Any(r => r.Id == rowId);
}

/// <summary>
/// Groups a piece's colour table rows (across its materials) into a few areas a person would name. The rules and their
/// numbers:
/// <list type="number">
/// <item> Only rows that cover something take part. Metal rows (finish, see <see cref="RowInfo.IsMetal"/>) form one
/// Metal area; glowing rows never merge with rows that do not glow. </item>
/// <item> Rows are merged, closest first, while two groups are nearer than <see cref="MergeDistance"/> (OKLab × 100,
/// lightness counted half, so a lighter trim of the same colour joins its base; rows of different materials count
/// <see cref="OtherMaterial"/> farther apart). </item>
/// <item> A group under <see cref="MinShare"/> of the piece joins the nearest bigger group within
/// <see cref="JoinDistance"/>, otherwise it goes to one "Details" area. </item>
/// <item> At most <see cref="MaxAreas"/> areas with colours (Details and Metal included): the smallest groups join
/// their nearest neighbour or Details until it fits. </item>
/// <item> The surface without colours to change becomes one painted area (left out when under
/// <see cref="MinPainted"/>). </item>
/// <item> The biggest group that is not metal is "Main", the next "Second", the others "Accent". </item>
/// </list>
/// </summary>
public static class AreaGrouper
{
    public const int MaxAreas = 5;
    public const float MergeDistance = 9f;
    public const float OtherMaterial = 3f;
    public const double MinShare = 0.03;
    public const float JoinDistance = 20f;
    public const double MinPainted = 0.01;

    /// <summary> The colour drawn for a part that could not be read (only for drawing: a quiet grey). </summary>
    public static readonly Rgb Unknown = Rgb.FromHex(0x6F6277);

    private sealed class Bunch
    {
        public readonly List<RowInfo> Rows = new();
        public double Weight;
        public OkLab Mean;
        public bool Metal;
        public bool Glows;
        public readonly HashSet<string> Materials = new(StringComparer.Ordinal);

        public void Add(Bunch other)
        {
            Materials.UnionWith(other.Materials);
            var w = Weight + other.Weight;
            Mean = w <= 0 ? Mean : new OkLab(
                (float)((Mean.L * Weight + other.Mean.L * other.Weight) / w),
                (float)((Mean.A * Weight + other.Mean.A * other.Weight) / w),
                (float)((Mean.B * Weight + other.Mean.B * other.Weight) / w));
            Weight = w;
            Rows.AddRange(other.Rows);
        }

        public RowInfo Anchor
            => Rows.OrderByDescending(r => r.Weight).ThenBy(r => r.Id, StringComparer.Ordinal).First();
    }

    public static List<AreaDef> Group(PieceReading reading)
    {
        var rows    = reading.Rows.Where(r => r.Weight > 0).ToList();
        var painted    = reading.PaintedWeight;
        var unreadable = reading.UnreadableWeight;
        var total      = rows.Sum(r => r.Weight) + painted + unreadable;
        if (total <= 0)
            return new List<AreaDef>();
        if (painted / total < MinPainted)
        {
            total  -= painted;
            painted = 0;
        }

        if (unreadable / total < MinPainted)
        {
            total     -= unreadable;
            unreadable = 0;
        }

        if (total <= 0)
            return new List<AreaDef>();

        // order the input the same way whatever order the rows came in, so the result never depends on it
        rows.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var groups = rows.Select(r =>
        {
            var g = new Bunch { Weight = r.Weight, Mean = ColourMath.ToOk(r.Colour), Metal = r.IsMetal, Glows = r.Glows };
            g.Rows.Add(r);
            g.Materials.Add(r.MaterialKey);
            return g;
        }).ToList();

        var metal = groups.Where(g => g.Metal).ToList();
        var cloth = groups.Where(g => !g.Metal).ToList();

        // 1. one Metal area
        Bunch? metalArea = null;
        foreach (var g in metal)
        {
            if (metalArea is null)
                metalArea = g;
            else
                metalArea.Add(g);
        }

        // 2. merge near colours, closest first
        while (true)
        {
            var best = (I: -1, J: -1, D: float.MaxValue);
            for (var i = 0; i < cloth.Count; ++i)
            for (var j = i + 1; j < cloth.Count; ++j)
            {
                if (cloth[i].Glows != cloth[j].Glows)
                    continue;
                var d = Distance(cloth[i], cloth[j]);
                if (d < best.D)
                    best = (i, j, d);
            }

            if (best.I < 0 || best.D >= MergeDistance)
                break;
            cloth[best.I].Add(cloth[best.J]);
            cloth.RemoveAt(best.J);
        }

        // 3. small groups join a near bigger one, or Details
        cloth.Sort(ByWeight);
        Bunch? details = null;
        void ToDetails(Bunch g)
        {
            if (details is null)
                details = g;
            else
                details.Add(g);
        }

        var kept = new List<Bunch>();
        foreach (var g in cloth)
        {
            if (g.Weight / total >= MinShare)
            {
                kept.Add(g);
                continue;
            }

            if (Nearest(g, kept) is { } near && Distance(g, near) < JoinDistance)
                near.Add(g);
            else
                ToDetails(g);
        }

        // 4. at most MaxAreas areas with colours
        while (kept.Count + (metalArea is null ? 0 : 1) + (details is null ? 0 : 1) > MaxAreas && kept.Count > 0)
        {
            kept.Sort(ByWeight);
            var smallest = kept[^1];
            kept.RemoveAt(kept.Count - 1);
            if (Nearest(smallest, kept) is { } near && Distance(smallest, near) < JoinDistance * 1.5f)
                near.Add(smallest);
            else
                ToDetails(smallest);
        }

        kept.Sort(ByWeight);
        var areas = new List<AreaDef>();
        for (var i = 0; i < kept.Count; ++i)
            areas.Add(Area(kept[i], i == 0 ? AreaKind.Main : i == 1 ? AreaKind.Second : AreaKind.Accent, total));
        if (details is not null)
            areas.Add(Area(details, AreaKind.Details, total));
        if (metalArea is not null)
            areas.Add(Area(metalArea, AreaKind.Metal, total));

        // by size; the painted part last (it is drawn hatched at the end of the bar)
        areas.Sort((a, b) => b.Share.CompareTo(a.Share) is var c and not 0 ? c : string.CompareOrdinal(a.Id, b.Id));
        if (painted > 0)
            areas.Add(new AreaDef
            {
                Id     = "painted",
                Kind   = AreaKind.Painted,
                Share  = (float)(painted / total),
                Mean   = ColourMath.ToOk(reading.PaintedColour),
                Colour = reading.PaintedColour,
            });
        if (unreadable > 0)
            areas.Add(new AreaDef
            {
                Id     = "unreadable",
                Kind   = AreaKind.Unreadable,
                Share  = (float)(unreadable / total),
                Mean   = ColourMath.ToOk(Unknown),
                Colour = Unknown,
            });
        return areas;
    }

    private static AreaDef Area(Bunch g, AreaKind kind, double total)
        => new()
        {
            Id     = g.Anchor.Id,
            Kind   = kind,
            Rows   = g.Rows.OrderByDescending(r => r.Weight).ThenBy(r => r.Id, StringComparer.Ordinal).ToList(),
            Share  = (float)(g.Weight / total),
            Mean   = g.Mean,
            Colour = ColourMath.FromOk(g.Mean),
        };

    private static int ByWeight(Bunch a, Bunch b)
        => b.Weight.CompareTo(a.Weight) is var c and not 0 ? c : string.CompareOrdinal(a.Anchor.Id, b.Anchor.Id);

    private static Bunch? Nearest(Bunch g, List<Bunch> among)
        => among.Where(o => o.Glows == g.Glows).OrderBy(o => Distance(g, o)).FirstOrDefault();

    /// <summary> How far apart two groups look: OKLab × 100, lightness counted half, other materials a little farther. </summary>
    private static float Distance(Bunch a, Bunch b)
    {
        float dl = (a.Mean.L - b.Mean.L) * 0.5f, da = a.Mean.A - b.Mean.A, db = a.Mean.B - b.Mean.B;
        var d = 100 * MathF.Sqrt(dl * dl + da * da + db * db);
        return a.Materials.Overlaps(b.Materials) ? d : d + OtherMaterial;
    }
}
