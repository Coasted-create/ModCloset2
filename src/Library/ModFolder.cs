using System.Text.Json;
using ModCloset2.Core;
using ModCloset2.Game;

namespace ModCloset2.Library;

/// <summary>
/// Read-only look at a Penumbra mod folder (port of the old ModFolder): where it is, its picture, author, tags, and its
/// definition in either Penumbra layout. Nothing here ever writes. No Dalamud types.
/// </summary>
public static class ModFolder
{
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".webp" };
    private static readonly string[] ConventionalNames = { "preview", "thumbnail", "thumb", "cover", "image", "screenshot", "icon" };
    private static readonly string[] ImageFolders = { "images", "image", "previews", "preview", "screenshots" };

    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary> Absolute folder of a mod, or null when it would leave the Penumbra mod root or does not exist. </summary>
    public static string? Resolve(string modRoot, string modDirectory)
    {
        if (string.IsNullOrWhiteSpace(modRoot) || string.IsNullOrWhiteSpace(modDirectory))
            return null;

        try
        {
            var root   = Path.GetFullPath(modRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var folder = Path.GetFullPath(Path.Combine(root, modDirectory));
            if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) || folder.Length <= root.Length)
                return null;

            return Directory.Exists(folder) ? folder : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The mod's display picture: the meta.json image, then preview / thumbnail / cover / … in the folder, then the first
    /// picture in an images / previews / screenshots sub folder. Pictures for which <paramref name="skip"/> says yes are
    /// passed over (pictures the old Mod Closet took). Null when there is none.
    /// </summary>
    public static string? FindImage(string folder, Func<string, bool>? skip = null)
    {
        bool Ok(string path)
            => skip is null || !skip(path);

        try
        {
            if (ReadMeta(folder, "Image") is { Length: > 0 } image)
            {
                var path = Path.IsPathRooted(image) ? image : Path.Combine(folder, image);
                if (IsImage(path) && File.Exists(path) && Ok(path))
                    return path;
            }

            foreach (var name in ConventionalNames)
                foreach (var ext in Extensions)
                {
                    var candidate = Path.Combine(folder, name + ext);
                    if (File.Exists(candidate) && Ok(candidate))
                        return candidate;
                }

            foreach (var sub in ImageFolders)
            {
                var dir = Path.Combine(folder, sub);
                if (Directory.Exists(dir) && Directory.EnumerateFiles(dir).Where(f => IsImage(f) && Ok(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } first)
                    return first;
            }
        }
        catch
        {
            // Unreadable folder: no picture.
        }

        return null;
    }

    /// <summary>
    /// Reads a text file of someone else's (a mod's, the old plugin's) without locking it: Penumbra must still be able to
    /// save, update or delete the mod while the closet reads it in the background.
    /// </summary>
    public static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static bool IsImage(string path)
        => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary> One string property of meta.json, or null. Never throws. </summary>
    private static string? ReadMeta(string folder, string property)
    {
        try
        {
            var meta = Path.Combine(folder, "meta.json");
            if (!File.Exists(meta))
                return null;

            using var doc = JsonDocument.Parse(ReadShared(meta), JsonOptions);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(property, out var v)
                && v.ValueKind == JsonValueKind.String
                    ? v.GetString()?.Trim()
                    : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A stamp that changes when the mod's definition changes: meta.json (new layout) or default_mod.json / group_*.json
    /// (old layout). Same formula as the old Catalog.
    /// </summary>
    public static string Stamp(string folder)
    {
        try
        {
            var meta   = new FileInfo(Path.Combine(folder, "meta.json"));
            var def    = new FileInfo(Path.Combine(folder, "default_mod.json"));
            var groups = Directory.EnumerateFiles(folder, "group_*.json").Select(f => new FileInfo(f)).ToList();
            return $"{(meta.Exists ? meta.LastWriteTimeUtc.Ticks : 0)}:{(meta.Exists ? meta.Length : 0)}:{(def.Exists ? def.LastWriteTimeUtc.Ticks : 0)}"
              + $":{groups.Count}:{(groups.Count > 0 ? groups.Max(g => g.LastWriteTimeUtc.Ticks) : 0)}";
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---- the definition ----

    /// <summary> One file list of a mod (default data, an option, or a combining group's container). </summary>
    public sealed record FileSet(List<string> Paths, List<Socket> EditSockets);

    public sealed record OptionDef(string Name, FileSet Files);

    /// <summary> One option group as written in the mod. <see cref="Containers"/>: a combining group's file lists. </summary>
    public sealed record GroupDef(string Name, string Type, long DefaultSettings, List<OptionDef> Options, List<FileSet> Containers, Socket? IdentifierSocket);

    public sealed record Definition(string Name, string Author, string? Image, List<string> Tags, FileSet Default, List<GroupDef> Groups);

    /// <summary>
    /// Reads a mod's definition from either Penumbra layout: everything inside meta.json ("DefaultData" + "Groups", the
    /// newer layout) or default_mod.json + group_*.json (the older one). Throws on unreadable files.
    /// </summary>
    public static Definition ReadDefinition(string folder)
    {
        string? Text(string file)
            => File.Exists(Path.Combine(folder, file)) ? ReadShared(Path.Combine(folder, file)) : null;

        var groupFiles = Directory.EnumerateFiles(folder, "group_*.json").Select(Path.GetFileName).OfType<string>().ToList();
        return ReadDefinition(Text, groupFiles);
    }

    /// <summary>
    /// Reads a mod's definition from file texts, wherever they come from (a folder, or a .pmp read in place by the tests).
    /// <paramref name="text"/> gives a file's text by name ("meta.json"), null when it is not there.
    /// </summary>
    public static Definition ReadDefinition(Func<string, string?> text, IEnumerable<string> groupFiles)
    {
        var name     = string.Empty;
        var author   = string.Empty;
        string? image = null;
        var tags     = new List<string>();
        var defaults = new FileSet(new List<string>(), new List<Socket>());
        var groups   = new List<GroupDef>();

        if (text("meta.json") is { } meta)
        {
            using var doc = JsonDocument.Parse(meta, JsonOptions);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                name   = Str(root, "Name");
                author = Str(root, "Author");
                image  = Str(root, "Image") is { Length: > 0 } i ? i : null;
                if (root.TryGetProperty("ModTags", out var mt) && mt.ValueKind == JsonValueKind.Array)
                    tags.AddRange(mt.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString() ?? string.Empty).Where(t => t.Length > 0));
                if (root.TryGetProperty("DefaultData", out var dd))
                    Add(defaults, ReadFiles(dd));
                if (root.TryGetProperty("Groups", out var gs) && gs.ValueKind == JsonValueKind.Array)
                    foreach (var g in gs.EnumerateArray())
                        if (ReadGroup(g) is { } group)
                            groups.Add(group);
            }
        }

        if (text("default_mod.json") is { } defaultMod)
        {
            using var doc = JsonDocument.Parse(defaultMod, JsonOptions);
            Add(defaults, ReadFiles(doc.RootElement));
        }

        foreach (var file in groupFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (text(file) is not { } group_)
                continue;
            using var doc = JsonDocument.Parse(group_, JsonOptions);
            if (ReadGroup(doc.RootElement) is { } group)
                groups.Add(group);
        }

        return new Definition(name, author, image, tags, defaults, groups);

        static void Add(FileSet into, FileSet from)
        {
            into.Paths.AddRange(from.Paths);
            into.EditSockets.AddRange(from.EditSockets);
        }
    }

    private static string Str(JsonElement e, string property)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? string.Empty : string.Empty;

    private static GroupDef? ReadGroup(JsonElement g)
    {
        if (g.ValueKind != JsonValueKind.Object)
            return null;

        var name = Str(g, "Name");
        var type = Str(g, "Type");
        var dflt = g.TryGetProperty("DefaultSettings", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var v) ? v : 0;
        var options = new List<OptionDef>();
        if (g.TryGetProperty("Options", out var os) && os.ValueKind == JsonValueKind.Array)
            foreach (var o in os.EnumerateArray())
                if (o.ValueKind == JsonValueKind.Object)
                    options.Add(new OptionDef(Str(o, "Name"), ReadFiles(o)));

        // Combining groups keep their files in "Containers" (one per combination of options), not in the options.
        var containers = new List<FileSet>();
        if (g.TryGetProperty("Containers", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var c in cs.EnumerateArray())
                containers.Add(ReadFiles(c));

        // IMC groups name the gear piece whose variant they change.
        Socket? identifier = null;
        if (g.TryGetProperty("Identifier", out var id) && id.ValueKind == JsonValueKind.Object)
        {
            var objectType = Str(id, "ObjectType");
            identifier = objectType is "Weapon" ? Socket.MainHand : GearSlots.SocketOf(Str(id, "EquipSlot")) is var s && s != Socket.None ? s : null;
        }

        return new GroupDef(name, type, dflt, options, containers, identifier);
    }

    /// <summary> Game paths (keys of "Files" and "FileSwaps") and the sockets of meta edits of one file list. </summary>
    private static FileSet ReadFiles(JsonElement root)
    {
        var set = new FileSet(new List<string>(), new List<Socket>());
        if (root.ValueKind != JsonValueKind.Object)
            return set;

        foreach (var name in new[] { "Files", "FileSwaps" })
            if (root.TryGetProperty(name, out var files) && files.ValueKind == JsonValueKind.Object)
                foreach (var f in files.EnumerateObject())
                    set.Paths.Add(f.Name);

        if (root.TryGetProperty("Manipulations", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray())
                if (EditSocket(m) is { } socket)
                    set.EditSockets.Add(socket);
        return set;
    }

    /// <summary>
    /// The socket a meta edit is about. EQP, EQDP, attributes and shapes name a gear "Slot", IMC an "EquipSlot"; EST names
    /// "Hair", "Face", "Body" (the top) or "Head"; GMP is the head piece's visor.
    /// </summary>
    private static Socket? EditSocket(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object)
            return null;
        var type = Str(m, "Type");
        if (!m.TryGetProperty("Manipulation", out var body) || body.ValueKind != JsonValueKind.Object)
            return null;

        var slot = Str(body, "EquipSlot") is { Length: > 0 } es ? es : Str(body, "Slot");
        if (type.Equals("Est", StringComparison.OrdinalIgnoreCase))
            return slot switch
            {
                "Hair" => Socket.Hair,
                "Face" => Socket.Face,
                "Body" => Socket.Body,
                "Head" => Socket.Head,
                _      => null,
            };
        if (type.Equals("Gmp", StringComparison.OrdinalIgnoreCase))
            return Socket.Head;
        if (Str(body, "ObjectType") is "Weapon")
            return Socket.MainHand;
        return GearSlots.SocketOf(slot) is var s && s != Socket.None ? s : null;
    }

    // ---- facts ----

    /// <summary>
    /// Everything the closet wants to know from a mod folder, read once. Never throws: a broken definition gives facts
    /// with what could be read and <see cref="ModFacts.Broken"/> set.
    /// </summary>
    /// <param name="skipPicture"> Pictures to pass over when looking for the mod's own (the old plugin's). </param>
    /// <param name="fallbackPicture"> The picture when the mod has none of its own (one the old plugin took). </param>
    public static ModFacts ReadFacts(string folder, Func<string, bool>? skipPicture = null, Func<string?>? fallbackPicture = null)
    {
        Definition? def    = null;
        var broken         = false;
        try
        {
            def = ReadDefinition(folder);
        }
        catch
        {
            broken = true;
        }

        DateTime added;
        try
        {
            added = Directory.GetCreationTimeUtc(folder);
        }
        catch
        {
            added = DateTime.MinValue;
        }

        return FactsOf(def, broken, folder, FindImage(folder, skipPicture) ?? fallbackPicture?.Invoke(), added, Stamp(folder));
    }

    /// <summary> The facts of a definition already read (null: it could not be read, <paramref name="broken"/> says so). </summary>
    public static ModFacts FactsOf(Definition? def, bool broken, string folder, string? picture, DateTime added, string stamp)
    {
        var codes     = new Dictionary<Socket, SocketCodes>();
        var gearSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups    = new List<GroupInfo>();
        var dflt      = new HashSet<Socket>();
        if (def is not null)
        {
            foreach (var path in def.Default.Paths)
                dflt.Add(Note(path));
            foreach (var s in def.Default.EditSockets)
                dflt.Add(s);

            foreach (var g in def.Groups)
            {
                var options = new List<OptionInfo>();
                var sockets = new HashSet<Socket>();
                foreach (var o in g.Options)
                {
                    var info = new OptionInfo { Name = o.Name, Sockets = SocketsOf(o.Files), RaceCodes = OptionCodes(o.Files.Paths), Numbers = NumbersOf(o.Files.Paths) };
                    foreach (var p in o.Files.Paths)
                        Note(p);
                    sockets.UnionWith(info.Sockets);
                    options.Add(info);
                }

                foreach (var c in g.Containers)
                {
                    foreach (var p in c.Paths)
                        Note(p);
                    sockets.UnionWith(SocketsOf(c));
                }

                if (g.IdentifierSocket is { } id)
                    sockets.Add(id);

                // A combining or IMC group: its options change what the group changes (they have no files of their own).
                if (g.Containers.Count > 0 || g.IdentifierSocket is not null)
                    foreach (var o in options.Where(o => o.Sockets.Count == 0))
                        o.Sockets.UnionWith(sockets);

                var group = new GroupInfo { Name = g.Name, Type = g.Type, Default = g.DefaultSettings, Options = options, Sockets = sockets };
                group.IsBody = IsBodyGroup(group);
                group.IsRace = IsRaceGroup(group);
                groups.Add(group);
            }
        }

        // the user's tags in Penumbra live in Penumbra's own data; meta.json has the mod author's tags
        return new ModFacts
        {
            Folder         = folder,
            Author         = def?.Author ?? string.Empty,
            Picture        = picture,
            Added          = added,
            Stamp          = stamp,
            Tags           = def?.Tags ?? new List<string>(),
            Groups         = groups,
            Codes          = codes,
            GearSlots      = gearSlots,
            DefaultSockets = dflt,
            Broken         = broken,
            Files          = def is null ? new Dictionary<Socket, IReadOnlyList<string>>() : FilesBySocket(def),
        };

        Socket Note(string path)
        {
            var socket = GamePaths.SocketOf(path);
            if (GamePaths.SlotOf(path) is { } slot)
                gearSlots.Add(slot);
            var code = GamePaths.RaceCodeOf(path);
            if (code != 0 && socket != Socket.Other)
            {
                if (!codes.TryGetValue(socket, out var c))
                    codes[socket] = c = new SocketCodes();
                (GamePaths.IsModel(path) ? c.Models : c.Other).Add(code);
            }

            return socket;
        }
    }

    /// <summary> At most this many file names per socket in <see cref="ModFacts.Files"/>. </summary>
    public const int MaxFilesPerSocket = 16;

    /// <summary> The names of the files of each socket (paths inside the game, the part after the last slash), sorted. </summary>
    public static Dictionary<Socket, IReadOnlyList<string>> FilesBySocket(Definition def)
    {
        var paths = def.Default.Paths
            .Concat(def.Groups.SelectMany(g => g.Options.SelectMany(o => o.Files.Paths).Concat(g.Containers.SelectMany(c => c.Paths))));
        return paths.GroupBy(GamePaths.SocketOf)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.Replace('\\', '/')).Select(p => p[(p.LastIndexOf('/') + 1)..].ToLowerInvariant())
                .Where(n => n.Length > 0).Distinct().Order(StringComparer.Ordinal).Take(MaxFilesPerSocket).ToList());
    }

    private static HashSet<Socket> SocketsOf(FileSet files)
    {
        var set = new HashSet<Socket>();
        foreach (var p in files.Paths)
            set.Add(GamePaths.SocketOf(p));
        set.UnionWith(files.EditSockets);
        return set;
    }

    /// <summary> The hair, face, tail and ear numbers an option's files are for. </summary>
    private static Dictionary<Socket, HashSet<int>> NumbersOf(IEnumerable<string> paths)
    {
        var result = new Dictionary<Socket, HashSet<int>>();
        foreach (var p in paths)
            if (GamePaths.NumberOf(p) is var (socket, number))
            {
                if (!result.TryGetValue(socket, out var set))
                    result[socket] = set = new HashSet<int>();
                set.Add(number);
            }

        return result;
    }

    /// <summary> Race codes of one option, models first (like the old ModFolder.OptionCodes). </summary>
    private static HashSet<int> OptionCodes(IEnumerable<string> paths)
    {
        var models = new HashSet<int>();
        var other  = new HashSet<int>();
        foreach (var path in paths)
            if (GamePaths.RaceCodeOf(path) is var code and not 0)
                (GamePaths.IsModel(path) ? models : other).Add(code);
        return models.Count > 0 ? models : other;
    }

    /// <summary>
    /// A single-choice group that chooses between bodies (the test of the old BodyFit.OptionsForBody): half its options
    /// name a body, or the group's name does. Size-only groups ("SIZE - socks": "watermelon crusher", "mini") count too
    /// when the group name says size.
    /// </summary>
    public static bool IsBodyGroup(GroupInfo group)
    {
        if (group.Multi || group.Options.Count < 2)
            return false;
        var withBodies = group.Options.Count(o => BodyFit.NamesIn(o.Name).Count > 0);
        if (withBodies * 2 >= group.Options.Count || BodyFit.NamesIn(group.Name).Count > 0)
            return true;
        return BodyFit.SaysSize(group.Name);
    }

    /// <summary> A single-choice group whose options are made for different race codes (a "Male / Female" choice). </summary>
    public static bool IsRaceGroup(GroupInfo group)
    {
        if (group.Multi || group.Options.Count < 2)
            return false;
        var distinct = group.Options.Where(o => o.RaceCodes.Count > 0).Select(o => string.Join(",", o.RaceCodes.Order())).Distinct().Count();
        return distinct >= 2;
    }
}
