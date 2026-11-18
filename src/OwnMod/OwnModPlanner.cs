using System.Text.Json.Nodes;
using ModCloset2.Core;
using ModCloset2.Detect;

namespace ModCloset2.OwnMod;

/// <summary> One file the copy takes, as it was on disk when the plan was made. </summary>
/// <param name="Relative"> Path relative to the mod's folder, with forward slashes, spelled as on disk. </param>
public sealed record CopiedFile(string Relative, long Length, DateTime WrittenUtc);

/// <summary> Everything <see cref="OwnModMaker.Write"/> needs, worked out by <see cref="OwnModMaker.Plan"/>. </summary>
public sealed class OwnModDetails : OwnModContent
{
    /// <summary> Full path of the mod's folder. </summary>
    public string ModFolder { get; init; } = string.Empty;

    /// <summary> The folder's own name (for the note in the copy). </summary>
    public string FolderName { get; init; } = string.Empty;

    public ModLayout Layout { get; init; }
    public IReadOnlyList<Socket> Sockets { get; init; } = Array.Empty<Socket>();

    /// <summary> The files the copy takes, each once. </summary>
    public IReadOnlyList<CopiedFile> Copy { get; init; } = Array.Empty<CopiedFile>();

    /// <summary> The mod's settings files as they were (the write stops when one changed). </summary>
    public IReadOnlyList<CopiedFile> Settings { get; init; } = Array.Empty<CopiedFile>();

    /// <summary> meta.json as it is (in the one-file layout with only the kept default data and groups). </summary>
    public JsonObject Meta { get; init; } = new();

    /// <summary> The default data with only the kept entries (null in the one-file layout, or when the mod has none). </summary>
    public JsonObject? Default { get; init; }

    /// <summary> The groups that come along, with only the kept entries, in the old order (separate layout). </summary>
    public IReadOnlyList<JsonObject> GroupNodes { get; init; } = Array.Empty<JsonObject>();

    /// <summary> The longest full path among the old mod's files. </summary>
    public int LongestPath { get; init; }

    /// <summary>
    /// Files of the character's own body (skin, face, hair, tail, ears) that a copied model or material names and the mod
    /// provides, by game path. They are not copied: they belong to the body, not to the piece (see the report).
    /// </summary>
    public IReadOnlyList<string> BodyFilesLeftOut { get; init; } = Array.Empty<string>();

    /// <summary> <see cref="Hands.Both"/>: an ordinary copy; else the hand the copied ring is worn on (<see cref="OwnModAsk.ToHand"/>). </summary>
    public Hands ToHand { get; init; }

    /// <summary> Game paths of the entries that come along, by container ("" = default data, else "group/option"), for tests. </summary>
    public IReadOnlyList<string> Kept { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Works out what a card's thing copied out of its mod holds (brief 10, "Plan"). The entries of the asked slots are found
/// the way the detection finds them (<see cref="GamePaths.Classify"/>, <see cref="GamePaths.SocketOf"/>, swap chains,
/// the files of the item's set), then everything they need that the mod provides: a model's materials (by file name, so
/// every variant and race of it comes along), a material's textures (by game path), a swap's target. Read only.
/// </summary>
internal sealed class OwnModPlanner
{
    private enum EntryKind : byte { File, Swap, Meta }

    private sealed class Entry
    {
        public int Container;
        public EntryKind Kind;
        public string Key = string.Empty;   // files and swaps: the key as written; metas: the index in the array
        public string Game = string.Empty;  // normalised game path (files, swaps)
        public string Local = string.Empty; // files: the local path as written; swaps: the normalised target
        public PathInfo Info;
        public MetaEdit? Meta;
        public string Shape = string.Empty; // SHP: the shape key
        public int Index;
    }

    private sealed class Container
    {
        public int Group = -1;  // -1: the default data
        public JsonObject? Node;
        public string Name = string.Empty;
    }

    private readonly OwnModAsk _ask;
    private readonly HashSet<Socket> _sockets;
    private FolderSource _source = null!;
    private ModJson _json = null!;
    private readonly List<Container> _containers = new();
    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, List<Entry>> _filesByGame = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entry>> _swapsByGame = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entry>> _filesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Entry>> _swapsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelNames?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>?> _materials = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _bodyLeftOut = new(StringComparer.Ordinal);

    public OwnModPlanner(OwnModAsk ask)
    {
        _ask     = ask;
        _sockets = ask.Sockets.ToHashSet();
    }

    private static OwnModDetails No(string why, string name = "", string author = "")
        => new() { Possible = false, Problem = why, ModName = name, Author = author };

    public OwnModDetails Plan()
    {
        if (_sockets.Count == 0 || _sockets.Any(s => !GamePaths.IsGear(s)))
            return No(Loc.T("Only clothes and jewellery can be made a mod of their own for now."));
        if (string.IsNullOrWhiteSpace(_ask.ModFolder) || !Directory.Exists(_ask.ModFolder))
            return No(Loc.T("The mod's folder cannot be read."));

        _source = new FolderSource(_ask.ModFolder);
        var (json, problem) = ModJson.Read(_source);
        if (json is null)
            return No(problem);
        _json = json;

        var name   = Str(json.Meta["Name"]);
        var author = Str(json.Meta["Author"]);
        if (name.Length == 0)
            name = _source.Name;

        if (!Collect(out problem))
            return No(problem, name, author);

        // ---- the thing: its entries, then everything they need
        var kept = new HashSet<Entry>();
        var wholeGroups = new HashSet<int>();
        foreach (var e in _entries)
            if (Seed(e))
                kept.Add(e);
        for (var gi = 0; gi < json.Groups.Count; ++gi)
            if (ImcSocket(json.Groups[gi].Node) is { } s && _sockets.Contains(s))
                wholeGroups.Add(gi);

        // Files that name only the item's set (a texture without a slot in its name, a skeleton, an effect) join the thing,
        // as the detection joins them to the parts of that set.
        var sets = kept.Where(e => e.Kind != EntryKind.Meta && e.Info.Set > 0 && e.Info.SetType != '\0').Select(e => (e.Info.SetType, e.Info.Set)).ToHashSet();
        foreach (var e in _entries)
            if (e.Kind == EntryKind.File && e.Info.Socket == Socket.None && !e.Info.Custom && e.Info.Set > 0 && sets.Contains((e.Info.SetType, e.Info.Set)))
                kept.Add(e);

        // A skeleton change never comes without its skeleton (review 5): every file the mod provides in the folder of the
        // skeleton an EST change of the thing names (for every race; there is nothing else in such a folder).
        foreach (var e in kept.Where(e => e.Kind == EntryKind.Meta && e.Meta!.Type == "est").ToList())
            if (SkeletonFolder(e) is { } folder)
                foreach (var f in _entries.Where(f => f.Kind is EntryKind.File or EntryKind.Swap && IsInSkeletonFolder(f.Game, folder)))
                    kept.Add(f);

        var thingNames = Close(kept);
        var bodyLeftOut = _bodyLeftOut.Order(StringComparer.Ordinal).ToList();

        // Attribute and shape edits for all slots go with the thing when one of its models carries that attribute or shape.
        foreach (var e in _entries.Where(e => e.Kind == EntryKind.Meta && IsSlotless(e.Meta!)))
            if (e.Meta!.Type == "atr" ? thingNames.Attributes.Contains(e.Meta.Attribute) : thingNames.Shapes.Contains(e.Shape))
                kept.Add(e);

        if (!kept.Any(e => e.Kind != EntryKind.Meta) && wholeGroups.Count == 0 && !kept.Any())
            return No(Loc.T("This piece has nothing of its own in the mod."), name, author);
        if (!kept.Any(e => e.Kind != EntryKind.Meta))
            return No(Loc.T("This piece has no files of its own in the mod, only settings."), name, author);

        // ---- a ring for the other hand: its ring models move to the other finger's game paths
        var handKeys = new HashSet<string>(StringComparer.Ordinal);
        if (_ask.ToHand != Hands.Both)
        {
            if (!_sockets.Contains(Socket.Rings))
                return No(Loc.T("Only rings can be copied for the other hand."), name, author);
            _to = _ask.ToHand == Hands.Left ? 'l' : 'r';
            _from = _to == 'l' ? 'r' : 'l';
            var ring = kept.Where(e => e.Kind != EntryKind.Meta && e.Info.Socket == Socket.Rings).ToList();
            var fingers = ring.Select(e => GamePaths.FingerOfPath(e.Game))
                .Concat(kept.Where(e => e.Kind == EntryKind.Meta).Select(e => GamePaths.Finger(e.Meta!.Slot)))
                .Concat(wholeGroups.Select(gi => FingerOfGroup(json.Groups[gi].Node)))
                .Where(c => c is not null).Select(c => c!.Value).ToHashSet();
            if (fingers.Contains(_to))
                return No(_to == 'l' ? Loc.T("This ring is already worn on the left hand.") : Loc.T("This ring is already worn on the right hand."), name, author);
            handKeys = ring.Where(e => GamePaths.FingerOfPath(e.Game) == _from && e.Game.EndsWith(".mdl", StringComparison.Ordinal)).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
            if (handKeys.Count == 0)
                return No(Loc.T("This piece has no ring of its own to copy for the other hand."), name, author);
        }

        // ---- safety and completeness of what is copied
        foreach (var e in kept.Where(e => e.Kind == EntryKind.File))
            if (!IsSafe(e.Local))
                return No(Loc.T("The mod names a file outside its own folder. Nothing can be copied out of it."), name, author);

        var copy = new Dictionary<string, CopiedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in kept.Where(e => e.Kind == EntryKind.File))
        {
            var key = ModSource.Normalize(e.Local);
            if (copy.ContainsKey(key))
                continue;
            if (Stamp(e.Local) is not { } stamp)
                return No(Loc.F("A file the copy needs is missing ({0}).", ModSource.Normalize(e.Local)), name, author);
            copy[key] = stamp;
        }

        // A preview picture inside the mod comes along too.
        if (Str(json.Meta["Image"]) is { Length: > 0 } image && IsSafe(image) && Stamp(image) is { } picture)
            copy.TryAdd(ModSource.Normalize(image), picture);

        // ---- the rest of the mod: what else it holds, and which copied files it uses too
        var rest = new HashSet<Entry>(_entries.Where(e => e.Kind != EntryKind.Meta && !kept.Contains(e) && e.Info.Socket != Socket.None && !_sockets.Contains(e.Info.Socket)));
        var hasMore = rest.Count > 0
                   || _entries.Any(e => e.Kind == EntryKind.Meta && !kept.Contains(e) && MetaSocket(e.Meta!) is { } s && s != Socket.None && !_sockets.Contains(s))
                   || Enumerable.Range(0, json.Groups.Count).Any(gi => !wholeGroups.Contains(gi) && ImcSocket(json.Groups[gi].Node) is { } s && s != Socket.None);
        var restNames = Close(rest);
        var restLocals = rest.Where(e => e.Kind == EntryKind.File).Select(e => ModSource.Normalize(e.Local)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shared = copy.Keys.Count(restLocals.Contains);

        var leftBehind = _entries.Count(e => e.Kind == EntryKind.Meta && !kept.Contains(e) && MetaSocket(e.Meta!) == Socket.None
            && !(IsSlotless(e.Meta!) && (e.Meta!.Type == "atr" ? restNames.Attributes.Contains(e.Meta.Attribute) : restNames.Shapes.Contains(e.Shape))));

        // ---- the settings of the copy
        var keptByContainer = kept.GroupBy(e => e.Container).ToDictionary(g => g.Key, g => g.ToList());
        var groups = new List<JsonObject>();
        var groupNames = new List<string>();
        for (var gi = 0; gi < json.Groups.Count; ++gi)
        {
            var mine = _containers.Select((c, i) => (c, i)).Where(x => x.c.Group == gi).Select(x => x.i).ToList();
            if (!wholeGroups.Contains(gi) && !mine.Any(keptByContainer.ContainsKey))
                continue;
            var node = (JsonObject)json.Groups[gi].Node.DeepClone();
            if (!wholeGroups.Contains(gi))
            {
                var list = IsCombining(node) ? node["Containers"] as JsonArray : node["Options"] as JsonArray;
                for (var k = 0; k < mine.Count && list is not null && k < list.Count; ++k)
                    if (list[k] is JsonObject option)
                    {
                        Filter(option, keptByContainer.GetValueOrDefault(mine[k]));
                        OtherHand(option, handKeys);
                    }
            }
            else if (_ask.ToHand != Hands.Both && FingerOfGroup(node) == _from && node["Identifier"] is JsonObject id)
                SwapFingerSlot(id);

            ApplyChoice(node, json.Groups[gi].Node);
            groups.Add(node);
            groupNames.Add(Str(node["Name"]));
        }

        JsonObject? defaults = null;
        if (json.Default is not null)
        {
            defaults = (JsonObject)json.Default.DeepClone();
            Filter(defaults, keptByContainer.GetValueOrDefault(0));
            OtherHand(defaults, handKeys);
        }

        var meta = (JsonObject)json.Meta.DeepClone();
        if (json.Layout == ModLayout.Single)
        {
            if (defaults is not null)
                meta["DefaultData"] = defaults;
            meta["Groups"] = new JsonArray(groups.Select(g => (JsonNode)g).ToArray());
            defaults = null;
        }

        var settings = json.Files.Select(f => Stamp(f)).Where(f => f is not null).Select(f => f!).ToList();
        var root = _source.Root;
        var longest = _source.Files.Count == 0 ? root.Length : _source.Files.Max(f => root.Length + 1 + f.Length);

        return new OwnModDetails
        {
            Possible    = true,
            ModName     = name,
            Author      = author,
            Files       = copy.Count,
            Bytes       = copy.Values.Sum(c => c.Length),
            Groups      = groupNames,
            Shared      = shared,
            ModHasMore  = hasMore,
            AuthorsLine = Names.AuthorsLine(Str(json.Meta["Description"]), Str(json.Meta["Website"]), name),
            LeftBehind  = leftBehind,
            ModFolder   = root,
            FolderName  = _source.Name,
            Layout      = json.Layout,
            Sockets     = _sockets.Order().ToList(),
            Copy        = copy.Values.OrderBy(c => c.Relative, StringComparer.OrdinalIgnoreCase).ToList(),
            Settings    = settings,
            Meta        = meta,
            Default     = json.Layout == ModLayout.Separate ? defaults : null,
            GroupNodes  = json.Layout == ModLayout.Separate ? groups : Array.Empty<JsonObject>(),
            LongestPath = longest,
            ToHand      = _ask.ToHand,
            BodyFilesLeftOut = bodyLeftOut,
            Kept        = kept.Where(e => e.Kind != EntryKind.Meta).Select(e => _containers[e.Container].Name + "|" + e.Game).Order(StringComparer.Ordinal).ToList(),
        };
    }

    // ------------------------------------------------------------------ reading the entries

    private bool Collect(out string problem)
    {
        problem = string.Empty;
        _containers.Add(new Container { Group = -1, Node = _json.Default, Name = string.Empty });
        for (var gi = 0; gi < _json.Groups.Count; ++gi)
        {
            var g = _json.Groups[gi].Node;
            var list = IsCombining(g) ? g["Containers"] : g["Options"];
            if (list is null)
                continue;
            if (list is not JsonArray options)
            {
                problem = Loc.T("The mod's settings are damaged.");
                return false;
            }

            for (var oi = 0; oi < options.Count; ++oi)
                _containers.Add(new Container { Group = gi, Node = options[oi] as JsonObject, Name = Str(g["Name"]) + "/" + Str(options[oi]?["Name"]) });
        }

        for (var ci = 0; ci < _containers.Count; ++ci)
        {
            var node = _containers[ci].Node;
            if (node is null)
                continue;

            if (node["Files"] is JsonObject files)
                foreach (var (key, value) in files)
                    if (value is JsonValue v && v.TryGetValue<string>(out var local))
                        Add(new Entry { Container = ci, Kind = EntryKind.File, Key = key, Game = ModDefinition.NormalizeGamePath(key), Local = local });

            if (node["FileSwaps"] is JsonObject swaps)
                foreach (var (key, value) in swaps)
                    if (value is JsonValue v && v.TryGetValue<string>(out var target))
                        Add(new Entry { Container = ci, Kind = EntryKind.Swap, Key = key, Game = ModDefinition.NormalizeGamePath(key), Local = ModDefinition.NormalizeGamePath(target) });

            if (node["Manipulations"] is JsonArray metas)
                for (var i = 0; i < metas.Count; ++i)
                    if (metas[i] is JsonObject change && ModDefinition.MetaOf(ModJson.Element(change)) is { } meta)
                        _entries.Add(new Entry
                        {
                            Container = ci, Kind = EntryKind.Meta, Index = i, Key = i.ToString(), Meta = meta,
                            Shape = Str(change["Manipulation"]?["Shape"]),
                        });
        }

        return true;
    }

    private void Add(Entry e)
    {
        e.Info = GamePaths.Classify(e.Game);
        _entries.Add(e);
        var byGame = e.Kind == EntryKind.File ? _filesByGame : _swapsByGame;
        var byName = e.Kind == EntryKind.File ? _filesByName : _swapsByName;
        Index(byGame, e.Game, e);
        Index(byName, FileName(e.Game), e);
    }

    private static void Index(Dictionary<string, List<Entry>> map, string key, Entry e)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = new List<Entry>();
        list.Add(e);
    }

    // ------------------------------------------------------------------ what belongs to the thing

    private bool Seed(Entry e)
        => e.Kind == EntryKind.Meta
            ? MetaSocket(e.Meta!) is { } s && _sockets.Contains(s)
            : _sockets.Contains(e.Info.Socket);

    /// <summary>
    /// The slot a meta change belongs to: the detection's <see cref="GamePaths.SocketOf"/> for EQP, EQDP, IMC, EST and GMP;
    /// attribute and shape edits (ATR, SHP) by their slot, None when they are for all slots (then the models decide);
    /// None for everything that names no slot (race scaling, the global EQP switches, attachment points).
    /// </summary>
    private static Socket? MetaSocket(MetaEdit m)
        => m.Type is "atr" or "shp" ? GamePaths.AttributeSocket(m.Slot) ?? Socket.None : GamePaths.SocketOf(m);

    private static bool IsSlotless(MetaEdit m)
        => m.Type is "atr" or "shp" && GamePaths.AttributeSocket(m.Slot) == Socket.None;

    private static Socket? ImcSocket(JsonObject group)
        => Str(group["Type"]) == "Imc" && group["Identifier"] is JsonObject id ? GamePaths.ImcSocket(ModDefinition.ImcIdentity(ModJson.Element(id))) : null;

    /// <summary>
    /// Follows what the entries need, as far as the mod provides it: a model's materials, a material's textures, a swap's
    /// target. Adds to <paramref name="set"/> until nothing new comes; returns the attributes and shapes of its models.
    /// </summary>
    private (HashSet<string> Attributes, HashSet<string> Shapes) Close(HashSet<Entry> set)
    {
        var attributes = new HashSet<string>(StringComparer.Ordinal);
        var shapes     = new HashSet<string>(StringComparer.Ordinal);
        var work = new Queue<Entry>(set);
        void Take(IEnumerable<Entry>? found)
        {
            if (found is null)
                return;
            foreach (var f in found)
            {
                // The character's own body (a gear model's skin material, its textures) is the body's, not the piece's:
                // the copy relies on the skin the user wears, as the game does for the item itself.
                if (GamePaths.TiesRace(f.Info.Socket))
                {
                    _bodyLeftOut.Add(f.Game);
                    continue;
                }

                if (set.Add(f))
                    work.Enqueue(f);
            }
        }

        while (work.TryDequeue(out var e))
        {
            if (e.Kind == EntryKind.Swap)
            {
                // A swap shows another path: when the mod provides that path (a file, or a swap again), it comes along.
                Take(_filesByGame.GetValueOrDefault(e.Local));
                Take(_swapsByGame.GetValueOrDefault(e.Local));
                continue;
            }

            if (e.Kind != EntryKind.File || !IsSafe(e.Local))
                continue;

            if (e.Game.EndsWith(".mdl", StringComparison.Ordinal))
            {
                if (ModelOf(e.Local) is not { } names)
                    continue;
                attributes.UnionWith(names.Attributes);
                shapes.UnionWith(names.Shapes);
                foreach (var material in names.Materials)
                {
                    var file = FileName(ModDefinition.NormalizeGamePath(material));
                    Take(_filesByName.GetValueOrDefault(file));
                    Take(_swapsByName.GetValueOrDefault(file));
                }
            }
            else if (e.Game.EndsWith(".mtrl", StringComparison.Ordinal))
            {
                foreach (var texture in TexturesOf(e.Local) ?? Array.Empty<string>())
                {
                    Take(_filesByGame.GetValueOrDefault(texture));
                    Take(_swapsByGame.GetValueOrDefault(texture));
                }
            }
        }

        return (attributes, shapes);
    }

    private ModelNames? ModelOf(string local)
    {
        var key = ModSource.Normalize(local);
        if (!_models.TryGetValue(key, out var names))
            _models[key] = names = _source.FullPath(local) is { } full ? References.Model(full) : null;
        return names;
    }

    private IReadOnlyList<string>? TexturesOf(string local)
    {
        var key = ModSource.Normalize(local);
        if (!_materials.TryGetValue(key, out var textures))
            _materials[key] = textures = _source.FullPath(local) is { } full ? References.MaterialTextures(full) : null;
        return textures;
    }

    // ------------------------------------------------------------------ the copy's settings

    /// <summary> Keeps only the given entries in an option (or the default data); every other field stays as it is. </summary>
    private static void Filter(JsonObject option, List<Entry>? keep)
    {
        var files = keep?.Where(e => e.Kind == EntryKind.File).Select(e => e.Key).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        var swaps = keep?.Where(e => e.Kind == EntryKind.Swap).Select(e => e.Key).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        var metas = keep?.Where(e => e.Kind == EntryKind.Meta).Select(e => e.Index).ToHashSet() ?? new HashSet<int>();

        if (option["Files"] is JsonObject f)
            foreach (var key in f.Select(p => p.Key).Where(k => !files.Contains(k)).ToList())
                f.Remove(key);
        if (option["FileSwaps"] is JsonObject s)
            foreach (var key in s.Select(p => p.Key).Where(k => !swaps.Contains(k)).ToList())
                s.Remove(key);
        if (option["Manipulations"] is JsonArray m)
            for (var i = m.Count - 1; i >= 0; --i)
                if (!metas.Contains(i))
                    m.RemoveAt(i);
    }

    /// <summary>
    /// The copy starts with the options that are on for the card now (<see cref="OwnModAsk.Chosen"/>), else the mod's own
    /// defaults. A single-choice group never starts on an option the copy emptied (it held only other pieces' files)
    /// while another option kept something (review 5): then the first option that kept something. An option that was
    /// empty in the mod already (the author's "none") is a real choice and stays one.
    /// </summary>
    private void ApplyChoice(JsonObject group, JsonObject original)
    {
        var name = Str(group["Name"]);
        var type = Str(group["Type"]);
        var single = type is "Single" or "";
        var options = group["Options"] as JsonArray;
        if (options is null)
            return;

        var picked = new List<int>();
        if (_ask.Chosen.TryGetValue(name, out var chosen) && chosen.Count > 0)
            for (var i = 0; i < options.Count; ++i)
                if (chosen.Contains(Str(options[i]?["Name"])))
                    picked.Add(i);

        if (!single)
        {
            if (picked.Count > 0)
                group["DefaultSettings"] = picked.Where(i => i < 63).Aggregate(0L, (mask, i) => mask | 1L << i);
            return;
        }

        var start = picked.Count > 0 ? picked[0] : group["DefaultSettings"] is JsonValue v && v.TryGetValue<long>(out var d) && d >= 0 && d < options.Count ? (int)d : 0;
        if (start < options.Count && Emptied(options, original["Options"] as JsonArray, start))
        {
            var first = Enumerable.Range(0, options.Count).FirstOrDefault(i => HoldsSomething(options[i] as JsonObject), -1);
            if (first >= 0)
                start = first;
        }

        group["DefaultSettings"] = start;
    }

    private static bool Emptied(JsonArray options, JsonArray? original, int index)
        => !HoldsSomething(options[index] as JsonObject) && HoldsSomething(original is not null && index < original.Count ? original[index] as JsonObject : null);

    private static bool HoldsSomething(JsonObject? option)
        => option is not null && (option["Files"] is JsonObject { Count: > 0 } || option["FileSwaps"] is JsonObject { Count: > 0 } || option["Manipulations"] is JsonArray { Count: > 0 });

    /// <summary>
    /// The skeleton folder an EST change names: "skeleton/top/t0123/" for the body, "skeleton/met/m0123/" for the head
    /// (Penumbra's layout under chara/human/cXXXX/). Null for other slots or without a number.
    /// </summary>
    private string? SkeletonFolder(Entry e)
    {
        var change = _containers[e.Container].Node?["Manipulations"]?.AsArray()[e.Index]?["Manipulation"];
        var value = change?["Entry"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
        if (value <= 0)
            return null;
        return e.Meta!.Slot switch
        {
            "body" => $"/skeleton/top/t{value:D4}/",
            "head" => $"/skeleton/met/m{value:D4}/",
            _      => null,
        };
    }

    private static bool IsInSkeletonFolder(string gamePath, string folder)
        => gamePath.StartsWith("chara/human/c", StringComparison.Ordinal) && gamePath.Contains(folder, StringComparison.Ordinal);

    // ------------------------------------------------------------------ helpers

    private CopiedFile? Stamp(string relative)
    {
        if (_source.FullPath(relative) is not { } full)
            return null;
        try
        {
            var info = new FileInfo(full);
            return new CopiedFile(ModSource.Normalize(Path.GetRelativePath(_source.Root, full)), info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary> A local path that stays in the mod's folder: no "..", no drive, no leading slash (<see cref="ModSource.IsSafe"/>, stricter). </summary>
    internal static bool IsSafe(string local)
        => ModSource.IsSafe(local) && local.TrimStart().Length > 0 && local.TrimStart()[0] is not ('/' or '\\');

    private char _from = 'r';
    private char _to = 'l';

    /// <summary>
    /// The copy for the other hand, in one option: the ring models' game paths go to the other finger ("…_rir.mdl" →
    /// "…_ril.mdl"), and meta changes of the finger slot name the other finger. Materials and textures keep their game
    /// paths: the game finds a ring's materials by the names inside its model, which do not change.
    /// </summary>
    private void OtherHand(JsonObject container, HashSet<string> modelKeys)
    {
        if (_ask.ToHand == Hands.Both)
            return;
        foreach (var section in new[] { "Files", "FileSwaps" })
            if (container[section] is JsonObject map && map.Any(p => modelKeys.Contains(p.Key)))
            {
                var entries = map.Select(p => (p.Key, Value: p.Value?.DeepClone())).ToList();
                map.Clear();
                foreach (var (key, value) in entries)
                    map[modelKeys.Contains(key) ? RenameFinger(key, _to) : key] = value;
            }

        if (container["Manipulations"] is JsonArray metas)
            foreach (var m in metas.OfType<JsonObject>())
                if (m["Manipulation"] is JsonObject body)
                    SwapFingerSlot(body);
    }

    /// <summary> "RFinger" ↔ "LFinger" in a meta change's or an IMC group's slot field, when it names the finger the copy leaves. </summary>
    private void SwapFingerSlot(JsonObject body)
    {
        foreach (var field in new[] { "Slot", "EquipSlot" })
            if (Str(body[field]) is { Length: > 0 } slot && GamePaths.Finger(slot.ToLowerInvariant()) == _from)
                body[field] = _to == 'l' ? "LFinger" : "RFinger";
    }

    private static char? FingerOfGroup(JsonObject group)
        => Str(group["Type"]) == "Imc" && group["Identifier"] is JsonObject id ? GamePaths.Finger(ModDefinition.ImcIdentity(ModJson.Element(id)).Slot) : null;

    /// <summary> "chara/accessory/a0001/model/c0201a0001_rir.mdl" → "…_ril.mdl" (only the file name's finger). </summary>
    internal static string RenameFinger(string gamePath, char to)
    {
        var cut = gamePath.LastIndexOfAny(['/', '\\']) + 1;
        var name = System.Text.RegularExpressions.Regex.Replace(gamePath[cut..], @"(?i)(c\d{4}a\d{4}_ri)[rl]", m => m.Groups[1].Value + (char.IsUpper(m.Value[^1]) ? char.ToUpperInvariant(to) : to));
        return gamePath[..cut] + name;
    }

    private static bool IsCombining(JsonObject group)
        => Str(group["Type"]) == "Combining";

    private static string FileName(string gamePath)
    {
        var cut = gamePath.LastIndexOf('/');
        return cut < 0 ? gamePath : gamePath[(cut + 1)..];
    }

    internal static string Str(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
}
