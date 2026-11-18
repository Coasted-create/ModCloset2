using ModCloset2.Library;

namespace ModCloset2.Wear;

/// <summary>
/// Which options a mod is put on with (port of the old Outfit.StartingOptions, ModFolder.OptionsForRace and
/// ModFolder.OptionsForBody, over facts read in the background so nothing reads files on click). Pure.
/// </summary>
public static class OptionPicker
{
    /// <summary>
    /// The options to use for a mod: what the collection has saved, else the mod's defaults, with single-choice groups whose
    /// default is made for another race / gender switched to the one made for this character, and groups that choose a body
    /// switched to the user's body.
    /// </summary>
    public static Dictionary<string, List<string>> Starting(IReadOnlyList<GroupInfo> groups, IReadOnlyDictionary<string, List<string>>? saved,
        IReadOnlySet<int> raceCodes, string body)
    {
        if (saved is { Count: > 0 })
            return saved.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());

        var options = Defaults(groups);
        if (raceCodes.Count > 0)
            foreach (var (group, option) in ForRace(groups, raceCodes))
                options[group] = new List<string> { option };
        if (body.Length > 0)
            foreach (var (group, option) in ForBody(groups, body, options))
                options[group] = new List<string> { option };
        return options;
    }

    /// <summary> The options each group has selected by default, group name → option names. </summary>
    public static Dictionary<string, List<string>> Defaults(IReadOnlyList<GroupInfo> groups)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var group in groups)
            if (group.Name.Length > 0 && group.Options.Count > 0)
                result[group.Name] = group.DefaultChoice();
        return result;
    }

    /// <summary>
    /// For single-choice option groups whose default option is made only for other races / genders (e.g. "Male" chosen by
    /// default in a Male / Female group), the first option that is made for the given race codes. Group name → option name.
    /// Groups whose default already fits, or that have no race-specific files, are not listed.
    /// </summary>
    public static Dictionary<string, string> ForRace(IReadOnlyList<GroupInfo> groups, IReadOnlySet<int> accepted)
    {
        var result = new Dictionary<string, string>();
        foreach (var group in groups)
        {
            if (group.Type != "Single" || group.Name.Length == 0 || group.Options.Count < 2)
                continue;

            var index = group.Default >= 0 && group.Default < group.Options.Count ? (int)group.Default : 0;
            var dflt  = group.Options[index].RaceCodes;
            if (dflt.Count == 0 || dflt.Overlaps(accepted))
                continue;

            if (group.Options.FirstOrDefault(o => o.Name.Length > 0 && o.RaceCodes.Overlaps(accepted)) is { } fit)
                result[group.Name] = fit.Name;
        }

        return result;
    }

    /// <summary> The options made for <paramref name="body"/> in single-choice groups that choose between bodies. </summary>
    public static Dictionary<string, string> ForBody(IReadOnlyList<GroupInfo> groups, string body, IReadOnlyDictionary<string, List<string>> current)
    {
        var singles = groups
            .Where(g => !g.Multi && g.Name.Length > 0 && g.Options.Count > 1)
            .Select(g =>
            {
                var index = g.Default is >= 0 && g.Default < g.Options.Count ? (int)g.Default : 0;
                var now   = current.TryGetValue(g.Name, out var c) && c.Count > 0 ? c[0] : g.Options[index].Name;
                return (g.Name, (IReadOnlyList<string>)g.Options.Select(o => o.Name).ToList(), now);
            })
            // New in Mod Closet 2: a group set to "none" stays off when the group is an optional piece in sizes (one sample mod:
            // "SIZE - panties & thights (DONT USE WITH SOCKS)", off by default because it clashes with its socks). The old
            // picker read "none" as "names no body" and switched it on. When the group itself is about the body ("Body",
            // "Body: None / Bibo / Rue", "Rue/YAB sizes"), "none" is the game's own body and is switched as before.
            .Where(g => !IsOff(g.now) || IsBodyChoice(g.Name));
        return BodyFit.OptionsForBody(singles, body);
    }

    /// <summary>
    /// A group that chooses the body itself: its name names a body (Rue, YAB…) or says body / base, and says nothing else
    /// but shape, size and choice words ("Body", "Body type", "Base body", "Rue/YAB sizes", "Body: None / Bibo / Rue").
    /// Any other noun makes it an optional piece on the body ("Body Tattoo", "Base Layer", "SIZE - socks"), whose "none"
    /// stays off.
    /// </summary>
    public static bool IsBodyChoice(string group)
    {
        var words = BodyFit.WordsOf(group).ToList();
        return words.Any(w => BodyWords.Contains(w) || BodyFit.IsBodyWord(w))
            && words.All(w => BodyWords.Contains(w) || ChoiceWords.Contains(w) || BodyFit.IsBodyWord(w) || w.All(char.IsDigit) || IsOff(w));
    }

    private static readonly HashSet<string> BodyWords = new() { "body", "bodies", "base", "körper", "koerper" };

    private static readonly HashSet<string> ChoiceWords = new()
    {
        "type", "types", "shape", "shapes", "form", "size", "sizes", "größe", "größen", "groesse", "model", "models", "choice", "choose",
        "option", "options", "select", "version", "versions", "vanilla", "default", "and", "or", "for", "your", "the", "a", "plus",
    };

    /// <summary> An option that means "this is off" ("none", "off", "no", "-----"). </summary>
    public static bool IsOff(string option)
    {
        var o = option.Trim().ToLowerInvariant();
        return o is "" or "none" or "off" or "no" or "nothing" or "disabled" or "disable" or "hide" or "hidden" or "remove" || o.All(c => c is '-' or '_' or ' ');
    }
}
