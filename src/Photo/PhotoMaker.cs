using System.Diagnostics;
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

    public PhotoMaker()
        : this(PhotoBudget.Default)
    {
    }

    public PhotoMaker(PhotoBudget budget)
        => _budget = budget;

    public int Version
        => 1;

    /// <summary> The working memory held between pictures, by part (for the report and the log). </summary>
    public IReadOnlyList<(string Part, long Bytes)> Held()
        => _pools.Held();

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
            if (_pools.HeldBytes > MaxHeldBytes)
                _pools = new PhotoPools();
        }
    }

    public void Rest()
    {
        _pools = new PhotoPools();
        _jpeg  = new JpegWriter();
        Forget();
    }

    private PhotoShot TakeUnsafe(PhotoAsk ask, CancellationToken stop, PhotoReport report)
    {
        if (!GamePaths.IsGear(ask.Socket))
            return new PhotoShot { Problem = PhotoProblems.NotGear, Detail = ask.Socket.ToString() };
        // a card's picture is 170 wide (340 on a sharp screen): 512 is plenty, and the drawing buffers grow with its square
        var width  = Math.Clamp(ask.Width, 64, MaxWidth) / 4 * 4;
        var height = width * 5 / 4;

        var watch = Stopwatch.StartNew();
        if (Open(ask.ModFolder) is not var (source, files))
            return new PhotoShot { Problem = PhotoProblems.ModNotOpened, Detail = ask.ModFolder };

        var (model, option, race) = (ask.ModelGamePath, ask.Option, ask.Race);
        if (model.Length == 0)
        {
            // what the closet asks: the slot and the player's race; the model comes from the definition alone
            if (ModelChoice.Choose(files, ask.Socket, ask.Race) is not { } chosen)
                return new PhotoShot { Problem = PhotoProblems.NoModel, Detail = $"{ask.Socket} in {ask.ModFolder}" };
            (model, option, race) = (chosen.GamePath, string.Empty, chosen.Race);
        }

        stop.ThrowIfCancellationRequested();
        _pools.Begin();
        var scene = SceneReader.Read(source, files, ask.Socket, model, option, race, _pools, _budget);
        report.ModelGamePath = scene.ModelGamePath;
        report.Race          = race;
        report.ReadMs        = watch.Elapsed.TotalMilliseconds;
        report.Triangles     = scene.Triangles;
        report.Missing.AddRange(scene.Missing);
        if (scene.Refused.Length > 0)
            return new PhotoShot { Problem = scene.Refused, Detail = scene.RefusedDetail };

        stop.ThrowIfCancellationRequested();
        watch.Restart();
        var camera = Framing.Frame(scene, width, height, _pools);
        report.View    = camera.Description;
        report.FrameMs = watch.Elapsed.TotalMilliseconds;

        stop.ThrowIfCancellationRequested();
        watch.Restart();
        TextureLoader.Load(scene, source, camera, width, height, _pools, _budget, stop);
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

        stop.ThrowIfCancellationRequested();
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
