using ModCloset2.Core;

namespace ModCloset2.Detect;

/// <summary> A shelf of the user's own the closet could offer in To sort ("New shelf: Wings"). </summary>
/// <param name="Name"> The shelf's name, translated (Loc.T). </param>
/// <param name="Kind"> The kind the parts share ("wings"). </param>
/// <param name="Place"> Where most of their models sit, as the body evidence says it ("back"; "no hint" without a model). </param>
/// <param name="Parts"> The parts it would take, most sure of that kind first. </param>
public sealed record ShelfProposal(string Name, string Kind, string Place, IReadOnlyList<PartKey> Parts);

/// <summary>
/// Proposes new shelves from the parts waiting under To sort. Pure: give it the unsure parts, get the proposals. A
/// proposal needs at least <see cref="MinParts"/> unsure parts that share a kind with no built-in shelf of its own
/// (wings, horns, glasses, piercings…). One proposal per kind, wherever the parts sit: piercings on the face, the navel
/// and the chest go on one "Piercings" shelf.
/// </summary>
public static class ShelfProposals
{
    /// <summary> How many unsure parts must share a kind. </summary>
    public const int MinParts = 3;

    /// <summary> Kinds that suggest a shelf of their own, with the English shelf name (shown through Loc.T). </summary>
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["wings"]    = "Wings",
        ["horns"]    = "Horns",
        ["glasses"]  = "Glasses",
        ["halo"]     = "Halos",
        ["mask"]     = "Masks",
        ["veil"]     = "Veils",
        ["piercing"] = "Piercings",
        ["bag"]      = "Bags",
    };

    /// <param name="parts"> Parts of the library; only those below <see cref="DetectorThresholds.Unsure"/> count. </param>
    /// <param name="existingShelves"> Names of shelves the user already has (in any language): those are not proposed again. </param>
    public static IReadOnlyList<ShelfProposal> Propose(IEnumerable<PartInfo> parts, IEnumerable<string>? existingShelves = null)
    {
        var existing = new HashSet<string>(existingShelves ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return parts
            .Where(p => p.Confidence < DetectorThresholds.Unsure && Names.ContainsKey(p.Kind))
            .GroupBy(p => p.Kind)
            .Where(g => g.Select(p => p.Key()).Distinct().Count() >= MinParts)
            .Select(g => (Group: g, Name: Names[g.Key]))
            .Where(x => !existing.Contains(x.Name) && !existing.Contains(Loc.T(x.Name)))
            .OrderByDescending(x => x.Group.Count())
            .ThenByDescending(x => x.Group.Average(p => p.Confidence))
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => new ShelfProposal(Loc.T(x.Name), x.Group.Key, x.Group.GroupBy(PlaceOf).OrderByDescending(p => p.Count()).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key,
                x.Group.OrderByDescending(p => p.Confidence).ThenBy(p => p.Mod, StringComparer.OrdinalIgnoreCase).Select(p => p.Key()).Distinct().ToList()))
            .ToList();
    }

    /// <summary> The body witness's phrase ("back"), or "no hint". </summary>
    private static string PlaceOf(PartInfo p)
        => p.Evidence.FirstOrDefault(e => e.Kind == EvidenceKind.Body)?.Value is { Length: > 0 } v ? v : "no hint";
}
