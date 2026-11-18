using System.Text.Json;
using System.Text.Json.Nodes;
using ModCloset2.Detect;

namespace ModCloset2.OwnMod;

/// <summary> Where a mod keeps its settings: in meta.json alone, or in default_mod.json and group_*.json beside it. </summary>
public enum ModLayout : byte
{
    /// <summary> meta.json, default_mod.json and one group_*.json per option group (Penumbra's long-standing layout). </summary>
    Separate = 0,

    /// <summary> Everything in meta.json ("DefaultData" and "Groups"), as newer packs have it. </summary>
    Single = 1,
}

/// <summary>
/// A mod's settings as JSON nodes, exactly as written (fields this code does not know stay as they are), in the order
/// the detection reads them (<see cref="ModDefinition"/>): the default data, then the groups (group files sorted by name,
/// or the "Groups" array in order). Read only.
/// </summary>
internal sealed class ModJson
{
    private const long MaxJsonBytes = 32L << 20;

    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public ModLayout Layout { get; private init; }
    public JsonObject Meta { get; private init; } = new();

    /// <summary> The default data: "DefaultData" of meta.json, or default_mod.json (null when the mod has none). </summary>
    public JsonObject? Default { get; private init; }

    /// <summary> The option groups, each with the file it was read from ("" in the one-file layout). </summary>
    public List<(string File, JsonObject Node)> Groups { get; } = new();

    /// <summary> The settings files read (meta.json, default_mod.json, group_*.json), relative to the mod. </summary>
    public List<string> Files { get; } = new();

    /// <summary> Reads the settings, or says in one sentence for the user why it cannot. </summary>
    public static (ModJson? Json, string Problem) Read(FolderSource source)
    {
        var meta = Object(source, "meta.json", out var problem);
        if (meta is null)
            return (null, problem.Length > 0 ? problem : Loc.T("The mod's settings (meta.json) cannot be read."));

        // Penumbra's first layout kept its groups as an object of named groups with "OptionFiles"; Penumbra converts such
        // a mod when it loads it, so one still on disk like this has not been opened since.
        if (meta["Groups"] is JsonNode g && g is not JsonArray || meta["FileVersion"] is JsonValue v && v.TryGetValue<int>(out var version) && version < 1)
            return (null, Loc.T("This mod is still in an old layout. Open it once in Penumbra, then try again."));

        var groupFiles = source.Files
            .Where(f => !f.Contains('/') && f.StartsWith("group_", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var single = meta.ContainsKey("DefaultData") || meta.ContainsKey("Groups");
        if (single && (groupFiles.Count > 0 || source.Length("default_mod.json") >= 0))
            return (null, Loc.T("This mod keeps its settings twice, in two ways. Open it once in Penumbra, then try again."));

        if (single)
        {
            var json = new ModJson { Layout = ModLayout.Single, Meta = meta, Default = meta["DefaultData"] as JsonObject };
            json.Files.Add("meta.json");
            if (meta["Groups"] is JsonArray groups)
                foreach (var node in groups)
                {
                    if (node is not JsonObject group)
                        return (null, Loc.T("The mod's settings are damaged."));
                    json.Groups.Add((string.Empty, group));
                }

            return (json, string.Empty);
        }

        JsonObject? defaults = null;
        var files = new List<string> { "meta.json" };
        if (source.Length("default_mod.json") >= 0)
        {
            defaults = Object(source, "default_mod.json", out problem);
            if (defaults is null)
                return (null, problem);
            files.Add("default_mod.json");
        }

        var separate = new ModJson { Layout = ModLayout.Separate, Meta = meta, Default = defaults };
        separate.Files.AddRange(files);
        foreach (var file in groupFiles)
        {
            var group = Object(source, file, out problem);
            if (group is null)
                return (null, problem);
            separate.Groups.Add((file, group));
            separate.Files.Add(file);
        }

        return (separate, string.Empty);
    }

    private static JsonObject? Object(FolderSource source, string name, out string problem)
    {
        problem = string.Empty;
        var bytes = source.Read(name, MaxJsonBytes);
        if (bytes is null)
        {
            problem = Loc.T("The mod's settings cannot be read.");
            return null;
        }

        try
        {
            var span = bytes.AsSpan();
            if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
                span = span[3..];
            if (JsonNode.Parse(span, null, DocumentOptions) is JsonObject o)
                return o;
        }
        catch (JsonException)
        {
            // damaged: said below
        }

        problem = Loc.T("The mod's settings are damaged.");
        return null;
    }

    /// <summary> A JSON node as an element, for the detection's readers. </summary>
    public static JsonElement Element(JsonNode node)
    {
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }
}
