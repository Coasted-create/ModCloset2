using System.Diagnostics;
using ModCloset2.Colours;
using ModCloset2.Core;
using ModCloset2.Detect;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary> What the last picture took and what was missing (for the tool and the log; the closet needs only the shot). </summary>
public sealed class PhotoReport
{
    public double ReadMs { get; set; }
    public double FrameMs { get; set; }
    public double TextureMs { get; set; }
    public double DrawMs { get; set; }
    public double EncodeMs { get; set; }
    public int Triangles { get; set; }
    public long BytesRead { get; set; }
    public string View { get; set; } = string.Empty;
    public string ModelGamePath { get; set; } = string.Empty;
    public int Race { get; set; }

    /// <summary> Share of the picture where the piece can be told from the background. </summary>
    public double Seen { get; set; }
    public bool LightBackdrop { get; set; }
    public List<string> Textures { get; } = new();
    public List<string> Missing { get; } = new();

    public PieceKind Kind { get; set; }

    /// <summary> Questions this picture asked the game, and bytes the game gave it (not those kept from before). </summary>
    public int GameAsks { get; set; }

    /// <summary> The camera the picture was taken with (for the tests: faces come out the same size). </summary>
    public PhotoCamera? Camera { get; set; }
    public long GameBytes { get; set; }

    public double TotalMs
        => ReadMs + FrameMs + TextureMs + DrawMs + EncodeMs;
}

/// <summary>
/// The closet's photographer (<see cref="IPhotoMaker"/>, brief 12): draws one part of one mod from the mod's own files
/// as a 4:5 JPEG. The closet's asks name the slot and the player's race only: the model is chosen from the mod's
/// definition (<see cref="ModelChoice"/>), without reading any other file. An ask that names a model (the tool, tests)
/// draws that one.
/// <para>
/// Made to run thousands of times inside the game's process: its working memory (<see cref="PhotoPools"/>) is kept and
/// reused from picture to picture and let go in <see cref="Rest"/>; everything read from a mod is checked and held to a
/// <see cref="PhotoBudget"/>; the stop is honoured between and inside its passes; it never throws. One call at a time.
/// </para>
/// </summary>
public sealed class PhotoMaker : IPhotoMaker
{
    /// <summary> Drawn at twice the size and scaled down (3 looked the same on cards and cost twice as much). </summary>
    public const int Supersample = 2;

    /// <summary>
    /// Working memory kept between pictures at most. A rare giant piece grows the pools beyond it; they are let go after
    /// that picture rather than held until the next rest (the sample packs, all of them, need about 75 MB together).
    /// </summary>
    public const long MaxHeldBytes = 128L << 20;

    /// <summary> The widest picture drawn (wider asks get this): 1024 would need about 190 MB of drawing buffers. </summary>
    public const int MaxWidth = 512;

    /// <summary> Less of the picture than this, told from the background, is no picture (glow effects, a few pixels). </summary>
    public const double NothingShare = 0.006;

    private readonly PhotoBudget _budget;
    private PhotoPools _pools = new();
    private JpegWriter _jpeg = new();

    // the last mod opened: the queue takes a mod's parts one after another, and reading its definition and listing its
    // folder again for each would be most of the garbage of a picture
    private string _modKey = string.Empty;
    private ModSource? _source;
    private ModFiles? _files;

    private readonly IGameFiles? _gameFiles;

    /// <summary>
    /// The mods pictures stood on lately (<see cref="PhotoAsk.Under"/>: a makeup shelf stands on the same few sculpts),
    /// by folder, with the stamp they were read with; at most <see cref="MaxUnder"/>, let go in <see cref="Rest"/>.
    /// </summary>
    private readonly List<(string Path, string Key, UnderMod Mod)> _under = new();

    /// <summary> Mods one picture may stand on (the closet gives the best few), and mods kept open between pictures. </summary>
    public const int MaxUnder = 4;

    /// <summary> The game's files the maker reads through (null without them), kept between takes until <see cref="Rest"/>. </summary>
    private GameCache? _game;

    /// <param name="game"> The game's own files, for what a mod does not bring (brief 13); null draws from the mod alone. </param>
    public PhotoMaker(IGameFiles? game = null)
        : this(PhotoBudget.Default, game)
    {
    }

    public PhotoMaker(PhotoBudget budget, IGameFiles? game = null)
    {
        _budget    = budget;
        _gameFiles = game;
        _game      = game is null ? null : new GameCache(game);
    }

    public int Version
        => 1;

    /// <summary>
    /// 1 for every kind of part but faces. The clothes and jewellery drawn in 0.5 come out the same, so none of them is
    /// taken again; hair and the rest as in 0.6.0. Faces and face textures are 2 (after 0.6.0): a face's optional
    /// features (scars, marks) are left off, makeup stands on its sculpt (<see cref="PhotoAsk.Under"/>), every head is
    /// framed whole at one size, and a face's missing skin is the race's plain skin rather than grey.
    /// </summary>
    public int VersionOf(Socket socket)
        => socket is Socket.Face or Socket.FaceTextures ? 2 : 1;

    /// <summary> Gear, hair, ears, tails, faces, makeup, bodies and skin, weapons and the rest (brief 13). </summary>
    public bool Takes(Socket socket)
        => socket is >= Socket.Head and <= Socket.OffHand or Socket.Hair or Socket.Face or Socket.Tail or Socket.EarShape
            or Socket.BodyBase or Socket.FaceTextures or Socket.Other;

    /// <summary> Game files read since the maker was made, and asked for but not there (for the closet's log line). </summary>
    public (int Read, int Missing) GameFileCounts
        => (_reads + (_game?.Reads ?? 0), _missing + (_game?.Missing ?? 0));

    private int _reads, _missing;

    /// <summary> The take's files while it runs (its game counts go to the report). </summary>
    private PhotoFiles? _take;

    /// <summary> The working memory held between pictures, by part (for the report and the log). </summary>
    public IReadOnlyList<(string Part, long Bytes)> Held()
        => _pools.Held().Append(("game files", _game?.HeldBytes ?? 0)).ToList();

    /// <summary> What the last <see cref="Take"/> took and found missing. </summary>
    public PhotoReport LastReport { get; private set; } = new();

    public PhotoShot Take(PhotoAsk ask, CancellationToken stop)
    {
        var report = new PhotoReport();
        LastReport = report;
        try
        {
            return TakeUnsafe(ask, stop, report);
        }
        catch (OperationCanceledException)
        {
            return new PhotoShot { Problem = PhotoProblems.Stopped };
        }
        catch (Exception ex)
        {
            // last line of defence: a mod must never break the closet; the pools may be half written, start them again
            _pools = new PhotoPools();
            return new PhotoShot { Problem = PhotoProblems.Exception(ex), Detail = $"{ex.GetType().Name}: {ex.Message}" };
        }
        finally
        {
            if (_take is { } files)
                (report.GameAsks, report.GameBytes) = (files.GameAsks, files.GameBytes);
            _take = null;
            if (_pools.HeldBytes > MaxHeldBytes)
                _pools = new PhotoPools();
        }
    }

    public void Rest()
    {
        _pools = new PhotoPools();
        _jpeg  = new JpegWriter();
        Forget();
        foreach (var u in _under)
            u.Mod.Source.Dispose();
        _under.Clear();
        if (_game is not null)
        {
            // the dummy's files go too; the counts stay for the log
            (_reads, _missing) = (_reads + _game.Reads, _missing + _game.Missing);
            _game = new GameCache(_gameFiles!);
        }
    }

    private PhotoShot TakeUnsafe(PhotoAsk ask, CancellationToken stop, PhotoReport report)
    {
        if (!Takes(ask.Socket))
            return new PhotoShot { Problem = PhotoProblems.NotTaken, Detail = ask.Socket.ToString() };
        // a card's picture is 170 wide (340 on a sharp screen): 512 is plenty, and the drawing buffers grow with its square
        var width  = Math.Clamp(ask.Width, 64, MaxWidth) / 4 * 4;
        var height = width * 5 / 4;

        var watch = Stopwatch.StartNew();
        if (Open(ask.ModFolder) is not var (source, mod))
            return new PhotoShot { Problem = PhotoProblems.ModNotOpened, Detail = ask.ModFolder };

        // what the picture is made of, from the definition and (for what the mod does not bring) the game's paths
        _pools.Begin();
        var files = new PhotoFiles(source, mod, _game, _pools, _budget, stop, OpenUnder(ask));
        _take = files;
        var plan  = PartPlanner.Plan(files, ask);
        report.Kind = plan.Kind;
        if (plan.Problem.Length > 0)
            return new PhotoShot { Problem = plan.Problem, Detail = plan.Detail };

        stop.ThrowIfCancellationRequested();
        if (plan.Kind == PieceKind.Flat)
            return TakeFlat(files, plan, width, height, report, watch);

        var scene = SceneReader.Read(files, plan, _pools, _budget);
        report.ModelGamePath = scene.ModelGamePath;
        report.Race          = plan.Race;
        report.ReadMs        = watch.Elapsed.TotalMilliseconds;
        report.Triangles     = scene.Triangles;
        report.Missing.AddRange(scene.Missing);
        if (scene.Refused.Length > 0)
            return new PhotoShot { Problem = scene.Refused, Detail = scene.RefusedDetail };

        stop.ThrowIfCancellationRequested();
        watch.Restart();
        var camera = Framing.Frame(scene, width, height, _pools);
        report.View    = camera.Description;
        report.Camera  = camera;
        report.FrameMs = watch.Elapsed.TotalMilliseconds;

        stop.ThrowIfCancellationRequested();
        watch.Restart();
        files.UseGame = plan.Kind != PieceKind.Gear;
        TextureLoader.Load(scene, files, camera, width, height, _pools, _budget, stop);
        report.TextureMs = watch.Elapsed.TotalMilliseconds;
        report.BytesRead = scene.BytesRead;
        report.Textures.AddRange(scene.Textures);
        report.Missing.Clear();
        report.Missing.AddRange(scene.Missing);
        if (scene.Refused.Length > 0)
            return new PhotoShot { Problem = scene.Refused, Detail = scene.RefusedDetail };

        stop.ThrowIfCancellationRequested();
        watch.Restart();
        var image = Studio.Draw(scene, camera, width, height, Supersample, _pools, stop, _budget.MaxOverdraw);
        report.DrawMs        = watch.Elapsed.TotalMilliseconds;
        report.Seen          = image.Seen / (double)(width * height);
        report.LightBackdrop = image.LightBackdrop;
        if (image.Refused.Length > 0)
            return new PhotoShot { Problem = image.Refused, Detail = image.RefusedDetail };

        // no picture rather than an empty one: too little of the piece can be told from the card
        if (report.Seen < NothingShare)
            return new PhotoShot { Nothing = true };

        return Encode(image, width, height, report, watch);
    }

    /// <summary> The flat picture: the part's texture as it is, when there is nothing to put it on. </summary>
    private PhotoShot TakeFlat(PhotoFiles files, PartPlan plan, int width, int height, PhotoReport report, Stopwatch watch)
    {
        var file = plan.Flat!.Value;
        report.ModelGamePath = file.GamePath;
        report.View          = "the texture itself, flat";
        var image = TextureLoader.Read(files, file, 512, _pools, _budget, out var header, out var why);
        report.ReadMs = watch.Elapsed.TotalMilliseconds;
        if (image is null)
            return new PhotoShot { Problem = PhotoProblems.NoModelOrTexture, Detail = $"{file.GamePath}: {why}" };
        report.Textures.Add($"{SceneReader.Name(file.GamePath)} {image.Width}x{image.Height} (of {header.Width}x{header.Height})");

        watch.Restart();
        var oneChannel = header.Format is TexDecoder.Bc4 or TexDecoder.L8 or TexDecoder.A8;
        var flat = Studio.Flat(image, oneChannel, width, height, _pools);
        report.DrawMs = watch.Elapsed.TotalMilliseconds;
        report.Seen   = flat.Seen / (double)(width * height);
        if (report.Seen < NothingShare)
            return new PhotoShot { Nothing = true };
        return Encode(flat, width, height, report, watch);
    }

    private PhotoShot Encode(PhotoImage image, int width, int height, PhotoReport report, Stopwatch watch)
    {
        watch.Restart();
        var file = _jpeg.Encode(image.Rgba.AsSpan(0, width * height * 4), width, height);
        report.EncodeMs = watch.Elapsed.TotalMilliseconds;
        return new PhotoShot { File = file, Extension = ".jpg" };
    }

    /// <summary> The mod's source and files, from the last call when the mod's settings files did not change since. </summary>
    private (ModSource, ModFiles)? Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var key = Stamp(path);
        if (key.Length > 0 && key == _modKey && _source is not null && _files is not null)
            return (_source, _files);

        Forget();
        var source = ModSource.Open(path);
        if (source is null)
            return null;
        _source = source;
        _files  = new ModFiles(ModDefinition.Read(source));
        _modKey = key;
        return (_source, _files);
    }

    /// <summary>
    /// The mods the ask says the picture may stand on, opened like any mod (same reader, same distrust), the first
    /// <see cref="MaxUnder"/> that open; the mod itself and folders that cannot be opened are passed over.
    /// </summary>
    private List<UnderMod> OpenUnder(PhotoAsk ask)
    {
        var list = new List<UnderMod>();
        if (ask.Under is not { Count: > 0 } under)
            return list;
        foreach (var path in under.Take(MaxUnder * 2))
        {
            if (list.Count >= MaxUnder || string.IsNullOrWhiteSpace(path))
                break;
            var key = Stamp(path);
            if (key.Length == 0 || key == _modKey)
                continue;
            var at = _under.FindIndex(u => u.Path == path);
            if (at >= 0 && _under[at].Key == key)
            {
                list.Add(_under[at].Mod);
                continue;
            }

            if (at >= 0)
            {
                _under[at].Mod.Source.Dispose();
                _under.RemoveAt(at);
            }

            if (ModSource.Open(path) is not { } source)
                continue;
            var mod = new UnderMod(source, new ModFiles(ModDefinition.Read(source)));
            _under.Add((path, key, mod));
            list.Add(mod);
        }

        // keep the latest few open; the oldest go (never one this picture uses)
        while (_under.Count > MaxUnder && _under.FindIndex(u => !list.Contains(u.Mod)) is var old and >= 0)
        {
            _under[old].Mod.Source.Dispose();
            _under.RemoveAt(old);
        }

        return list;
    }

    private void Forget()
    {
        _source?.Dispose();
        _source = null;
        _files  = null;
        _modKey = string.Empty;
    }

    /// <summary>
    /// What says a mod's definition is the one read before: its path and the size and time of the settings files at its
    /// root (meta.json, default_mod.json, group_*.json), or of the pack. "" when it cannot be said (never reused).
    /// </summary>
    private static string Stamp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var f = new FileInfo(path);
                return $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";
            }

            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
                return string.Empty;
            var stamp = new System.Text.StringBuilder(dir.FullName);
            foreach (var f in dir.EnumerateFiles("*.json"))
                stamp.Append('|').Append(f.Name).Append(':').Append(f.Length).Append(':').Append(f.LastWriteTimeUtc.Ticks);
            return stamp.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The races the mod has models for, most models first (ties: the race whose models are on by default, then the lower
    /// code). Used by the model choice, and by the tool to fill its asks the way the library does.
    /// </summary>
    public static IReadOnlyList<int> RacesByCount(ModDefinition def)
    {
        var count = new Dictionary<int, (int All, int On)>();
        foreach (var (_, option, on) in def.AllOptions())
            foreach (var f in option.Files)
            {
                var info = GamePaths.Classify(f.GamePath);
                if (info.File != FileKind.Model || !GamePaths.IsGear(info.Socket) || info.Race <= 0)
                    continue;
                var c = count.GetValueOrDefault(info.Race);
                count[info.Race] = (c.All + 1, c.On + (on ? 1 : 0));
            }

        return count.OrderByDescending(kv => kv.Value.All).ThenByDescending(kv => kv.Value.On).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToList();
    }
}
