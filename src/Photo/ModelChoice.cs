using System.Text.RegularExpressions;
using ModCloset2.Core;
using ModCloset2.Detect;
using ModCloset2.Game;
using GamePaths = ModCloset2.Detect.GamePaths;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary>
/// Which model a picture of a slot shows, chosen from the mod's definition alone: no model, material or texture is
/// opened to choose (the closet's asks name only the slot and the player's race, and reading every model of a mod for
/// each of its pieces would be a second run of the detection per photo).
/// <list type="number">
/// <item> The candidates: the model files the definition redirects for this slot (game paths of the slot, a file of
/// the mod behind them, swaps to such a file included) with the mod's default options on; only when none is on, those
/// of options that are off. </item>
/// <item> The race: the player's own when the mod has a model of it; else the nearest along the player's fallback chain
/// (<see cref="Races.Chain"/>, what the library uses to decide what fits); else the race the mod has most models of
/// (<see cref="PhotoMaker.RacesByCount"/>); else any. Race 0 (nobody logged in): the mod's most common. </item>
/// <item> Several models left (two item sets in one slot, a right and a left ring): the first in the definition's
/// order (the default data, then the groups and their options as written, files as written). The same every time. </item>
/// </list>
/// </summary>
public static class ModelChoice
{
    public readonly record struct Choice(string GamePath, int Race);

    public static Choice? Choose(ModFiles files, Socket socket, int race)
    {
        var candidates = files.Models.Where(m => m.Socket == socket && m.On).ToList();
        if (candidates.Count == 0)
            candidates = files.Models.Where(m => m.Socket == socket).ToList();
        if (candidates.Count == 0)
            return null;

        var races = candidates.Select(c => c.Race).ToHashSet();
        var pick  = Races.Chain(race).FirstOrDefault(races.Contains);
        if (pick == 0)
            pick = files.RacesByCount.FirstOrDefault(races.Contains);
        var first = pick != 0 ? candidates.First(c => c.Race == pick) : candidates[0];
        return new Choice(first.GamePath, first.Race);
    }
}

/// <summary> What a picture shows, by kind of part (brief 13, "What each kind of part shows"). </summary>
public enum PieceKind
{
    /// <summary> Clothes and jewellery with a model of the mod's own: drawn from the mod alone, as in 0.5. </summary>
    Gear,

    /// <summary> Clothes and jewellery the mod only recolours: the game's model with the mod's materials and textures. </summary>
    GearFromGame,
    Hair,
    EarShape,
    Tail,

    /// <summary> A face model of the mod (a sculpt), with its own textures. </summary>
    Face,

    /// <summary> Makeup, eyes, face paint: the face the textures belong to, with the mod's textures. </summary>
    FaceTextures,

    /// <summary> Body models and skin textures: the body, the game's own underwear on where the mod brings no body. </summary>
    Body,

    /// <summary> A weapon, a mount, a minion, furniture: the mod's model alone. </summary>
    Thing,

    /// <summary> Nothing to put a texture on: the texture itself, flat. </summary>
    Flat,
}

/// <summary> What a model is in a picture. </summary>
public enum ModelRole
{
    /// <summary> The piece (or the model the piece's files go on). </summary>
    Piece,

    /// <summary> The game's plain head or body the piece is shown on: mannequin grey, never the mod's. </summary>
    Dummy,
}

/// <summary> One model of a picture. </summary>
public readonly record struct PlannedModel(FileRef File, ModelRole Role, string Option = "");

/// <summary> What one picture is made of, decided before anything but the definition is read. </summary>
public sealed class PartPlan
{
    public PieceKind Kind { get; init; }
    public Socket Socket { get; init; }
    public int Race { get; init; }
    public List<PlannedModel> Models { get; } = new();

    /// <summary> A face paint texture of the mod (it is no material's: the game puts it on the face by itself). </summary>
    public FileRef? Decal { get; init; }

    /// <summary> For <see cref="PieceKind.Flat"/>: the texture to show. </summary>
    public FileRef? Flat { get; init; }

    /// <summary> When there is nothing to draw: the problem (fixed sentence) and what more is known. </summary>
    public string Problem { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;

    /// <summary> The model's game path for the report ("" for a flat picture). </summary>
    public string ModelGamePath
        => Models.FirstOrDefault(m => m.Role == ModelRole.Piece).File.GamePath ?? string.Empty;
}

/// <summary>
/// Decides a picture (<see cref="PartPlan"/>) for every kind of part the maker takes, from the definition and, for
/// what the mod does not bring, from which files the game has (asked by path, <see cref="IGameFiles.Has"/>, nothing
/// read). The rules, by kind:
/// <list type="bullet">
/// <item> <b>Gear</b>: the mod's model (<see cref="ModelChoice"/>). Without one, the game's model of the item the mod's
/// files of this slot name (set, slot and race from their paths; races tried: the player's chain, the files' own, then
/// Midlander man and woman), with the mod's materials and textures. </item>
/// <item> <b>Hair, ears, tail, face</b>: the mod's model of the slot (race as for gear). Without one, the game's model of
/// the number the mod's files name (h0145, t0003, f0004, z0001; races as for gear). Hair and ears sit on the game's
/// plain head of the model's race: its first face the game has (f0001 to f0010, then f0101 to f0110). A tail has the
/// game's own underwear of the race (smallclothes, e0000) below it for scale when the game has it. </item>
/// <item> <b>Face textures</b>: the face the files name (race and face number in their paths), else the player's race
/// (chain) and its first face; the mod's face model when it brings one, else the game's. A face paint texture goes on
/// the face's skin. </item>
/// <item> <b>Body</b>: the smallclothes (e0000: top, legs, hands, feet; the mod's first, the game's second), which carry
/// the skin and the game's own underwear, of the race whose body model files in the mod are largest together, else the
/// race its skin files name, else the player's; the mod's other body models of that race (seams) on them. The skin's
/// material and textures come from the mod first. No smallclothes anywhere: the skin texture, flat. </item>
/// <item> <b>Weapons, the rest</b>: the mod's first model of the slot. </item>
/// <item> <b>Nothing to put the files on</b> (no model in the mod, the game has none or is not there): the mod's
/// colour texture of the part, flat. </item>
/// </list>
/// </summary>
public static partial class PartPlanner
{
    [GeneratedRegex(@"^chara/human/c(\d{4})/obj/(hair|face|tail|zear|body)/([a-z])(\d{4})/")]
    private static partial Regex HumanFolder();

    [GeneratedRegex(@"c(\d{4})[ea](\d{4})_(met|top|glv|dwn|sho|ear|nek|wrs|rir|ril)")]
    private static partial Regex GearName();

    /// <summary> The fallback races when nothing else names one: Midlander woman, Midlander man. </summary>
    private static readonly int[] Fallback = [201, 101];

    public static PartPlan Plan(PhotoFiles files, PhotoAsk ask)
    {
        var socket = ask.Socket;
        if (ask.ModelGamePath.Length > 0)
            return Named(files, ask);
        if (GamePaths.IsGear(socket))
            return Gear(files, ask);
        return socket switch
        {
            Socket.Hair         => Human(files, ask, PieceKind.Hair, "hair", 'h', "hir"),
            Socket.EarShape     => Human(files, ask, PieceKind.EarShape, "zear", 'z', "zer"),
            Socket.Tail         => Human(files, ask, PieceKind.Tail, "tail", 't', "til"),
            Socket.Face         => Human(files, ask, PieceKind.Face, "face", 'f', "fac"),
            Socket.FaceTextures => FaceTextures(files, ask),
            Socket.BodyBase     => Body(files, ask),
            Socket.MainHand or Socket.OffHand or Socket.Other => Thing(files, ask),
            _                   => new PartPlan { Socket = socket, Problem = PhotoProblems.NotTaken, Detail = socket.ToString() },
        };
    }

    /// <summary> A model the ask names (the tool, the tests): drawn as its slot's kind, from the mod alone for gear. </summary>
    private static PartPlan Named(PhotoFiles files, PhotoAsk ask)
    {
        var path = ModDefinition.NormalizeGamePath(ask.ModelGamePath);
        var kind = GamePaths.IsGear(ask.Socket) ? PieceKind.Gear : KindOf(ask.Socket);
        var plan = new PartPlan { Kind = kind, Socket = ask.Socket, Race = ask.Race };
        plan.Models.Add(new PlannedModel(new FileRef(path, files.Mod.Local(path)), ModelRole.Piece, ask.Option));
        AddDummy(files, plan, path);
        return plan;
    }

    private static PieceKind KindOf(Socket s)
        => s switch
        {
            Socket.Hair => PieceKind.Hair, Socket.EarShape => PieceKind.EarShape, Socket.Tail => PieceKind.Tail, Socket.Face => PieceKind.Face,
            Socket.FaceTextures => PieceKind.FaceTextures, Socket.BodyBase => PieceKind.Body, _ => PieceKind.Thing,
        };

    // ------------------------------------------------------------------ gear

    private static PartPlan Gear(PhotoFiles files, PhotoAsk ask)
    {
        if (ModelChoice.Choose(files.Mod, ask.Socket, ask.Race) is { } own)
        {
            var plan = new PartPlan { Kind = PieceKind.Gear, Socket = ask.Socket, Race = own.Race };
            plan.Models.Add(new PlannedModel(new FileRef(own.GamePath, files.Mod.Local(own.GamePath)), ModelRole.Piece));
            return plan;
        }

        // a recolour: the game's model of the item the mod's files of this slot name. Every path is asked once; the
        // questions to the game are capped per picture (PhotoBudget.MaxGameAsks) and each sees the stop.
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        var slotFiles = files.Mod.Entries.Where(e => e.Socket == ask.Socket && e.Info.Set > 0).ToList();
        foreach (var e in slotFiles.OrderByDescending(e => e.On))
        {
            files.Stop.ThrowIfCancellationRequested();
            if (files.AsksRanOut)
                break;
            var name = GearName().Match(e.GamePath[(e.GamePath.LastIndexOf('/') + 1)..]);
            var setType = e.Info.SetType == 'a' ? 'a' : 'e';
            var suffixes = name.Success ? new[] { name.Groups[3].Value } : Suffixes(ask.Socket);
            foreach (var race in RaceOrder(ask.Race, e.Race))
                foreach (var suffix in suffixes)
                {
                    var folder = setType == 'a' ? "accessory" : "equipment";
                    var path = $"chara/{folder}/{setType}{e.Info.Set:D4}/model/c{race:D4}{setType}{e.Info.Set:D4}_{suffix}.mdl";
                    if (!tried.Add(path))
                        continue;
                    order.Add(path);
                    if (files.Find(path, extension: ".mdl") is { } model)
                    {
                        var plan = new PartPlan { Kind = PieceKind.GearFromGame, Socket = ask.Socket, Race = race };
                        plan.Models.Add(new PlannedModel(model, ModelRole.Piece));
                        return plan;
                    }
                }
        }

        return FlatOr(files, ask, PhotoProblems.NoModel, slotFiles.Count == 0 ? $"{ask.Socket}: no files of this slot"
            : $"{ask.Socket}: tried {order.Count}{(files.AsksRanOut ? " (the questions to the game ran out)" : "")}: {string.Join(", ", order.Take(6))}");
    }

    private static string[] Suffixes(Socket s)
        => s switch
        {
            Socket.Head => ["met"], Socket.Body => ["top"], Socket.Hands => ["glv"], Socket.Legs => ["dwn"], Socket.Feet => ["sho"],
            Socket.Ears => ["ear"], Socket.Neck => ["nek"], Socket.Wrists => ["wrs"], Socket.Rings => ["rir", "ril"], _ => [],
        };

    /// <summary> The races to try for a game model: the player's chain, the files' own, then the fallbacks; each once. </summary>
    private static IEnumerable<int> RaceOrder(int player, int own)
        => Races.Chain(player).Concat(own > 0 ? [own] : []).Concat(Fallback).Where(r => r > 0).Distinct();

    // ------------------------------------------------------------------ hair, ears, tail, face

    private static PartPlan Human(PhotoFiles files, PhotoAsk ask, PieceKind kind, string folder, char letter, string suffix)
    {
        FileRef? model = null;
        var race = 0;
        if (ModelChoice.Choose(files.Mod, ask.Socket, ask.Race) is { } own)
        {
            model = new FileRef(own.GamePath, files.Mod.Local(own.GamePath));
            race  = own.Race;
        }
        else
        {
            // the mod only brings textures or materials: the game's model of the number its paths name
            var asked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (fileRace, id) in Numbers(files, folder))
            {
                files.Stop.ThrowIfCancellationRequested();
                if (files.AsksRanOut)
                    break;
                foreach (var r in RaceOrder(ask.Race, fileRace))
                    if (asked.Add($"{r}/{id}") && files.Find($"chara/human/c{r:D4}/obj/{folder}/{letter}{id:D4}/model/c{r:D4}{letter}{id:D4}_{suffix}.mdl", extension: ".mdl") is { } found)
                    {
                        (model, race) = (found, r);
                        break;
                    }

                if (model is not null)
                    break;
            }
        }

        if (model is not { } m)
            return FlatOr(files, ask, PhotoProblems.NoModelOrTexture, $"{ask.Socket}: no model of the mod, and none of the game's for its files");

        var plan = new PartPlan { Kind = kind, Socket = ask.Socket, Race = race };
        plan.Models.Add(new PlannedModel(m, ModelRole.Piece));
        AddDummy(files, plan, m.GamePath);
        return plan;
    }

    /// <summary> Race and number of the mod's files in a human folder of this kind ("hair"), in the definition's order. </summary>
    private static List<(int Race, int Id)> Numbers(PhotoFiles files, string folder)
    {
        var list = new List<(int, int)>();
        var seen = new HashSet<(int, int)>();
        var i = 0;
        foreach (var e in files.Mod.Entries)
        {
            if ((++i & 255) == 0)
                files.Stop.ThrowIfCancellationRequested();
            var m = HumanFolder().Match(e.GamePath);
            if (m.Success && m.Groups[2].Value == folder)
            {
                var n = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[4].Value));
                if (seen.Add(n))
                    list.Add(n);
            }
        }

        return list;
    }

    /// <summary> The dummy for a piece: the plain head under hair and ears, the underwear under a tail. The game's only. </summary>
    private static void AddDummy(PhotoFiles files, PartPlan plan, string piecePath)
    {
        var race = HumanFolder().Match(piecePath) is { Success: true } m ? int.Parse(m.Groups[1].Value) : plan.Race;
        if (plan.Kind is PieceKind.Hair or PieceKind.EarShape && Face(files, race, gameOnly: true) is { } head)
            plan.Models.Add(new PlannedModel(head, ModelRole.Dummy));
        if (plan.Kind is PieceKind.Tail)
            foreach (var r in RaceOrder(race, 0))
                if (files.FindGame($"chara/equipment/e0000/model/c{r:D4}e0000_dwn.mdl") is { } hips)
                {
                    plan.Models.Add(new PlannedModel(hips, ModelRole.Dummy));
                    break;
                }
    }

    /// <summary> The first face of a race the game has (or the mod, unless <paramref name="gameOnly"/>), with the race's chain. </summary>
    private static FileRef? Face(PhotoFiles files, int race, bool gameOnly, int face = 0)
    {
        foreach (var r in RaceOrder(race, 0))
            foreach (var f in face > 0 ? [face] : FaceNumbers)
            {
                var path = $"chara/human/c{r:D4}/obj/face/f{f:D4}/model/c{r:D4}f{f:D4}_fac.mdl";
                if ((gameOnly ? files.FindGame(path) : files.Find(path, extension: ".mdl")) is { } hit)
                    return hit;
            }

        return null;
    }

    private static readonly int[] FaceNumbers = [.. Enumerable.Range(1, 10), .. Enumerable.Range(101, 10)];

    // ------------------------------------------------------------------ face textures

    private static PartPlan FaceTextures(PhotoFiles files, PhotoAsk ask)
    {
        var faces = Numbers(files, "face");
        FileRef? model = null;
        var race = 0;
        foreach (var (r, f) in faces)
            if (!files.AsksRanOut && Face(files, r, gameOnly: false, f) is { } hit)
            {
                (model, race) = (hit, r);
                break;
            }

        // makeup that names no face (eyes, a face paint): the face of the sculpt it stands on, when it is given one
        if (model is null && files.UseGame)
            for (var i = 0; i < files.Under.Count && model is null; ++i)
            {
                var faces2 = files.Under[i].Mod.Models.Where(m => m.Socket == Socket.Face && HumanFolder().IsMatch(m.GamePath)).ToList();
                foreach (var r in RaceOrder(ask.Race, 0).Concat(faces2.Select(m => m.Race)).Distinct())
                    if (faces2.Where(m => m.Race == r).OrderByDescending(m => m.On).FirstOrDefault() is { GamePath: not null } face
                        && files.Under[i].Mod.Local(face.GamePath) is { } underLocal)
                    {
                        (model, race) = (new FileRef(face.GamePath, underLocal, i + 1), r);
                        break;
                    }
            }

        if (model is null)
            foreach (var r in RaceOrder(ask.Race, 0))
                if (Face(files, r, gameOnly: false) is { } hit)
                {
                    (model, race) = (hit, r);
                    break;
                }

        var decal = files.Mod.Entries.FirstOrDefault(e => e.Info.File == FileKind.Texture && e.GamePath.StartsWith("chara/common/texture/decal_face/", StringComparison.Ordinal));
        if (model is not { } m)
            return FlatOr(files, ask, PhotoProblems.NoModelOrTexture, "no face for the textures, in the mod or the game");
        var plan = new PartPlan
        {
            Kind = PieceKind.FaceTextures, Socket = ask.Socket, Race = race,
            Decal = decal.GamePath is null ? null : new FileRef(decal.GamePath, files.Mod.Local(decal.GamePath)),
        };
        plan.Models.Add(new PlannedModel(m, ModelRole.Piece));
        return plan;
    }

    // ------------------------------------------------------------------ body

    private static PartPlan Body(PhotoFiles files, PhotoAsk ask)
    {
        // The body a skin is seen on is the game's smallclothes (e0000: top, legs, hands, feet), which carry the skin
        // and the game's own underwear; a body mod brings its own smallclothes or only pieces that go with them (the
        // seams at the neck and wrists). So: the smallclothes, the mod's first and the game's second, with the mod's
        // other body models of that race on them.
        var bodies = files.Mod.Models.Where(IsBodyModel).ToList();
        var races  = new List<int>();
        if (bodies.Count > 0)
        {
            // a body is made for one race: the race whose body model files are largest together (a file's size is known
            // without opening it; a mod may carry a small patch for another race beside the body it is made of)
            var sizes = bodies.GroupBy(b => b.Race).ToDictionary(g => g.Key, g => g.Sum(b => Math.Max(0, files.Mod.Local(b.GamePath) is { } l ? files.Source.Length(l) : 0)));
            races.AddRange(sizes.Keys.OrderByDescending(r => sizes[r]).ThenBy(r => r));
        }

        var own = files.Mod.Entries.FirstOrDefault(e => e.Socket == Socket.BodyBase && e.Race > 0).Race;
        races.AddRange(RaceOrder(ask.Race, own).OrderBy(r => r == own ? 0 : 1));
        foreach (var race in races.Distinct())
        {
            var plan = new PartPlan { Kind = PieceKind.Body, Socket = ask.Socket, Race = race };
            foreach (var r in RaceOrder(race, 0)) // a Miqo'te woman wears the Midlander woman's smallclothes
            {
                foreach (var slot in new[] { "top", "dwn", "glv", "sho" })
                    if (plan.Models.All(m => !m.File.GamePath.EndsWith($"e0000_{slot}.mdl", StringComparison.Ordinal))
                        && files.Find($"chara/equipment/e0000/model/c{r:D4}e0000_{slot}.mdl", extension: ".mdl") is { } m)
                        plan.Models.Add(new PlannedModel(m, ModelRole.Piece));
                if (plan.Models.Count == 4)
                    break;
            }

            if (plan.Models.Count == 0)
                continue; // no smallclothes for this race: a body's seams alone are no picture
            foreach (var b in bodies.Where(b => b.Race == race && !b.GamePath.Contains("/e0000/", StringComparison.Ordinal)
                                                && (b.On || !bodies.Any(x => x.On && x.Race == race))))
                plan.Models.Add(new PlannedModel(new FileRef(b.GamePath, files.Mod.Local(b.GamePath)), ModelRole.Piece));
            return plan;
        }

        return FlatOr(files, ask, PhotoProblems.NoModelOrTexture, "no smallclothes in the mod or the game to put the skin on");
    }

    private static bool IsBodyModel(ModFiles.ModelEntry m)
        => m.GamePath.Contains("/obj/body/", StringComparison.Ordinal) && m.GamePath.StartsWith("chara/human/", StringComparison.Ordinal)
        || m.GamePath.StartsWith("chara/equipment/e0000/model/", StringComparison.Ordinal);

    // ------------------------------------------------------------------ the rest

    private static PartPlan Thing(PhotoFiles files, PhotoAsk ask)
    {
        var sockets = ask.Socket is Socket.MainHand or Socket.OffHand ? new[] { Socket.MainHand, Socket.OffHand } : new[] { ask.Socket };
        var models = files.Mod.Models.Where(m => sockets.Contains(m.Socket)).OrderByDescending(m => m.On).ToList();
        if (models.Count == 0)
            return FlatOr(files, ask, PhotoProblems.NothingToDraw, $"{ask.Socket}: no model in the mod");
        var plan = new PartPlan { Kind = PieceKind.Thing, Socket = ask.Socket, Race = models[0].Race };
        plan.Models.Add(new PlannedModel(new FileRef(models[0].GamePath, files.Mod.Local(models[0].GamePath)), ModelRole.Piece));
        return plan;
    }

    /// <summary>
    /// The flat picture: the mod's texture of this part that shows most of what it is (a colour picture before a face
    /// paint, before an index map, before a mask, before a normal map), or the problem when it has none.
    /// </summary>
    private static PartPlan FlatOr(PhotoFiles files, PhotoAsk ask, string problem, string detail)
    {
        var textures = files.Mod.Entries.Where(e => e.Info.File == FileKind.Texture && BelongsTo(e, ask.Socket)).ToList();
        var best = textures.OrderBy(e => FlatRank(e.GamePath)).ThenByDescending(e => e.On).FirstOrDefault();
        if (best.GamePath is null)
            return new PartPlan { Socket = ask.Socket, Problem = problem, Detail = detail };
        return new PartPlan { Kind = PieceKind.Flat, Socket = ask.Socket, Flat = new FileRef(best.GamePath, files.Mod.Local(best.GamePath)) };
    }

    /// <summary> A texture of this part: its path's slot, or (for skin and face paint) a file at a made-up path. </summary>
    private static bool BelongsTo(ModFiles.ModelEntry e, Socket socket)
        => e.Socket == socket
        || socket == Socket.BodyBase && e.Info.Custom
        || socket == Socket.FaceTextures && e.Socket == Socket.Face && e.GamePath.Contains("/texture/", StringComparison.Ordinal);

    private static int FlatRank(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (Regex.IsMatch(name, @"(base|diffuse|_d\.|_d_|custom|iris|catchlight)"))
            return 0;
        if (name.Contains("decal", StringComparison.Ordinal))
            return 1;
        if (Regex.IsMatch(name, @"(_id|_id_|index)"))
            return 2;
        if (Regex.IsMatch(name, @"(mask|_m\.|_m_|_s\.)"))
            return 3;
        return 4;
    }
}
