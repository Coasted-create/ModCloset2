using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace ModCloset2.Ui.Game;

/// <summary>
/// Mod pictures for the window, loaded lazily through Dalamud's texture provider (it decodes on its own threads, never
/// in Draw). Until a picture has arrived the caller draws the tinted tile. Only a few new loads start per frame
/// (scrolling through thousands of cards must not start thousands of loads), and the cache is bounded by count and by
/// memory: the least recently drawn pictures are let go first.
/// </summary>
public sealed class PictureCache : IDisposable
{
    public const int MaxNewPerFrame = 4;
    public const int MaxLoading     = 12;
    public const int MaxKept        = 240;

    /// <summary> About 400 MB of RGBA pixels: big mod pictures are often 2,000 px a side. </summary>
    public const long MaxBytes = 400L << 20;

    private sealed class Entry
    {
        public Task<IDalamudTextureWrap>? Task;
        public IDalamudTextureWrap? Wrap;
        public bool Failed;
        public long Frame;
        public long Bytes;
    }

    private readonly ITextureProvider _textures;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _frame;
    private int  _startedThisFrame;
    private int  _loading;
    private long _bytes;

    public PictureCache(ITextureProvider textures)
        => _textures = textures;

    public int Count
        => _entries.Count;

    /// <summary> The picture if it is ready; null while it loads, when it failed, or when this frame's loads are used up. </summary>
    public IDalamudTextureWrap? Get(string path)
    {
        if (!_entries.TryGetValue(path, out var e))
        {
            if (_startedThisFrame >= MaxNewPerFrame || _loading >= MaxLoading)
                return null;
            e = new Entry();
            try
            {
                e.Task = _textures.GetFromFile(path).RentAsync();
                _loading++;
            }
            catch
            {
                // a path the provider refuses outright: show the tile, do not ask again
                e.Failed = true;
            }

            _startedThisFrame++;
            _entries[path] = e;
        }

        e.Frame = _frame;
        if (e.Task is { IsCompleted: true } task)
        {
            _loading--;
            e.Task = null;
            if (task.IsCompletedSuccessfully)
            {
                e.Wrap  =  task.Result;
                e.Bytes =  (long)e.Wrap.Width * e.Wrap.Height * 4;
                _bytes  += e.Bytes;
            }
            else
                e.Failed = true;
        }

        return e.Failed ? null : e.Wrap;
    }

    /// <summary> Call once per frame after drawing: lets go of what was not drawn lately when over the limits. </summary>
    public void EndFrame()
    {
        _frame++;
        _startedThisFrame = 0;
        if (_entries.Count <= MaxKept && _bytes <= MaxBytes)
            return;

        foreach (var (path, e) in _entries.Where(kv => kv.Value.Frame < _frame - 1 && kv.Value.Task is null).OrderBy(kv => kv.Value.Frame).ToList())
        {
            if (_entries.Count <= MaxKept * 3 / 4 && _bytes <= MaxBytes * 3 / 4)
                break;
            Release(e);
            _entries.Remove(path);
        }
    }

    /// <summary> Forget everything (the library was read again: pictures may have changed). </summary>
    public void Clear()
    {
        foreach (var e in _entries.Values)
            Release(e);
        _entries.Clear();
        _loading = 0;
    }

    private void Release(Entry e)
    {
        _bytes -= e.Bytes;
        e.Wrap?.Dispose();
        e.Wrap = null;
        // still loading: dispose it when it arrives, nobody will draw it
        e.Task?.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                t.Result.Dispose();
        }, TaskScheduler.Default);
    }

    public void Dispose()
        => Clear();
}
