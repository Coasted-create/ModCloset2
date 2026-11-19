using System.Text.RegularExpressions;

namespace ModCloset2.Detect;

/// <summary>
/// The body mods a part can be made for ("Rue", "Bibo+", "TBSE"…), with the words mods use for them. One list for the
/// detection (the bodies of a part) and the closet's body matching (Library's BodyFit).
/// </summary>
public static partial class BodyMods
{
    /// <summary> Each body mod with the words that name it, in the order the filter lists them. </summary>
    public static readonly (string Name, string[] Words)[] All =
    {
        ("Rue", new[] { "rue" }),
        ("RueXB", new[] { "ruexb", "rue xb" }), // a body of its own (Rue's clothes do not fit it): "RueXB+", "Rue XB"
        ("Tre", new[] { "tre" }),
        ("YAB", new[] { "yab" }),
        ("Yanilla", new[] { "yanilla" }),
        ("Bibo+", new[] { "bibo", "bibop" }),
        ("TBSE", new[] { "tbse" }),
        ("Lavabod", new[] { "lavabod", "lava" }),
        ("LaRue", new[] { "larue" }),
        ("Gen3", new[] { "gen3" }),
        ("Solace", new[] { "solace" }),
        ("Neolithe", new[] { "neolithe", "neobelly", "neosbelly", "neombelly", "neolbelly" }), // "neo" alone is too common a word
        ("Thicclander", new[] { "thicclander" }),
        ("W25", new[] { "w25" }),
    };

    // What may follow a body's word in one token: a size or a variant ("RueL", "TBSEx"). Not "xb": RueXB is a body of its own.
    private static readonly string[] Suffixes = { "s", "m", "l", "xl", "xs", "wc", "x", "plus", "p" };

    [GeneratedRegex("[a-z0-9äöüß]+")]
    private static partial Regex Word();

    /// <summary>
    /// The bodies a text names ("Gilded Rue M WC" → Rue; "[YAB &amp; Rue+]" → YAB, Rue; "NeoLithe+M" → Neolithe; "Rue XB" →
    /// RueXB, not Rue).
    /// </summary>
    public static IEnumerable<string> In(string text)
    {
        var tokens = Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();
        for (var i = 0; i < tokens.Count; ++i)
        {
            // Two words that are one body's name ("rue xb").
            if (i + 1 < tokens.Count && OfPhrase(tokens[i] + " " + tokens[i + 1]) is { } both)
            {
                yield return both;
                ++i;
            }
            else if (Of(tokens[i]) is { } name)
            {
                yield return name;
            }
        }
    }

    /// <summary> The body two words name together ("rue xb"), else null. </summary>
    private static string? OfPhrase(string words)
    {
        foreach (var (name, list) in All)
            if (list.Contains(words))
                return name;
        return null;
    }

    /// <summary> The body a mod is named after: the first word of its name names one ("Neolithe core", "RueXB+ body"), else null. </summary>
    public static string? NamedAfter(string modName)
    {
        var tokens = Word().Matches(modName.ToLowerInvariant()).Select(m => m.Value).Take(2).ToList();
        if (tokens.Count == 0)
            return null;
        return (tokens.Count == 2 ? OfPhrase(tokens[0] + " " + tokens[1]) : null) ?? Of(tokens[0]);
    }

    /// <summary> The body one lower-case word names, also with a size or variant glued on ("ruel", "tbsex"); else null. </summary>
    public static string? Of(string word)
    {
        foreach (var (name, words) in All)
            foreach (var w in words)
                if (word == w || w.Length >= 3 && word.Length > w.Length && word.StartsWith(w, StringComparison.Ordinal)
                    && Suffixes.Contains(word[w.Length..]))
                    return name;
        return null;
    }

    /// <summary> The bodies several texts name, in the list's order, each once. </summary>
    public static List<string> InAll(IEnumerable<string> texts)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in texts)
            foreach (var n in In(t))
                found.Add(n);
        return All.Select(b => b.Name).Where(found.Contains).ToList();
    }
}
