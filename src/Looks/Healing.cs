using ModCloset2.Core;

namespace ModCloset2.Looks;

/// <summary>
/// Finding a look's piece again when its mod's folder name changed (renamed, reinstalled, updated), and the closest parts
/// for a piece whose mod is gone. Pure (the library and the detector come in as interfaces).
/// </summary>
public static class Healing
{
    /// <summary> What is needed to find a piece again: the library's parts by socket, with their name words and author. </summary>
    public sealed class Index
    {
        private readonly Dictionary<(Socket, string), List<LookLibraryPart>> _byWord = new();
        private readonly Dictionary<(Socket, string), List<LookLibraryPart>> _byAuthor = new();
        private readonly Dictionary<PartKey, string[]> _words = new();

        /// <param name="words"> Words of names seen before (kept by the caller between indexes: names rarely change). </param>
        public Index(IEnumerable<LookLibraryPart> parts, Dictionary<string, string[]>? words = null)
        {
            words ??= new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var part in parts)
            {
                if (!words.TryGetValue(part.Name, out var nameWords))
                    words[part.Name] = nameWords = LookTexts.Words(part.Name);
                Put(part, nameWords);
            }
        }

        private void Put(LookLibraryPart part, string[] words)
        {
            _words[part.Key] = words;
            foreach (var word in words)
                if (word.Length >= 3)
                    Add(_byWord, (part.Key.Socket, word), part);
            if (part.Author.Length > 0 && part.Author.Trim() is { Length: > 0 } author)
                Add(_byAuthor, (part.Key.Socket, author.ToLowerInvariant()), part);
        }

        private static void Add(Dictionary<(Socket, string), List<LookLibraryPart>> map, (Socket, string) key, LookLibraryPart part)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<LookLibraryPart>();
            list.Add(part);
        }

        /// <summary> A word or an author shared by more parts than this says little when a rarer one is there. </summary>
        public const int Common = 300;

        /// <summary>
        /// Parts of the same socket that share a word of the name or the author. Very common words ("top", "set") and
        /// authors of hundreds of mods are left out when a rarer word is there.
        /// </summary>
        public IEnumerable<LookLibraryPart> Candidates(Socket socket, ModPrint print)
        {
            var lists = new List<List<LookLibraryPart>>();
            foreach (var word in LookTexts.Words(print.Name).Where(w => w.Length >= 3).Distinct())
                if (_byWord.TryGetValue((socket, word), out var list))
                    lists.Add(list);
            if (print.Author.Trim().Length > 0 && _byAuthor.TryGetValue((socket, print.Author.Trim().ToLowerInvariant()), out var byAuthor))
                lists.Add(byAuthor);
            if (lists.Any(l => l.Count <= Common))
                lists.RemoveAll(l => l.Count > Common);
            else if (lists.Count > 1)
                lists = new List<List<LookLibraryPart>> { lists.MinBy(l => l.Count)! };

            var seen = new HashSet<PartKey>();
            foreach (var list in lists)
                foreach (var part in list)
                    if (seen.Add(part.Key))
                        yield return part;
        }

        public string[] WordsOf(LookLibraryPart part)
            => _words.TryGetValue(part.Key, out var w) ? w : LookTexts.Words(part.Name);
    }

    /// <summary>
    /// How sure the closet is that <paramref name="candidateWords"/>' part is the mod the print was taken from: the name
    /// (0.5 when the words are the same, versions left out; else up to 0.4 when at least half the words are shared), the
    /// author (+0.2 the same, −0.2 another one), the files (up to 0.4 for the share of the print's file names it has).
    /// A name with nothing in common scores 0 whatever the rest says: authors use the same file names in many mods.
    /// </summary>
    public static double Score(ModPrint print, string[] candidateWords, string candidateAuthor, IReadOnlyList<string>? candidateFiles)
        => Score(print, LookTexts.Words(print.Name), candidateWords, candidateAuthor, candidateFiles);

    /// <summary> <see cref="Score(ModPrint, string[], string, IReadOnlyList{string}?)"/> with the print's name words made once (a search scores thousands). </summary>
    private static double Score(ModPrint print, string[] words, string[] candidateWords, string candidateAuthor, IReadOnlyList<string>? candidateFiles)
    {
        var score = 0.0;
        if (words.Length > 0 && words.SequenceEqual(candidateWords))
            score += 0.5;
        else if (Alike(words, candidateWords) is var alike and >= 0.5)
            score += 0.4 * alike;
        else
            return 0;

        var a = print.Author.Trim();
        var b = candidateAuthor.Trim();
        if (a.Length > 0 && b.Length > 0)
            score += a.Equals(b, StringComparison.OrdinalIgnoreCase) ? 0.2 : -0.2;

        if (print.Files.Count > 0 && candidateFiles is { Count: > 0 })
        {
            var names = candidateFiles.Select(FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            score += 0.4 * print.Files.Count(names.Contains) / print.Files.Count;
        }

        return score;
    }

    /// <summary>
    /// <see cref="LookTexts.Alike"/> for the few words of two names, without making sets (a search scores thousands of
    /// names): shared distinct words over all distinct words.
    /// </summary>
    private static double Alike(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;
        int da = 0, db = 0, shared = 0;
        for (var i = 0; i < a.Length; ++i)
            if (Array.IndexOf(a, a[i]) == i)
            {
                da++;
                if (Array.IndexOf(b, a[i]) >= 0)
                    shared++;
            }

        for (var i = 0; i < b.Length; ++i)
            if (Array.IndexOf(b, b[i]) == i)
                db++;
        return (double)shared / (da + db - shared);
    }

    /// <summary> The file name of a path, lower case ("chara/…/mt_top_a.mtrl" → "mt_top_a.mtrl"). </summary>
    public static string FileName(string path)
    {
        var cut = path.LastIndexOfAny(new[] { '/', '\\' });
        return (cut >= 0 ? path[(cut + 1)..] : path).ToLowerInvariant();
    }

    /// <summary> A print's file list from a part's files: distinct names, sorted, a few. </summary>
    public static List<string> PrintFiles(IReadOnlyList<string> files)
        => files.Select(FileName).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Take(LookRules.PrintFiles).ToList();

    /// <summary>
    /// The part a missing piece's mod became, or null. First the same mod (an update that moved the part to another
    /// socket): a part of it on the same shelf. Then parts of the same socket with a name word or the author in common,
    /// scored (<see cref="Score"/>); the best wins when it scores at least <see cref="LookRules.HealScore"/> and leads the
    /// next by <see cref="LookRules.HealLead"/>. Parts in <paramref name="taken"/> (other pieces of the look) are skipped.
    /// Without an <paramref name="index"/> only the same mod is looked at.
    /// </summary>
    public static LookLibraryPart? Find(LookPiece piece, string shelf, ILooksLibrary library, Func<Index>? index, ISet<PartKey> taken)
    {
        var same = library.PartsOf(piece.Mod).Where(p => !taken.Contains(p.Key)).ToList();
        // (a body kept when body mods sat on Skin is the same place as Bodies now)
        if (same.FirstOrDefault(p => LookRules.SameShelf(p.Shelf, shelf)) is { } moved)
            return moved;
        if (index is null || piece.Print is not { } print || print.Name.Length == 0 && print.Files.Count == 0)
            return null;

        var idx    = index();
        var words  = LookTexts.Words(print.Name);
        var scored = new List<(LookLibraryPart Part, double Score)>();
        foreach (var candidate in idx.Candidates(piece.Socket, print))
        {
            if (taken.Contains(candidate.Key))
                continue;
            // a name with nothing in common scores 0 and can never win: not kept (most candidates of a common word)
            if (Score(print, words, idx.WordsOf(candidate), candidate.Author, null) is var score and > 0)
                scored.Add((candidate, score));
        }

        // the files only for the few best by name and author (reading a part's files is not free)
        var best = scored.OrderByDescending(s => s.Score).Take(12)
            .Select(s => (s.Part, Score: print.Files.Count > 0 ? Score(print, words, idx.WordsOf(s.Part), s.Part.Author, library.FilesOf(s.Part.Key)) : s.Score))
            .OrderByDescending(s => s.Score).ThenBy(s => s.Part.Key.Mod, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (best.Count == 0 || best[0].Score < LookRules.HealScore)
            return null;
        if (best.Count > 1 && best[0].Score - best[1].Score < LookRules.HealLead)
            return null; // two are as likely: better a gap the user fills than the wrong piece
        return best[0].Part;
    }

    /// <summary>
    /// Parts to offer for a piece whose mod is gone, best first: on that shelf, the same socket and a model in the same
    /// place (the detection's "looks the same") first, then the same socket by how alike the names are, then the rest
    /// of the shelf by name.
    /// </summary>
    public static List<LookLibraryPart> Replacements(LookPiece piece, IReadOnlyList<LookLibraryPart> shelf, IDetector? detector, int max)
    {
        var print  = piece.Print ?? new ModPrint();
        var words  = LookTexts.Words(print.Name);
        var result = new List<LookLibraryPart>();
        var used   = new HashSet<PartKey> { piece.Key };

        if (detector is not null && print.Signature.Length > 0)
        {
            var byInfo = new Dictionary<PartInfo, LookLibraryPart>(ReferenceEqualityComparer.Instance);
            foreach (var part in shelf.Where(p => p.Key.Socket == piece.Socket))
                byInfo.TryAdd(part.Info, part);
            var moved = new PartInfo { Mod = piece.Mod, Socket = piece.Socket, Signature = print.Signature };
            try
            {
                foreach (var info in detector.LookAlikes(moved, byInfo.Keys))
                    if (byInfo.TryGetValue(info, out var part) && used.Add(part.Key))
                        result.Add(part);
            }
            catch
            {
                // the detector never throws by contract; if it does, the names below still give an order
            }
        }

        foreach (var part in shelf.Where(p => p.Key.Socket == piece.Socket && !used.Contains(p.Key))
                     .OrderByDescending(p => LookTexts.Alike(words, LookTexts.Words(p.Name)))
                     .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList())
        {
            used.Add(part.Key);
            result.Add(part);
        }

        foreach (var part in shelf.Where(p => !used.Contains(p.Key))
                     .OrderByDescending(p => LookTexts.Alike(words, LookTexts.Words(p.Name)))
                     .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList())
        {
            used.Add(part.Key);
            result.Add(part);
        }

        return result.Take(max).ToList();
    }
}
