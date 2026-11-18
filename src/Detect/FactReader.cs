using System.Diagnostics;
using System.Text.RegularExpressions;
using ModCloset2.Core;

namespace ModCloset2.Detect;

/// <summary>
/// Step one of the detection: reads a mod's definition and the few files it needs (materials, one model per part) and
/// collects the facts. Never writes, never throws for a damaged mod (problems are listed), honours the reading budget.
/// </summary>
public static partial class FactReader
{
    [GeneratedRegex(@"^(?:v\d{2}_)?(?:mt_)?c\d{4}[a-z]\d{4}_(?:(?:met|top|glv|dwn|sho|ear|nek|wrs|rir|ril|hir|fac|til|zer|iri|etc|acc)(?:_|$))?")]
    private static partial Regex GamePrefix();

    [GeneratedRegex(@"^chara/human/c(\d{4})/obj/([a-z]+)/([a-z])(\d{4})/")]
    private static partial Regex HumanFolder();

    /// <summary> One socket's collection while reading. </summary>
    private sealed class Acc
    {
        public readonly PartFacts Facts;
        public readonly HashSet<string> Paths = new(StringComparer.Ordinal);
        public readonly HashSet<int> Sets = new();
        public readonly HashSet<int> ModelSets = new();
        public readonly HashSet<int> Races = new();
        public readonly HashSet<int> ModelRaces = new();
        public readonly HashSet<string> Metas = new(StringComparer.Ordinal);
        public readonly HashSet<char> Fingers = new();      // ring models (and swaps of them) and the finger slot's meta changes
        public readonly HashSet<char> TextureFingers = new(); // ring materials and textures
        public readonly HashSet<(int Group, int Option)> Options = new();
        public readonly List<(string GamePath, string Local, int Group, int Option, bool On, int Race)> Models = new();
        public readonly List<(string GamePath, string Local)> Materials = new();
        public readonly List<string> Locals = new();

        public Acc(Socket s)
            => Facts = new PartFacts { Socket = s };
    }

    public static ModFacts Read(ModSource source, ModHints hints, CancellationToken cancel)
    {
        var watch = Stopwatch.StartNew();
        var facts = new ModFacts
        {
            Mod             = source.Name,
            Name            = hints.Name,
            SortFolder      = hints.SortFolder,
            Tags            = hints.Tags.ToList(),
            PreferRaceCodes = hints.PreferRaceCodes.ToList(),
            PreferBody      = hints.PreferBody,
        };

        var def = ModDefinition.Read(source);
        facts.Problems.AddRange(def.Problems);
        facts.Author = def.Author;
        if (facts.Name.Length == 0)
            facts.Name = def.Name;
        foreach (var t in def.Tags.Where(t => !facts.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            facts.Tags.Add(t);
        foreach (var g in def.Groups)
            facts.Groups.Add(new GroupFacts { Name = g.Name, Type = g.Type, Options = (g.Type == "Combining" ? g.Containers : g.Options).Select(o => o.Name).ToList() });

        cancel.ThrowIfCancellationRequested();

        // ---- witness 1: every game path and meta edit, into sockets
        var parts    = new Dictionary<Socket, Acc>();
        var setFiles = new List<(PathInfo Info, string GamePath, string Local, int Group, int Option)>();
        var custom   = new List<(string GamePath, string Local, int Group, int Option)>();
        var atrs     = new List<(string Attribute, int Group, int Option)>();
        var options  = Options(def);

        Acc Part(Socket s)
        {
            if (!parts.TryGetValue(s, out var acc))
                parts[s] = acc = new Acc(s);
            return acc;
        }

        // Swap chains (rule 5 of the tuning): an option swaps a game path to a path of the mod's own and the mod provides
        // that path as a file. The file is then what the game path shows: it belongs to the game path's slot.
        var provided = new Dictionary<string, List<(string Local, int Group, int Option, bool On)>>(StringComparer.Ordinal);
        foreach (var (gi, oi, option, on) in options)
            foreach (var f in option.Files.Where(f => f.LocalPath.Length > 0))
            {
                if (!provided.TryGetValue(f.GamePath, out var list))
                    provided[f.GamePath] = list = new();
                list.Add((f.LocalPath, gi, oi, on));
            }

        var chainTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, _, option, _) in options)
            foreach (var swap in option.Swaps)
                if (IsSwapChain(swap, provided.ContainsKey))
                    chainTargets.Add(swap.LocalPath);

        foreach (var (gi, oi, option, on) in options)
        {
            foreach (var swap in option.Swaps)
            {
                var into = GamePaths.Classify(swap.GamePath);
                if (chainTargets.Contains(swap.LocalPath))
                {
                    // The chain: the mod's own file stands in for the game file (model, material or texture). It counts
                    // with the swap's option (that option decides what the game path shows) and only when the file's
                    // option is on too.
                    foreach (var (local, _, _, fileOn) in provided[swap.LocalPath])
                        Add(Part(into.Socket), into, swap.GamePath, local, gi, oi, on && fileOn);
                    continue;
                }

                if (!swap.GamePath.EndsWith(".mdl", StringComparison.Ordinal))
                    continue;

                // An item swap: the part shows a game model from this socket (often another one: a hat as ears).
                var from = GamePaths.Classify(swap.LocalPath).Socket;
                if (into.Socket is not (Socket.None or Socket.Other) && from != Socket.None && !Part(into.Socket).Facts.SwappedFrom.Contains(from))
                    Part(into.Socket).Facts.SwappedFrom.Add(from);
            }

            foreach (var (gamePath, local) in option.Files.Select(f => (f.GamePath, f.LocalPath)).Concat(option.Swaps.Select(s => (s.GamePath, string.Empty))))
            {
                var info = GamePaths.Classify(gamePath);
                if (chainTargets.Count > 0 && info.Socket == Socket.Other
                    && (info.OtherKind == "helper" || info.File is FileKind.Material or FileKind.Texture))
                {
                    // A path of the mod's own in a mod with swap chains: the model is counted where it stands in, its
                    // materials and textures (even at a monster's path) belong to whichever part's material names them.
                    if (!chainTargets.Contains(gamePath))
                        custom.Add((gamePath, local, gi, oi));
                    continue;
                }

                if (chainTargets.Contains(gamePath) && !GamePaths.IsGear(info.Socket))
                    continue; // counted at the game path it stands in for

                if (info.Socket == Socket.None)
                {
                    if (info.Custom)
                        custom.Add((gamePath, local, gi, oi));
                    else
                        setFiles.Add((info, gamePath, local, gi, oi));
                    continue;
                }

                var acc = Part(info.Socket);
                Add(acc, info, gamePath, local, gi, oi, on);
            }

            foreach (var meta in option.Metas)
            {
                if (meta.Type is "atr" or "shp")
                {
                    if (meta.Attribute.Length > 0)
                        atrs.Add((meta.Attribute, gi, oi));
                    continue;
                }

                var socket = GamePaths.SocketOf(meta);
                if (socket == Socket.None)
                {
                    if (meta.Type is "rsp" or "globaleqp")
                        Part(Socket.Other).Facts.OtherKinds["helper"] = Part(Socket.Other).Facts.OtherKinds.GetValueOrDefault("helper") + 1;
                    continue;
                }

                // Meta edits only add to a part that has files; a mod of nothing but meta edits gets one Other part.
                var acc = Part(socket);
                acc.Metas.Add(meta.Type);
                if (GamePaths.Finger(meta.Slot) is { } finger)
                    acc.Fingers.Add(finger);
                acc.Options.Add((gi, oi));
                if (meta.Set > 0)
                    acc.Sets.Add(meta.Set);
            }
        }

        // IMC option groups change the part they name whenever the mod is on.
        for (var gi = 0; gi < def.Groups.Count; ++gi)
            if (def.Groups[gi].Imc is { } imc && GamePaths.ImcSocket(imc) is var s && s != Socket.None)
            {
                var acc = Part(s);
                acc.Metas.Add("imc");
                if (GamePaths.Finger(imc.Slot) is { } finger)
                    acc.Fingers.Add(finger);
                for (var oi = 0; oi < def.Groups[gi].Options.Count; ++oi)
                    acc.Options.Add((gi, oi));
                if (def.Groups[gi].Options.Count == 0)
                    acc.Options.Add((gi, -1));
            }

        // Faces with textures but no model are textures of a face (makeup, eyes).
        if (parts.TryGetValue(Socket.Face, out var face) && face.Models.Count == 0 && face.Paths.Count > 0)
        {
            parts.Remove(Socket.Face);
            var ft = Part(Socket.FaceTextures);
            Merge(ft, face);
        }

        // Files that only name a gear set (a texture without a slot, a gear effect, a gear skeleton) join the parts of
        // that set; with no such part an effect is an effect and the rest is left out.
        foreach (var (info, gamePath, local, gi, oi) in setFiles)
        {
            var owners = parts.Values.Where(a => GamePaths.IsGear(a.Facts.Socket) && a.Sets.Contains(info.Set)).ToList();
            if (owners.Count > 0)
            {
                // The option only speaks for these parts when it changes nothing worn of its own ("Bracelets" ships
                // the set's shared textures too, but it is about the bracelets).
                var opt = options.First(x => x.Group == gi && x.Option == oi);
                var ownSockets = opt.Def.Files.Select(f => GamePaths.Classify(f.GamePath).Socket).Where(s => s != Socket.None).ToHashSet();
                foreach (var o in owners)
                {
                    Add(o, info with { Socket = o.Facts.Socket }, gamePath, local, gi, oi, opt.On);
                    if (ownSockets.Count > 0 && !ownSockets.Contains(o.Facts.Socket))
                        o.Options.Remove((gi, oi));
                }

                continue;
            }

            var kind = info.OtherKind.Length > 0 ? info.OtherKind : info.File == FileKind.Effect ? "effect" : string.Empty;
            if (kind.Length > 0)
                Add(Part(Socket.Other), info with { Socket = Socket.Other, OtherKind = kind }, gamePath, local, gi, oi, true);
            else
                facts.Unclaimed.Add(gamePath);
        }

        cancel.ThrowIfCancellationRequested();

        // ---- materials: shaders, and which custom textures they use
        var materialTextures = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var materialShader   = new Dictionary<string, string>(StringComparer.Ordinal);
        var allMaterials = parts.Values.SelectMany(a => a.Materials).Concat(custom.Where(c => c.GamePath.EndsWith(".mtrl", StringComparison.Ordinal)).Select(c => (c.GamePath, c.Local)))
            .Where(m => m.Local.Length > 0).DistinctBy(m => m.GamePath + "|" + m.Local).Take(Tuning.MaxMaterials).ToList();
        foreach (var (gamePath, local) in allMaterials)
        {
            if (source.BytesRead > Tuning.MaxModBytes)
                break;
            var bytes = source.Read(local, Tuning.MaxMaterialBytes);
            if (bytes is null)
            {
                facts.Problems.Add($"Material missing or too big: {gamePath}");
                continue;
            }

            if (MtrlData.TryRead(bytes, out var error) is not { } mtrl)
            {
                facts.Problems.Add($"{gamePath}: {error}");
                continue;
            }

            materialShader.TryAdd(gamePath, mtrl.Shader);
            if (!materialTextures.TryGetValue(gamePath, out var list))
                materialTextures[gamePath] = list = new List<string>();
            list.AddRange(mtrl.Textures.Select(ModDefinition.NormalizeGamePath));
        }

        foreach (var acc in parts.Values)
            foreach (var (gamePath, _) in acc.Materials)
                if (materialShader.TryGetValue(gamePath, out var shader) && shader.Length > 0 && !acc.Facts.Shaders.Contains(shader))
                    acc.Facts.Shaders.Add(shader);

        cancel.ThrowIfCancellationRequested();

        // ---- one representative model per part
        foreach (var acc in parts.Values.Where(a => a.Models.Count > 0).OrderBy(a => a.Facts.Socket))
        {
            cancel.ThrowIfCancellationRequested();
            acc.Facts.HasModel = true;
            ReadModel(source, acc, def, hints, materialShader, facts);
        }

        // ---- files at custom paths: they belong to the part whose materials or model name them
        foreach (var (gamePath, local, gi, oi) in custom)
        {
            var owners = parts.Values.Where(a =>
                    a.Materials.Any(m => materialTextures.TryGetValue(m.GamePath, out var tex) && tex.Contains(gamePath))
                 || a.Facts.Model?.Materials.Any(m => ModDefinition.NormalizeGamePath(m) == gamePath) == true)
                .ToList();
            if (owners.Count == 0)
            {
                facts.Unclaimed.Add(gamePath);
                continue;
            }

            var info = GamePaths.Classify(gamePath);
            foreach (var o in owners)
                Add(o, info with { Socket = o.Facts.Socket }, gamePath, local, gi, oi, options.First(x => x.Group == gi && x.Option == oi).On);
        }

        // ---- attribute edits (Atr / Shp metas) belong to the part whose model has that attribute
        foreach (var (attribute, gi, oi) in atrs)
            foreach (var acc in parts.Values.Where(a => a.Facts.Model?.Attributes.Contains(attribute) == true))
                acc.Options.Add((gi, oi));

        // ---- finish each part
        foreach (var acc in parts.Values.OrderBy(a => a.Facts.Socket))
        {
            var f = acc.Facts;
            if (acc.Paths.Count == 0 && f.OtherKinds.Count == 0 && f.Socket != Socket.Other)
                continue; // meta edits only: support for the parts that have files

            f.PathCount = acc.Paths.Count;
            // The finger a ring is worn on is the model's (and the slot's settings'); a ring's materials go where its model
            // names them, whichever finger wears it. A retexture without a model is for the finger its textures name.
            if (f.Socket == Socket.Rings)
                f.Fingers = string.Concat((acc.Fingers.Count > 0 ? acc.Fingers : acc.TextureFingers).Order());
            f.Paths     = acc.Paths.Order(StringComparer.Ordinal).Take(Tuning.MaxPathsKept).ToList();
            f.Sets      = acc.Sets.Order().ToList();
            f.ModelSets = acc.ModelSets.Order().ToList();
            f.Metas     = acc.Metas.Order(StringComparer.Ordinal).ToList();
            f.RaceCodes = (acc.ModelRaces.Count > 0 ? acc.ModelRaces : acc.Races).Where(r => r > 0).Order().ToList();
            f.Groups    = acc.Options.Select(o => o.Group).Where(g => g >= 0).Distinct().Order().Select(g => def.Groups[g].Name).Distinct().ToList();
            f.Texts     = Texts(acc, def, options, materialTextures);
            facts.Parts.Add(f);
        }

        if (facts.Parts.Count == 0 && facts.Unclaimed.Count > 0)
        {
            // A mod of files at made-up paths only. Usually skin textures that a body mod's own materials point at
            // ("chara/bibo/midlander_d.tex"): when the paths name a body or skin it is a skin part, else one unsure
            // Other part, so the mod is not lost. The decision says how sure.
            var bodyLike = facts.Unclaimed.Any(u => Words.Tokens(u).Any(t => BodyPathWords.Contains(t)));
            var part = new PartFacts
            {
                Socket          = bodyLike ? Socket.BodyBase : Socket.Other,
                PathCount       = facts.Unclaimed.Count,
                Paths           = facts.Unclaimed.Take(Tuning.MaxPathsKept).ToList(),
                Groups          = def.Groups.Select(g => g.Name).ToList(),
                FromCustomPaths = true,
            };
            if (!bodyLike)
                part.OtherKinds["custom"] = facts.Unclaimed.Count;
            part.Texts.AddRange(facts.Unclaimed.Take(40).Select(u => new TextFact { Source = "file", Text = u }));
            part.Texts.AddRange(def.Groups.Select(g => new TextFact { Source = "group", Text = g.Name }));
            facts.Parts.Add(part);
        }

        var metaCount = options.Sum(o => o.Def.Metas.Count) + def.Groups.Count(g => g.Imc is not null);
        if (facts.Parts.Count == 0 && metaCount > 0)
        {
            // Settings only (EQP fixes, attribute toggles, race sizes): nothing to wear, one helper part.
            var helper = new PartFacts { Socket = Socket.Other, Groups = def.Groups.Select(g => g.Name).ToList() };
            helper.OtherKinds["helper"] = metaCount;
            helper.Metas = options.SelectMany(o => o.Def.Metas.Select(m => m.Type)).Distinct().Order(StringComparer.Ordinal).ToList();
            facts.Parts.Add(helper);
        }

        facts.BytesRead    = source.BytesRead;
        facts.Milliseconds = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
        return facts;
    }

    /// <summary> Every option with its group index (-1 for the default data), option index and whether it is on by default. </summary>
    private static List<(int Group, int Option, OptionDef Def, bool On)> Options(ModDefinition def)
    {
        var list = new List<(int, int, OptionDef, bool)> { (-1, -1, def.Default, true) };
        for (var gi = 0; gi < def.Groups.Count; ++gi)
        {
            var g  = def.Groups[gi];
            var on = g.DefaultOn();
            var source = g.Type == "Combining" ? g.Containers : g.Options;
            for (var oi = 0; oi < source.Count; ++oi)
                list.Add((gi, oi, source[oi], on.Contains(oi)));
        }

        return list;
    }

    private static void Add(Acc acc, PathInfo info, string gamePath, string local, int gi, int oi, bool on)
    {
        acc.Paths.Add(gamePath);
        if (info.Socket == Socket.BodyBase && !gamePath.StartsWith("chara/human/", StringComparison.Ordinal))
            acc.Races.UnionWith(GamePaths.SkinRaces(gamePath));
        if (acc.Facts.Socket == Socket.Rings && GamePaths.FingerOfPath(gamePath) is { } finger)
            (info.File == FileKind.Model ? acc.Fingers : acc.TextureFingers).Add(finger);
        acc.Options.Add((gi, oi));
        if (local.Length > 0)
            acc.Locals.Add(local);
        if (info.Set > 0 && info.SetType != '\0')
            acc.Sets.Add(info.Set);
        if (info.Race > 0)
            acc.Races.Add(info.Race);
        if (acc.Facts.Socket == Socket.Other || acc.Facts.Socket is Socket.MainHand or Socket.OffHand)
        {
            var kind = info.OtherKind.Length > 0 ? info.OtherKind : "helper";
            acc.Facts.OtherKinds[kind] = acc.Facts.OtherKinds.GetValueOrDefault(kind) + 1;
        }

        switch (info.File)
        {
            case FileKind.Model when local.Length > 0:
                acc.Models.Add((gamePath, local, gi, oi, on, info.Race));
                if (info.Set > 0)
                    acc.ModelSets.Add(info.Set);
                if (info.Race > 0)
                    acc.ModelRaces.Add(info.Race);
                break;
            case FileKind.Material:
                acc.Materials.Add((gamePath, local));
                break;
        }
    }

    private static void Merge(Acc into, Acc from)
    {
        into.Paths.UnionWith(from.Paths);
        into.Sets.UnionWith(from.Sets);
        into.Races.UnionWith(from.Races);
        into.Metas.UnionWith(from.Metas);
        into.Options.UnionWith(from.Options);
        into.Materials.AddRange(from.Materials);
        into.Locals.AddRange(from.Locals);
    }

    /// <summary>
    /// Reads the part's representative model: the game path and file from options that are on by default, made for the
    /// player's race and body when there is a choice, LOD 0 only.
    /// </summary>
    private static void ReadModel(ModSource source, Acc acc, ModDefinition def, ModHints hints, Dictionary<string, string> shaders, ModFacts facts)
    {
        var prefer = hints.PreferRaceCodes;
        int RaceRank(int race)
        {
            var i = -1;
            for (var k = 0; k < prefer.Count; ++k)
                if (prefer[k] == race)
                {
                    i = k;
                    break;
                }

            return i < 0 ? prefer.Count : i;
        }

        bool NamesBody(int gi, int oi)
        {
            if (hints.PreferBody.Length == 0 || gi < 0)
                return false;
            var g = def.Groups[gi];
            var name = (g.Type == "Combining" ? g.Containers : g.Options).ElementAtOrDefault(oi)?.Name ?? string.Empty;
            return Words.Tokens(name).Contains(hints.PreferBody.ToLowerInvariant());
        }

        // Of the options that are on, only the one Penumbra applies first shows its file for a game path.
        var applied = def.AppliedByDefault();
        bool Shows((string GamePath, string Local, int Group, int Option, bool On, int Race) m)
        {
            if (!m.On)
                return false;
            foreach (var (gi, oi, option) in applied)
                if (option.Files.Any(f => f.GamePath == m.GamePath) || option.Swaps.Any(s => s.GamePath == m.GamePath))
                    return gi == m.Group && oi == m.Option;
            return true;
        }

        var ranked = acc.Models
            .Select((m, i) => (m, i))
            .OrderByDescending(x => Shows(x.m))
            .ThenByDescending(x => x.m.On)
            .ThenBy(x => RaceRank(x.m.Race))
            .ThenByDescending(x => NamesBody(x.m.Group, x.m.Option))
            .ThenBy(x => x.i)
            .Select(x => x.m)
            .ToList();

        foreach (var (gamePath, local, gi, oi, _, race) in ranked)
        {
            var length = source.Length(local);
            if (length < 0)
            {
                facts.Problems.Add($"Model file missing: {gamePath}");
                continue;
            }

            if (length > Tuning.MaxModelBytes || source.BytesRead + length > Tuning.MaxModBytes)
            {
                facts.Problems.Add($"Model too big to look at: {gamePath}");
                return;
            }

            var bytes = source.Read(local, Tuning.MaxModelBytes);
            if (bytes is null)
            {
                facts.Problems.Add($"Model could not be read: {gamePath}");
                continue;
            }

            if (MdlReader.TryRead(bytes, out var error) is not { } model)
            {
                facts.Problems.Add($"{gamePath}: {error}");
                continue;
            }

            // Materials this mod ships with the skin shader count as skin too (a gear model's own "skin" material).
            var skin = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in model.Materials)
            {
                var file = name.TrimStart('/');
                var match = shaders.FirstOrDefault(kv => kv.Key.EndsWith("/" + file, StringComparison.Ordinal) || kv.Key == file);
                if (match.Value?.StartsWith("skin", StringComparison.OrdinalIgnoreCase) == true)
                    skin.Add(name);
            }

            var optionName = gi < 0 ? "Default" : $"{def.Groups[gi].Name}/{(def.Groups[gi].Type == "Combining" ? def.Groups[gi].Containers : def.Groups[gi].Options).ElementAtOrDefault(oi)?.Name}";
            var gear = GamePaths.IsGear(acc.Facts.Socket);
            var info = GamePaths.Classify(gamePath);
            var (hidden, hiddenNames, unknown) = HiddenByDefault(model, acc.Facts.Socket, info, def, applied);
            var everything = Geometry.Summarise(model, gamePath, optionName, race, gear, skin);
            var shown = hidden == 0 ? everything : Geometry.Summarise(model, gamePath, optionName, race, gear, skin, hidden);
            if (hidden != 0 && shown.Area <= 0 && everything.Area > 0)
            {
                // Everything worn is switched off by default: measure all of it rather than nothing.
                shown = everything;
                shown.Visibility = "everything (nothing shown by default)";
            }
            else if (hidden != 0)
            {
                shown.Visibility = unknown.Count > 0 ? "default, some toggles unknown" : "default";
                shown.Everything = everything;
            }
            else
            {
                shown.Visibility = unknown.Count > 0 ? "everything (toggles unknown)" : "everything";
            }

            shown.HiddenAttributes  = hiddenNames;
            shown.UnknownAttributes = unknown;
            acc.Facts.Model = shown;
            return;
        }
    }

    // atr_tv_b: a variant attribute of the top ("t"), switched by bit 1 of the item's IMC attribute mask.
    [GeneratedRegex(@"^atr_([a-z])v_([a-j])$")]
    private static partial Regex VariantAttribute();

    /// <summary> The letter gear variant attributes use for a socket (atr_gv_a: gloves). </summary>
    private static char VariantLetter(Socket s)
        => s switch
        {
            Socket.Head   => 'm', Socket.Body  => 't', Socket.Hands  => 'g', Socket.Legs  => 'd', Socket.Feet => 's',
            Socket.Ears   => 'e', Socket.Neck  => 'n', Socket.Wrists => 'w', Socket.Rings => 'r', _           => '\0',
        };

    /// <summary>
    /// Which of the model's attributes are off with the mod's default settings, by Penumbra's rules:
    /// <list type="bullet">
    /// <item> gear variant attributes (atr_gv_c…) follow the item's IMC attribute mask (bit 0 = a). The mask comes from
    /// the mod's IMC option group for that set and slot (default mask switched by the options that are on) or an IMC
    /// edit of an option that is on. Without either it is the game's own entry, which we cannot know: those attributes
    /// are listed as unknown and measured as shown. Accessories never hide "a" (Penumbra skips it too). </item>
    /// <item> custom attributes (atrx_…) are shown unless an ATR edit of an option that is on switches them off; the most
    /// specific edit (slot and set, then slot, then any) wins, and among equals the first one Penumbra applies. </item>
    /// <item> the game's other attributes (atr_nek, atr_hij…) are hidden only by other items worn with it: shown. </item>
    /// </list>
    /// </summary>
    private static (uint Hidden, List<string> HiddenNames, List<string> Unknown) HiddenByDefault(ModelData model, Socket socket, PathInfo info,
        ModDefinition def, List<(int Group, int Option, OptionDef Def)> applied)
    {
        uint hidden = 0;
        var names   = new List<string>();
        var unknown = new List<string>();
        if (model.Attributes.Count == 0)
            return (0, names, unknown);

        // The IMC mask for this item: an IMC option group first (Penumbra applies groups before the default data), else
        // an IMC edit of an option that is on.
        int? imc = null;
        foreach (var gi in Enumerable.Range(0, def.Groups.Count).Reverse().OrderByDescending(i => def.Groups[i].Priority))
        {
            var g = def.Groups[gi];
            if (g.Imc is { } id && GamePaths.ImcSocket(id) == socket && (id.Set == 0 || id.Set == info.Set))
            {
                imc = ModDefinition.ImcMask(g);
                break;
            }
        }

        if (imc is null)
            foreach (var (_, _, option) in applied)
            {
                var edit = option.Metas.FirstOrDefault(m => m.Type == "imc" && m.Mask >= 0 && GamePaths.ImcSocket(m) == socket && m.Set == info.Set);
                if (edit is not null)
                {
                    imc = edit.Mask;
                    break;
                }
            }

        var letter = VariantLetter(socket);
        for (var i = 0; i < model.Attributes.Count && i < 32; ++i)
        {
            var name = model.Attributes[i];
            var variant = VariantAttribute().Match(name);
            bool? off = null;
            if (variant.Success && variant.Groups[1].Value[0] == letter)
            {
                var bit = variant.Groups[2].Value[0] - 'a';
                if (info.SetType == 'a' && bit == 0)
                    off = false;
                else if (imc is { } mask)
                    off = (mask >> bit & 1) == 0;
                else
                    unknown.Add(name);
            }
            else if (name.StartsWith("atrx_", StringComparison.Ordinal))
            {
                off = AtrOff(name, socket, info.Set, applied);
            }

            if (off == true)
            {
                hidden |= 1u << i;
                names.Add(name);
            }
        }

        return (hidden, names, unknown);
    }

    /// <summary> True when an ATR edit of an option that is on switches the attribute off for this item. </summary>
    private static bool AtrOff(string attribute, Socket socket, int set, List<(int Group, int Option, OptionDef Def)> applied)
    {
        (int Specific, bool Entry)? best = null;
        foreach (var (_, _, option) in applied)
            foreach (var m in option.Metas)
            {
                if (m.Type != "atr" || m.Attribute != attribute || m.Entry is not { } entry)
                    continue;
                var slot = AtrSocket(m.Slot);
                if (slot is null || slot != Socket.None && slot != socket || m.Set != 0 && m.Set != set)
                    continue;
                var specific = slot == Socket.None ? 0 : m.Set != 0 ? 2 : 1;
                if (best is null || specific > best.Value.Specific)
                    best = (specific, entry);
            }

        return best is { Entry: false };
    }

    /// <summary> An ATR edit's slot: None for "all slots", null for a slot that is no socket of ours. </summary>
    private static Socket? AtrSocket(string slot)
        => GamePaths.AttributeSocket(slot);

    /// <summary>
    /// A swap chain (rule 5 of the tuning): the swap sends a game path of a slot to a path the mod provides as a file of
    /// its own; that file is then what the game path shows. Shared with "Make it its own mod", which must agree.
    /// </summary>
    public static bool IsSwapChain(FileRedirect swap, Func<string, bool> provided)
        => provided(swap.LocalPath) && GamePaths.Classify(swap.GamePath).Socket is not (Socket.None or Socket.Other);

    /// <summary> Names that speak about this part: its groups, its options, its local folders and file names, its materials. </summary>
    private static List<TextFact> Texts(Acc acc, ModDefinition def, List<(int Group, int Option, OptionDef Def, bool On)> options, Dictionary<string, List<string>> materialTextures)
    {
        var list = new List<TextFact>();
        var seen = new HashSet<(string, string)>();

        void Add(string source, string text)
        {
            text = text.Trim();
            if (text.Length > 0 && seen.Add((source, text.ToLowerInvariant())))
                list.Add(new TextFact { Source = source, Text = text });
        }

        // Groups that only switch parts of the model on and off (IMC groups, options of attribute edits only) name
        // sub-parts ("Skirt", "Rings"), not the item: they are "toggle" texts. A group none of whose options for this
        // part is on by default speaks like an option that is off.
        bool IsToggle(GroupDef g)
            => g.Type == "Imc" || (g.Type == "Combining" ? g.Containers : g.Options).All(o => o.Files.Count == 0 && o.Swaps.Count == 0
                && o.Metas.All(m => m.Type is "atr" or "shp")) && (g.Type == "Combining" ? g.Containers : g.Options).Any(o => o.Metas.Count > 0);

        foreach (var g in acc.Options.Select(o => o.Group).Where(g => g >= 0).Distinct().Order())
        {
            var anyOn  = acc.Options.Any(o => o.Group == g && options.Any(x => x.Group == g && x.Option == o.Option && x.On));
            var toggle = IsToggle(def.Groups[g]);
            Add(toggle ? anyOn ? "toggle" : "offtoggle" : anyOn ? "group" : "offgroup", def.Groups[g].Name);
        }

        foreach (var (gi, oi) in acc.Options.Where(o => o.Group >= 0 && o.Option >= 0).OrderBy(o => o))
        {
            var o = options.FirstOrDefault(x => x.Group == gi && x.Option == oi);
            if (o.Def is null)
                continue;
            if (IsToggle(def.Groups[gi]))
                Add(o.On ? "toggle" : "offtoggle", o.Def.Name);
            else
                Add(o.On ? "option" : "offoption", o.Def.Name);
        }

        // Local folders: what the mod author named the folders, minus the part that repeats the game path.
        var paths = acc.Paths;
        foreach (var local in acc.Locals.Distinct().Take(60))
        {
            var l = ModSource.Normalize(local).ToLowerInvariant();
            var cut = l.IndexOf("chara/", StringComparison.Ordinal);
            var dir = cut >= 0 ? l[..cut] : l.Contains('/') ? l[..l.LastIndexOf('/')] : string.Empty;
            if (dir.Length > 0)
                Add("file", dir.TrimEnd('/'));
            if (cut < 0)
                Add("file", GamePrefix().Replace(Path.GetFileNameWithoutExtension(l), string.Empty));
        }

        // Material and texture names without the game's prefix ("mt_c0201e6097_glv_nails" → "nails").
        var names = new List<string>();
        if (acc.Facts.Model is { } m)
            names.AddRange(m.Materials.Select(x => Path.GetFileNameWithoutExtension(x.TrimStart('/'))));
        names.AddRange(paths.Where(p => p.EndsWith(".mtrl", StringComparison.Ordinal) || p.EndsWith(".tex", StringComparison.Ordinal))
            .Select(p => Path.GetFileNameWithoutExtension(p[(p.LastIndexOf('/') + 1)..])));
        foreach (var n in names.Where(n => !Geometry.IsSkinMaterial(n + "_")).Distinct().Take(40))
        {
            var stripped = GamePrefix().Replace(n.ToLowerInvariant(), string.Empty);
            if (stripped.Length > 1)
                Add("material", stripped);
        }

        return list;
    }

    /// <summary> Words in made-up paths that say the files are a body's skin. </summary>
    private static readonly HashSet<string> BodyPathWords = new(StringComparer.Ordinal)
    {
        "bibo", "bibop", "gen3", "gen2", "tbse", "yab", "rue", "lavabod", "larue", "tre", "skin", "body", "bodies", "tattoo", "tattoos",
        "midlander", "highlander", "elezen", "miqote", "roegadyn", "lalafell", "aura", "raen", "xaela", "hrothgar", "viera", "b0001",
    };

    /// <summary> Race code and object folder of a human path, for tests. </summary>
    internal static (int Race, string Type) HumanOf(string gamePath)
    {
        var m = HumanFolder().Match(gamePath);
        return m.Success ? (int.Parse(m.Groups[1].Value), m.Groups[2].Value) : (0, string.Empty);
    }
}
