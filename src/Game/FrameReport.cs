using System.Diagnostics;
using System.Text;

namespace ModCloset2.Game;

/// <summary>
/// What one part of a framework update did, for the log line when it takes long: the steps that ran ("keeping 3 parts")
/// and the calls into other plugins (how many, their time together, the slowest one by name). Per thread, so only the
/// framework thread's own work is counted. Cheap when nothing is slow: two timestamps per call, a few numbers, no
/// allocation (the step list is made once per thread). Names and steps are fixed English words and counts: never a mod's
/// name (users paste the log in public).
/// </summary>
public static class FrameReport
{
    /// <summary> Steps kept per part of the update (more are counted, not named). </summary>
    public const int MaxSteps = 8;

    [ThreadStatic] private static int _calls;
    [ThreadStatic] private static long _callTicks;
    [ThreadStatic] private static string? _slowest;
    [ThreadStatic] private static long _slowestTicks;
    [ThreadStatic] private static (string What, int Count)[]? _steps;
    [ThreadStatic] private static int _stepCount;
    [ThreadStatic] private static int _moreSteps;
    [ThreadStatic] private static long _gcPauseTicks;
    [ThreadStatic] private static int _gcCount;
    [ThreadStatic] private static double _ownMs;
    [ThreadStatic] private static double _lastOwnMs;

    /// <summary> A new frame of the framework update starts: what the closet's parts took in the last one is <see cref="LastOwnMs"/>. </summary>
    public static void NewFrame()
    {
        _lastOwnMs = _ownMs;
        _ownMs     = 0;
    }

    /// <summary> One part of the update took this long (adds to this frame's own time). </summary>
    public static void PartEnded(double ms)
        => _ownMs += ms;

    /// <summary> What the closet's parts of the update took together in the frame before (the pace skips periodic reads after a long one). </summary>
    public static double LastOwnMs
        => _lastOwnMs;

    /// <summary> Starts counting for one part of the update. </summary>
    public static void Begin()
    {
        _gcPauseTicks = GC.GetTotalPauseDuration().Ticks;
        _gcCount      = GC.CollectionCount(0);
        _calls        = 0;
        _callTicks    = 0;
        _slowest      = null;
        _slowestTicks = 0;
        _stepCount    = 0;
        _moreSteps    = 0;
    }

    /// <summary> A call into another plugin starts: the timestamp to hand to <see cref="CallEnded"/>. </summary>
    public static long CallStarted()
        => Stopwatch.GetTimestamp();

    /// <summary> A call into another plugin ended. <paramref name="name"/> is a constant ("Penumbra.ResourcePaths"). </summary>
    public static void CallEnded(string name, long started)
    {
        var ticks = Stopwatch.GetTimestamp() - started;
        _calls++;
        _callTicks += ticks;
        if (ticks > _slowestTicks)
        {
            _slowestTicks = ticks;
            _slowest      = name;
        }
    }

    /// <summary>
    /// A step of the closet ran ("trying on", 3 parts). The same step right after itself adds its count. <paramref name="what"/>
    /// is a constant phrase that reads with the count after it ("trying on" 3 → "trying on 3"), or alone with 0.
    /// </summary>
    public static void Step(string what, int count = 0)
    {
        var steps = _steps ??= new (string, int)[MaxSteps];
        if (_stepCount > 0 && _moreSteps == 0 && ReferenceEquals(steps[_stepCount - 1].What, what))
        {
            steps[_stepCount - 1].Count += count;
            return;
        }

        if (_stepCount < MaxSteps)
            steps[_stepCount++] = (what, count);
        else
            _moreSteps++;
    }

    /// <summary> How many calls into other plugins were counted since <see cref="Begin"/> (the tests read it). </summary>
    public static int Calls
        => _calls;

    /// <summary> The log line for a part of the update that took <paramref name="ms"/>. </summary>
    public static string Describe(string name, double ms)
    {
        var text = new StringBuilder($"Mod Closet 2: {name} took {ms:0} ms");
        if (_stepCount > 0)
        {
            text.Append(" · ");
            for (var i = 0; i < _stepCount; ++i)
            {
                if (i > 0)
                    text.Append(", ");
                var (what, count) = _steps![i];
                text.Append(what);
                if (count > 0)
                    text.Append(' ').Append(count);
            }

            if (_moreSteps > 0)
                text.Append($" and {_moreSteps} more");
        }

        if (_calls > 0)
        {
            text.Append($" · {_calls} call{(_calls == 1 ? string.Empty : "s")} to other plugins, {Ms(_callTicks):0} ms");
            if (_slowest is not null)
                text.Append($", slowest {_slowest} {Ms(_slowestTicks):0} ms");
        }
        else
        {
            text.Append(" · no calls to other plugins");
        }

        // the runtime's garbage collection stops every thread, the frame's too: then it was not the closet's work
        var paused = TimeSpan.FromTicks(GC.GetTotalPauseDuration().Ticks - _gcPauseTicks).TotalMilliseconds;
        var gcs    = GC.CollectionCount(0) - _gcCount;
        if (paused >= 1 || gcs > 0)
            text.Append($" · garbage collection {gcs}×, paused {paused:0} ms");

        return text.ToString();
    }

    private static double Ms(long ticks)
        => ticks * 1000.0 / Stopwatch.Frequency;
}

/// <summary> When a slow part of the update is logged: over its limit, at most once per 5 s per part, always over 200 ms. </summary>
public static class SlowFrames
{
    public const double AlwaysMs = 200;
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(5);

    public static bool ShouldLog(string name, double ms, double limitMs, DateTime now, Dictionary<string, DateTime> logged)
    {
        if (ms < limitMs)
            return false;
        if (ms < AlwaysMs && logged.TryGetValue(name, out var last) && now - last < Quiet)
            return false;
        logged[name] = now;
        return true;
    }
}
