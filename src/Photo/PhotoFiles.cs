using ModCloset2.Core;
using ModCloset2.Detect;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary>
/// A file a picture needs: its game path, and the local file for it, or null when it is the game's own.
/// <see cref="Layer"/> says whose local file: 0 the mod's, 1 and on the mods the picture stands on
/// (<see cref="PhotoAsk.Under"/>, in their order).
/// </summary>
public readonly record struct FileRef(string GamePath, string? Local, int Layer = 0)
{
    public bool FromGame
        => Local is null;

    /// <summary> A file of the mod itself (not of a mod it stands on, not the game's). </summary>
    public bool IsMod
        => Local is not null && Layer == 0;
}

/// <summary> A mod a picture may stand on (<see cref="PhotoAsk.Under"/>): its source and files, default options. </summary>
public sealed record UnderMod(ModSource Source, ModFiles Mod);

/// <summary>
/// The game's files a stretch of work reads, kept between takes (a hair shelf is hundreds of pictures on the same head)
/// and let go in <see cref="PhotoMaker.Rest"/>. Models and materials are kept whole (32 MB at most, files up to 8 MB).
/// Textures are not: <see cref="IGameFiles.Read"/> gives a whole file, of which a picture uses one level, so only the
/// header and the levels from the one used down to the smallest are kept (<see cref="TextureTail"/>, 24 MB at most,
/// 8 MB a texture); a later picture that needs a finer level reads the file again. Either way the bytes a picture uses
/// are the file's own, so a picture does not depend on what was read before it.
/// A file the game does not have is remembered as such. The game's files obey the same distrust as a mod's: the
/// readers check them, and one larger than <see cref="MaxFileBytes"/> is treated as not there.
/// </summary>
public sealed class GameCache(IGameFiles game)
{
    public const long MaxBytes = 32L << 20;
    public const int MaxFileBytes = 96 << 20;

    /// <summary> Files larger than this are read again when needed rather than kept (textures; the dummy's model fits). </summary>
    public const int MaxKeptFileBytes = 8 << 20;

    /// <summary> What is kept of the textures read (<see cref="TextureTail"/>), and of one texture. </summary>
    public const long MaxTailBytes = 24L << 20;
    public const int MaxKeptTailBytes = 8 << 20;

    /// <summary> What is kept of a game texture: its header, its length, and its bytes from <see cref="Start"/> to the end. </summary>
    public sealed record TextureTail(byte[] Head, long Length, long Start, byte[] Bytes);

    private readonly Dictionary<string, TextureTail> _tails = new(StringComparer.Ordinal);
    private long _tailBytes;

    /// <summary>
    /// The answers remembered about which files the game has (and has not), at most: a first pass with the closet
    /// open never rests. When full, every remembered answer is dropped (the files kept stay).
    /// </summary>
    public const int MaxRemembered = 50_000;

    private int _misses;

    /// <summary> Bytes the game gave since this cache began (what pictures allocated from the game). </summary>
    public long FreshBytes { get; private set; }

    /// <summary> Answers remembered now (for the tests and the log). </summary>
    public int Remembered
        => _has.Count + _misses;

    private void Remember()
    {
        if (Remembered < MaxRemembered)
            return;
        _has.Clear();
        foreach (var key in _files.Where(f => f.Value is null).Select(f => f.Key).ToList())
            _files.Remove(key);
        _misses = 0;
    }

    private readonly Dictionary<string, byte[]?> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _has = new(StringComparer.Ordinal);
    private long _bytes;

    /// <summary>
    /// Files read from the game, and reads that gave nothing (for the log). Asking whether the game has a file (the race
    /// and face numbers tried one after another) is not counted: only files a picture went on to read.
    /// </summary>
    public int Reads, Missing;

    public bool Has(string gamePath)
    {
        if (_files.TryGetValue(gamePath, out var known))
            return known is not null;
        if (_has.TryGetValue(gamePath, out var has))
            return has;
        try
        {
            has = game.Has(gamePath);
        }
        catch (Exception)
        {
            has = false; // the contract says it never throws; the game's side is not ours to trust either
        }

        Remember();
        return _has[gamePath] = has;
    }

    /// <summary> True when the file is kept (reading it costs nothing). </summary>
    public bool Keeps(string gamePath)
        => _files.TryGetValue(gamePath, out var known) && known is not null;

    public byte[]? Read(string gamePath)
    {
        if (_files.TryGetValue(gamePath, out var known))
            return known;
        var bytes = ReadOnce(gamePath);
        if (bytes is null)
        {
            Remember();
            ++_misses;
        }

        // keep what fits; a full cache keeps the earlier files (the dummy's, read first, are the ones used again)
        if (bytes is null || bytes.Length <= MaxKeptFileBytes && _bytes + bytes.Length <= MaxBytes)
        {
            _files[gamePath] = bytes;
            _bytes += bytes?.Length ?? 0;
        }

        return bytes;
    }

    /// <summary> What is kept of a texture, when anything is. </summary>
    public TextureTail? Tail(string gamePath)
        => _tails.GetValueOrDefault(gamePath);

    /// <summary> Keeps what a picture kept of a texture, when it is more than is kept already and fits. </summary>
    public void KeepTail(string gamePath, TextureTail tail)
    {
        var size = tail.Head.Length + tail.Bytes.Length;
        if (size > MaxKeptTailBytes)
            return;
        var old = _tails.GetValueOrDefault(gamePath);
        if (old is not null && old.Start <= tail.Start)
            return;
        var oldSize = old is null ? 0 : old.Head.Length + old.Bytes.Length;
        if (_tailBytes - oldSize + size > MaxTailBytes)
            return;
        _tailBytes += size - oldSize;
        _tails[gamePath] = tail;
    }

    /// <summary> A file read and not kept (a texture: what is kept of it is its <see cref="TextureTail"/>). </summary>
    public byte[]? ReadOnce(string gamePath)
    {
        if (_files.TryGetValue(gamePath, out var known))
            return known;
        byte[]? bytes = null;
        try
        {
            bytes = game.Read(gamePath);
        }
        catch (Exception)
        {
            bytes = null;
        }

        if (bytes is { Length: > MaxFileBytes })
            bytes = null;
        if (bytes is null)
        {
            ++Missing;
        }
        else
        {
            ++Reads;
            FreshBytes += bytes.Length;
        }

        return bytes;
    }

    public long HeldBytes
        => _bytes + _tailBytes;
}

/// <summary>
/// Where every file of a picture comes from: the mod with its default settings first, then the game's own file
/// (brief 13). <see cref="UseGame"/> is false for the meshes of a gear model the mod brings itself, so the pictures of
/// 0.5 (drawn from the mod alone) come out the same.
/// </summary>
/// <para>
/// One picture's questions to the game are counted and capped (<see cref="PhotoBudget.MaxGameAsks"/>), and so are the
/// bytes the game gives it (<see cref="PhotoBudget.MaxGameBytes"/>); the game is asked only for paths of the kind
/// wanted (a model ".mdl", a material ".mtrl", a texture ".tex"); every question sees the stop.
/// </para>
public sealed class PhotoFiles(ModSource source, ModFiles mod, GameCache? game, PhotoPools pools, PhotoBudget? budget = null,
    CancellationToken stop = default, IReadOnlyList<UnderMod>? under = null)
{
    public ModSource Source { get; } = source;

    /// <summary> The mods the picture may stand on, best first (looked in after the mod, before the game). </summary>
    public IReadOnlyList<UnderMod> Under { get; } = under ?? Array.Empty<UnderMod>();

    /// <summary> The source a file's local path belongs to. </summary>
    public ModSource SourceOf(FileRef f)
        => f.Layer > 0 && f.Layer <= Under.Count ? Under[f.Layer - 1].Source : Source;
    public ModFiles Mod { get; } = mod;
    public GameCache? Game { get; } = game;
    public bool UseGame { get; set; } = true;
    public CancellationToken Stop { get; } = stop;
    public PhotoBudget Budget { get; } = budget ?? PhotoBudget.Default;

    private readonly long _startBytes = game?.FreshBytes ?? 0;

    /// <summary> Questions this picture asked the game, and whether it asked all it may. </summary>
    public int GameAsks { get; private set; }

    public bool AsksRanOut
        => GameAsks >= Budget.MaxGameAsks;

    /// <summary> Bytes the game gave this picture (files it read, not those kept from before). </summary>
    public long GameBytes
        => (Game?.FreshBytes ?? 0) - _startBytes;

    /// <summary> False once this picture has read as much of the game as it may. </summary>
    public bool MayReadGame
        => GameBytes < Budget.MaxGameBytes;

    /// <summary> The kinds of file the game is asked for. </summary>
    public static bool IsGameKind(string gamePath, string? extension = null)
        => extension is not null
            ? gamePath.EndsWith(extension, StringComparison.Ordinal)
            : gamePath.EndsWith(".mdl", StringComparison.Ordinal) || gamePath.EndsWith(".mtrl", StringComparison.Ordinal)
              || gamePath.EndsWith(".tex", StringComparison.Ordinal);

    private bool AskGame(string p, string? extension)
    {
        Stop.ThrowIfCancellationRequested();
        if (Game is null || !IsGameKind(p, extension) || AsksRanOut)
            return false;
        ++GameAsks;
        return Game.Has(p);
    }

    /// <summary> The game's file, unless it is not kept and this picture has read all it may. </summary>
    private byte[]? GameRead(string gamePath)
        => Game is null || !Game.Keeps(gamePath) && !MayReadGame ? null : Game.Read(gamePath);

    /// <summary> A game file read for this picture and not kept (a texture), within its bytes; null past them. </summary>
    public byte[]? GameReadOnce(string gamePath)
        => Game is null || !MayReadGame ? null : Game.ReadOnce(gamePath);

    public List<int> RacesByCountAll
        => Mod.RacesByCountAll;

    /// <summary>
    /// The file for a game path: the mod's; else (when the game is used) that of the first mod it stands on that has it;
    /// else the game's when it has one (the path of the kind <paramref name="extension"/> or one the game is asked for,
    /// and the picture may still ask); else null.
    /// </summary>
    public FileRef? Find(string gamePath, bool modOnly = false, string? extension = null)
    {
        var p = ModDefinition.NormalizeGamePath(gamePath);
        if (Mod.Local(p) is { } local)
            return new FileRef(p, local);
        if (modOnly || !UseGame)
            return null;
        return FindUnder(p) ?? (AskGame(p, extension) ? new FileRef(p, null) : null);
    }

    /// <summary> The file of the first mod the picture stands on that has it. </summary>
    public FileRef? FindUnder(string gamePath)
    {
        var p = ModDefinition.NormalizeGamePath(gamePath);
        for (var i = 0; i < Under.Count; ++i)
            if (Under[i].Mod.Local(p) is { } local)
                return new FileRef(p, local, i + 1);
        return null;
    }

    /// <summary> The game's own model only (the dummy: never a file of the mod). </summary>
    public FileRef? FindGame(string gamePath)
    {
        var p = ModDefinition.NormalizeGamePath(gamePath);
        return AskGame(p, ".mdl") ? new FileRef(p, null) : null;
    }

    /// <summary> The size of a file, -1 when it is not there. </summary>
    public long Length(FileRef f)
        => f.Local is { } l ? SourceOf(f).Length(l) : GameRead(f.GamePath)?.Length ?? -1;

    /// <summary> A large file (a model): from a mod folder into the pools' buffer, else the whole array. </summary>
    public (byte[] Bytes, int Length)? ReadLarge(FileRef f, long maxBytes)
    {
        if (f.Local is { } l)
            return pools.ReadFile(SourceOf(f), l, maxBytes);
        return GameRead(f.GamePath) is { } b && b.Length <= maxBytes ? (b, b.Length) : null;
    }

    /// <summary> A small file (a material): its bytes. </summary>
    public byte[]? ReadSmall(FileRef f, long maxBytes)
    {
        if (f.Local is { } l)
            return SourceOf(f).Read(l, maxBytes);
        return GameRead(f.GamePath) is { } b && b.Length <= maxBytes ? b : null;
    }
}
