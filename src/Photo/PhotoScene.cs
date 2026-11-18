using System.Diagnostics;
using System.Text.RegularExpressions;
using ModCloset2.Colours;
using ModCloset2.Core;
using ModCloset2.Detect;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary> What a mesh is in a picture. </summary>
public enum ItemRole
{
    /// <summary> What the picture is about: the camera frames it. </summary>
    Piece,

    /// <summary> Drawn as it is but not framed: the skin a piece carries, the parts of a game model the mod leaves alone. </summary>
    Context,

    /// <summary> The game's plain head or body under the piece, in mannequin grey. </summary>
    Dummy,
}

/// <summary> One mesh to draw with its surface and what it is in the picture. </summary>
public sealed record SceneItem(PhotoMesh Mesh, PhotoSurface Surface, ItemRole Role)
{
    public bool IsPiece
        => Role == ItemRole.Piece;
}

/// <summary> What one picture is made of: the meshes of one part's model with their surfaces, and what was missing. </summary>
public sealed class PhotoScene
{
    public Socket Socket { get; init; }
    public PieceKind Kind { get; init; }
    public string ModelGamePath { get; init; } = string.Empty;
    public int Race { get; init; }
    public List<SceneItem> Items { get; } = new();

    /// <summary> A face paint texture to lay on the face's skin (its second texture coordinates), and the image read. </summary>
    public FileRef? Decal { get; init; }
    public TexImage? DecalImage { get; set; }

    /// <summary> The vertices and triangles of all items (the maker's, reused). </summary>
    public MeshArena Arena { get; init; } = new();

    /// <summary> What the mod does not provide or could not be read (said once each). </summary>
    public List<string> Missing { get; } = new();

    /// <summary> Textures read: file name, the size read (after choosing a mip level) and the full size. </summary>
    public List<string> Textures { get; } = new();

    public double ReadMs { get; set; }
    public long BytesRead { get; set; }

    /// <summary> Why the picture is not attempted at all: one of <see cref="PhotoProblems"/>; "" when it is drawn. </summary>
    public string Refused { get; private set; } = string.Empty;

    /// <summary> More about it, may name files (never logged or kept). </summary>
    public string RefusedDetail { get; private set; } = string.Empty;

    public void Refuse(string problem, string detail)
    {
        Refused       = problem;
        RefusedDetail = detail;
    }

    /// <summary> Textures decoded so far, and their texels together (counted against the budget). </summary>
    public int TextureCount { get; set; }
    public long Texels { get; set; }

    public int Triangles
    {
        get
        {
            var n = 0;
            foreach (var i in Items)
                if (i.Surface.Kind != SurfaceKind.Hidden)
                    n += i.Mesh.TriangleCount;
            return n;
        }
    }

    public void Note(string missing)
    {
        if (!Missing.Contains(missing))
            Missing.Add(missing);
    }
}

/// <summary>
/// The files of a mod by game path, as Penumbra would serve them with the mod's default settings: the options that are
/// on first (in Penumbra's order, the first one wins), then swaps that lead to a file of the mod, then every other
/// option for paths nothing on sets (a model's material may only ship in another option of the same mod).
/// </summary>
public sealed class ModFiles
{
    private readonly Dictionary<string, string> _on = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _any = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _swaps = new(StringComparer.Ordinal);

    public ModDefinition Definition { get; }

    public ModFiles(ModDefinition def)
    {
        Definition = def;
        foreach (var (_, _, option) in def.AppliedByDefault())
        {
            foreach (var f in option.Files.Where(f => f.LocalPath.Length > 0))
                _on.TryAdd(f.GamePath, f.LocalPath);
            foreach (var s in option.Swaps)
                _swaps.TryAdd(s.GamePath, s.LocalPath);
        }

        foreach (var (_, option, _) in def.AllOptions())
            foreach (var f in option.Files.Where(f => f.LocalPath.Length > 0))
                _any.TryAdd(f.GamePath, f.LocalPath);

        // every file the definition names, in its order, with what its path says (a swap counts when it leads to a file
        // of the mod); the models among them by slot
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, option, on) in def.AllOptions())
            foreach (var path in option.Files.Where(f => f.LocalPath.Length > 0).Select(f => f.GamePath)
                         .Concat(option.Swaps.Where(w => Local(w.LocalPath) is not null).Select(w => w.GamePath)))
            {
                if (seen.TryGetValue(path, out var at))
                {
                    _entries[at] = _entries[at] with { On = _entries[at].On || on };
                    continue;
                }

                var info = Detect.GamePaths.Classify(path);
                seen[path] = _entries.Count;
                _entries.Add(new ModelEntry(path, info.Socket, info.Race, on) { Info = info });
            }
    }

    /// <summary> A file the definition names: its game path, slot, race, and whether an option that is on names it. </summary>
    public readonly record struct ModelEntry(string GamePath, Socket Socket, int Race, bool On)
    {
        public PathInfo Info { get; init; }
    }

    private readonly List<ModelEntry> _entries = new();
    private List<ModelEntry>? _models;

    /// <summary> Every file the definition names (models, materials, textures, the rest), in the definition's order. </summary>
    public IReadOnlyList<ModelEntry> Entries
        => _entries;

    /// <summary> The models the definition names that belong to a slot (gear, hair, face…), in the definition's order. </summary>
    public IReadOnlyList<ModelEntry> Models
        => _models ??= _entries.Where(e => e.Info.File == FileKind.Model && e.Socket is not Socket.None).ToList();

    private IReadOnlyList<int>? _races;

    /// <summary> <see cref="PhotoMaker.RacesByCount"/> of the definition, worked out once (the maker keeps the open mod). </summary>
    public IReadOnlyList<int> RacesByCount
        => _races ??= PhotoMaker.RacesByCount(Definition);

    private List<int>? _racesAll;

    /// <summary> The races the mod has models of (any kind), most models first, ties by the lower code. </summary>
    public List<int> RacesByCountAll
        => _racesAll ??= Models.Where(m => m.Race > 0).GroupBy(m => m.Race).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).ToList();

    /// <summary> Every game path the mod has a file for. </summary>
    public IEnumerable<string> GamePaths
        => _any.Keys;

    /// <summary> The local file for a game path, or null when the mod does not provide it (the game's own file). </summary>
    public string? Local(string gamePath)
    {
        var p = ModDefinition.NormalizeGamePath(gamePath);
        for (var hop = 0; hop < 4; ++hop)
        {
            if (_on.TryGetValue(p, out var local))
                return local;
            if (!_swaps.TryGetValue(p, out var next) || next.Length == 0 || next == p)
                break;
            p = next; // a swap to another path: the mod may provide that one
        }

        return _any.GetValueOrDefault(p);
    }

    /// <summary> True when an option that is on by default provides the path. </summary>
    public bool IsOn(string gamePath)
        => _on.ContainsKey(ModDefinition.NormalizeGamePath(gamePath));
}

/// <summary>
/// Builds the scene of one picture from its <see cref="PartPlan"/>: each model (parts switched off by default left
/// out), each mesh's material and the files of the textures its shader uses, all through the resolver (the mod first,
/// then the game). Materials and textures nobody provides are listed, and those meshes are drawn plain. Never throws;
/// a model that is damaged or over budget leaves the scene <see cref="PhotoScene.Refused"/>.
/// </summary>
public static partial class SceneReader
{
    /// <summary> Texture samplers the photo reads (ids as the shaders name them). </summary>
    public const uint SamplerSpecular = 0x2B99E025;

    public static PhotoScene Read(PhotoFiles files, PartPlan plan, PhotoPools pools, PhotoBudget budget)
    {
        var watch  = Stopwatch.StartNew();
        var before = files.Source.BytesRead + pools.BytesRead;
        var scene  = new PhotoScene
        {
            Socket = plan.Socket, Kind = plan.Kind, ModelGamePath = plan.ModelGamePath, Race = plan.Race, Arena = pools.Arena, Decal = plan.Decal,
        };
        try
        {
            var surfaces = new Dictionary<string, PhotoSurface>(StringComparer.Ordinal);
            foreach (var model in plan.Models)
            {
                // gear with a model of the mod's own is drawn from the mod alone, as 0.5 drew it; everything else may use
                // the game's files for what the mod does not bring
                files.UseGame = plan.Kind != PieceKind.Gear;
                if (!Fill(scene, files, model, plan, pools, budget, surfaces))
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // a mod must never break the background job
            scene.Refuse(PhotoProblems.Exception(ex), $"{ex.GetType().Name}: {ex.Message}");
        }

        scene.ReadMs    = watch.Elapsed.TotalMilliseconds;
        scene.BytesRead = files.Source.BytesRead + pools.BytesRead - before;
        return scene;
    }

    /// <summary> One model into the scene; false when the picture is refused (only the piece's model can refuse it). </summary>
    private static bool Fill(PhotoScene scene, PhotoFiles files, PlannedModel planned, PartPlan plan, PhotoPools pools, PhotoBudget budget,
        Dictionary<string, PhotoSurface> surfaces)
    {
        var path  = planned.File.GamePath;
        var piece = planned.Role == ModelRole.Piece;
        var file  = planned.File;
        if (piece && planned.Option.Length > 0 && OptionLocal(files.Mod, path, planned.Option) is { } optionLocal)
            file = new FileRef(path, optionLocal);
        // gear with its own model is the mod's alone (as 0.5 drew it)
        if (file.Local is null && (plan.Kind == PieceKind.Gear || files.Game?.Has(path) != true))
        {
            if (!piece)
                return true; // a dummy the game does not have: the piece alone
            scene.Refuse(plan.Kind == PieceKind.Gear ? PhotoProblems.ModelNotInMod : PhotoProblems.ModelMissing, path);
            return false;
        }

        var length = files.Length(file);
        if (length < 0)
        {
            if (!piece)
                return true;
            scene.Refuse(PhotoProblems.ModelMissing, $"{path} → {file.Local ?? "(the game)"}");
            return false;
        }

        if (length > budget.MaxModelBytes)
        {
            if (!piece)
                return true;
            scene.Refuse(PhotoProblems.ModelTooBig, $"{path}: {length:N0} bytes");
            return false;
        }

        if (files.ReadLarge(file, budget.MaxModelBytes) is not { } bytes)
        {
            if (!piece)
                return true;
            scene.Refuse(PhotoProblems.ModelNotOpened, $"{path} → {file.Local ?? "(the game)"}");
            return false;
        }

        // the parts switched off by default stay off in the picture; a dummy shows what the game shows by default
        var socket = GamePaths.Classify(path).Socket;
        var mdl = PhotoModelReader.Read(bytes.Bytes, bytes.Length, pools.Arena, budget,
            model => piece ? ~(Visibility.Hidden(model.Attributes, scene.Socket, path, files.Mod.Definition)
                                | (FaceFeaturesApply(plan.Kind, path) ? Visibility.FaceFeaturesOff(model, files.Mod.Definition, FeaturesMod(files.Mod)) : 0))
                : uint.MaxValue,
            out var error, out var detail, out var overBudget);
        if (mdl is null)
        {
            if (!piece)
                return true;
            scene.Refuse(overBudget ? PhotoProblems.TooManyTriangles : PhotoProblems.ModelUnreadable(error), $"{path}: {error} ({detail})");
            return false;
        }

        _ = socket;
        var mine = new Dictionary<int, PhotoSurface>();
        foreach (var mesh in mdl.Meshes)
        {
            if (!mine.TryGetValue(mesh.MaterialIndex, out var surface))
            {
                var name = mesh.MaterialIndex < mdl.Materials.Count ? mdl.Materials[mesh.MaterialIndex] : string.Empty;
                if (!piece)
                {
                    surface = DummySurface(name);
                }
                else if (surfaces.TryGetValue(path + "|" + name, out var known))
                {
                    surface = known;
                }
                else
                {
                    if (surfaces.Count >= budget.MaxMaterials)
                    {
                        scene.Refuse(PhotoProblems.TooManyMaterials, $"{path}: more than {budget.MaxMaterials}");
                        return false;
                    }

                    surface = Surface(scene, files, plan.Kind, path, name, budget);
                    surfaces[path + "|" + name] = surface;
                }

                mine[mesh.MaterialIndex] = surface;
            }

            var role = !piece ? ItemRole.Dummy : IsPiece(plan.Kind, surface, scene) ? ItemRole.Piece : ItemRole.Context;
            scene.Items.Add(new SceneItem(mesh, surface, role));
        }

        return true;
    }

    /// <summary> A face drawn as a face part: its optional features (scars, tattoos, marks) stay off. </summary>
    private static bool FaceFeaturesApply(PieceKind kind, string modelPath)
        => kind is PieceKind.Face or PieceKind.FaceTextures && modelPath.Contains("/obj/face/", StringComparison.Ordinal);

    /// <summary>
    /// A mod about a face's optional features themselves: all its face files belong to the features' materials (the
    /// face's "etc" materials other than the lashes), and it brings no face model and no face skin.
    /// </summary>
    public static bool FeaturesMod(ModFiles mod)
    {
        var face = mod.Entries.Where(e => e.GamePath.StartsWith("chara/human/", StringComparison.Ordinal)
                                          && e.GamePath.Contains("/obj/face/", StringComparison.Ordinal)).ToList();
        return face.Count > 0 && face.All(e =>
        {
            var name = e.GamePath[(e.GamePath.LastIndexOf('/') + 1)..];
            return name.Contains("_etc_", StringComparison.Ordinal) && !name.Contains("lash", StringComparison.Ordinal)
                   && !name.EndsWith(".mdl", StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Whether a mesh is what the picture is about (the camera frames it): for gear with its own model everything but
    /// the skin, as in 0.5; for what the mod only recolours or paints, the meshes whose material or textures come from
    /// the mod (the camera "looks at what the mod changes"); for hair, tails, faces, bodies and things, all of the model.
    /// </summary>
    private static bool IsPiece(PieceKind kind, PhotoSurface s, PhotoScene scene)
        => kind switch
        {
            PieceKind.Gear => s.Kind is not (SurfaceKind.Skin or SurfaceKind.Hidden),
            PieceKind.GearFromGame => s.Kind is not (SurfaceKind.Skin or SurfaceKind.Hidden) && s.FromMod,
            PieceKind.FaceTextures => s.Kind != SurfaceKind.Hidden && (s.FromMod || scene.Decal is not null && s.Kind == SurfaceKind.SkinTextured && s.IsFace),
            _ => s.Kind != SurfaceKind.Hidden,
        };

    /// <summary>
    /// The dummy's surfaces, by the material's name alone (nothing of a dummy's materials is read): skin and eyes in the
    /// mannequin's grey, the lashes, brows and shadow meshes of a face left out.
    /// </summary>
    private static PhotoSurface DummySurface(string name)
    {
        var file = Name(name);
        return file.Contains("_etc_", StringComparison.Ordinal) || file.Contains("_hir", StringComparison.Ordinal)
            ? new PhotoSurface { Name = file, Kind = SurfaceKind.Hidden }
            : new PhotoSurface { Name = file, Kind = SurfaceKind.Skin };
    }

    /// <summary> The model's file in the option the ask names ("Group/Option", or "Default"). </summary>
    private static string? OptionLocal(ModFiles files, string gamePath, string option)
    {
        foreach (var (g, o, _) in files.Definition.AllOptions())
        {
            var label = g is null ? "Default" : $"{g.Name}/{o.Name}";
            if (label != option && !(g is not null && o.Name == option))
                continue;
            foreach (var f in o.Files)
                if (f.GamePath == gamePath && f.LocalPath.Length > 0)
                    return f.LocalPath;
        }

        return null;
    }

    /// <summary> True for the kinds whose picture shows skin with its own textures (face, makeup, body). </summary>
    private static bool TexturedSkin(PieceKind kind)
        => kind is PieceKind.Face or PieceKind.FaceTextures or PieceKind.Body;

    /// <summary> A mesh's material, resolved and read as the game would; never null (a plain surface when it cannot be had). </summary>
    private static PhotoSurface Surface(PhotoScene scene, PhotoFiles files, PieceKind kind, string modelPath, string name, PhotoBudget budget)
    {
        var file = Name(name);
        if (name.Length == 0)
            return new PhotoSurface { Name = "(none)", Kind = SurfaceKind.Missing };

        // Gear, hair and the rest: the body's skin under the piece is never the piece, drawn as the mannequin whatever
        // the mod ships for it. Faces and bodies: the skin is the piece, read with its textures.
        var skinByName = Geometry.IsSkinMaterial(name) || PieceReader.IsBodyMaterial(file);
        if (skinByName && !TexturedSkin(kind))
            return new PhotoSurface { Name = file, Kind = SurfaceKind.Skin };

        var materialRef = MaterialPath(files, modelPath, name);
        var faceSkin = kind is PieceKind.Face or PieceKind.FaceTextures && FaceSkinName(file) is not null;
        if (materialRef is null && faceSkin && FaceSkinStandIn(files, file) is { } standIn)
        {
            // a face's skin found nowhere (a sculpt's own skin material, not shipped): the plain face skin of its race
            scene.Note($"face skin not found, the race's plain face skin stands in: {file}");
            materialRef = standIn;
        }

        if (materialRef is not { } mref)
        {
            scene.Note($"material not in the mod (the game's own): {file}");
            return new PhotoSurface { Name = file, Kind = skinByName ? SurfaceKind.Skin : SurfaceKind.Missing };
        }

        // materials are small (a few kilobytes); the budget keeps a hostile one from being large
        var bytes = files.ReadSmall(mref, budget.MaxMaterialBytes);
        var mtrl  = bytes is null ? null : MaterialFile.Read(bytes, out _);
        if (mtrl is null && faceSkin && FaceSkinStandIn(files, file) is { } standIn2 && standIn2 != mref)
        {
            scene.Note($"face skin unreadable, the race's plain face skin stands in: {file}");
            mref  = standIn2;
            bytes = files.ReadSmall(mref, budget.MaxMaterialBytes);
            mtrl  = bytes is null ? null : MaterialFile.Read(bytes, out _);
        }

        if (bytes is null || mtrl is null)
        {
            scene.Note($"material missing, too big or damaged: {file}");
            return new PhotoSurface { Name = file, Kind = skinByName ? SurfaceKind.Skin : SurfaceKind.Missing };
        }

        var extras = MaterialExtras.Read(bytes);
        var shader = mtrl.Shader.ToLowerInvariant();
        var fromMod = mref.IsMod;
        if (mtrl.IsSkin)
        {
            if (!TexturedSkin(kind))
                return new PhotoSurface { Name = file, Kind = SurfaceKind.Skin, Shader = shader };
            var skin = new PhotoSurface
            {
                Name        = file,
                Shader      = shader,
                Kind        = SurfaceKind.SkinTextured,
                IsFace      = file.Contains("_fac_", StringComparison.Ordinal) && !file.Contains("teeth", StringComparison.Ordinal),
                TwoSided    = !extras.HideBackfaces,
                DiffuseFile = Texture(scene, files, mtrl, MaterialFile.SamplerDiffuse),
                NormalFile  = Texture(scene, files, mtrl, MaterialFile.SamplerNormal),
                MaskFile    = Texture(scene, files, mtrl, MaterialFile.SamplerMask),
            };
            skin.FromMod = fromMod || skin.Files.Any(f => f.IsMod);

            // a face skin whose colour picture is found nowhere (a sculpt's material naming a texture it does not ship)
            // would be the mannequin's grey: the race's plain face skin lends its pictures for what is missing
            if (faceSkin && skin.DiffuseFile is null)
                foreach (var lend in FaceSkinStandIns(files, file).Where(l => l != mref).Take(3))
                {
                    if (files.ReadSmall(lend, budget.MaxMaterialBytes) is not { } lentBytes || MaterialFile.Read(lentBytes, out _) is not { IsSkin: true } lent
                        || lent.TextureOf(MaterialFile.SamplerDiffuse, null) is not { Length: > 0 })
                        continue;
                    scene.Note($"face skin's colour picture not found, the race's plain face skin lends its own: {file}");
                    skin.DiffuseFile = Texture(scene, files, lent, MaterialFile.SamplerDiffuse);
                    skin.NormalFile ??= Texture(scene, files, lent, MaterialFile.SamplerNormal);
                    skin.MaskFile ??= Texture(scene, files, lent, MaterialFile.SamplerMask);
                    if (skin.DiffuseFile is not null)
                        break;
                }

            return skin;
        }

        // A face's paint layers (charactertattoo.shpk: the facial features' marks and tattoos, coloured by the player's
        // choice, their texture a mask): drawn like clothes they show their maps as pale blue patches. On a face part
        // they are off like the features themselves, unless the mod is about them.
        if (kind is PieceKind.Face or PieceKind.FaceTextures && shader.Contains("tattoo", StringComparison.Ordinal) && !FeaturesMod(files.Mod))
            return new PhotoSurface { Name = file, Kind = SurfaceKind.Hidden, Shader = shader };

        // Hair and eyes: the character's on clothes (left out, as in 0.5); the piece itself on hair, tails, faces.
        var human = kind is not (PieceKind.Gear or PieceKind.GearFromGame or PieceKind.Thing);
        if (shader.Contains("occlusion", StringComparison.Ordinal) || (mtrl.IsHair || mtrl.IsIris) && !human)
            return new PhotoSurface { Name = file, Kind = SurfaceKind.Hidden, Shader = shader };
        if (mtrl.IsHair || mtrl.IsIris)
        {
            var strands = new PhotoSurface
            {
                Name        = file,
                Shader      = shader,
                Kind        = mtrl.IsHair ? SurfaceKind.Hair : SurfaceKind.Iris,
                TwoSided    = true,
                NormalFile  = Texture(scene, files, mtrl, MaterialFile.SamplerNormal),
                MaskFile    = Texture(scene, files, mtrl, MaterialFile.SamplerMask),
                DiffuseFile = mtrl.IsIris ? Texture(scene, files, mtrl, MaterialFile.SamplerDiffuse) : null,
            };
            strands.FromMod = fromMod || strands.Files.Any(f => f.IsMod);
            return strands;
        }

        if (shader is not ("character.shpk" or "characterlegacy.shpk" or "characterglass.shpk" or "characterstockings.shpk"))
            scene.Note($"shader not known to the photo, drawn like clothes: {shader} ({file})");

        var hasIndex = mtrl.Samplers.ContainsKey(MaterialFile.SamplerIndex);
        var rows = mtrl.Table == TableKind.Dawntrail ? MaterialExtras.DawntrailRows(mtrl)
            : extras.LegacyRows.Length > 0 ? extras.LegacyRows
            : Array.Empty<TableRow>();
        var surface = new PhotoSurface
        {
            Name               = file,
            Shader             = shader,
            Kind               = rows.Length > 0 ? SurfaceKind.Table : SurfaceKind.Painted,
            Rows               = rows,
            RowFromNormalAlpha = rows.Length > 0 && !hasIndex,
            TwoSided           = !extras.HideBackfaces,
            Blend              = shader == "characterglass.shpk" || extras.Transparent,
            IndexFile          = Texture(scene, files, mtrl, MaterialFile.SamplerIndex),
            NormalFile         = Texture(scene, files, mtrl, MaterialFile.SamplerNormal),
            MaskFile           = Texture(scene, files, mtrl, MaterialFile.SamplerMask),
            DiffuseFile        = Texture(scene, files, mtrl, MaterialFile.SamplerDiffuse),
            SpecularFile       = Texture(scene, files, mtrl, SamplerSpecular),
        };
        surface.FromMod = fromMod || surface.Files.Any(f => f.IsMod);

        if (surface.Kind == SurfaceKind.Painted && surface.DiffuseFile is null)
            scene.Note($"no colour table and no colour picture: {file}, drawn plain");
        return surface;
    }

    /// <summary> The file of a sampler's texture, or null (noted when the material names one nobody provides). </summary>
    private static FileRef? Texture(PhotoScene scene, PhotoFiles files, MaterialFile mtrl, uint sampler)
    {
        if (mtrl.TextureOf(sampler, null) is not { Length: > 0 } path)
            return null;
        if (files.Find(path, extension: ".tex") is { } found)
            return found;
        scene.Note(files.UseGame && files.Game is not null ? $"texture neither in the mod nor the game's: {Name(path)}" : $"texture not in the mod (the game's own): {Name(path)}");
        return null;
    }

    [GeneratedRegex(@"^mt_c(\d{4})([hftbz])(\d{4})_")]
    private static partial Regex HumanMaterial();

    [GeneratedRegex(@"^mt_c(\d{4})f(\d{4})_fac_([a-z])\.mtrl$")]
    private static partial Regex FaceSkinMaterial();

    /// <summary> A face's skin material ("mt_c0801f0004_fac_a.mtrl"): its race, or null. </summary>
    private static int? FaceSkinName(string file)
        => FaceSkinMaterial().Match(file) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    /// <summary>
    /// The plain face skin of a face skin's race (along the race's chain), from the mods the picture stands on or the
    /// game: what a face part is drawn with when its own skin material is found nowhere or cannot be read, rather than
    /// the grey of the mannequin.
    /// </summary>
    private static FileRef? FaceSkinStandIn(PhotoFiles files, string file)
        => FaceSkinStandIns(files, file).FirstOrDefault() is { GamePath: not null } hit ? hit : null;

    /// <summary> The plain face skins of a face skin's race that are there, in the order tried (at most 12 places). </summary>
    private static IEnumerable<FileRef> FaceSkinStandIns(PhotoFiles files, string file)
    {
        if (FaceSkinName(file) is not { } race || !files.UseGame)
            yield break;
        foreach (var r in ModCloset2.Game.Races.Chain(race).Where(r => r > 0).Distinct().Take(4))
            foreach (var f in new[] { 1, 2, 101 })
            {
                var folder = $"chara/human/c{r:D4}/obj/face/f{f:D4}/material";
                var name = $"mt_c{r:D4}f{f:D4}_fac_a.mtrl";
                if ((files.Find($"{folder}/v0001/{name}", extension: ".mtrl") ?? files.Find($"{folder}/{name}", extension: ".mtrl")) is { } hit)
                    yield return hit;
            }
    }

    /// <summary>
    /// Where the game looks for a model's material, the mod first. The places, in order: the name as a full path; for
    /// the human parts, the folder its own name says (hair materials of a Miqo'te's hairstyle live in the Midlander's
    /// folder: "mt_c0201h0145_hir_a" is in c0201/obj/hair/h0145), with and without the variant folder; the model's
    /// folder with "material/v0001/" and with "material/". Then any file of the mod with that name (another variant,
    /// another folder). Then the game, at the same places. Ported from the colour tests' PackFiles.ResolveMaterial (the
    /// old CharacterExport.ResolveMaterial); for gear with its own model the order is the one 0.5 used.
    /// </summary>
    public static FileRef? MaterialPath(PhotoFiles files, string modelPath, string name)
    {
        var n    = ModDefinition.NormalizeGamePath(name);
        var file = n[(n.LastIndexOf('/') + 1)..];
        var folder = modelPath.Contains("/model/", StringComparison.Ordinal) ? modelPath[..modelPath.IndexOf("/model/", StringComparison.Ordinal)] : string.Empty;
        var places = new List<string>(6);
        if (!name.StartsWith('/') && n.Contains('/'))
            places.Add(n);
        if (HumanMaterial().Match(file) is { Success: true } h)
        {
            var type = h.Groups[2].Value[0] switch { 'h' => "hair", 'f' => "face", 't' => "tail", 'b' => "body", _ => "zear" };
            var own  = $"chara/human/c{h.Groups[1].Value}/obj/{type}/{h.Groups[2].Value}{h.Groups[3].Value}/material";
            places.Add($"{own}/v0001/{file}");
            places.Add($"{own}/{file}");
        }

        if (folder.Length > 0)
        {
            places.Add($"{folder}/material/v0001/{file}");
            places.Add($"{folder}/material/{file}");
        }

        foreach (var p in places)
            if (files.Find(p, modOnly: true) is { } inMod)
                return inMod;

        if (file.Length > 0)
        {
            var candidates = files.Mod.GamePaths.Where(p => p.EndsWith("/" + file, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
            var hit = candidates.FirstOrDefault(p => folder.Length > 0 && p.StartsWith(folder + "/", StringComparison.Ordinal) && files.Mod.IsOn(p))
                   ?? candidates.FirstOrDefault(p => folder.Length > 0 && p.StartsWith(folder + "/", StringComparison.Ordinal))
                   ?? candidates.FirstOrDefault(files.Mod.IsOn)
                   ?? candidates.FirstOrDefault();
            if (hit is not null)
                return files.Find(hit, modOnly: true);
        }

        foreach (var p in places)
            if (files.UseGame && files.FindUnder(p) is { } under)
                return under; // the mods the picture stands on (a sculpt's own materials), before the game

        foreach (var p in places)
            if (files.Find(p, extension: ".mtrl") is { } inGame)
                return inGame;
        return null;
    }

    public static string Name(string path)
    {
        var cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return (cut >= 0 ? path[(cut + 1)..] : path).ToLowerInvariant();
    }
}
