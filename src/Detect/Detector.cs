using ModCloset2.Core;

namespace ModCloset2.Detect;

/// <summary>
/// The smart detection (<see cref="IDetector"/>). Reads a mod's facts (<see cref="FactReader"/>) and decides from them
/// (<see cref="Decision"/>). Stateless: one instance can serve many background threads at once.
/// </summary>
public sealed class Detector : IDetector
{
    /// <summary> Bump when results may differ from before (rules, dictionary, reader), so caches are redone. </summary>
    public const int CurrentVersion = 7;

    public int Version
        => CurrentVersion;

    /// <summary>
    /// Detects a mod folder, or a .pmp pack read in place. Never throws: problems go to <see cref="ModDetection.Problems"/>.
    /// When <paramref name="cancel"/> fires, the result has no parts, the problem "cancelled" and DetectorVersion 0:
    /// do not cache it.
    /// </summary>
    public ModDetection Detect(string modFolder, ModHints hints, CancellationToken cancel = default)
    {
        try
        {
            using var source = ModSource.Open(modFolder);
            if (source is null)
                return new ModDetection { DetectorVersion = Version, Problems = { "The mod folder is missing." } };
            return Decision.Decide(FactReader.Read(source, hints, cancel), Version);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (Exception ex)
        {
            // Last line of defence: a mod must never break the scan.
            return new ModDetection { DetectorVersion = Version, Problems = { $"Could not read the mod ({ex.GetType().Name}: {ex.Message})." } };
        }
    }

    /// <summary> The facts and the decision together (tests, census). Null facts when the mod cannot be opened or the read was cancelled. </summary>
    public (ModFacts? Facts, ModDetection Detection) DetectWithFacts(string modFolder, ModHints hints, CancellationToken cancel = default)
    {
        try
        {
            using var source = ModSource.Open(modFolder);
            if (source is null)
                return (null, new ModDetection { DetectorVersion = Version, Problems = { "The mod folder is missing." } });
            var facts = FactReader.Read(source, hints, cancel);
            return (facts, Decision.Decide(facts, Version));
        }
        catch (OperationCanceledException)
        {
            return (null, Cancelled());
        }
        catch (Exception ex)
        {
            return (null, new ModDetection { DetectorVersion = Version, Problems = { $"Could not read the mod ({ex.GetType().Name}: {ex.Message})." } });
        }
    }

    private static ModDetection Cancelled()
        => new() { DetectorVersion = 0, Problems = { "cancelled" } };

    public IReadOnlyList<PartInfo> LookAlikes(PartInfo moved, IEnumerable<PartInfo> candidates)
    {
        if (moved.Signature.Length != Decision.SignatureLength)
            return Array.Empty<PartInfo>();

        var limit = Tuning.Votes.LookAlikeDistance;
        return candidates
            .Where(c => c.Socket == moved.Socket && !(c.Mod == moved.Mod && c.Socket == moved.Socket))
            .Select(c => (Part: c, Distance: Decision.Distance(moved.Signature, c.Signature)))
            .Where(x => x.Distance <= limit)
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Part.Mod, StringComparer.OrdinalIgnoreCase)
            .Take(Tuning.Votes.LookAlikeMax)
            .Select(x => x.Part)
            .ToList();
    }

    public int WriteCensus(string file, IEnumerable<(string Folder, ModHints Hints)> mods, Action<int>? progress = null, CancellationToken cancel = default)
        => Census.Write(this, file, mods, progress, cancel);
}
