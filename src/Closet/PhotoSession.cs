using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ModCloset2.Core;
using ModCloset2.Library;

namespace ModCloset2.Closet;

// Photos of pieces (docs/briefs/12-photos-in-the-closet.md), the closet's side: which part is photographed next, one
// worker thread that draws (IPhotoMaker) and writes (PhotoStore), and which picture a card shows. The frame only decides
// the next part (a few dictionary lookups) and picks up what is done; at rest it costs a handful of comparisons and
// allocates nothing.

public sealed partial class ClosetSession
{
    /// <summary> Draws the photos, or null: then the closet takes none and <see cref="Photos"/> is null. </summary>
    public IPhotoMaker? PhotoMaker { get; set; }

    /// <summary> Where the photos live (<c>photos/</c> beside the settings), or null: then none are taken. </summary>
    public string? PhotoFolder { get; set; }

    /// <summary>
    /// True while photos must wait: in combat, in a duty, in a cutscene or group pose, while a zone loads. (The library
    /// still reading is the session's own check.)
    /// </summary>
    public Func<bool>? PhotosWait { get; set; }

    /// <summary> The player's race code, for the model the maker draws (0: the maker picks). </summary>
    public Func<int>? PhotoRace { get; set; }

    /// <summary> The width of a photo; the height is 5/4 of it (the shape of a card's picture). </summary>
    public const int PhotoWidth = 384;

    /// <summary> "Everything else" goes at one picture per this, and only while a face is open. </summary>
    private static readonly TimeSpan SlowPace = TimeSpan.FromSeconds(0.25);

    /// <summary> Arrivals a face is waiting for are gathered: the lists are made again at most this often for photos. </summary>
    private static readonly TimeSpan PhotoGather = TimeSpan.FromSeconds(0.3);

    /// <summary> Photos nobody is looking at make the lists again at most this often while the worker works. </summary>
    private static readonly TimeSpan PhotoCatchUp = TimeSpan.FromSeconds(10);

    /// <summary> The maker lets its memory go when nothing was asked of it this long. </summary>
    private static readonly TimeSpan PhotoRestAfter = TimeSpan.FromSeconds(3);

    /// <summary> A face's ask counts this long after it was last made (what is on screen asks every frame). </summary>
    private static readonly TimeSpan AskLasts = TimeSpan.FromSeconds(2);

    /// <summary> The settings sheet's count is made again at most this often. </summary>
    private static readonly TimeSpan PhotoCountEvery = TimeSpan.FromSeconds(0.5);

    /// <summary> At most this many asks are kept; the oldest fall out first. </summary>
    private const int MaxAsks = 96;

    /// <summary> Parts looked at per frame when looking for the next one (the rest wait for the next frame). </summary>
    private const int PhotoLooksPerFrame = 200;

    /// <summary> The plugin's version: a take that failed is tried once more under another one. </summary>
    private static readonly string PluginVersion = typeof(ClosetSession).Assembly.GetName().Version?.ToString() ?? "0";

    private PhotoWorker? _photoWorker;
    private bool _photoWorkerTried;

    /// <summary> What the closet has of each part (from the index, then from every take), on the game's thread only. </summary>
    private readonly Dictionary<PartKey, PhotoStore.Entry> _photos = new();

    private readonly Dictionary<PartKey, DateTime> _asks = new();
    private readonly List<PartKey> _askScratch = new();
    private bool _askNew;

    /// <summary> The file a face was last made to see for a part it asked about (one rebuild per arrival, not one per ask). </summary>
    private readonly Dictionary<PartKey, string> _shownFile = new();

    private int _looksPhotoStamp;
    private bool _photoArrived;
    private int _offScreen;
    private int _takenSinceRest;
    private DateTime _photoShownAt = DateTime.MinValue;
    private DateTime _catchUpAt = DateTime.MinValue;
    private DateTime _slowAt = DateTime.MinValue;
    private DateTime _lastWork = DateTime.MinValue;
    private bool _rested = true;
    private long _photoFrame = -1;
    private long _faceFrame = -1;

    /// <summary> Nothing is left to photograph for this key (library, places, maker, switch, face, shelf): the frame skips the queue. </summary>
    private bool _photoDone;
    private (int Library, int Place, int Maker, bool Off, bool Face, string Shelf, int Order, int List) _photoDoneFor;

    // where "the rest of the shelf" and "everything else" have got to
    private (string Shelf, int List) _shelfWalkFor = (string.Empty, -1);
    private int _shelfWalk;
    private List<LibPart> _allThings = new();
    private int _allThingsFor = -1;
    private int _allWalk;
    private int _allClean;
    private int _prunedFor = -1;
    private DateTime _prunedAt = DateTime.MinValue;

    // the settings sheet's count
    private (int Taken, int Total) _photoCount;
    private int _photoCountPlace = -1;
    private bool _photoCountDirty = true;
    private DateTime _photoCountAt = DateTime.MinValue;

    /// <summary> How many times the lists were made again (the tests count it). </summary>
    public int ViewBuilds { get; private set; }

    /// <summary>
    /// Moves once when the worker comes to rest after having taken photos (not per photo): the looks read their parts'
    /// pictures again then.
    /// </summary>
    public int PhotoStamp
        => _looksPhotoStamp;

    private bool PhotosOn
        => PhotoMaker is not null && PhotoFolder is not null;

    public PhotoStatus? Photos
    {
        get
        {
            if (!PhotosOn)
                return null;
            CountPhotos();
            return new PhotoStatus
            {
                On      = !_data.PhotosOff,
                Taken   = _photoCount.Taken,
                Total   = _photoCount.Total,
                Working = _photoWorker is { Idle: false, Broken: false, Stopped: false },
            };
        }
    }

    public void SetPhotos(bool on)
    {
        if (_data.PhotosOff == !on)
            return;
        _data.PhotosOff = !on;
        _save();
        _version++;
    }

    /// <summary>
    /// A face draws this part's card or tile without a photo. When the closet has one for it, the face is drawing
    /// something older: the lists are made again (once per new file, gathered). When it still needs one, it goes first in
    /// the queue. Anything else (no gear, nothing to show, a failed take, already being taken) is ignored. No allocation
    /// per call.
    /// </summary>
    public void WantPicture(PartKey part)
    {
        if (!PhotosOn || _data.PhotosOff || part.Socket == Socket.None || !_byKey.TryGetValue(part, out var lib))
            return;
        if (FileOfThing(lib) is { } file)
        {
            if (!_shownFile.TryGetValue(part, out var shown) || !string.Equals(shown, file, StringComparison.Ordinal))
            {
                _shownFile[part] = file;
                _photoArrived    = true;
            }

            return;
        }

        if (Wanted(lib) is null)
            return;
        if (!_asks.ContainsKey(part))
            _askNew = true;
        _asks[part] = _now();
        if (_asks.Count > MaxAsks)
            DropOldestAsk();
    }

    private void DropOldestAsk()
    {
        var oldest = default(PartKey);
        var at     = DateTime.MaxValue;
        foreach (var (key, when) in _asks)
            if (when < at)
            {
                at     = when;
                oldest = key;
            }

        _asks.Remove(oldest);
    }

    // ======== which picture a thing shows ========

    /// <summary> The photo of one part, null when it has none (yet). </summary>
    private string? PhotoOf(PartKey key)
        => _photos.Count > 0 && _photos.TryGetValue(key, out var e) && e.File.Length > 0 && PhotoFolder is { } folder ? Path.Combine(folder, e.File) : null;

    /// <summary> A thing's photo: the slot a click would wear, else any of its slots (all hold the same thing). </summary>
    private string? PhotoOfThing(LibPart click, IReadOnlyList<LibPart> thing)
    {
        if (_photos.Count == 0)
            return null;
        if (PhotoOf(click.Key) is { } own)
            return own;
        foreach (var p in thing)
            if (PhotoOf(p.Key) is { } other)
                return other;
        return null;
    }

    /// <summary> The file (as the index names it) a part's thing has, without allocating; null when none. </summary>
    private string? FileOfThing(LibPart part)
    {
        if (_photos.Count == 0)
            return null;
        if (_photos.TryGetValue(part.Key, out var own) && own.File.Length > 0)
            return own.File;
        if (_thingParts.TryGetValue(LeadOf(part.Key), out var thing))
            foreach (var p in thing)
                if (_photos.TryGetValue(p.Key, out var other) && other.File.Length > 0)
                    return other.File;
        return null;
    }

    /// <summary> A part's picture: its photo (or its thing's), else the mod's own picture, else null (the face shows the icon). </summary>
    public string? PictureOf(LibPart part)
        => PhotoOfThing(part, ThingOf(part.Key)) ?? part.Mod.Picture;

    /// <summary> An Outfits card: the mod's own picture (what authors make them for), else the photo of its largest thing. </summary>
    private string? OutfitPicture(IReadOnlyList<LibPart> parts)
    {
        if (parts.Count == 0)
            return null;
        if (parts[0].Mod.Picture is { } own)
            return own;
        foreach (var part in parts.OrderByDescending(p => System.Numerics.BitOperations.PopCount((ulong)p.Info.Covers)).ThenBy(p => p.Key.Socket))
            if (PhotoOfThing(part, ThingOf(part.Key)) is { } photo)
                return photo;
        return null;
    }

    // ======== the queue (game's thread) ========

    /// <summary> For the tests: faults the store raises where it writes a photo, or looks through its folder. </summary>
    internal Func<Exception?>? PhotoWriteFault { get; set; }
    internal Func<Exception?>? PhotoPruneFault { get; set; }
    internal Func<Exception?>? PhotoMoveFault { get; set; }

    /// <summary> For the tests: a fault the worker's loop raises outside any job (the worker then ends). </summary>
    internal Func<Exception?>? PhotoLoopFault { get; set; }

    /// <summary> For the tests: one frame of the photo queue alone, with a face open or not. </summary>
    internal void PhotoFrame(bool face)
    {
        _frame++;
        if (face)
            _faceFrame = _frame;
        UpdatePhotos();
    }

    /// <summary>
    /// Once a frame: picks up what the worker finished, and when it is free, hands it the next part. Order: what a face
    /// asked for (newest first), what is on or tried on, the rest of the selected shelf in its card order, then everything
    /// else (slowly, only while a face is open). Nothing while photos must wait or the library still reads. When nothing is
    /// left for the current library, places, maker, switch, face and shelf, the frame stops after a few comparisons until
    /// one of those changes or a face asks for something new.
    /// </summary>
    private void UpdatePhotos()
    {
        if (!PhotosOn || _photoFrame == _frame)
            return;
        _photoFrame = _frame;
        var worker = _photoWorker ?? Worker();
        if (worker is null)
            return;

        var now = _now();
        if (!worker.Results.IsEmpty)
            TakeResults(worker, now);
        if (_photoArrived && now - _photoShownAt >= PhotoGather)
        {
            _photoArrived = false;
            _photoShownAt = now;
            _viewDirty    = true;
        }

        // the photo folder cannot be written: no more photos this session (the ones there keep showing)
        if (!worker.Loaded || worker.Broken || worker.Stopped || !worker.Idle)
            return;
        // what the worker finished just before it became free
        if (!worker.Results.IsEmpty)
            TakeResults(worker, now);

        // the library changed and its parts are not placed yet (no face open, or not yet this frame): a part that is gone
        // must not be taken again after its photo was removed, so nothing is handed out until they are
        if (_library.Version != _libraryVersion || _membershipDirty || _libraryDirtyMods.Count > 0)
            return;

        var face = _faceFrame == _frame;
        // the shelf's list by identity: a new order (or the first one, made after this on the very first frame) is a change
        var key  = (_library.Version, _placeStamp, PhotoMaker!.Version, _data.PhotosOff, face, _selected, _orderStamp, RuntimeHelpers.GetHashCode(_ordered));
        if (_photoDone && !_askNew && key == _photoDoneFor)
        {
            if (!_rested)
                RestPhotos(worker, now);
            return;
        }

        if (_photoDone && key != _photoDoneFor)
        {
            // something changed after all was done (a mod, the maker, the switch, the shelf): look through it all again
            _shelfWalk = 0;
            _allClean  = 0;
        }

        _photoDone = false;
        _askNew    = false;

        var progress = _library.Progress;
        if (_data.PhotosOff || !progress.Ready || progress.Detecting || Waiting())
        {
            RestPhotos(worker, now);
            return;
        }

        Prune(worker, now);
        ForgetOldAsks(now);

        if ((NextAsked() ?? NextWorn() ?? NextOnShelf()) is { } fast)
        {
            Start(worker, fast, now);
            return;
        }

        var shelfDone = _shelfWalk >= Things(_ordered).Count || _selected is Pieces.Outfits || !string.IsNullOrWhiteSpace(_search);
        if (face)
        {
            if (now - _slowAt < SlowPace)
                return;
            if (NextAnywhere() is { } slow)
            {
                _slowAt = now;
                Start(worker, slow, now);
                return;
            }
        }

        // nothing left: until something changes, the frame does nothing more here
        if (shelfDone && _asks.Count == 0 && (!face || _allClean >= _allThings.Count))
        {
            _photoDone    = true;
            _photoDoneFor = key;
        }

        RestPhotos(worker, now);
    }

    /// <summary>
    /// Takes the worker's results. An arrival makes the lists again (gathered) only when it is for something on or tried on
    /// (the rail's thumb cannot ask); others wait for a face to ask (<see cref="WantPicture"/>), for the worker to rest, or
    /// for <see cref="PhotoCatchUp"/>.
    /// </summary>
    private void TakeResults(PhotoWorker worker, DateTime now)
    {
        var got = 0;
        while (worker.Results.TryDequeue(out var result))
        {
            foreach (var (key, entry) in result)
            {
                // a part that had a photo: its old file is gone now, and a card may still show that path (a face does not
                // ask for a part that shows a photo), so the lists are made again soon
                var replaced = _photos.TryGetValue(key, out var before) && before.File.Length > 0 && before.File != entry.File;
                _photos[key] = entry;
                // also when the retake shows nothing: the old file is gone, the card goes back to the mod's picture or icon
                if (replaced)
                    _photoArrived = true;
                if (entry.File.Length == 0)
                    continue;
                _takenSinceRest++;
                if (replaced || Worn(key))
                    _photoArrived = true;
                else
                    _offScreen++;
            }

            got += result.Count;
        }

        if (got == 0)
            return;
        Game.FrameReport.Step("photos", got);
        _photoCountDirty = true;
        if (_offScreen > 0 && now - _catchUpAt >= PhotoCatchUp)
        {
            _catchUpAt    = now;
            _offScreen    = 0;
            _photoArrived = true;
        }
    }

    /// <summary> On or tried on, any slot of its thing. </summary>
    private bool Worn(PartKey key)
    {
        if (_on.Contains(key) || _tries.Any(t => !t.Nothing && t.Key == key))
            return true;
        if (_thingParts.TryGetValue(LeadOf(key), out var thing))
            foreach (var p in thing)
                if (_on.Contains(p.Key))
                    return true;
        return false;
    }

    private bool Waiting()
    {
        try
        {
            return PhotosWait?.Invoke() ?? false;
        }
        catch
        {
            return true;
        }
    }

    private PhotoWorker? Worker()
    {
        if (_photoWorker is not null || _photoWorkerTried)
            return _photoWorker;
        _photoWorkerTried = true;
        try
        {
            var store = new PhotoStore(PhotoFolder!, PluginVersion, _log) { WriteFault = PhotoWriteFault, PruneFault = PhotoPruneFault, MoveFault = PhotoMoveFault };
            _photoWorker = new PhotoWorker(PhotoMaker!, store, _log) { LoopFault = PhotoLoopFault };
        }
        catch (Exception ex)
        {
            _log?.Invoke($"photos: the worker could not start ({ex.GetType().Name})");
        }

        return _photoWorker;
    }

    private void Start(PhotoWorker worker, LibPart part, DateTime now)
    {
        var ask = new PhotoAsk
        {
            ModFolder = part.Mod.Facts?.Folder ?? string.Empty,
            Socket    = part.Key.Socket,
            Race      = Race(),
            Width     = PhotoWidth,
        };
        _asks.Remove(part.Key);
        worker.Take(part.Key, part.KeyText, StampOf(part), ask);
        // a stretch of work begins: the first catch-up for photos nobody looks at comes after its time, not at once
        if (_rested)
            _catchUpAt = now;
        _lastWork = now;
        _rested   = false;
    }

    private int Race()
    {
        try
        {
            return PhotoRace?.Invoke() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Nothing handed over for a few seconds: the maker lets its memory go, and when photos came in since the last rest,
    /// the lists are made again once (photos nobody asked for: Outfits cards, suggestion tiles) and the looks read again.
    /// </summary>
    private void RestPhotos(PhotoWorker worker, DateTime now)
    {
        if (_rested || now - _lastWork < PhotoRestAfter)
            return;
        _rested = true;
        worker.Rest();
        if (_takenSinceRest > 0)
        {
            _takenSinceRest = 0;
            _offScreen      = 0;
            _photoArrived   = true;
            _looksPhotoStamp++;
        }
    }

    private void ForgetOldAsks(DateTime now)
    {
        if (_asks.Count == 0)
            return;
        _askScratch.Clear();
        foreach (var (key, when) in _asks)
            if (now - when > AskLasts)
                _askScratch.Add(key);
        foreach (var key in _askScratch)
            _asks.Remove(key);
    }

    /// <summary> A part a face asked for, newest ask first; asks that need nothing any more are dropped. </summary>
    private LibPart? NextAsked()
    {
        if (_asks.Count == 0)
            return null;
        LibPart? best = null;
        var at = DateTime.MinValue;
        _askScratch.Clear();
        foreach (var (key, when) in _asks)
        {
            if (!_byKey.TryGetValue(key, out var part) || Wanted(part) is not { } want)
            {
                _askScratch.Add(key);
                continue;
            }

            if (when > at)
            {
                at   = when;
                best = want;
            }
        }

        foreach (var key in _askScratch)
            _asks.Remove(key);
        return best;
    }

    /// <summary> What is on or tried on: the rail shows their thumbs on every shelf, and cannot ask. </summary>
    private LibPart? NextWorn()
    {
        foreach (var key in _on)
            if (_byKey.TryGetValue(key, out var part) && Wanted(part) is { } want)
                return want;
        foreach (var t in _tries)
            if (!t.Nothing && _byKey.TryGetValue(t.Key, out var part) && Wanted(part) is { } want)
                return want;
        return null;
    }

    /// <summary> The rest of the selected shelf, in its card order (from where the last look stopped). </summary>
    private LibPart? NextOnShelf()
    {
        if (_selected is Pieces.Outfits || !string.IsNullOrWhiteSpace(_search))
            return null;
        var shelf = Things(_ordered);
        var walk  = (_selected, RuntimeHelpers.GetHashCode(_ordered));
        if (_shelfWalkFor != walk)
        {
            _shelfWalkFor = walk;
            _shelfWalk    = 0;
        }

        for (var looked = 0; _shelfWalk < shelf.Count && looked < PhotoLooksPerFrame; ++looked, ++_shelfWalk)
            if (Wanted(shelf[_shelfWalk]) is { } want)
                return want;
        return null;
    }

    /// <summary> Everything else: one thing per part of every mod, walked a little each frame. </summary>
    private LibPart? NextAnywhere()
    {
        if (_allThingsFor != _placeStamp)
        {
            _allThingsFor = _placeStamp;
            _allThings    = OnePerThing(_byKey.Values.Where(p => MergesSlots(p.Key.Socket) && !_aside.ContainsKey(p.Key)).OrderBy(p => p.Key.Socket));
            _allWalk      = 0;
            _allClean     = 0;
        }

        for (var looked = 0; looked < PhotoLooksPerFrame && _allClean < _allThings.Count; ++looked)
        {
            if (_allWalk >= _allThings.Count)
                _allWalk = 0;
            if (Wanted(_allThings[_allWalk++]) is { } want)
            {
                _allClean = 0;
                return want;
            }

            _allClean++;
        }

        return null;
    }

    /// <summary>
    /// The part of a thing to photograph, or null when it needs none: gear only (no hair, tails, faces for now), and only
    /// when no slot of the thing has a picture, "nothing to show" or a failed take under the current stamp. The slot a
    /// click would wear. No allocation for a thing of one slot.
    /// </summary>
    private LibPart? Wanted(LibPart part)
    {
        // only what is placed now (a shelf's list can still name a part the library just dropped)
        if (!MergesSlots(part.Key.Socket) || part.Mod.Facts is not { Folder.Length: > 0 } || !_byKey.TryGetValue(part.Key, out var placed)
         || !ReferenceEquals(placed, part))
            return null;
        if (_thingParts.TryGetValue(LeadOf(part.Key), out var thing) && thing.Count > 1)
        {
            foreach (var p in thing)
                if (Settled(p))
                    return null;
            return ClickPart(thing);
        }

        return Settled(part) ? null : part;
    }

    private bool Settled(LibPart part)
        => _photos.TryGetValue(part.Key, out var e) && e.Stamp == StampOf(part);

    private readonly ConditionalWeakTable<LibMod, StrongBox<(int Version, string Stamp)>> _stamps = new();

    /// <summary> What decides a part's picture: the mod's fingerprint as the library has it, the maker's version, the width. </summary>
    private string StampOf(LibPart part)
    {
        var version = PhotoMaker?.Version ?? 0;
        var box = _stamps.GetValue(part.Mod, static _ => new StrongBox<(int, string)>((-1, string.Empty)));
        if (box.Value.Item1 != version)
            box.Value = (version, PhotoStore.Hash($"{part.Mod.Facts?.Stamp}|{version}|{PhotoWidth}")[..8]);
        return box.Value.Item2;
    }

    /// <summary>
    /// When the library has read everything (and at most once a minute after that, when it changed): the parts that are
    /// gone lose their photos. The list is made here, the files are removed on the worker.
    /// </summary>
    private void Prune(PhotoWorker worker, DateTime now)
    {
        // an empty read of the library is no reason to forget photos
        if (_prunedFor == _library.Version || now - _prunedAt < TimeSpan.FromMinutes(1) && _prunedFor >= 0 || _library.Parts.Count == 0)
            return;
        _prunedFor = _library.Version;
        _prunedAt  = now;
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in _library.Parts)
            if (MergesSlots(part.Key.Socket))
                live.Add(part.KeyText);
        foreach (var key in _photos.Keys.Where(k => !live.Contains(k.ToString())).ToList())
            _photos.Remove(key);
        worker.Prune(live);
    }

    /// <summary> The settings sheet's numbers: made again at most twice a second, and only when photos came in or parts moved. </summary>
    private void CountPhotos()
    {
        var now = _now();
        if (!_photoCountDirty && _photoCountPlace == _placeStamp || now - _photoCountAt < PhotoCountEvery && _photoCountPlace >= 0)
            return;
        _photoCountDirty = false;
        _photoCountPlace = _placeStamp;
        _photoCountAt    = now;
        var things = OnePerThing(_byKey.Values.Where(p => MergesSlots(p.Key.Socket) && !_aside.ContainsKey(p.Key)).OrderBy(p => p.Key.Socket));
        var taken  = things.Count(p => FileOfThing(p) is not null);
        _photoCount = (taken, things.Count);
    }

    private void StopPhotos()
        => _photoWorker?.Dispose();

    // ======== the worker ========

    /// <summary>
    /// One thread below normal priority that draws (the maker) and writes (the store). It loads the index first, then waits
    /// for parts; the game's thread hands it one at a time and takes the results from <see cref="Results"/>.
    /// </summary>
    private sealed class PhotoWorker : IDisposable
    {
        private readonly IPhotoMaker _maker;
        private readonly PhotoStore _store;
        private readonly Action<string>? _log;
        private readonly Thread _thread;
        private readonly CancellationTokenSource _stop = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly object _gate = new();
        private (PartKey Key, string Text, string Stamp, PhotoAsk Ask)? _job;
        private IReadOnlySet<string>? _prune;
        private bool _rest;
        private volatile bool _loaded;
        private bool _busy;

        // the summary of a stretch of work
        private int _taken;
        private int _nothing;
        private int _failed;
        private readonly Dictionary<string, int> _kinds = new(StringComparer.Ordinal);

        /// <summary> While working, the index is saved every this many photos, or after this long, whichever comes first. </summary>
        private const int SaveEvery = 50;
        private static readonly TimeSpan SaveAfter = TimeSpan.FromSeconds(30);
        private int _sinceSave;
        private readonly System.Diagnostics.Stopwatch _saved = System.Diagnostics.Stopwatch.StartNew();

        private void SaveNow()
        {
            _store.Save();
            _sinceSave = 0;
            _saved.Restart();
        }

        /// <summary> Photos that could not be kept in a row (one kept resets it); at three the worker stops taking them. </summary>
        private int _keepFailures;
        private volatile bool _broken;
        private readonly HashSet<string> _said = new(StringComparer.Ordinal);

        /// <summary> The photo folder cannot be written: no more photos this session. </summary>
        public bool Broken
            => _broken;

        /// <summary> Says something about an exception once a session per kind of exception, by its type only. </summary>
        private void Once(string what, Exception ex)
        {
            if (_said.Add($"{what}|{ex.GetType().Name}"))
                _log?.Invoke($"photos: {what} ({ex.GetType().Name})");
        }
        private readonly System.Diagnostics.Stopwatch _working = new();

        public readonly ConcurrentQueue<List<(PartKey Key, PhotoStore.Entry Entry)>> Results = new();

        public PhotoWorker(IPhotoMaker maker, PhotoStore store, Action<string>? log)
        {
            _maker  = maker;
            _store  = store;
            _log    = log;
            _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "ModCloset2 photos" };
            _thread.Start();
        }

        public bool Loaded
            => _loaded;

        public bool Idle
        {
            get
            {
                lock (_gate)
                    return !_stopped && _job is null && !_busy && _prune is null;
            }
        }

        private volatile bool _stopped;

        /// <summary> For the tests: raised in the loop outside any job. </summary>
        public Func<Exception?>? LoopFault { get; init; }

        /// <summary> The worker has ended (unloading, or something it could not survive): it is handed nothing more. </summary>
        public bool Stopped
            => _stopped;

        public void Take(PartKey key, string text, string stamp, PhotoAsk ask)
        {
            lock (_gate)
                _job = (key, text, stamp, ask);
            _wake.Set();
        }

        public void Prune(IReadOnlySet<string> live)
        {
            lock (_gate)
                _prune = live;
            _wake.Set();
        }

        public void Rest()
        {
            lock (_gate)
                _rest = true;
            _wake.Set();
        }

        private void Run()
        {
            try
            {
                // the index is read first; whatever goes wrong with it, the worker goes on (an empty index: photos are taken again)
                var known = new List<(PartKey, PhotoStore.Entry)>();
                try
                {
                    _store.Load();
                    foreach (var (text, entry) in _store.Index)
                        if (PartKey.TryParse(text, out var key))
                            known.Add((key, entry));
                }
                catch (Exception ex)
                {
                    Once("the index could not be read", ex);
                    known.Clear();
                }

                Results.Enqueue(known);
                _loaded = true;

                while (!_stop.IsCancellationRequested)
                {
                    _wake.WaitOne(500);
                    if (_stop.IsCancellationRequested)
                        break;
                    if (LoopFault?.Invoke() is { } fault)
                        throw fault;

                    (PartKey Key, string Text, string Stamp, PhotoAsk Ask)? job;
                    IReadOnlySet<string>? prune;
                    bool rest;
                    lock (_gate)
                    {
                        job    = _job;
                        prune  = _prune;
                        rest   = _rest;
                        _rest  = false;
                        _busy  = job is not null || prune is not null;
                        _job   = null;
                        _prune = null;
                    }

                    // one bad job never ends the worker: it is said once per kind, and the next one goes on
                    try
                    {
                        if (prune is not null)
                        {
                            _store.Prune(prune);
                            _store.Save();
                        }

                        if (job is { } j)
                        {
                            Draw(j.Key, j.Text, j.Stamp, j.Ask);
                            // the index does not wait for a rest: a long first pass never rests, and a game crash would
                            // lose it (the next start would then sweep every newer photo away as unnamed)
                            if (++_sinceSave >= SaveEvery || _saved.Elapsed >= SaveAfter)
                                SaveNow();
                        }

                        if (rest)
                        {
                            SaveNow();
                            _maker.Rest();
                            Summary();
                        }
                    }
                    catch (Exception ex)
                    {
                        Once("a photo job failed", ex);
                    }
                    finally
                    {
                        lock (_gate)
                            _busy = false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"photos: the worker stopped ({ex.GetType().Name})");
            }
            finally
            {
                _stopped = true;
                try
                {
                    _store.Save();
                    _maker.Rest();
                }
                catch
                {
                    // unloading: nothing more to do
                }
            }
        }

        private void Draw(PartKey key, string text, string stamp, PhotoAsk ask)
        {
            _working.Start();
            try
            {
                PhotoShot shot;
                try
                {
                    shot = _maker.Take(ask, _stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // the maker itself broke: counted under the exception's kind, never its message
                    shot = new PhotoShot { Problem = ex.GetType().Name };
                }

                // stopped in the middle: nothing is kept (it is taken again next time)
                if (_stop.IsCancellationRequested)
                    return;

                PhotoStore.Entry entry;
                if (shot.File is { Length: > 0 })
                {
                    try
                    {
                        entry = _store.Put(text, stamp, shot.File, shot.Extension, _stop.Token);
                        _taken++;
                        _keepFailures = 0;
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        // unloading during the waits: nothing kept, nothing counted
                        return;
                    }
                    catch (Exception ex)
                    {
                        // the disk is full, the folder cannot be made or written, a scanner holds the file: the part is settled
                        // for this session only (not in the index: the next session tries again), an older photo stays shown
                        entry = new PhotoStore.Entry(stamp, _store.Index.TryGetValue(text, out var old) ? old.File : string.Empty, "session",
                            "the photo could not be kept");
                        _failed++;
                        _kinds[entry.Why] = _kinds.GetValueOrDefault(entry.Why) + 1;
                        if (++_keepFailures >= 3 && !_broken)
                        {
                            // the one line about it; the maker lets its memory go, and no summary follows
                            _broken = true;
                            _log?.Invoke($"photos: the photo folder cannot be written ({ex.GetType().Name}); no more photos this session");
                            _taken = _nothing = _failed = 0;
                            _kinds.Clear();
                            _maker.Rest();
                        }
                    }
                }
                else if (shot.Problem.Length > 0)
                {
                    // a take that failed is tried again under the next plugin version, never twice under this one
                    // only the sentence: Detail can name files and is never logged or kept
                    _failed++;
                    entry = _store.Failed(text, stamp, shot.Problem);
                    _kinds[entry.Why] = _kinds.GetValueOrDefault(entry.Why) + 1;
                }
                else
                {
                    _nothing++;
                    entry = _store.Put(text, stamp, null, shot.Extension);
                }

                Results.Enqueue(new List<(PartKey, PhotoStore.Entry)> { (key, entry) });
            }
            catch (Exception ex)
            {
                _failed++;
                _kinds[ex.GetType().Name] = _kinds.GetValueOrDefault(ex.GetType().Name) + 1;
                Once("a photo could not be kept", ex);
                // settled for this session all the same, or it would be handed out again the next frame
                Results.Enqueue(new List<(PartKey, PhotoStore.Entry)> { (key, new PhotoStore.Entry(stamp, string.Empty, "session", ex.GetType().Name)) });
            }
            finally
            {
                _working.Stop();
            }
        }

        /// <summary>
        /// One line per stretch of work, when the maker rests. No mod names: what could not be drawn is counted by the
        /// maker's sentence (most frequent first, at most five, the rest as "other").
        /// </summary>
        private void Summary()
        {
            if (_taken + _nothing + _failed == 0)
                return;
            _log?.Invoke(SummaryLine(_taken, _nothing, _failed, _kinds, _working.Elapsed));
            _taken = _nothing = _failed = 0;
            _kinds.Clear();
            _working.Reset();
        }

        /// <summary> Unload: stops the worker (the maker honours the stop between its passes) and waits for it a moment. </summary>
        public void Dispose()
        {
            _stop.Cancel();
            _wake.Set();
            if (!_thread.Join(TimeSpan.FromSeconds(3)))
                _log?.Invoke("photos: the worker did not stop in time");
        }
    }

    /// <summary> The worker's summary line (no names: kinds are the maker's fixed sentences, already made safe). </summary>
    internal static string SummaryLine(int taken, int nothing, int failed, IReadOnlyDictionary<string, int> kinds, TimeSpan worked)
    {
        var line = $"photos: {taken} taken, {nothing} with nothing to show, {failed} could not be drawn";
        if (failed > 0 && kinds.Count > 0)
        {
            var named = kinds.Where(kv => kv.Key != "other").OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            var shown = named.Take(5).ToList();
            var other = kinds.GetValueOrDefault("other") + named.Skip(5).Sum(kv => kv.Value);
            var parts = shown.Select(kv => $"{kv.Value} {kv.Key}").ToList();
            if (other > 0)
                parts.Add($"{other} other");
            line += $" ({string.Join(", ", parts)})";
        }

        return line + $", {worked.TotalSeconds:F0} s";
    }
}
