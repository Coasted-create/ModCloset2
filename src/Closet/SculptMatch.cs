using System.Text.RegularExpressions;
using ModCloset2.Detect;

namespace ModCloset2.Closet;

/// <summary>
/// Which face sculpts a makeup (or other face texture) mod most likely belongs to, for its photo (brief 13, "Under"): paint
/// made for a sculpt sits wrong on the game's plain face. Pure: names and the file names the library keeps, nothing read.
/// <list type="number">
/// <item>Only sculpts for a race and face the paint's files name (<c>c0801f0004…</c>).</item>
/// <item>Those whose names go with the paint's, best first: the same author (a tag in brackets, or the mod's author), then
/// distinctive words both names share (a makeup named after its sculpt). Words every such mod has ("makeup", "face",
/// "sculpt", race names, numbers, …) and words in most of the sculpts at hand say nothing and are left out.</item>
/// <item>Then the sculpts that are on right now (what the user wears is what their makeup is most likely made for).</item>
/// <item>At most three; none: empty (the game's face).</item>
/// </list>
/// </summary>
public static partial class SculptMatch
{
    /// <summary> A race and a face number as a face file names them (c0801f0004 → 801, 4). </summary>
    public readonly record struct FaceId(int Race, int Face);

    /// <summary> A sculpt of the library: its mod's folder, name, author and the faces its models are for (its words made once). </summary>
    public sealed class Candidate(string folder, string name, string author, IReadOnlyCollection<FaceId> faces)
    {
        public string Folder { get; } = folder;
        public string Name { get; } = name;
        public string Author { get; } = author;
        public IReadOnlyCollection<FaceId> Faces { get; } = faces;
        internal HashSet<string> TagSet { get; } = Tags(name);
        internal HashSet<string> WordSet { get; } = Distinct(name);
        internal string AuthorWords { get; } = string.Join(" ", Words.Tokens(author ?? string.Empty));
    }

    public const int Max = 3;

    /// <summary> What the same author is worth against one shared word: it always wins. </summary>
    private const int AuthorWeight = 1000;

    [GeneratedRegex(@"c(\d{4})f(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex FaceRegex();

    [GeneratedRegex(@"[\[\(\{【]([^\]\)\}】]{1,40})[\]\)\}】]")]
    private static partial Regex TagRegex();

    /// <summary> The races and faces a list of file names names (c0801f0004_fac_base.tex, mt_c0801f0004_fac_a.mtrl…). </summary>
    public static HashSet<FaceId> FacesNamed(IEnumerable<string> fileNames)
    {
        var set = new HashSet<FaceId>();
        foreach (var name in fileNames)
            foreach (Match m in FaceRegex().Matches(name))
                set.Add(new FaceId(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        return set;
    }

    /// <summary> Words that say nothing about which sculpt: every face mod has them. </summary>
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "makeup", "make", "up", "face", "faces", "facial", "sculpt", "sculpts", "sculpted", "sculpting", "head", "heads",
        "texture", "textures", "tex", "skin", "paint", "facepaint", "mod", "mods", "edit", "edited", "version", "update",
        "updated", "new", "old", "for", "the", "and", "with", "by", "of", "on", "in", "to", "a", "an", "my", "your", "custom",
        "female", "male", "fem", "femme", "masc", "girl", "boy", "woman", "man", "bibo", "gen3", "tbse", "dawntrail", "dt",
        "endwalker", "ew", "compatible", "compat", "base", "default", "standalone", "pack", "set", "collection", "free",
        "lips", "lip", "eyes", "eye", "eyeliner", "liner", "lashes", "brows", "blush", "freckles", "options", "option",
        // races and clans
        "hyur", "midlander", "midlanders", "highlander", "highlanders", "elezen", "wildwood", "duskwight", "lalafell", "lala",
        "plainsfolk", "dunesfolk", "miqote", "miqo", "miqos", "seeker", "keeper", "keepers", "seekers", "roegadyn", "roe",
        "hellsguard", "aura", "au", "ra", "raen", "xaela", "hrothgar", "hroth", "helion", "helions", "lost", "viera", "rava",
        "veena", "cat", "bunny", "lizard",
    };

    /// <summary> The bracket tags of a name, lower case ("[Kai] Nyx (v2)" → kai, v2). </summary>
    public static HashSet<string> Tags(string name)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in TagRegex().Matches(name ?? string.Empty))
        {
            var tag = string.Join(" ", Words.Tokens(m.Groups[1].Value));
            if (tag.Length >= 2 && !IsNumberish(tag) && !Common.Contains(tag))
                set.Add(tag);
        }

        return set;
    }

    /// <summary> The distinctive words of a name outside its brackets, lower case. </summary>
    public static HashSet<string> Distinct(string name)
    {
        var outside = TagRegex().Replace(name ?? string.Empty, " ");
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Words.Tokens(outside))
            if (t.Length >= 3 && !IsNumberish(t) && !Common.Contains(t))
                set.Add(t);
        return set;
    }

    /// <summary> 4, 04, v2, 1.2, 2nd: numbers and versions. </summary>
    private static bool IsNumberish(string t)
        => t.All(char.IsDigit) || t.Length >= 2 && t[0] == 'v' && t[1..].All(c => char.IsDigit(c) || c == '.')
           || t.Any(char.IsDigit) && t.Count(char.IsLetter) <= 2;


    /// <summary>
    /// The sculpts a paint mod (its name, author and the faces its files name) most likely belongs to, best first, at most
    /// <see cref="Max"/>, as their folders. The caller leaves out the paint's own mod (its own sculpt needs nothing).
    /// </summary>
    /// <param name="worn"> Whether a sculpt is on right now; null: none is. </param>
    public static IReadOnlyList<string> Rank(string name, string author, IReadOnlyCollection<FaceId> faces, IReadOnlyList<Candidate> candidates,
        Func<Candidate, bool>? worn = null)
    {
        if (faces.Count == 0 || candidates.Count == 0)
            return Array.Empty<string>();
        var fit = candidates.Where(c => c.Folder.Length > 0 && c.Faces.Any(faces.Contains)).ToList();
        if (fit.Count == 0)
            return Array.Empty<string>();

        // a word most of the sculpts at hand have says nothing either (a library of "… Sculpt by X" from one author)
        var words = fit.Select(c => c.WordSet).ToList();
        var crowd = new HashSet<string>(StringComparer.Ordinal);
        if (fit.Count >= 4)
            foreach (var group in words.SelectMany(w => w).GroupBy(w => w, StringComparer.Ordinal))
                if (group.Count() * 2 > fit.Count)
                    crowd.Add(group.Key);

        var tags   = Tags(name);
        var mine   = Distinct(name);
        var byWhom = string.Join(" ", Words.Tokens(author ?? string.Empty));
        mine.ExceptWith(crowd);
        var scored = new List<(Candidate C, int Score, bool Worn)>(fit.Count);
        for (var i = 0; i < fit.Count; ++i)
        {
            var c = fit[i];
            var same   = tags.Overlaps(c.TagSet) || byWhom.Length >= 2 && byWhom == c.AuthorWords;
            var shared = words[i].Count(w => mine.Contains(w));
            scored.Add((c, (same ? AuthorWeight : 0) + shared, worn?.Invoke(c) ?? false));
        }

        return scored.Where(s => s.Score > 0).OrderByDescending(s => s.Score).ThenByDescending(s => s.Worn)
            .ThenBy(s => s.C.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.C.Folder, StringComparer.Ordinal)
            .Concat(scored.Where(s => s.Score == 0 && s.Worn).OrderBy(s => s.C.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.C.Folder, StringComparer.Ordinal))
            .Select(s => s.C.Folder).Distinct(StringComparer.Ordinal).Take(Max).ToList();
    }
}
