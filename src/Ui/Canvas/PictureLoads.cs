namespace ModCloset2.Ui.Canvas;

/// <summary>
/// The bookkeeping behind the window's pictures, without the game: loads started lazily (a few per frame, a bounded
/// number at once), finished loads collected every frame whether or not their card is still drawn, failed loads tried
/// again after a while (a few times, then given up for the session), loads that never finish abandoned, and the least
/// recently drawn pictures let go when over the limits. <typeparamref name="T"/> is the loaded picture (a texture in
/// the game, anything in tests).
/// </summary>
public sealed class PictureLoads<T> where T : class
{
    public int MaxNewPerFrame { get; init; } = 4;
    public int MaxLoading { get; init; } = 12;
    public int MaxKept { get; init; } = 240;
    public long MaxBytes { get; init; } = 400L << 20;

    /// <summary> Seconds before a failed load is tried again (times the number of failures so far). </summary>
    public double RetryAfter { get; init; } = 2;

    /// <summary> Tries after the first failure before a picture is given up for the session. </summary>
    public int Retries { get; init; } = 3;

    /// <summary> Seconds after which a load that has not finished is abandoned (and counted as a failure). </summary>
    public double GiveUpLoadAfter { get; init; } = 30;

    private sealed class Entry
    {
        public Task<T>? Task;
        public double StartedAt;
        public T? Item;
        public int Failures;
        public double RetryAt;
        public bool GaveUp;
        public long Frame;
        public long Bytes;
    }

    private readonly Func<string, Task<T>> _start;
    private readonly Func<T, long> _bytes;
    private readonly Action<T> _release;
    private readonly Func<double> _now;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _frame;
    private int _startedThisFrame;
    private long _bytesKept;

    public PictureLoads(Func<string, Task<T>> start, Func<T, long> bytes, Action<T> release, Func<double> now)
    {
        _start   = start;
        _bytes   = bytes;
        _release = release;
        _now     = now;
    }

    public int Count
        => _entries.Count;

    /// <summary> Loads under way now. </summary>
    public int Loading { get; private set; }

    /// <summary> Loads started since this was made (tests read it). </summary>
    public int Started { get; private set; }

    /// <summary> The picture if it is ready; null while it loads, after a failure (until it is tried again), or when this frame's loads are used up. </summary>
    public T? Get(string path)
    {
        if (!_entries.TryGetValue(path, out var e))
        {
            if (!CanStart())
                return null;
            e = new Entry();
            _entries[path] = e;
            Start(path, e);
        }

        e.Frame = _frame;
        Collect(e);
        if (e.Item is null && e.Task is null && !e.GaveUp && e.Failures > 0 && _now() >= e.RetryAt && CanStart())
            Start(path, e);
        return e.Item;
    }

    private bool CanStart()
        => _startedThisFrame < MaxNewPerFrame && Loading < MaxLoading;

    private void Start(string path, Entry e)
    {
        _startedThisFrame++;
        Started++;
        try
        {
            e.Task      = _start(path);
            e.StartedAt = _now();
            Loading++;
        }
        catch
        {
            Failed(e);
        }
    }

    /// <summary> A finished load is taken in (or counted as failed); one that hangs too long is abandoned. </summary>
    private void Collect(Entry e)
    {
        if (e.Task is not { } task)
            return;
        if (!task.IsCompleted)
        {
            if (_now() - e.StartedAt <= GiveUpLoadAfter)
                return;
            Loading--;
            e.Task = null;
            ReleaseLater(task);
            Failed(e);
            return;
        }

        Loading--;
        e.Task = null;
        if (task.IsCompletedSuccessfully && task.Result is { } item)
        {
            e.Item      = item;
            e.Bytes     = _bytes(item);
            _bytesKept += e.Bytes;
            e.Failures  = 0;
        }
        else
            Failed(e);
    }

    private void Failed(Entry e)
    {
        e.Failures++;
        if (e.Failures > Retries)
            e.GaveUp = true;
        else
            e.RetryAt = _now() + RetryAfter * e.Failures;
    }

    /// <summary> Once per frame after drawing: takes in every finished load, then lets go of what was not drawn lately when over the limits. </summary>
    public void EndFrame()
    {
        _frame++;
        _startedThisFrame = 0;
        foreach (var e in _entries.Values)
            Collect(e);
        if (_entries.Count <= MaxKept && _bytesKept <= MaxBytes)
            return;

        foreach (var (path, e) in _entries.Where(kv => kv.Value.Frame < _frame - 1 && kv.Value.Task is null).OrderBy(kv => kv.Value.Frame).ToList())
        {
            if (_entries.Count <= MaxKept * 3 / 4 && _bytesKept <= MaxBytes * 3 / 4)
                break;
            Release(e);
            _entries.Remove(path);
        }
    }

    /// <summary> Forget everything (the window closed a while ago, or the library was read again). </summary>
    public void Clear()
    {
        foreach (var e in _entries.Values)
            Release(e);
        _entries.Clear();
        Loading   = 0;
        _bytesKept = 0;
    }

    private void Release(Entry e)
    {
        _bytesKept -= e.Bytes;
        if (e.Item is { } item)
            _release(item);
        e.Item = null;
        if (e.Task is { } task)
            ReleaseLater(task);
        e.Task = null;
    }

    /// <summary> A load nobody will draw: let go of its picture when it arrives. </summary>
    private void ReleaseLater(Task<T> task)
        => task.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result is { } item)
                _release(item);
        }, TaskScheduler.Default);
}
