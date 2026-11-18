using ModCloset2.Closet;
using ModCloset2.Core;

namespace ModCloset2.Game;

/// <summary>
/// Where the game's own files come from (<see cref="LuminaGameFiles"/> in the game, a stand-in in the tests). May throw;
/// <see cref="GameFiles"/> catches.
/// </summary>
public interface IGameFileSource
{
    /// <summary> The file's whole size in bytes as the game keeps it, without reading it; null when the game has no such file. </summary>
    long? SizeOf(string gamePath);

    /// <summary> The whole file in the layout a mod ships it in, as a new array the caller owns; null when the game has none. </summary>
    byte[]? Read(string gamePath);

    /// <summary> Whether the game has a file at this path, from its file lists only. </summary>
    bool Has(string gamePath);
}

/// <summary>
/// The photos' <see cref="IGameFiles"/>: the game's own unmodded files (docs/briefs/13-photos-of-everything.md), with the
/// rules the maker relies on around any <see cref="IGameFileSource"/>:
/// <list type="bullet">
/// <item>only game paths ("chara/human/c0801/…"): a path on the disk (what Penumbra resolves a game path to) is refused;</item>
/// <item>never more than <see cref="MaxBytes"/> for one file, asked before anything is read;</item>
/// <item>never throws: a failure is null, said once a session per kind (the exception's type), never with a path;</item>
/// <item>counted for the worker's summary line (<see cref="IGameFileCounts"/>).</item>
/// </list>
/// Safe from any thread (the counts are atomic, the source must be safe itself); it reads from the disk, so never on the frame.
/// </summary>
public sealed class GameFiles : IGameFiles, IGameFileCounts
{
    /// <summary> No game file is read whole above this (the maker takes no texture file larger, see PhotoBudget). </summary>
    public const long MaxBytes = 96L << 20;

    /// <summary> The game's paths are shorter than this (Lumina refuses longer ones). </summary>
    private const int MaxPath = 260;

    private readonly IGameFileSource _source;
    private readonly Action<string>? _log;
    private readonly HashSet<string> _said = new(StringComparer.Ordinal);
    private long _read;
    private long _missing;
    private long _failed;

    public GameFiles(IGameFileSource source, Action<string>? log = null)
    {
        _source = source;
        _log    = log;
    }

    public (long Read, long Missing, long Failed) GameFileCounts
        => (Interlocked.Read(ref _read), Interlocked.Read(ref _missing), Interlocked.Read(ref _failed));

    public byte[]? Read(string gamePath)
    {
        try
        {
            if (!IsGamePath(gamePath))
                return Fail("a path that is not a game path was refused");

            var size = _source.SizeOf(gamePath);
            if (size is null)
                return Missing();
            if (size > MaxBytes || size < 0)
                return Fail("a game file was larger than a picture may take");

            var bytes = _source.Read(gamePath);
            if (bytes is null)
                return Missing();
            Interlocked.Increment(ref _read);
            return bytes;
        }
        catch (OutOfMemoryException)
        {
            return Fail("a game file could not be read (OutOfMemoryException)");
        }
        catch (Exception ex)
        {
            return Fail($"a game file could not be read ({ex.GetType().Name})");
        }
    }

    public bool Has(string gamePath)
    {
        try
        {
            return IsGamePath(gamePath) && _source.Has(gamePath);
        }
        catch (Exception ex)
        {
            Say($"the game's file list could not be asked ({ex.GetType().Name})");
            return false;
        }
    }

    /// <summary>
    /// A game path: relative, forward slashes, at least one folder, no ".." step, nothing a disk path has (a drive, a
    /// backslash, a leading slash), shorter than the game allows.
    /// </summary>
    public static bool IsGamePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length >= MaxPath || path[0] is '/' or ' ' || path[^1] is '/' or ' ')
            return false;
        if (path.IndexOf('/') <= 0 || path.Contains('\\') || path.Contains(':') || path.Contains("//", StringComparison.Ordinal))
            return false;
        foreach (var step in path.Split('/'))
            if (step is "." or ".." || step.Length == 0)
                return false;
        foreach (var c in path)
            if (char.IsControl(c))
                return false;
        return true;
    }

    private byte[]? Missing()
    {
        Interlocked.Increment(ref _missing);
        return null;
    }

    private byte[]? Fail(string what)
    {
        Interlocked.Increment(ref _failed);
        Say(what);
        return null;
    }

    private void Say(string what)
    {
        bool first;
        lock (_said)
            first = _said.Add(what);
        if (!first)
            return;
        try
        {
            _log?.Invoke($"photos: {what}");
        }
        catch
        {
            // a log that fails is no reason to fail a picture
        }
    }
}
