using System.Text.RegularExpressions;

namespace ModCloset2.Library;

/// <summary>
/// Which body (Rue, Tre, YAB, …) a mod or one of its options is made for, read from how mods name themselves and their
/// options ("Rue M", "Tre - Small", "[YAB/Rue]"), and picking the options made for your body. Port of the old BodyFit.
/// </summary>
public static partial class BodyFit
{
    /// <summary>
    /// Known bodies and the words mods use for them (lower case, whole words only here: "LaRue" is not "Rue"): the
    /// detection's list (<see cref="Detect.BodyMods.All"/>), one list for both.
    /// </summary>
    public static readonly (string Name, string[] Words)[] Bodies = Detect.BodyMods.All;

    public static readonly string[] Names = Bodies.Select(b => b.Name).ToArray();

    private static readonly string[] SizeWords = { "size", "sizes", "größe", "groesse" };

    [GeneratedRegex("[a-z0-9äöüß]+")]
    private static partial Regex Word();

    /// <summary> The bodies a text names ("Gilded Rue M WC" → Rue; "Unwell Rue Yab" → Rue, YAB). </summary>
    public static HashSet<string> NamesIn(string text)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Word().Matches(text.ToLowerInvariant()))
            foreach (var (name, words) in Bodies)
                if (words.Contains(m.Value))
                    found.Add(name);
        return found;
    }

    /// <summary> The lower-case words of a text ("Body: None / Bibo+" → body, none, bibo). </summary>
    public static IEnumerable<string> WordsOf(string text)
        => Word().Matches(text.ToLowerInvariant()).Select(m => m.Value);

    /// <summary> True when the word is one a mod uses for a known body ("rue", "yab", "bibo"…). </summary>
    public static bool IsBodyWord(string word)
        => Bodies.Any(b => b.Words.Contains(word));

    /// <summary> True when a group name says it picks a size ("SIZE - socks", "Top Sizes"). </summary>
    public static bool SaysSize(string text)
        => Word().Matches(text.ToLowerInvariant()).Any(m => SizeWords.Contains(m.Value));

    /// <summary> The words of a text that are not body names ("Rue M" → {m}), to match sizes when switching body. </summary>
    private static HashSet<string> OtherWords(string text)
        => Word().Matches(text.ToLowerInvariant()).Select(m => m.Value)
            .Where(w => !Bodies.Any(b => b.Words.Contains(w))).ToHashSet();

    /// <summary>
    /// For each single-choice option group that chooses between bodies, the option made for <paramref name="body"/> when the
    /// current choice is for another body (or names none in a group that is clearly a body choice). Sizes are kept where the
    /// names allow it ("YAB M" → "Rue M"). Groups that are already right, or don't mention bodies, are left alone.
    /// </summary>
    public static Dictionary<string, string> OptionsForBody(IEnumerable<(string Group, IReadOnlyList<string> Options, string Current)> singleGroups, string body)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(body))
            return result;

        foreach (var (group, options, current) in singleGroups)
        {
            if (options.Count < 2)
                continue;

            var mine = options.Where(o => NamesIn(o).Contains(body)).ToList();
            if (mine.Count == 0)
                continue;

            var now = NamesIn(current);
            if (now.Contains(body))
                continue;

            var withBodies = options.Count(o => NamesIn(o).Count > 0);
            var isBodyChoice = now.Count > 0 || withBodies * 2 >= options.Count || NamesIn(group).Count > 0;
            if (!isBodyChoice)
                continue;

            // Prefer the option that keeps the rest of the name (size, style), then the one with the fewest extra words.
            var keep = OtherWords(current);
            var best = mine.OrderByDescending(o => OtherWords(o).Intersect(keep).Count())
                .ThenBy(o => OtherWords(o).Except(keep).Count())
                .ThenBy(o => NamesIn(o).Count)
                .First();
            result[group] = best;
        }

        return result;
    }
}
