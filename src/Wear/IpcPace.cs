namespace ModCloset2.Wear;

/// <summary> The closet's kinds of work that call Penumbra or Glamourer from the framework update. </summary>
public enum IpcWork
{
    /// <summary> A step the user asked for (try on, Keep, Put back, options, undo): it redraws and writes. </summary>
    Step,

    /// <summary> A look saved before a try put back on the character it came from (after a relog). </summary>
    Restore,

    /// <summary> The check after a Keep or a try that what was put on shows. </summary>
    SlotCheck,

    /// <summary> The collection's settings, read every few seconds while a face is open. </summary>
    Settings,

    /// <summary> What shows on the character (Penumbra's resource paths, Glamourer's state), read every few seconds. </summary>
    Truth,

    /// <summary> The colours on their own: following what is worn, checking their rows, putting them on again. </summary>
    Colours,
}

/// <summary>
/// When the closet may call Penumbra and Glamourer from the framework update, frame by frame. Teleporting froze the game
/// (0.8.0): the character is torn down and built again, Glamourer and Penumbra answer slowly then (a GetState took 829 ms),
/// and several calls ran in the same frame. Three rules:
/// <list type="bullet">
/// <item> <b>Quiet while loading</b>: while a loading screen or a cutscene is up, the player has no object, and for
/// <see cref="SettleSeconds"/> after the last such frame (and after a territory change), nothing calls: no reads, no
/// colour repair, no redraw, no option write. What the user asks for waits in the wardrobe's queue. </item>
/// <item> <b>Settled, in order</b>: once quiet ends, first the periodic reads run once (the collection's settings when a
/// face is open, then what shows); only then do the queue and the colours go on (<see cref="AfterLoading"/>). </item>
/// <item> <b>One call per frame</b>: the periodic reads, the slot check, the colours and a look put back each take the
/// frame's turn (<see cref="Take"/>); whoever comes second waits for the next frame. A step the user asked for always
/// runs (and takes the turn). The periodic reads also skip a frame that follows a long one (<see cref="LongFrame"/>),
/// but not for longer than <see cref="MaxSkipSeconds"/>. </item>
/// </list>
/// Pure: the plugin feeds it once per frame (<see cref="Frame"/>), the tests with a clock of their own.
/// </summary>
public sealed class IpcPace
{
    /// <summary> How long after the last loading frame (or a territory change) the closet stays quiet. </summary>
    public const double SettleSeconds = 1.5;

    /// <summary> What the closet's own work may take of a frame. </summary>
    public const double BudgetMs = 8;

    /// <summary> A frame of the game this long is a hitch: the periodic reads wait for a quieter one. </summary>
    public const double LongFrameMs = 50;

    /// <summary> The periodic reads skip long frames, but run after this long anyway (a slow computer has only long frames). </summary>
    public const double MaxSkipSeconds = 1.0;

    /// <summary> The order after loading (refresh first) never holds the queue and the colours longer than this. </summary>
    public const double AfterLoadingTimeout = 5.0;

    /// <summary> Steps the user asked for wait while quiet, but not longer than this (they then run, and say why they cannot). </summary>
    public const double StepWaitLimit = 30.0;

    private readonly Func<double> _clock;
    private double _quietUntil = double.MinValue;
    private double _quietSince = double.NaN;
    private double _afterLoadingSince;
    private double _skippingSince = double.NaN;
    private IpcWork? _taken;

    /// <param name="clock"> Seconds, rising. </param>
    public IpcPace(Func<double> clock)
        => _clock = clock;

    /// <summary> Nothing may call: loading, a cutscene, no player, or the seconds after. </summary>
    public bool Quiet { get; private set; }

    /// <summary> The frame before this one was long (the game's or the closet's part of it). </summary>
    public bool LongFrame { get; private set; }

    /// <summary> Quiet has just ended and the periodic reads have not run yet: the queue and the colours wait for them. </summary>
    public bool AfterLoading { get; private set; }

    /// <summary> What took this frame's turn (null: nothing yet). </summary>
    public IpcWork? TakenBy
        => _taken;

    /// <summary> How many times each kind of work took a turn (for the tests and the report). </summary>
    public Dictionary<IpcWork, int> Turns { get; } = new();

    /// <summary>
    /// Once per frame, before anything calls. <paramref name="loading"/>: a loading screen or a cutscene is up, or the
    /// player has no object. <paramref name="lastFrameMs"/>: how long the game's last frame took; <paramref name="lastOwnMs"/>:
    /// how much of it was the closet's own work.
    /// </summary>
    public void Frame(bool loading, double lastFrameMs, double lastOwnMs = 0)
    {
        var now = _clock();
        _taken = null;
        if (loading)
            _quietUntil = Math.Max(_quietUntil, now + SettleSeconds);
        var quiet = loading || now < _quietUntil;
        if (quiet && !Quiet)
            _quietSince = now;
        if (Quiet && !quiet)
        {
            AfterLoading       = true;
            _afterLoadingSince = now;
            _quietSince        = double.NaN;
        }

        Quiet = quiet;
        if (AfterLoading && now - _afterLoadingSince > AfterLoadingTimeout)
            AfterLoading = false;
        LongFrame = lastFrameMs >= LongFrameMs || lastOwnMs >= BudgetMs;
    }

    /// <summary> The territory changed (Dalamud's event): quiet for <see cref="SettleSeconds"/> from now at least. </summary>
    public void TerritoryChanged()
        => _quietUntil = Math.Max(_quietUntil, _clock() + SettleSeconds);

    /// <summary> The periodic reads after loading have run (or had nothing to read): the queue and the colours go on. </summary>
    public void Refreshed()
        => AfterLoading = false;

    /// <summary> May this work call now? Does not take the turn. </summary>
    public bool May(IpcWork work)
    {
        if (work == IpcWork.Step)
            return !Quiet && !AfterLoading || Quiet && _clock() - _quietSince >= StepWaitLimit;
        if (Quiet || _taken is not null)
            return false;
        switch (work)
        {
            case IpcWork.Settings:
            case IpcWork.Truth:
                if (!LongFrame)
                    return true;
                // a long frame: wait for a quieter one, but not for ever
                var now = _clock();
                if (double.IsNaN(_skippingSince))
                    _skippingSince = now;
                return now - _skippingSince >= MaxSkipSeconds;
            default:
                // the colours, a look put back, the slot check: after the reads that follow loading
                return !AfterLoading;
        }
    }

    /// <summary> Takes this frame's turn when the work may call now. </summary>
    public bool Take(IpcWork work)
    {
        if (!May(work))
            return false;
        Took(work);
        return true;
    }

    /// <summary> The work called (a step, which always may; or the colours, which ask first and call when they need to). </summary>
    public void Took(IpcWork work)
    {
        _taken ??= work;
        if (work is IpcWork.Settings or IpcWork.Truth)
            _skippingSince = double.NaN;
        Turns[work] = Turns.GetValueOrDefault(work) + 1;
    }
}

/// <summary> The wardrobe's side of the periodic reads (implemented by the wardrobe, by fakes in the tests). </summary>
public interface IPeriodicReader
{
    /// <summary> A face is open: the collection's settings are kept fresh. </summary>
    bool Watching { get; }

    /// <summary> Something follows what shows (the colours, the looks): what shows is read. </summary>
    bool Following { get; }

    /// <summary> Penumbra answers and a character is there. </summary>
    bool CanRead { get; }

    /// <summary> What shows means something now: nothing is tried, nothing waits, no redraw is on its way. </summary>
    bool TruthWanted { get; }

    /// <summary> First call of the settings: the collection and the temporary settings other plugins hold in it. False: no collection. </summary>
    bool ReadSettingsFirst();

    /// <summary> Second call of the settings: the collection's own settings. </summary>
    void ReadSettingsSecond();

    /// <summary> First call of what shows: Penumbra's resource paths. False: nothing to go on with. </summary>
    bool ReadTruthFirst();

    /// <summary> Second call of what shows: Glamourer's state; puts both together. </summary>
    void ReadTruthSecond();
}

/// <summary>
/// The wardrobe's periodic reads, one call per frame: the collection's settings (two calls) every
/// <see cref="SettingsEvery"/> s while a face is open, what shows (two calls) every <see cref="TruthWatching"/> or
/// <see cref="TruthIdle"/> s. Each call takes the frame's turn of <see cref="IpcPace"/>; the second half waits for a
/// frame of its own. A read that took longer than <see cref="IpcPace.BudgetMs"/> makes the next one wait longer (up to
/// <see cref="MaxStretch"/> times as long), so a big collection does not stutter every few seconds. After loading, both
/// run once at once, in that order, and then tell the pace (<see cref="IpcPace.Refreshed"/>).
/// </summary>
public sealed class PeriodicReads
{
    public const double SettingsEvery = 3, TruthWatching = 2, TruthIdle = 3;

    /// <summary> How much longer than usual a read may wait when its calls are slow. </summary>
    public const double MaxStretch = 4;

    private enum Stage { None, Settings, Truth }

    private readonly IpcPace _pace;
    private readonly Func<double> _clock;
    private Stage _second;
    private double _settingsStretch = 1, _truthStretch = 1;
    private double _firstMs;
    private double _truthStarted;
    private bool _settling;

    public PeriodicReads(IpcPace pace, Func<double> clock)
    {
        _pace  = pace;
        _clock = clock;
    }

    /// <summary> When the settings were last read (the steps read them too, and set this). </summary>
    public double SettingsReadAt { get; set; } = double.MinValue;

    /// <summary> When what shows is read next (a redraw moves it). </summary>
    public double TruthNext { get; set; } = double.MinValue;

    /// <summary> The first half of a read ran; the second waits for a frame of its own. </summary>
    public bool Halfway
        => _second != Stage.None;

    /// <summary> A step read the settings itself: a half-done periodic read of them is dropped. </summary>
    public void SettingsRead()
    {
        SettingsReadAt = _clock();
        if (_second == Stage.Settings)
            _second = Stage.None;
    }

    /// <summary> Once per frame, after the queue. </summary>
    public void Tick(IPeriodicReader r)
    {
        if (_pace.Quiet)
        {
            // loading: nothing is read, a half-done read is dropped
            _settling = false;
            _second   = Stage.None;
            return;
        }

        if (_pace.AfterLoading && !_settling)
        {
            // settled after loading: both reads now, the settings first (only those a face needs)
            _settling      = true;
            _second        = Stage.None;
            SettingsReadAt = double.MinValue;
            TruthNext      = double.MinValue;
        }

        if (!r.Watching && !r.Following)
        {
            _second = Stage.None;
            Settled();
            return;
        }

        var now = _clock();
        switch (_second)
        {
            case Stage.Settings:
                if (!r.CanRead || !r.Watching)
                {
                    _second = Stage.None;
                    break;
                }

                if (_pace.Take(IpcWork.Settings))
                {
                    var ms = Timed(r.ReadSettingsSecond);
                    _second          = Stage.None;
                    SettingsReadAt   = _clock();
                    _settingsStretch = Stretch(_firstMs + ms);
                }

                return;
            case Stage.Truth:
                if (!r.CanRead || !r.TruthWanted)
                {
                    _second = Stage.None;
                    Settled();
                    break;
                }

                if (_pace.Take(IpcWork.Truth))
                {
                    var ms = Timed(r.ReadTruthSecond);
                    _second       = Stage.None;
                    _truthStretch = Stretch(_firstMs + ms);
                    TruthNext     = Math.Max(TruthNext, _truthStarted + (r.Watching ? TruthWatching : TruthIdle) * _truthStretch);
                    Settled();
                }

                return;
        }

        if (r.Watching && r.CanRead && now >= SettingsReadAt + SettingsEvery * _settingsStretch)
        {
            if (_pace.Take(IpcWork.Settings))
            {
                var first = false;
                _firstMs = Timed(() => first = r.ReadSettingsFirst());
                if (first)
                {
                    _second = Stage.Settings;
                }
                else
                {
                    SettingsReadAt   = _clock();
                    _settingsStretch = Stretch(_firstMs);
                }
            }

            return; // the settings first: what shows waits for its own frame
        }

        if (now < TruthNext)
            return;
        if (!r.CanRead || !r.TruthWanted)
        {
            TruthNext = now + (r.Watching ? TruthWatching : TruthIdle);
            Settled();
            return;
        }

        if (!_pace.Take(IpcWork.Truth))
            return;
        // the next read: after the usual time, longer when this one turns out slow (set again once it is done)
        _truthStarted = now;
        TruthNext     = now + (r.Watching ? TruthWatching : TruthIdle) * _truthStretch;
        var more = false;
        _firstMs = Timed(() => more = r.ReadTruthFirst());
        if (more)
        {
            _second = Stage.Truth;
        }
        else
        {
            _truthStretch = Stretch(_firstMs);
            TruthNext     = Math.Max(TruthNext, now + (r.Watching ? TruthWatching : TruthIdle) * _truthStretch);
            Settled();
        }
    }

    private void Settled()
    {
        if (!_settling)
            return;
        _settling = false;
        _pace.Refreshed();
    }

    private double Timed(Action read)
    {
        var start = _clock();
        read();
        return (_clock() - start) * 1000;
    }

    private static double Stretch(double ms)
        => Math.Clamp(ms / IpcPace.BudgetMs, 1, MaxStretch);
}
