using System.Collections.Concurrent;
using System.Diagnostics;
using ModCloset2.Core;
using Newtonsoft.Json.Linq;

namespace ModCloset2.Colours;

/// <summary> Things the engine takes from outside that tests replace (time, background work, chance, the log). </summary>
public sealed class ColourOptions
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    /// <summary> Seconds on a clock that only goes forward. </summary>
    public Func<double> Clock { get; init; } = () => Watch.Elapsed.TotalSeconds;

    /// <summary> Runs slow work (reading a piece's files) away from the framework thread. </summary>
    public Action<Action> Background { get; init; } = work => Task.Run(work);

    public Random Random { get; init; } = new();

    /// <summary> One line for the plugin log (never shown to the user). </summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// The colours: one bar per worn piece, split into its areas, and four outfit colours everything can follow. Reads what
/// is worn in the background (cached by the files the game loaded), groups each piece's rows into areas, works out the
/// colour of every area from the user's choices, the outfit colours and the "Dress everything" switch, and puts the
/// result on the character as Glamourer advanced dyes (at most ten times a second). Remembers choices per part and area.
/// <para>
/// Threading: the commands and <see cref="Tick"/> run on the framework (draw) thread and return at once; reading runs
/// in the background. Nothing here throws into the caller: Glamourer saying no becomes <see cref="Problem"/>.
/// </para>
/// </summary>
public sealed class OutfitColours : IOutfitColours, IDisposable
{
    /// <summary> At most ten applies a second while the user drags in a picker. </summary>
    public const double ApplyInterval = 0.1;

    /// <summary> After Glamourer said no, wait this long before trying again. </summary>
    public const double RetryInterval = 1.0;

    /// <summary> A lit area goes out by itself when nobody pointed at it for this long (the face calls Point every frame). </summary>
    public const double PointTimeout = 1.5;

    /// <summary>
    /// After what shows in a slot changed, the old model may still be drawn until the game has redrawn the character;
    /// the new piece is read once its model changed, or after this long when it did not (same files).
    /// </summary>
    public const double SettleTime = 2.0;

    /// <summary> How often the drawn models are looked at: while something waits, and otherwise. </summary>
    public const double PollWaiting = 0.25, PollIdle = 1.0;

    /// <summary>
    /// A drawn model with materials the game has not loaded yet (a redraw someone else started) is read once they are
    /// there, or after this long with what is there (a material that never comes must not hold the piece back).
    /// </summary>
    public const double MaterialWait = 2.0;

    public const int CacheSize = 24;
    public const int MaxReads = 2;

    private readonly IColourWorld _world;
    private readonly IGlamourerStates _glamourer;
    private readonly IColourStore _store;
    private readonly IColourUndo _undo;
    private readonly ILiveModels _models;
    private readonly IFileSource _files;
    private readonly ColourOptions _options;
    private readonly ColourMemory _memory = new();
    private readonly object _gate = new();

    private sealed class SlotTrack
    {
        public SlotShowing Showing = null!;
        public string ShowKey = string.Empty;
        public LiveModel? Model;
        public string ModelSignature = string.Empty;
        public string SignatureAtChange = string.Empty;
        public double ChangedAt;
        public bool Settled;

        /// <summary> Since when the drawn model has had materials the game has not loaded (null: all there). </summary>
        public double? IncompleteSince;
    }

    /// <summary> One worn piece: the slots that show the same thing, and what was read of them. </summary>
    private sealed class Run
    {
        public string Id = string.Empty;
        public string MemoryKey = string.Empty;
        public SlotShowing Showing = null!;
        public readonly List<int> Slots = new();
        public string Signature = string.Empty;
        public List<AreaDef>? Areas;
    }

    private sealed record CacheEntry(PieceReading Reading, List<AreaDef> Areas);

    private readonly Dictionary<int, SlotTrack> _slots = new();
    private List<Run> _runs = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _cacheOrder = new();
    private readonly HashSet<string> _reading = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Signature, CacheEntry Entry)> _done = new();

    /// <summary>
    /// The rows of ours on the character (Glamourer key → what showed in that slot when they were written). Kept in the
    /// store, so after a restart of the plugin they can still be released.
    /// </summary>
    private readonly Dictionary<string, Owned> _owned = new(StringComparer.Ordinal);

    /// <summary> A row of ours: what showed in its slot when it was written, and the entry written (to know it is still ours). </summary>
    private sealed record Owned(string ShowKey, JObject Written);

    /// <summary> The character the owned rows are on ("" unknown). </summary>
    private string _ownedBy = string.Empty;
    private bool _ownedChanged;
    private double _nextOwnedSave;
    private double _nextCheck;
    private bool _glamourerWas = true;

    /// <summary> How often the owned rows are written to the store at most, and how often Glamourer is asked whether they are still there. </summary>
    public const double OwnedSaveInterval = 2.0, CheckInterval = 3.0;

    public const string KeyOwned = "colours.owned";

    private int _worldVersion = int.MinValue;
    private double _nextPoll;
    private bool _dirty;
    private double _nextApply;
    private bool _rebuild = true;
    private string _applyProblem = string.Empty;
    private (string Bar, int Area, double Seen)? _lit;
    private (string Description, string Before, HashSet<string> Parts)? _step;
    private bool _disposed;

    /// <summary> How the colours were before the colour try that is on (null: none is on). </summary>
    private ColourMemory? _tryBase;

    private IReadOnlyList<PieceColours> _pieces = Array.Empty<PieceColours>();
    private IReadOnlyList<SavedPalette> _saved = Array.Empty<SavedPalette>();
    private string _problem = string.Empty;

    public OutfitColours(IColourWorld world, IGlamourerStates glamourer, IColourStore store, IColourUndo undo, ILiveModels models, IFileSource files,
        ColourOptions? options = null)
    {
        _world     = world;
        _glamourer = glamourer;
        _store     = store;
        _undo      = undo;
        _models    = models;
        _files     = files;
        _options   = options ?? new ColourOptions();
        _memory.Load(store);
        LoadOwned();
        // colours remembered from last time come back on what is worn (once it has been read)
        _dirty = true;
        BuildSaved();
    }

    // ======================================================================= IOutfitColours

    public int Version { get; private set; }

    public string Problem
    {
        get
        {
            lock (_gate)
                return _problem;
        }
    }

    public OutfitPalette Palette
    {
        get
        {
            lock (_gate)
                return _memory.Palette;
        }
    }

    public bool DressEverything
    {
        get
        {
            lock (_gate)
                return _memory.DressEverything;
        }
        set
        {
            lock (_gate)
            {
                if (value == _memory.DressEverything)
                    return;
                Step(value ? Loc.T("Dress everything in the outfit colours") : Loc.T("Stop dressing everything in the outfit colours"), WornParts(), () =>
                    _memory.DressEverything = value);
            }
        }
    }

    public IReadOnlyList<SavedPalette> Saved
    {
        get
        {
            lock (_gate)
                return _saved;
        }
    }

    public IReadOnlyList<PieceColours> Pieces
    {
        get
        {
            lock (_gate)
            {
                BuildIfNeeded();
                return _pieces;
            }
        }
    }

    public PieceColours? For(string piece)
    {
        lock (_gate)
        {
            BuildIfNeeded();
            return _pieces.FirstOrDefault(p => p.Piece == piece);
        }
    }

    public void SetPalette(ColourRole role, uint rgb, bool done)
    {
        lock (_gate)
        {
            if (role == ColourRole.Own)
                return;
            PointNothingLocked(); // a lit area would hide the new colour
            Begin(Loc.T("Change the outfit colours"), WornParts());
            var p = _memory.Palette;
            rgb &= 0xFFFFFF;
            _memory.Palette = new OutfitPalette
            {
                Main   = role == ColourRole.Main ? rgb : p.Main,
                Second = role == ColourRole.Second ? rgb : p.Second,
                Accent = role == ColourRole.Accent ? rgb : p.Accent,
                Metal  = role == ColourRole.Metal ? rgb : p.Metal,
            };
            Changed();
            if (done)
                End();
        }
    }

    public void UseSaved(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _memory.Saved.Count)
                return;
            var p = _memory.Saved[index];
            Step(Loc.T("Use saved colours"), WornParts(), () => _memory.Palette = p);
        }
    }

    public void SaveCurrent()
    {
        lock (_gate)
        {
            End();
            if (_memory.SaveCurrent())
                SaveMemory();
            BuildSaved();
            ++Version;
        }
    }

    public void RemoveSaved(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _memory.Saved.Count)
                return;
            End();
            _memory.Saved.RemoveAt(index);
            SaveMemory();
            BuildSaved();
            ++Version;
        }
    }

    public void Surprise()
    {
        lock (_gate)
        {
            var next = Colours.Surprise.Next(_options.Random, _memory.Palette);
            Step(Loc.T("Surprise colours"), WornParts(), () => _memory.Palette = next);
        }
    }

    public void SetArea(string bar, int area, uint rgb, bool done)
    {
        lock (_gate)
        {
            if (Find(bar, area) is not { } found || found.Area.Painted)
                return;
            var (run, def) = found;
            PointNothingLocked(); // a lit area would hide the new colour
            Begin(Loc.F("Colour {0}", run.Showing.Name), new[] { run.MemoryKey });
            _memory.Choose(run.MemoryKey, def, AreaChoice.ByHand(rgb));
            Changed();
            if (done)
                End();
        }
    }

    public void Follow(string bar, int area, ColourRole role)
    {
        lock (_gate)
        {
            if (Find(bar, area) is not { } found || found.Area.Painted)
                return;
            var (run, def) = found;
            Step(Loc.F("Colour {0}", run.Showing.Name), new[] { run.MemoryKey }, () =>
                _memory.Choose(run.MemoryKey, def, role == ColourRole.Own ? AreaChoice.Own : AreaChoice.Follow(role)));
        }
    }

    public void ResetPiece(string bar)
    {
        lock (_gate)
        {
            if (RunOf(bar) is not { Areas: { } areas } run)
                return;
            Step(Loc.F("Put {0} back to its own colours", run.Showing.Name), new[] { run.MemoryKey }, () =>
            {
                foreach (var def in areas.Where(a => !a.Painted))
                    _memory.Choose(run.MemoryKey, def, AreaChoice.Own);
            });
        }
    }

    public void Point(string bar, int area)
    {
        lock (_gate)
        {
            var now = _options.Clock();
            if (_lit is { } lit && lit.Bar == bar && lit.Area == area)
            {
                _lit = (bar, area, now);
                return;
            }

            if (Find(bar, area) is not { } found || found.Area.Painted)
            {
                PointNothingLocked();
                return;
            }

            _lit   = (bar, area, now);
            _dirty = true;
        }
    }

    public void PointNothing()
    {
        lock (_gate)
            PointNothingLocked();
    }

    private void PointNothingLocked()
    {
        if (_lit is null)
            return;
        _lit   = null;
        _dirty = true;
    }

    // ======================================================================= for the plugin

    /// <summary> Every frame, on the framework thread: picks up reads, follows what is worn, applies colours. </summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            try
            {
                var now = _options.Clock();
                TakeReads();
                Follow(now);
                if (_lit is { } lit && now - lit.Seen > PointTimeout)
                    PointNothingLocked();
                if (_dirty && now >= _nextApply)
                    Apply(now);
                else if (!_dirty && now >= _nextCheck)
                    CheckRowsStillThere(now);
                if (_ownedChanged && now >= _nextOwnedSave)
                {
                    SaveOwned();
                    _nextOwnedSave = now + OwnedSaveInterval;
                }
                UpdateProblem();
                BuildIfNeeded();
            }
            catch (Exception ex)
            {
                // never break the frame: one line in the log, and carry on next frame
                _options.Log?.Invoke($"Mod Closet 2 colours: {ex.GetType().Name}: {ex.Message}");
                _nextApply = _options.Clock() + RetryInterval;
            }
        }
    }

    /// <summary> Puts the colours back to an undo step's state (the journal calls this with a step's "before" string). </summary>
    public void Restore(string snapshot)
    {
        lock (_gate)
        {
            _step = null;
            if (_tryBase is not null)
                DropTry(); // an undo while trying: the try goes, then the step is taken back
            if (!_memory.Restore(snapshot))
                return;
            SaveMemory();
            BuildSaved();
            Changed();
        }
    }

    /// <summary> Ends pointing (puts the lit rows back at once), keeps the memory. </summary>
    /// <summary>
    /// Ends a colour try and pointing, and puts the character's rows right one last time: what shows is followed once
    /// more (the session and the wardrobe have put back by now) and the rows are applied even while the game redraws
    /// (that gate protects Penumbra's loading, not Glamourer's materials), so nothing of a try is left behind on the
    /// gear. Keeps the memory.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            End();
            var tried = _tryBase is not null;
            var lit   = _lit is not null;
            _lit = null;
            if (tried)
                DropTry(); // as EndTry(false): nothing of the colour try stays
            try
            {
                var now = _options.Clock();
                _worldVersion = int.MinValue; // look again, whatever the version says
                Follow(now);
                if (_dirty || lit || tried)
                    Apply(now, final: true);
            }
            catch (Exception ex)
            {
                // unloading: nothing more to do than say so
                _options.Log?.Invoke($"Mod Closet 2 colours: the colours could not be put back: {ex.Message}");
            }

            SaveMemory();
            SaveOwned();
            _disposed = true;
        }
    }

    /// <summary>
    /// Gives a part the colours another part has been given, when it has none of its own yet (the closet copied a thing
    /// into a mod of its own: same files, another mod). Does nothing when <paramref name="from"/> has no colours
    /// remembered or <paramref name="to"/> already has some. Saved; no undo step; nothing is applied for it (a copy is
    /// not worn yet; once it shows, it is coloured like any part with remembered colours). A colour try is not
    /// disturbed: the kept colours get the copy, and so does the try, so it is there whether the try is kept or dropped.
    /// </summary>
    public void CopyMemory(PartKey from, PartKey to)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var copied = _memory.CopyPart(from.ToString(), to.ToString());
            copied |= _tryBase?.CopyPart(from.ToString(), to.ToString()) ?? false;
            if (!copied)
                return;
            SaveMemory();
            if (_runs.Any(r => r.MemoryKey == to.ToString()))
                Changed(); // the copy is worn already: its bar shows the colours (and the character follows, as for memory)
        }
    }

    /// <summary>
    /// Penumbra renamed a mod's folder (its parts' keys hold the folder name): what the colours remember of it, kept or
    /// in a colour try, moves to the new name, and is saved.
    /// </summary>
    public void ModMoved(string oldDirectory, string newDirectory)
    {
        lock (_gate)
        {
            if (oldDirectory.Length == 0 || newDirectory.Length == 0 || oldDirectory == newDirectory)
                return;
            var moved = _memory.RenameMod(oldDirectory, newDirectory);
            moved |= _tryBase?.RenameMod(oldDirectory, newDirectory) ?? false;

            // what shows and the rows of ours move along, so the slots do not look changed (no flicker)
            string Renamed(string key)
                => PartKey.TryParse(key, out var part) && part.Mod.Equals(oldDirectory, StringComparison.OrdinalIgnoreCase)
                    ? (part with { Mod = newDirectory }).ToString()
                    : key;
            foreach (var track in _slots.Values)
                track.ShowKey = Renamed(track.ShowKey);
            foreach (var run in _runs)
                run.MemoryKey = Renamed(run.MemoryKey);
            foreach (var (key, owned) in _owned.ToList())
                if (Renamed(owned.ShowKey) is var showKey && showKey != owned.ShowKey)
                {
                    _owned[key]    = owned with { ShowKey = showKey };
                    _ownedChanged = true;
                }

            if (!moved)
                return;
            SaveMemory();
            Changed();
        }
    }

    // ======================================================================= for the looks: snapshots and colour tries

    /// <summary>
    /// The colours of these parts now (the outfit colours, the switch, every choice for their areas), in the same format
    /// <see cref="Restore"/> takes. Null when the colours cannot tell now (unloaded).
    /// </summary>
    public string? Snapshot(IReadOnlyCollection<PartKey> parts)
    {
        lock (_gate)
            return _disposed ? null : _memory.Snapshot(parts.Select(p => p.ToString()));
    }

    /// <summary> The colour the biggest area of this worn part shows now (0xRRGGBB), null when not known (yet). </summary>
    public uint? ShowingMain(PartKey part)
    {
        lock (_gate)
        {
            if (_runs.FirstOrDefault(r => r.Showing.Part == part && r.Areas is not null) is not { Areas: { } areas } run)
                return null;
            var biggest = areas.Where(a => !a.Painted).OrderByDescending(a => a.Share).FirstOrDefault();
            if (biggest is null)
                return null;
            return (Target(run, biggest) is { } t ? Recolour.Colour(biggest.Colour, biggest.Mean, t.Target, biggest.Metal) : biggest.Colour).Hex;
        }
    }

    /// <summary> A colour try is on. </summary>
    public bool Trying
    {
        get
        {
            lock (_gate)
                return _tryBase is not null;
        }
    }

    /// <summary>
    /// Shows a snapshot's colours as part of a try: on the character as for <see cref="Restore"/>, but nothing is stored
    /// and no undo step is recorded. A second call replaces the first. Parts named in the snapshot that do not show yet
    /// get their colours when they show (once read); parts that never show are ignored. While the try is on, changes from
    /// the pickers belong to it too.
    /// </summary>
    public void TryOn(string snapshot)
    {
        lock (_gate)
        {
            End();
            var probe = _memory.Copy();
            if (!probe.Restore(snapshot))
            {
                _options.Log?.Invoke("Mod Closet 2 colours: a colour try could not be read");
                return;
            }

            _tryBase ??= _memory.Copy();
            _memory.Assign(_tryBase);
            _memory.Restore(snapshot);
            Changed();
        }
    }

    /// <summary>
    /// Ends the colour try. <paramref name="keep"/>: the tried colours become the kept ones (stored); returns the two
    /// snapshots for the caller's own undo step (before the try, now), null when no try was on or nothing differs. Not
    /// keep: everything is as before the try (palette, switch, choices, what the character shows); returns null.
    /// </summary>
    public (string Before, string After)? EndTry(bool keep)
    {
        lock (_gate)
        {
            if (_tryBase is not { } before)
                return null;
            End();
            if (!keep)
            {
                DropTry();
                return null;
            }

            // the parts whose choices the try changed, and the worn ones (a snapshot over them restores both ways)
            var parts = ColourMemory.Differing(before, _memory).Concat(WornParts()).Distinct(StringComparer.Ordinal).ToList();
            var was   = before.Snapshot(parts);
            var now   = _memory.Snapshot(parts);
            _tryBase  = null;
            SaveMemory();
            Changed();
            return was == now ? null : (was, now);
        }
    }

    private void DropTry()
    {
        if (_tryBase is null)
            return;
        _memory.Assign(_tryBase);
        _tryBase = null;
        _step    = null;
        Changed();
    }

    /// <summary>
    /// Writes the memory to the store; during a colour try the kept colours are written (as before the try), with the
    /// saved palettes as they are now.
    /// </summary>
    private void SaveMemory()
    {
        if (_tryBase is null)
        {
            _memory.Save(_store);
            return;
        }

        var kept = _tryBase.Copy();
        kept.Saved.Clear();
        kept.Saved.AddRange(_memory.Saved);
        kept.Save(_store);
    }

    // ======================================================================= steps (undo)

    private IEnumerable<string> WornParts()
        => _runs.Select(r => r.MemoryKey);

    /// <summary> Opens a step (once; a drag keeps it open until done) with the state before. </summary>
    private void Begin(string description, IEnumerable<string> parts)
    {
        var set = parts.Concat(WornParts()).ToHashSet(StringComparer.Ordinal);
        if (_step is { } open)
        {
            if (open.Description == description && set.IsSubsetOf(open.Parts))
                return;
            End();
        }

        _step = (description, _memory.Snapshot(set), set);
    }

    /// <summary> Closes the open step: one undo entry with before and after, and the memory is saved. </summary>
    private void End()
    {
        if (_step is not { } step)
            return;
        _step = null;
        var after = _memory.Snapshot(step.Parts);
        if (after != step.Before && _tryBase is null) // during a colour try, changes belong to the try
        {
            try
            {
                _undo.Record(step.Description, step.Before, after);
            }
            catch (Exception ex)
            {
                _options.Log?.Invoke($"Mod Closet 2 colours: undo step not recorded: {ex.Message}");
            }
        }

        SaveMemory();
    }

    private void Step(string description, IEnumerable<string> parts, Action change)
    {
        End();
        Begin(description, parts);
        change();
        Changed();
        End();
    }

    private void Changed()
    {
        _dirty   = true;
        _rebuild = true;
        BuildSaved(); // "in use" follows the palette
        ++Version;
    }

    // ======================================================================= following what is worn

    private static string ShowKey(SlotShowing s)
        => s.Part?.ToString() ?? "item|" + s.Name;

    private void Follow(double now)
    {
        var worldVersion = _world.Version;
        if (worldVersion == _worldVersion && now < _nextPoll)
            return;
        _worldVersion = worldVersion;

        // 1. what shows per slot
        var showing = new Dictionary<int, SlotShowing>();
        foreach (var s in _world.Showing)
            if (s.Slot is >= 0 and < 10)
                showing[s.Slot] = s;
        var changed = false;
        foreach (var slot in _slots.Keys.Where(k => !showing.ContainsKey(k)).ToList())
        {
            _slots.Remove(slot);
            changed = true;
        }

        foreach (var (slot, s) in showing)
        {
            var key = ShowKey(s);
            if (!_slots.TryGetValue(slot, out var track))
            {
                _slots[slot] = track = new SlotTrack { ChangedAt = now };
                changed      = true;
            }
            else if (track.ShowKey != key)
            {
                track.ChangedAt         = now;
                track.SignatureAtChange = track.ModelSignature;
                track.Settled           = false;
                changed                 = true;
            }
            else if (!ReferenceEquals(track.Showing, s) && track.Showing != s)
            {
                _rebuild = true; // same part, other words (a name, a picture)
            }

            track.Showing = s;
            track.ShowKey = key;
        }

        // 2. the drawn models (not while the game redraws the character)
        if (!_world.Busy && _world.PlayerAddress != 0)
        {
            IReadOnlyList<LiveModel>? live = null;
            try
            {
                live = _models.Read(_world.PlayerAddress);
            }
            catch (Exception ex)
            {
                _options.Log?.Invoke($"Mod Closet 2 colours: models not read: {ex.Message}");
            }

            if (live is not null)
                foreach (var (slot, track) in _slots)
                {
                    var model     = live.FirstOrDefault(m => m.Slot == slot);
                    var signature = model?.Signature ?? string.Empty;
                    var complete  = model is null || model.Materials.All(m => m is not null);
                    if (signature != track.ModelSignature)
                    {
                        track.Model          = model;
                        track.ModelSignature = signature;
                        changed              = true;
                        if (!complete && track.Settled)
                        {
                            // the same thing, redrawn by someone else: wait for its materials before reading it again
                            // (meanwhile its rows are held, so nothing goes back to the mod's colours)
                            track.Settled           = false;
                            track.SignatureAtChange = string.Empty;
                            track.ChangedAt         = now;
                        }
                    }

                    track.IncompleteSince = complete ? null : track.IncompleteSince ?? now;
                    var materialsThere = complete || now - track.IncompleteSince >= MaterialWait;
                    if (!track.Settled && materialsThere && (track.ModelSignature != track.SignatureAtChange || now - track.ChangedAt >= SettleTime))
                    {
                        track.Settled = true;
                        changed       = true;
                    }
                }
        }

        _nextPoll = now + (_slots.Values.Any(t => !t.Settled) ? PollWaiting : PollIdle);
        if (changed)
            Regroup();
    }

    /// <summary> Slots showing the same part are one piece (a pair of rings); each piece gets its reading. </summary>
    private void Regroup()
    {
        var runs = new List<Run>();
        foreach (var (slot, track) in _slots.OrderBy(kv => kv.Key))
        {
            var run = runs.FirstOrDefault(r => r.MemoryKey == MemoryKeyOf(track));
            if (run is null)
            {
                run = new Run { MemoryKey = MemoryKeyOf(track), Showing = track.Showing };
                runs.Add(run);
            }

            run.Slots.Add(slot);
        }

        // two different things on one shelf (tights and socks both Legwear): the second gets its own id
        foreach (var group in runs.GroupBy(r => r.Showing.Piece))
        {
            var i = 0;
            foreach (var run in group)
                run.Id = i++ == 0 ? group.Key : $"{group.Key}@{run.Slots[0]}";
        }

        foreach (var run in runs)
        {
            var tracks = run.Slots.Select(s => _slots[s]).ToList();
            if (tracks.Any(t => !t.Settled))
                continue; // still waiting for the game: the bar shows "reading"
            tracks = tracks.Where(t => t.Model is not null).ToList();
            if (tracks.Count == 0)
            {
                run.Areas = new List<AreaDef>(); // nothing drawn there (hidden headgear…): no bar
                continue;
            }

            run.Signature = string.Join('|', tracks.Select(t => t.ModelSignature));
            if (_cache.TryGetValue(run.Signature, out var entry))
            {
                Use(run.Signature);
                Adopt(run, entry);
            }
            else
            {
                StartRead(run.Signature, tracks.Select(t => t.Model!).ToList());
            }
        }

        // what was said in the log about pieces no longer worn may be said again when they come back
        foreach (var key in _logged.Keys.Where(k => runs.All(r => r.MemoryKey != k)).ToList())
            _logged.Remove(key);

        _runs    = runs;
        _dirty   = true; // rows of pieces that went are released, new ones coloured when read
        _rebuild = true;
    }

    /// <summary>
    /// What the memory knows a piece by: its part, or for a game item no mod changes, the model it draws (the same item
    /// in another slot or variant is another thing to colour).
    /// </summary>
    private static string MemoryKeyOf(SlotTrack track)
        => track.Showing.Part?.ToString() ?? "model|" + (track.Model?.Path ?? track.Showing.Name);

    private void StartRead(string signature, List<LiveModel> models)
    {
        if (_reading.Contains(signature) || _reading.Count >= MaxReads)
            return; // a read already runs (or two do): the next Follow tries again
        _reading.Add(signature);
        var files = _files;
        var log   = _options.Log;
        _options.Background(() =>
        {
            CacheEntry entry;
            try
            {
                var reading = PieceReader.Read(models, files);
                entry = new CacheEntry(reading, AreaGrouper.Group(reading));
            }
            catch (Exception ex)
            {
                log?.Invoke($"Mod Closet 2 colours: a piece could not be read: {ex.Message}");
                entry = new CacheEntry(new PieceReading { Signature = signature }, new List<AreaDef>());
            }

            _done.Enqueue((signature, entry));
        });
    }

    private void TakeReads()
    {
        var any = false;
        while (_done.TryDequeue(out var done))
        {
            _reading.Remove(done.Signature);
            _cache[done.Signature] = done.Entry;
            Use(done.Signature);
            any = true;
        }

        if (!any)
            return;
        while (_cacheOrder.Count > CacheSize)
        {
            _cache.Remove(_cacheOrder.Last!.Value);
            _cacheOrder.RemoveLast();
        }

        foreach (var run in _runs.Where(r => r.Areas is null && r.Signature.Length > 0))
            if (_cache.TryGetValue(run.Signature, out var entry))
            {
                Adopt(run, entry);
                _dirty   = true;
                _rebuild = true;
            }

        // pieces that waited for a free reader
        if (_runs.Any(r => r.Areas is null && r.Signature.Length > 0 && !_reading.Contains(r.Signature)))
            Regroup();
    }

    /// <summary> What the log was told per worn piece (its memory key), so a material that stays unreadable is said once. </summary>
    private readonly Dictionary<string, HashSet<string>> _logged = new(StringComparer.Ordinal);

    /// <summary> A piece takes a reading: its areas, its memory marked as used, and new notes to the log (once per piece). </summary>
    private void Adopt(Run run, CacheEntry entry)
    {
        run.Areas = entry.Areas;
        _memory.Touch(run.MemoryKey);
        if (entry.Reading.Notes.Count == 0 || _options.Log is not { } log)
            return;
        if (!_logged.TryGetValue(run.MemoryKey, out var said))
            _logged[run.MemoryKey] = said = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in entry.Reading.Notes.Where(said.Add).Take(12))
            log($"Mod Closet 2 colours: {run.Showing.Name}: {note}");
    }

    private void Use(string signature)
    {
        _cacheOrder.Remove(signature);
        _cacheOrder.AddFirst(signature);
    }

    private Run? RunOf(string bar)
        => _runs.FirstOrDefault(r => r.Id == bar);

    private (Run Run, AreaDef Area)? Find(string bar, int area)
        => RunOf(bar) is { Areas: { } areas } run && area >= 0 && area < areas.Count ? (run, areas[area]) : null;

    // ======================================================================= what each area shows

    /// <summary> The colour an area should show, or null for the mod's own. </summary>
    private (Rgb Target, ColourRole Follows, bool Pinned)? Target(Run run, AreaDef area)
    {
        if (area.Painted)
            return null;
        var choice = _memory.Choice(run.MemoryKey, area);
        if (choice is { } c)
        {
            if (c.Hand is { } hand)
                return (Rgb.FromHex(hand), ColourRole.Own, true);
            if (c.Follows == ColourRole.Own)
                return null;
            return (Rgb.FromHex(_memory.Palette.Of(c.Follows)), c.Follows, false);
        }

        // never chosen: the mod's own colours, unless everything is dressed in the outfit colours
        return _memory.DressEverything ? (Rgb.FromHex(_memory.Palette.Of(area.DefaultRole)), area.DefaultRole, false) : null;
    }

    /// <summary> Every row the colours want on the character now, with its values. </summary>
    internal List<RowWrite> Wanted()
    {
        var list = new List<RowWrite>();
        foreach (var run in _runs)
        {
            if (run.Areas is not { } areas)
                continue;
            for (var i = 0; i < areas.Count; ++i)
            {
                var area = areas[i];
                var lit  = _lit is { } l && l.Bar == run.Id && l.Area == i;
                var target = Target(run, area);
                if (area.Painted || !lit && target is null)
                    continue;
                foreach (var row in area.Rows)
                {
                    var values = lit ? Recolour.Lit(row) : Recolour.Row(row, area.Mean, target!.Value.Target, area.Metal);
                    foreach (var slot in row.Slots.Where(run.Slots.Contains))
                        list.Add(new RowWrite(slot, row.Material, row.Row, values));
                }
            }
        }

        return list;
    }

    /// <summary> The character the rows are on changed: the rows of ours are on the other one, not here; forget them. </summary>
    private void CheckOwner()
    {
        var who = _world.PlayerKey;
        if (who.Length == 0 || who == _ownedBy)
            return;
        if (_ownedBy.Length > 0 && _owned.Count > 0)
        {
            _options.Log?.Invoke("Mod Closet 2 colours: another character; the rows written on the last one are left as they are");
            _owned.Clear();
        }

        _ownedBy = who;
        SaveOwned(); // at once: a restart on this character must not release the other one's rows
    }

    /// <param name="final"> The last pass on unload: applied even while the game redraws the character. </param>
    private void Apply(double now, bool final = false)
    {
        if (!_glamourer.Available || _world.WhyNot.Length > 0 || _world.Busy && !final)
            return; // stays dirty: applied when things allow it

        CheckOwner();
        var wanted = Wanted();
        var keys   = wanted.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);

        // rows of ours no longer wanted go back to the game's colours, unless their slot still shows the same thing and
        // is only being read again (then they stay until the new reading says what to do, so nothing flickers)
        var release = new List<(string Key, JObject Written)>();
        var held    = new Dictionary<string, Owned>(StringComparer.Ordinal);
        foreach (var (key, owned) in _owned)
        {
            if (keys.Contains(key))
                continue;
            if (!final && ColourMath.TryParseMaterialKey(key, out var slot, out _, out _) && _slots.TryGetValue(slot, out var track)
             && track.ShowKey == owned.ShowKey && _runs.FirstOrDefault(r => r.Slots.Contains(slot)) is { Areas: null })
                held[key] = owned;
            else
                release.Add((key, owned.Written));
        }

        if (keys.Count == 0 && release.Count == 0)
        {
            // nothing of ours to put on or take off: no need to ask Glamourer
            _applyProblem = string.Empty;
            _dirty        = false; // a finished reading or a change of what shows asks again
            _nextApply    = now + ApplyInterval;
            return;
        }

        var state = _glamourer.GetState(_world.PlayerIndex, out var code);
        if (state is null)
        {
            _applyProblem = Says(code);
            _nextApply    = now + RetryInterval;
            return;
        }

        var (materials, changed, notOurs) = GlamourerRows.Patch(state["Materials"], wanted, release);
        if (notOurs.Count > 0)
            _options.Log?.Invoke($"Mod Closet 2 colours: {notOurs.Count} row(s) changed by someone else since; left as they are");
        if (changed)
        {
            var result = _glamourer.ApplyState(GlamourerRows.MaterialsOnly(state, materials), _world.PlayerIndex, 0);
            if (result != 0)
            {
                _applyProblem = Says(result);
                _nextApply    = now + RetryInterval;
                return;
            }
        }

        var before = string.Join(',', _owned.Keys.Order(StringComparer.Ordinal));
        _owned.Clear();
        foreach (var w in wanted)
            _owned[w.Key] = new Owned(_slots.TryGetValue(w.Slot, out var t) ? t.ShowKey : string.Empty, GlamourerRows.Entry(w.Values));
        foreach (var (key, owned) in held)
            _owned[key] = owned;
        _ownedChanged = true;
        if (string.Join(',', _owned.Keys.Order(StringComparer.Ordinal)) != before)
        {
            SaveOwned(); // which rows are ours changed: at once; new values of the same rows (a drag) every few seconds
            _nextOwnedSave = now + OwnedSaveInterval;
        }

        _applyProblem = string.Empty;
        _dirty        = false;
        _nextApply    = now + ApplyInterval;
        _nextCheck    = now + CheckInterval;
    }

    /// <summary>
    /// Every few seconds while rows of ours should be on the character: when Glamourer came back (reloaded) or none of
    /// our rows are in its state any more (its state was reset), they are put on again. A row someone changed is not a
    /// sign of that (it is theirs now), only all of them gone.
    /// </summary>
    private void CheckRowsStillThere(double now)
    {
        _nextCheck = now + CheckInterval;
        var available = _glamourer.Available;
        if (available && !_glamourerWas)
            _dirty = true; // Glamourer came back: our rows went with the old one
        _glamourerWas = available;
        if (_dirty || !available || _owned.Count == 0 || _world.Busy || _world.WhyNot.Length > 0)
            return;
        CheckOwner();
        if (_owned.Count == 0)
            return;
        var state = _glamourer.GetState(_world.PlayerIndex, out _);
        if (state?["Materials"] is not JObject current)
            return;
        var materials = GlamourerRows.Canon(current);
        if (_owned.Keys.All(k => materials[k] is null || GlamourerRows.IsRevert(materials[k])))
        {
            _options.Log?.Invoke("Mod Closet 2 colours: Glamourer has none of our rows any more; putting them on again");
            _dirty = true;
        }
    }

    /// <summary> {"owner": "name@world", "rows": {"key": {"s": what showed, "w": the entry written}}} </summary>
    private void LoadOwned()
    {
        try
        {
            if (JObject.Parse(_store.Get(KeyOwned) is { Length: > 0 } text ? text : "{}") is not { } o)
                return;
            _ownedBy = o["owner"]?.Value<string>() ?? string.Empty;
            if (o["rows"] is JObject rows)
                foreach (var p in rows.Properties())
                    if (ColourMath.TryParseMaterialKey(p.Name, out _, out _, out _) && p.Value is JObject row && row["w"] is JObject written)
                        _owned[p.Name] = new Owned(row["s"]?.Value<string>() ?? string.Empty, written);
        }
        catch
        {
            // an older or damaged list: without it, rows of earlier sessions are simply not known (and never released)
            _owned.Clear();
        }
    }

    private void SaveOwned()
    {
        _ownedChanged = false;
        try
        {
            var rows = new JObject();
            foreach (var (key, owned) in _owned)
                rows[key] = new JObject { ["s"] = owned.ShowKey, ["w"] = owned.Written };
            _store.Set(KeyOwned, new JObject { ["owner"] = _ownedBy, ["rows"] = rows }.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch
        {
            // the store is the foundation's
        }
    }

    private static string Says(int code)
        => code switch
        {
            6   => Loc.T("Another plugin is holding your look right now, so colours cannot change it."),
            2   => Loc.T("Your character could not be found. Try again in a moment."),
            255 => Loc.T("Glamourer did not answer."),
            _   => Loc.T("Glamourer did not take the colours."),
        };

    private void UpdateProblem()
    {
        var problem = !_glamourer.Available ? Loc.T("Glamourer is not running.")
            : _world.WhyNot.Length > 0 ? _world.WhyNot
            : _applyProblem;
        if (problem == _problem)
            return;
        _problem = problem;
        ++Version;
    }

    // ======================================================================= what the faces see

    private void BuildIfNeeded()
    {
        if (!_rebuild)
            return;
        _rebuild = false;
        var list = new List<PieceColours>();
        foreach (var run in _runs.OrderBy(r => Order(r.Showing.Piece)).ThenBy(r => r.Slots[0]))
        {
            if (run.Areas is { Count: 0 })
                continue; // read, and nothing to colour or show (a body part only)
            var areas = new List<ColourArea>();
            var hasOwn = false;
            if (run.Areas is { } defs)
                for (var i = 0; i < defs.Count; ++i)
                {
                    var def    = defs[i];
                    var target = Target(run, def);
                    var choice = def.Painted ? null : _memory.Choice(run.MemoryKey, def);
                    var pinned = choice?.Pinned ?? false;
                    hasOwn |= pinned;
                    areas.Add(new ColourArea
                    {
                        Index     = i,
                        Share     = def.Share,
                        Rgb       = (target is { } t ? Recolour.Colour(def.Colour, def.Mean, t.Target, def.Metal) : def.Colour).Hex,
                        Label     = Label(def.Kind),
                        ShareText = ShareText(def.Share, run.Showing.Piece),
                        Follows   = target?.Follows ?? ColourRole.Own,
                        Pinned    = pinned,
                        Painted   = def.Painted,
                        Metal     = def.Metal,
                    });
                }

            list.Add(new PieceColours
            {
                Id        = run.Id,
                Piece     = run.Showing.Piece,
                PieceName = run.Showing.PieceName,
                Icon      = run.Showing.Icon,
                Key       = run.Showing.Part ?? default,
                Name      = run.Showing.Name,
                Picture   = run.Showing.Picture,
                Areas     = areas,
                HasOwn    = hasOwn,
                Reading   = run.Areas is null,
            });
        }

        _pieces = list;
        ++Version;
    }

    private void BuildSaved()
        => _saved = _memory.Saved.Select(p => new SavedPalette { Colours = p, InUse = ColourMemory.Near(p, _memory.Palette) }).ToList();

    private static int Order(string piece)
    {
        for (var i = 0; i < Core.Pieces.BuiltIn.Count; ++i)
            if (Core.Pieces.BuiltIn[i].Id == piece)
                return i;
        return 1000;
    }

    public static string Label(AreaKind kind)
        => kind switch
        {
            AreaKind.Main    => Loc.T("Main area"),
            AreaKind.Second  => Loc.T("Second area"),
            AreaKind.Accent  => Loc.T("Accent"),
            AreaKind.Details => Loc.T("Details"),
            AreaKind.Metal   => Loc.T("Metal"),
            AreaKind.Unreadable => Loc.T("Cannot be coloured"),
            _                => Loc.T("Painted part"),
        };

    /// <summary> "about half of the top", "a small part" … for the picker's heading. </summary>
    public static string ShareText(float share, string piece)
    {
        var noun = Noun(piece);
        return share switch
        {
            >= 0.97f => Loc.F("all of {0}", noun),
            >= 0.8f  => Loc.F("most of {0}", noun),
            >= 0.6f  => Loc.F("more than half of {0}", noun),
            >= 0.4f  => Loc.F("about half of {0}", noun),
            >= 0.28f => Loc.F("about a third of {0}", noun),
            >= 0.18f => Loc.F("about a quarter of {0}", noun),
            >= 0.07f => Loc.T("a small part"),
            _        => Loc.T("a tiny part"),
        };
    }

    private static string Noun(string piece)
    {
        var cut = piece.IndexOf('@');
        return (cut >= 0 ? piece[..cut] : piece) switch
        {
            Core.Pieces.Head      => Loc.T("the headwear"),
            Core.Pieces.Top       => Loc.T("the top"),
            Core.Pieces.Gloves    => Loc.T("the gloves"),
            Core.Pieces.Bottoms   => Loc.T("the bottoms"),
            Core.Pieces.Legwear   => Loc.T("the legwear"),
            Core.Pieces.Shoes     => Loc.T("the shoes"),
            Core.Pieces.Earrings  => Loc.T("the earrings"),
            Core.Pieces.Necklace  => Loc.T("the necklace"),
            Core.Pieces.Bracelets => Loc.T("the bracelets"),
            Core.Pieces.Rings     => Loc.T("the rings"),
            Core.Pieces.Nails     => Loc.T("the nails"),
            _                     => Loc.T("the piece"),
        };
    }
}
