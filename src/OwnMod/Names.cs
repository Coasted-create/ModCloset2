using System.Text.RegularExpressions;

namespace ModCloset2.OwnMod;

/// <summary> Folder and file names for the new mod, and the author's sentence about edits and sharing. Pure. </summary>
public static partial class Names
{
    /// <summary> The longest folder name the copy gets, whatever room the paths leave. </summary>
    public const int MaxFolderName = 64;

    /// <summary> No full path in the copy gets longer than this, unless the old mod already has longer ones. </summary>
    public const int MaxFullPath = 240;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary> A name safe as one folder or file name on Windows: no &lt;&gt;:"/\|?* or control characters, no trailing dot or space, no device name. </summary>
    public static string Safe(string name)
    {
        var chars = name.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray();
        var s = Spaces().Replace(new string(chars), " ").Trim().TrimEnd('.', ' ');
        if (s.Length == 0)
            s = "Mod";
        if (Reserved.Contains(s.Split('.')[0]))
            s = "_" + s;
        return s;
    }

    /// <summary> Cut to at most <paramref name="max"/> characters without leaving a dot or a space at the end. </summary>
    public static string Cut(string name, int max)
    {
        if (name.Length <= max)
            return name;
        var s = name[..Math.Max(1, max)].TrimEnd('.', ' ');
        return s.Length == 0 ? "_" : s;
    }

    /// <summary>
    /// A free folder name in the mod root made from the mod's name: safe, at most <paramref name="max"/> characters (the
    /// room the copy's paths leave), not taken by a folder or a file (a number is added: "Name (2)").
    /// </summary>
    public static string FreeFolder(string modRoot, string name, int max)
    {
        max = Math.Min(max, MaxFolderName);
        var taken = new HashSet<string>(Directory.EnumerateFileSystemEntries(modRoot).Select(e => Path.GetFileName(e)), StringComparer.OrdinalIgnoreCase);
        var baseName = Cut(Safe(name), max);
        if (!taken.Contains(baseName))
            return baseName;
        for (var i = 2; i < 10_000; ++i)
        {
            var suffix = $" ({i})";
            var candidate = Cut(baseName, Math.Max(1, max - suffix.Length)) + suffix;
            if (!taken.Contains(candidate))
                return candidate;
        }

        return Cut(baseName, Math.Max(1, max - 33)) + " " + Guid.NewGuid().ToString("N");
    }

    /// <summary> Penumbra's name for a group file: group_001_name.json (numbered from 1, the name in lower case). </summary>
    public static string GroupFile(int index, string groupName)
        => $"group_{index + 1:D3}_{Safe(groupName).ToLowerInvariant()}.json";

    // A negation, up to three words, then an edit, a port, a share or a resale: "do not edit", "no ports", "must remain
    // private and not be redistributed", "don't reupload". Ported from the old plugin's ModData.EditWarning, with sharing.
    [GeneratedRegex(@"\b(?:no|not|do\s+not|don'?t|never|may\s+not|must\s+not|cannot|can'?t|without\s+(?:my\s+)?permission\s+to)\s+(?:[\w/-]+\s+){0,3}?(?:edit(?:s|ed|ing)?|port(?:s|ed|ing)?|modif(?:y|ied|ying|ications?)|alter(?:ed|ations?)?|re-?distribut\w*|re-?upload\w*|re-?post\w*|re-?sell\w*|re-?sold|shar(?:e|ed|ing)|sell(?:ing)?|sold)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Restriction();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();

    /// <summary>
    /// The line of the mod's description, website or name that forbids or restricts edits, ports or sharing, as written;
    /// "" when there is none.
    /// </summary>
    public static string AuthorsLine(string description, string website, string name)
    {
        foreach (var text in new[] { description, website, name })
        {
            if (string.IsNullOrEmpty(text))
                continue;
            var match = Restriction().Match(text);
            if (!match.Success)
                continue;
            var start = text.LastIndexOf('\n', match.Index) + 1;
            var end   = text.IndexOf('\n', match.Index);
            var line  = text[start..(end < 0 ? text.Length : end)].Trim().TrimStart('•', '-', '*', ' ').Trim();
            return line.Length > 300 ? line[..300].TrimEnd() + "…" : line;
        }

        return string.Empty;
    }
}
