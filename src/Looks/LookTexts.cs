using System.Text;
using System.Text.RegularExpressions;
using ModCloset2.Core;

namespace ModCloset2.Looks;

/// <summary> Everything the looks say, through Loc. Pure. </summary>
public static partial class LookTexts
{
    /// <summary> "the shoes", "the top": a shelf as a thing one changes. </summary>
    public static string Noun(string shelf)
        => shelf switch
        {
            Pieces.Head      => Loc.T("the headwear"),
            Pieces.Top       => Loc.T("the top"),
            Pieces.Gloves    => Loc.T("the gloves"),
            Pieces.Bottoms   => Loc.T("the bottoms"),
            Pieces.Legwear   => Loc.T("the legwear"),
            Pieces.Shoes     => Loc.T("the shoes"),
            Pieces.Earrings  => Loc.T("the earrings"),
            Pieces.Necklace  => Loc.T("the necklace"),
            Pieces.Bracelets => Loc.T("the bracelets"),
            Pieces.Rings     => Loc.T("the rings"),
            Pieces.Hair      => Loc.T("the hair"),
            Pieces.Face      => Loc.T("the face"),
            Pieces.Makeup    => Loc.T("the makeup"),
            Pieces.Ears      => Loc.T("the ears"),
            Pieces.Tail      => Loc.T("the tail"),
            Pieces.Bodies    => Loc.T("the body"),
            Pieces.Skin      => Loc.T("the skin"),
            Pieces.Nails     => Loc.T("the nails"),
            _                => Loc.T("the piece"),
        };

    /// <summary>
    /// "You changed the shoes", "You took off Denim jacket", "You changed 3 pieces", "You changed the colours"; "" for no
    /// change. <paramref name="diffs"/> name the thing: the shelf's noun (<see cref="Noun"/>) when it is the only thing
    /// on its shelf, else its own name.
    /// </summary>
    public static string Change(IReadOnlyList<(DiffKind Kind, string Thing)> diffs, bool colours)
    {
        if (diffs.Count == 0)
            return colours ? Loc.T("You changed the colours") : string.Empty;
        if (diffs.Count > 1)
            return Loc.F("You changed {0} pieces", diffs.Count);
        var (kind, thing) = diffs[0];
        return kind switch
        {
            DiffKind.Added   => Loc.F("You added {0}", thing),
            DiffKind.TookOff => Loc.F("You took off {0}", thing),
            _                => Loc.F("You changed {0}", thing),
        };
    }

    /// <summary> True for the shelves <see cref="Noun"/> has a word for. </summary>
    public static bool HasNoun(string shelf)
        => Pieces.IsOutfitPiece(shelf) || Pieces.IsBodyPiece(shelf);

    /// <summary> "6 pieces", "1 piece", "8 pieces, with hair", "3 pieces, hair and body only". </summary>
    public static string Meta(int pieces, bool withBody, bool bodyOnly = false)
    {
        var count = pieces == 1 ? Loc.T("1 piece") : Loc.F("{0} pieces", pieces);
        return bodyOnly ? Loc.F("{0}, hair and body only", count) : withBody ? Loc.F("{0}, with hair", count) : count;
    }

    /// <summary> "the hair", "the hair and the makeup", "the hair, the face and the tail". </summary>
    public static string Nouns(IEnumerable<string> shelves)
    {
        var nouns = shelves.Select(Noun).Distinct().ToList();
        return nouns.Count switch
        {
            0 => string.Empty,
            1 => nouns[0],
            _ => Loc.F("{0} and {1}", string.Join(", ", nouns.Take(nouns.Count - 1)), nouns[^1]),
        };
    }

    /// <summary>
    /// The try bar's quiet line: "5 pieces · your hair and body stay as they are", "5 pieces · with the hair and the
    /// makeup" (the body shelves the look wears), "3 pieces · hair and body only, your clothes stay as they are".
    /// </summary>
    public static string TryNote(int pieces, int missing, IReadOnlyCollection<string> body, bool bodyOnly)
    {
        var parts = new List<string> { pieces == 1 ? Loc.T("1 piece") : Loc.F("{0} pieces", pieces) };
        if (missing > 0)
            parts.Add(Loc.F("{0} missing", missing));
        parts.Add(bodyOnly ? Loc.T("your clothes stay as they are")
                : body.Count > 0 ? Loc.F("with {0}", Nouns(body))
                : Loc.T("your hair and body stay as they are"));
        return string.Join(" · ", parts);
    }

    /// <summary> "Today", "Yesterday", "Sunday", "Last week", "3 weeks ago", "Over a month ago" (dates in the user's time). </summary>
    public static string When(DateTime wornLocal, DateTime nowLocal)
    {
        var days = (nowLocal.Date - wornLocal.Date).TotalDays;
        if (days <= 0)
            return Loc.T("Today");
        if (days < 2)
            return Loc.T("Yesterday");
        if (days < 7)
            return Loc.Name(wornLocal.DayOfWeek);
        if (days < 14)
            return Loc.T("Last week");
        if (days < 35)
            return Loc.F("{0} weeks ago", (int)(days / 7));
        return Loc.T("Over a month ago");
    }

    /// <summary> The name a look takes from its main piece: the mod's name without tags, versions and underscores. </summary>
    public static string CleanName(string name)
    {
        var text = name.Replace('_', ' ').Trim();
        // leading tags: "[Author] Summer Set", "(author) - sage", "{NSFW} …"
        while (LeadingTag().Match(text) is { Success: true } tag && tag.Length < text.Length)
            text = text[tag.Length..].TrimStart(' ', '-', '–', '|', ':', '.');
        text = TrailingVersion().Replace(text, string.Empty).Trim(' ', '-', '–', '|', ':');
        text = Spaces().Replace(text, " ").Trim();
        if (text.Length == 0)
            return name.Trim();
        // a name all in lower case reads better with a capital first letter ("sage" → "Sage")
        return text.Any(char.IsUpper) ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary> A name as words for comparing: lower case, letters and digits only, versions left out. </summary>
    public static string[] Words(string name)
    {
        var text = TrailingVersion().Replace(name.Replace('_', ' '), " ");
        text = AnyVersion().Replace(text.ToLowerInvariant(), " ");
        var words = new List<string>();
        var word  = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
                continue;
            }

            if (word.Length > 0)
                words.Add(word.ToString());
            word.Clear();
        }

        if (word.Length > 0)
            words.Add(word.ToString());
        return words.ToArray();
    }

    /// <summary> How alike two names are: shared words over all words (0..1). </summary>
    public static double Alike(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;
        var sa = a.ToHashSet();
        var sb = b.ToHashSet();
        var shared = sa.Count(sb.Contains);
        return (double)shared / (sa.Count + sb.Count - shared);
    }

    [GeneratedRegex(@"^\s*(\[[^\]]*\]|\([^)]*\)|\{[^}]*\})\s*")]
    private static partial Regex LeadingTag();

    [GeneratedRegex(@"[\s\-–]*\(?\s*\bv(er(sion)?)?\s*\.?\s*\d+([.\-]\d+)*[a-z]?\s*\)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersion();

    [GeneratedRegex(@"\bv\d+([.\-]\d+)*\b", RegexOptions.IgnoreCase)]
    private static partial Regex AnyVersion();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
