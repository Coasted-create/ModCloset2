using System.Globalization;
using System.Text;

namespace ModCloset2.Closet;

/// <summary>
/// Search across all shelves: every word of the query must be found (in the name, author, Penumbra folder, shelf or kind
/// names), as a part of a word or, for words of five letters or more, with one typo (a letter missing, added, wrong or two
/// swapped). Pure.
/// </summary>
public static class SearchText
{
    /// <summary> The words of a text, lower case, accents kept ("Kurzer Kimono" → kurzer, kimono). </summary>
    public static string[] Words(string text)
    {
        var words = new List<string>();
        var word  = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
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

    /// <summary> What a part can be found by: one lower-case text and its words. </summary>
    public sealed class Haystack
    {
        private string[]? _words;

        public string Text { get; }

        /// <summary> The words, made when a typo has to be looked for (most searches find their words as they are). </summary>
        public string[] Words
            => _words ??= SearchText.Words(Text).Distinct().ToArray();

        public Haystack(IEnumerable<string> fields)
            => Text = string.Join(" \u0001 ", fields.Where(f => !string.IsNullOrWhiteSpace(f))).ToLowerInvariant();
    }

    /// <summary> True when every query word is found. An empty query matches nothing. </summary>
    public static bool Matches(IReadOnlyList<string> query, Haystack hay)
    {
        if (query.Count == 0)
            return false;
        foreach (var q in query)
            if (!hay.Text.Contains(q, StringComparison.Ordinal) && !(q.Length >= 5 && hay.Words.Any(w => WithinOne(q, w))))
                return false;
        return true;
    }

    /// <summary> True when two words differ by at most one letter added, missing, changed, or two neighbours swapped. </summary>
    public static bool WithinOne(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
            return false;
        if (a == b)
            return true;

        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
            ++i;

        if (a.Length == b.Length)
        {
            // one letter changed, or two neighbours swapped
            if (a.AsSpan(i + 1).SequenceEqual(b.AsSpan(i + 1)))
                return true;
            return i + 1 < a.Length && a[i] == b[i + 1] && a[i + 1] == b[i] && a.AsSpan(i + 2).SequenceEqual(b.AsSpan(i + 2));
        }

        // one letter added or missing
        var (longer, shorter) = a.Length > b.Length ? (a, b) : (b, a);
        return longer.AsSpan(i + 1).SequenceEqual(shorter.AsSpan(i));
    }

    /// <summary> A count for people ("2,300" in English, "2.300" in German). </summary>
    public static string Number(int n)
        => n.ToString("N0", Loc.Language == "de" ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.InvariantCulture);
}
