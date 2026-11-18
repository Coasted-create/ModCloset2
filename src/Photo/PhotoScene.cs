using System.Diagnostics;
using ModCloset2.Colours;
using ModCloset2.Core;
using ModCloset2.Detect;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary> One mesh to draw with its surface; <see cref="IsPiece"/> is false for the skin it carries. </summary>
public sealed record SceneItem(PhotoMesh Mesh, PhotoSurface Surface, bool IsPiece);

/// <summary> What one picture is made of: the meshes of one part's model with their surfaces, and what was missing. </summary>
public sealed class PhotoScene
{
    public Socket Socket { get; init; }
    public string ModelGamePath { get; init; } = string.Empty;
    public int Race { get; init; }
    public List<SceneItem> Items { get; } = new();

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

        // the gear models the definition names, in its order (a swap counts when it leads to a file of the mod)
        var models = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, option, on) in def.AllOptions())
            foreach (var path in option.Files.Where(f => f.LocalPath.Length > 0).Select(f => f.GamePath)
                         .Concat(option.Swaps.Where(w => Local(w.LocalPath) is not null).Select(w => w.GamePath)))
            {
                var info = Detect.GamePaths.Classify(path);
                if (info.File != FileKind.Model || !Detect.GamePaths.IsGear(info.Socket))
                    continue;
                if (models.TryGetValue(path, out var at))
                {
                    _models[at] = _models[at] with { On = _models[at].On || on };
                    continue;
                }

                models[path] = _models.Count;
                _models.Add(new ModelEntry(path, info.Socket, info.Race, on));
            }
    }

    /// <summary> A gear model the definition names: its game path, slot, race, and whether an option that is on names it. </summary>
    public readonly record struct ModelEntry(string GamePath, Socket Socket, int Race, bool On);

    private readonly List<ModelEntry> _models = new();

    /// <summary> The gear models the definition names, in the definition's order. </summary>
    public IReadOnlyList<ModelEntry> Models
        => _models;

    private IReadOnlyList<int>? _races;

    /// <summary> <see cref="PhotoMaker.RacesByCount"/> of the definition, worked out once (the maker keeps the open mod). </summary>
    public IReadOnlyList<int> RacesByCount
        => _races ??= PhotoMaker.RacesByCount(Definition);

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
/// Builds the scene of one part: its model (the one the library measured: default options, its race, parts switched
/// off by default left out), each mesh's material as the mod ships it, and the files of the textures the shader uses.
/// Materials and textures the mod does not ship are listed, and those meshes are drawn plain. Never throws; a model
/// that is damaged or over budget leaves the scene <see cref="PhotoScene.Refused"/>.
/// </summary>
public static class SceneReader
{
    /// <summary> Texture samplers the photo reads (ids as the shaders name them). </summary>
    public const uint SamplerSpecular = 0x2B99E025;

    public static PhotoScene Read(ModSource source, ModFiles files, Socket socket, string modelGamePath, string option, int race,
        PhotoPools pools, PhotoBudget budget)
    {
        var watch  = Stopwatch.StartNew();
        var before = source.BytesRead + pools.BytesRead;
        var scene  = new PhotoScene { Socket = socket, ModelGamePath = ModDefinition.NormalizeGamePath(modelGamePath), Race = race, Arena = pools.Arena };
        try
        {
            Fill(scene, source, files, option, pools, budget);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // a mod must never break the background job
            scene.Refuse(PhotoProblems.Exception(ex), $"{ex.GetType().Name}: {ex.Message}");
        }

        scene.ReadMs    = watch.Elapsed.TotalMilliseconds;
        scene.BytesRead = source.BytesRead + pools.BytesRead - before;
        return scene;
    }

    private static void Fill(PhotoScene scene, ModSource source, ModFiles files, string option, PhotoPools pools, PhotoBudget budget)
    {
        var path  = scene.ModelGamePath;
        var local = ModelLocal(files, path, option);
        if (local is null)
        {
            scene.Refuse(PhotoProblems.ModelNotInMod, path);
            return;
        }

        var length = source.Length(local);
        if (length < 0)
        {
            scene.Refuse(PhotoProblems.ModelMissing, $"{path} → {local}");
            return;
        }

        if (length > budget.MaxModelBytes)
        {
            scene.Refuse(PhotoProblems.ModelTooBig, $"{path}: {length:N0} bytes");
            return;
        }

        if (pools.ReadFile(source, local, budget.MaxModelBytes) is not { } file)
        {
            scene.Refuse(PhotoProblems.ModelNotOpened, $"{path} → {local}");
            return;
        }

        // the parts switched off by default stay off in the picture
        var mdl = PhotoModelReader.Read(file.Bytes, file.Length, pools.Arena, budget,
            attributes => ~Visibility.Hidden(attributes, scene.Socket, path, files.Definition), out var error, out var detail, out var overBudget);
        if (mdl is null)
        {
            scene.Refuse(overBudget ? PhotoProblems.TooManyTriangles : PhotoProblems.ModelUnreadable(error), $"{path}: {error} ({detail})");
            return;
        }

        var surfaces = new Dictionary<int, PhotoSurface>();
        foreach (var mesh in mdl.Meshes)
        {
            if (!surfaces.TryGetValue(mesh.MaterialIndex, out var surface))
            {
                var name = mesh.MaterialIndex < mdl.Materials.Count ? mdl.Materials[mesh.MaterialIndex] : string.Empty;
                if (surfaces.Count >= budget.MaxMaterials)
                {
                    scene.Refuse(PhotoProblems.TooManyMaterials, $"{path}: more than {budget.MaxMaterials}");
                    return;
                }

                surface = Surface(scene, source, files, path, name, budget);
                surfaces[mesh.MaterialIndex] = surface;
            }

            scene.Items.Add(new SceneItem(mesh, surface, surface.Kind is not (SurfaceKind.Skin or SurfaceKind.Hidden)));
        }
    }

    /// <summary>
    /// The model's file: from the option the library measured it in ("Group/Option", or "" / "Default" for the default
    /// data), else whatever the mod serves for the path with its default settings.
    /// </summary>
    private static string? ModelLocal(ModFiles files, string gamePath, string option)
    {
        if (option.Length > 0)
            foreach (var (g, o, _) in files.Definition.AllOptions())
            {
                var label = g is null ? "Default" : $"{g.Name}/{o.Name}";
                if (label != option && !(g is not null && o.Name == option))
                    continue;
                foreach (var f in o.Files)
                    if (f.GamePath == gamePath && f.LocalPath.Length > 0)
                        return f.LocalPath;
            }

        return files.Local(gamePath);
    }

    /// <summary> A mesh's material, resolved and read as the game would; never null (a plain surface when it cannot be had). </summary>
    private static PhotoSurface Surface(PhotoScene scene, ModSource source, ModFiles files, string modelPath, string name, PhotoBudget budget)
    {
        var file = Name(name);
        if (name.Length == 0)
            return new PhotoSurface { Name = "(none)", Kind = SurfaceKind.Missing };

        // the body's skin under the piece: never the piece, drawn as the mannequin whatever the mod ships for it
        if (Geometry.IsSkinMaterial(name) || PieceReader.IsBodyMaterial(file))
            return new PhotoSurface { Name = file, Kind = SurfaceKind.Skin };

        var gamePath = MaterialPath(files, modelPath, name);
        var local    = gamePath is null ? null : files.Local(gamePath);
        if (local is null)
        {
            scene.Note($"material not in the mod (the game's own): {file}");
            return new PhotoSurface { Name = file, Kind = SurfaceKind.Missing };
        }

        // materials are small (a few kilobytes); the budget keeps a hostile one from being large
        if (source.Read(local, budget.MaxMaterialBytes) is not { } bytes || MaterialFile.Read(bytes, out _) is not { } mtrl)
        {
            scene.Note($"material missing, too big or damaged: {file}");
            return new PhotoSurface { Name = file, Kind = SurfaceKind.Missing };
        }

        var extras = MaterialExtras.Read(bytes);
        var shader = mtrl.Shader.ToLowerInvariant();
        if (mtrl.IsSkin)
            return new PhotoSurface { Name = file, Kind = SurfaceKind.Skin, Shader = shader };

        // Hair and eyes are the character's, not the piece's; occlusion meshes only darken what is under them in the game.
        if (mtrl.IsHair || mtrl.IsIris || shader.Contains("occlusion", StringComparison.Ordinal))
            return new PhotoSurface { Name = file, Kind = SurfaceKind.Hidden, Shader = shader };

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

        if (surface.Kind == SurfaceKind.Painted && surface.DiffuseFile is null)
            scene.Note($"no colour table and no colour picture: {file}, drawn plain");
        return surface;
    }

    /// <summary> The local file of a sampler's texture, or null (noted when the material names one the mod does not ship). </summary>
    private static string? Texture(PhotoScene scene, ModFiles files, MaterialFile mtrl, uint sampler)
    {
        if (mtrl.TextureOf(sampler, null) is not { Length: > 0 } path)
            return null;
        if (files.Local(path) is { } local)
            return local;
        scene.Note($"texture not in the mod (the game's own): {Name(path)}");
        return null;
    }

    /// <summary>
    /// Where the game looks for a model's material: a full path as written, or the name in the model's gear folder
    /// (".../material/v0001/" for the first variant). When the mod ships it at another variant or folder, that file.
    /// Ported from the colour tests' PackFiles.ResolveMaterial (the old CharacterExport.ResolveMaterial).
    /// </summary>
    public static string? MaterialPath(ModFiles files, string modelPath, string name)
    {
        var n = ModDefinition.NormalizeGamePath(name);
        if (!name.StartsWith('/') && files.Local(n) is not null)
            return n;
        var file   = n[(n.LastIndexOf('/') + 1)..];
        var folder = modelPath.Contains("/model/", StringComparison.Ordinal) ? modelPath[..modelPath.IndexOf("/model/", StringComparison.Ordinal)] : string.Empty;
        var exact  = folder.Length > 0 ? $"{folder}/material/v0001/{file}" : null;
        if (exact is not null && files.Local(exact) is not null)
            return exact;
        if (file.Length == 0)
            return exact;
        var candidates = files.GamePaths.Where(p => p.EndsWith("/" + file, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        return candidates.FirstOrDefault(p => folder.Length > 0 && p.StartsWith(folder + "/", StringComparison.Ordinal) && files.IsOn(p))
            ?? candidates.FirstOrDefault(p => folder.Length > 0 && p.StartsWith(folder + "/", StringComparison.Ordinal))
            ?? candidates.FirstOrDefault(files.IsOn)
            ?? candidates.FirstOrDefault()
            ?? exact;
    }

    public static string Name(string path)
    {
        var cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return (cut >= 0 ? path[(cut + 1)..] : path).ToLowerInvariant();
    }
}
