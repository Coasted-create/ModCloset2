using System.Text;
using System.Text.Json;

namespace ModCloset2.Detect;

/// <summary> One file redirection of a mod: game path (lower case, forward slashes) → local file (relative to the mod). </summary>
public readonly record struct FileRedirect(string GamePath, string LocalPath);

/// <summary>
/// One meta edit (EQP, EQDP, IMC, EST, GMP, RSP, ATR, SHP…), reduced to what says where it applies. Field names in the
/// mod files differ by type (SetId / PrimaryId / Id, Slot / EquipSlot); they are read into the same fields here.
/// </summary>
/// <param name="Variant"> IMC: the variant the entry is for. </param>
/// <param name="Mask"> IMC: the entry's attribute mask (-1 when the edit does not carry one). </param>
/// <param name="Entry"> ATR / SHP: whether the attribute is switched on or off. </param>
public sealed record MetaEdit(string Type, int Set, string Slot, string ObjectType, string BodySlot, string Race, string Gender, string Attribute,
    int Variant = 0, int Mask = -1, bool? Entry = null);

/// <summary> One option (or the default data, or one container of a Combining group) with what it changes. </summary>
public sealed class OptionDef
{
    public string Name { get; init; } = string.Empty;
    public List<FileRedirect> Files { get; } = new();

    /// <summary> Game paths redirected to other game paths: the replaced path, and in LocalPath the game path used instead. </summary>
    public List<FileRedirect> Swaps { get; } = new();
    public List<MetaEdit> Metas { get; } = new();

    /// <summary> IMC groups: the attribute bits this option switches. </summary>
    public int AttributeMask { get; init; }

    /// <summary> Multi groups: options with a higher priority are applied first (and win). </summary>
    public int Priority { get; init; }
}

/// <summary> One option group with its options (and containers for Combining groups). </summary>
public sealed class GroupDef
{
    public string Name { get; init; } = string.Empty;

    /// <summary> "Single", "Multi", "Imc" or "Combining" (as written in the mod). </summary>
    public string Type { get; init; } = string.Empty;
    public long DefaultSettings { get; init; }

    /// <summary> Groups with a higher priority are applied first; Penumbra keeps the first value for a file or edit. </summary>
    public int Priority { get; init; }
    public List<OptionDef> Options { get; } = new();
    public List<OptionDef> Containers { get; } = new();

    /// <summary> Imc groups: the IMC entry the group edits whenever the mod is on (null otherwise). </summary>
    public MetaEdit? Imc { get; set; }

    /// <summary> Imc groups: the attribute mask of the entry before any option changes it. </summary>
    public int ImcDefaultMask { get; set; }

    /// <summary> Indices of the options (or containers) that are on by default. </summary>
    public HashSet<int> DefaultOn()
    {
        var on = new HashSet<int>();
        if (Type == "Combining")
        {
            var mask = 0;
            for (var i = 0; i < Options.Count && i < 31; ++i)
                if ((DefaultSettings >> i & 1) == 1)
                    mask |= 1 << i;
            on.Add(mask);
            return on;
        }

        if (Type is "Single" or "")
        {
            on.Add(DefaultSettings >= 0 && DefaultSettings < Options.Count ? (int)DefaultSettings : 0);
            return on;
        }

        for (var i = 0; i < Options.Count && i < 63; ++i)
            if ((DefaultSettings >> i & 1) == 1)
                on.Add(i);
        return on;
    }
}

/// <summary>
/// A mod's definition from either Penumbra layout: everything in meta.json ("DefaultData" + "Groups"), or meta.json +
/// default_mod.json + group_*.json. Ported from the old plugin's ModFiles. Strictly read only; never throws: problems
/// go to <see cref="Problems"/>.
/// </summary>
public sealed class ModDefinition
{
    private const long MaxJsonBytes = 32L << 20;

    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public string Name { get; private set; } = string.Empty;
    public string Author { get; private set; } = string.Empty;
    public List<string> Tags { get; } = new();
    public OptionDef Default { get; } = new() { Name = "Default" };
    public List<GroupDef> Groups { get; } = new();
    public List<string> Problems { get; } = new();

    /// <summary> True when meta.json was there and readable. </summary>
    public bool HasMeta { get; private set; }

    public static ModDefinition Read(ModSource source)
    {
        var m = new ModDefinition();
        m.ReadFile(source, "meta.json", m.ReadMeta, required: true);
        m.ReadFile(source, "default_mod.json", root => ReadOption(root, m.Default), required: false);

        foreach (var file in source.Files
                     .Where(f => !f.Contains('/') && f.StartsWith("group_", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            m.ReadFile(source, file, root =>
            {
                if (ReadGroup(root) is { } g)
                    m.Groups.Add(g);
            }, required: false);

        return m;
    }

    private void ReadMeta(JsonElement root)
    {
        HasMeta = true;
        Name    = Str(root, "Name");
        Author  = Str(root, "Author");
        if (root.TryGetProperty("ModTags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            foreach (var t in tags.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } tag)
                    Tags.Add(tag);

        // The single-file layout (pcp / newer packs): everything in meta.json.
        if (root.TryGetProperty("DefaultData", out var dd))
            ReadOption(dd, Default);
        if (root.TryGetProperty("Groups", out var gs) && gs.ValueKind == JsonValueKind.Array)
            foreach (var g in gs.EnumerateArray())
                if (ReadGroup(g) is { } group)
                    Groups.Add(group);
    }

    private void ReadFile(ModSource source, string name, Action<JsonElement> read, bool required)
    {
        var length = source.Length(name);
        if (length < 0)
        {
            if (required)
                Problems.Add($"{name} is missing.");
            return;
        }

        var bytes = source.Read(name, MaxJsonBytes);
        if (bytes is null)
        {
            Problems.Add($"{name} could not be read.");
            return;
        }

        try
        {
            // Skip a UTF-8 byte order mark: some tools write one and the parser rejects it.
            var span = bytes.AsMemory();
            if (span.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                span = span[3..];
            using var doc = JsonDocument.Parse(span, JsonOptions);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                read(doc.RootElement);
            else
                Problems.Add($"{name} has no settings in it.");
        }
        catch (Exception ex)
        {
            Problems.Add(ex is JsonException ? $"{name} is damaged (not valid JSON)." : $"{name} could not be read ({ex.GetType().Name}).");
        }
    }

    private static GroupDef? ReadGroup(JsonElement g)
    {
        if (g.ValueKind != JsonValueKind.Object)
            return null;

        var group = new GroupDef
        {
            Name            = Str(g, "Name"),
            Type            = Str(g, "Type"),
            DefaultSettings = g.TryGetProperty("DefaultSettings", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var v) ? v : 0,
            Priority        = Int(g, "Priority"),
        };

        if (g.TryGetProperty("Options", out var os) && os.ValueKind == JsonValueKind.Array)
            foreach (var o in os.EnumerateArray())
            {
                var option = new OptionDef { Name = Str(o, "Name"), AttributeMask = Int(o, "AttributeMask"), Priority = Int(o, "Priority") };
                ReadOption(o, option);
                group.Options.Add(option);
            }

        if (g.TryGetProperty("Containers", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var c in cs.EnumerateArray())
            {
                var container = new OptionDef { Name = Str(c, "Name") };
                ReadOption(c, container);
                group.Containers.Add(container);
            }

        if (group.Type == "Imc" && g.TryGetProperty("Identifier", out var id) && id.ValueKind == JsonValueKind.Object)
        {
            group.Imc = ImcIdentity(id);
            if (g.TryGetProperty("DefaultEntry", out var de) && de.ValueKind == JsonValueKind.Object)
                group.ImcDefaultMask = Int(de, "AttributeMask");
        }

        return group;
    }

    private static void ReadOption(JsonElement o, OptionDef target)
    {
        if (o.ValueKind != JsonValueKind.Object)
            return;

        if (o.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Object)
            foreach (var f in files.EnumerateObject())
                if (f.Value.ValueKind == JsonValueKind.String)
                    target.Files.Add(new FileRedirect(NormalizeGamePath(f.Name), f.Value.GetString() ?? string.Empty));

        if (o.TryGetProperty("FileSwaps", out var swaps) && swaps.ValueKind == JsonValueKind.Object)
            foreach (var f in swaps.EnumerateObject())
                target.Swaps.Add(new FileRedirect(NormalizeGamePath(f.Name), f.Value.ValueKind == JsonValueKind.String ? NormalizeGamePath(f.Value.GetString() ?? string.Empty) : string.Empty));

        if (o.TryGetProperty("Manipulations", out var manips) && manips.ValueKind == JsonValueKind.Array)
            foreach (var m in manips.EnumerateArray())
                if (MetaOf(m) is { } meta)
                    target.Metas.Add(meta);
    }

    /// <summary>
    /// One raw meta change as written in a mod ({"Type": …, "Manipulation": {…}}), reduced the way the detection reads
    /// it. Null when it is not shaped like one. Shared with "Make it its own mod".
    /// </summary>
    public static MetaEdit? MetaOf(JsonElement change)
        => change.ValueKind == JsonValueKind.Object && change.TryGetProperty("Manipulation", out var body) && body.ValueKind == JsonValueKind.Object
            ? Meta(Str(change, "Type"), body)
            : null;

    /// <summary> The IMC entry an IMC option group's "Identifier" names, read the way the detection reads it. </summary>
    public static MetaEdit ImcIdentity(JsonElement identifier)
        => Identity("Imc", identifier);

    private static MetaEdit Meta(string type, JsonElement body)
    {
        var meta = Identity(type, body);
        if (!body.TryGetProperty("Entry", out var entry))
            return meta;

        // ATR / SHP: Entry is true or false. IMC: Entry is the whole entry with its attribute mask.
        if (entry.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return meta with { Entry = entry.GetBoolean() };
        if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("AttributeMask", out _))
            return meta with { Mask = Int(entry, "AttributeMask") };
        return meta;
    }

    private static MetaEdit Identity(string type, JsonElement body)
    {
        var set = Int(body, "SetId");
        if (set == 0)
            set = Int(body, "PrimaryId");
        if (set == 0)
            set = Int(body, "Id");
        var slot = Str(body, "Slot");
        if (slot.Length == 0)
            slot = Str(body, "EquipSlot");
        return new MetaEdit(type.ToLowerInvariant(), set, slot.ToLowerInvariant(), Str(body, "ObjectType").ToLowerInvariant(),
            Str(body, "BodySlot").ToLowerInvariant(), Str(body, "Race").ToLowerInvariant(), Str(body, "Gender").ToLowerInvariant(), Str(body, "Attribute"),
            Int(body, "Variant"));
    }

    /// <summary> Lower case with forward slashes, as Penumbra compares game paths. </summary>
    public static string NormalizeGamePath(string path)
        => path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();

    /// <summary>
    /// Every option with its group (null for the default data) and whether it is on with the mod's default settings.
    /// Combining groups contribute their containers.
    /// </summary>
    public IEnumerable<(GroupDef? Group, OptionDef Option, bool On)> AllOptions()
    {
        yield return (null, Default, true);
        foreach (var g in Groups)
        {
            var on = g.DefaultOn();
            if (g.Type == "Combining")
            {
                for (var i = 0; i < g.Containers.Count; ++i)
                    yield return (g, g.Containers[i], on.Contains(i));
                continue;
            }

            for (var i = 0; i < g.Options.Count; ++i)
                yield return (g, g.Options[i], on.Contains(i));
        }
    }

    /// <summary>
    /// The options that are on with the mod's default settings, in the order Penumbra applies them: groups by priority
    /// (highest first, later groups first on a tie), options of a multi group by priority, the default data last. For a
    /// game path or a meta edit set by several of them, the first one wins.
    /// </summary>
    public List<(int Group, int Option, OptionDef Def)> AppliedByDefault()
    {
        var list = new List<(int, int, OptionDef)>();
        foreach (var gi in Enumerable.Range(0, Groups.Count).Reverse().OrderByDescending(i => Groups[i].Priority))
        {
            var g  = Groups[gi];
            var on = g.DefaultOn();
            if (g.Type == "Combining")
            {
                foreach (var ci in on.Where(i => i < g.Containers.Count))
                    list.Add((gi, ci, g.Containers[ci]));
                continue;
            }

            if (g.Type == "Imc")
                continue; // attribute masks only; see ImcMask

            foreach (var oi in Enumerable.Range(0, g.Options.Count).OrderByDescending(i => g.Options[i].Priority).Where(on.Contains))
                list.Add((gi, oi, g.Options[oi]));
        }

        list.Add((-1, -1, Default));
        return list;
    }

    /// <summary> The attribute mask an IMC group sets with its default settings: the default entry's, switched by each option that is on. </summary>
    public static int ImcMask(GroupDef g)
    {
        var mask = g.ImcDefaultMask;
        var on   = g.DefaultOn();
        for (var i = 0; i < g.Options.Count; ++i)
            if (on.Contains(i))
                mask ^= g.Options[i].AttributeMask;
        return mask;
    }

    private static string Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return string.Empty;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? string.Empty,
            JsonValueKind.Number => v.GetRawText(),
            _                    => string.Empty,
        };
    }

    private static int Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            return (int)Math.Clamp(n, int.MinValue, int.MaxValue);
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s))
            return s;
        return 0;
    }

    /// <summary> For tests: the definition as one line of text per option ("group/option: n files"). </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var (g, o, on) in AllOptions())
            sb.Append(g?.Name ?? "Default").Append('/').Append(o.Name).Append(on ? " (on)" : "").Append(": ").Append(o.Files.Count).Append(" files\n");
        return sb.ToString();
    }
}
