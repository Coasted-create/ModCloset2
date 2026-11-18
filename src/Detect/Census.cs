using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModCloset2.Core;

namespace ModCloset2.Detect;

/// <summary> One line of the census: the facts of a mod and what the detector decided from them. </summary>
public sealed class CensusLine
{
    public int DetectorVersion { get; set; }
    public ModFacts Facts { get; set; } = new();
    public List<PartInfo> Decided { get; set; } = new();
}

/// <summary>
/// The census: one gzip'd JSON-lines file with, per mod, the raw facts the detector looked at and what it decided
/// (lists and numbers only, never file contents). The facts are enough to run the decision again
/// (<see cref="Decision.Decide"/>), which is how the rules are tuned on a real library without the game.
/// </summary>
public static class Census
{
    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static int Write(Detector detector, string file, IEnumerable<(string Folder, ModHints Hints)> mods, Action<int>? progress, CancellationToken cancel)
    {
        var count = 0;
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Written to a temporary file first: a census cut short by closing the game never replaces a good one.
        var temp = file + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var gzip = new GZipStream(stream, CompressionLevel.Optimal))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
        {
            foreach (var (folder, hints) in mods)
            {
                if (cancel.IsCancellationRequested)
                    break;

                var (facts, detection) = detector.DetectWithFacts(folder, hints, cancel);
                if (facts is null)
                {
                    if (cancel.IsCancellationRequested)
                        break;
                    facts = new ModFacts { Mod = Path.GetFileName(folder.TrimEnd('/', '\\')), Name = hints.Name, Problems = detection.Problems.ToList() };
                }

                var line = new CensusLine { DetectorVersion = detector.Version, Facts = facts, Decided = detection.Parts };
                writer.WriteLine(JsonSerializer.Serialize(line, Json));
                ++count;
                progress?.Invoke(count);
            }
        }

        File.Move(temp, file, overwrite: true);
        return count;
    }

    /// <summary>
    /// Reads a census file line by line; damaged lines are skipped (and counted in <paramref name="damaged"/>). A file cut
    /// short or replaced while it is read ends the reading there (counted as damaged), without an exception. The file is
    /// opened so that others can still write or delete it.
    /// </summary>
    public static IEnumerable<CensusLine> Read(string file, Action? damaged = null)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, ModSource.Sharing);
        using var gzip   = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        while (true)
        {
            string? text;
            try
            {
                text = reader.ReadLine();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                damaged?.Invoke();
                yield break;
            }

            if (text is null)
                yield break;
            if (text.Length == 0)
                continue;

            CensusLine? line = null;
            try
            {
                line = JsonSerializer.Deserialize<CensusLine>(text, Json);
            }
            catch (JsonException)
            {
                damaged?.Invoke();
            }

            if (line is not null)
                yield return line;
        }
    }
}
