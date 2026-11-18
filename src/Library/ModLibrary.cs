using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using ModCloset2.Closet;
using ModCloset2.Core;
using ModCloset2.Game;

namespace ModCloset2.Library;

/// <summary>
/// The mod library: the mod list from Penumbra, what each mod's folder says (background), what Penumbra says each mod
/// changes (step 1, over IPC on the framework thread, cached on disk), the user's Penumbra folders, and the detector's
/// answer (step 2, on low-priority background threads, cached on disk). Builds the parts the closet shows.
/// Everything public is for the framework / draw thread.
/// </summary>
public sealed class ModLibrary : ILibrary, IDisposable
{
    private const int IpcItemsPerFrame    = 30;
    private const int CachedItemsPerFrame = 500;
    private const int FoldersPerFrame     = 200;
    private const int DetectThreads       = 2;
    private static readonly TimeSpan PublishEvery = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// While the detector works through the library (minutes on a first scan), results are handed on in bigger, rarer
    /// batches: each one costs the session a little work on the framework thread.
    /// </summary>
    private static readonly TimeSpan PublishEveryWhileDetecting = TimeSpan.FromSeconds(4);

    /// <summary> More changed mods than this in one batch: the session rebuilds everything instead of mod by mod. </summary>
    private const int MaxListedChanges = 2000;

    private readonly Ipc _ipc;
    private readonly ItemIndex _itemIndex;
    private readonly IPluginLog _log;
    private readonly Func<PlayerInfo> _player;
    private readonly Func<string> _body;
    private readonly string _pluginConfigs;
    private readonly DiskCache<ModItems> _itemsCache;
    private readonly DiskCache<DetectionEntry> _detectCache;
    private readonly DiskCache<SeenEntry> _seen;
    private IDetector _detector;

    // ---- the mods ----

    private Dictionary<string, LibMod> _mods = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _order = new();
    private string _root = string.Empty;
    private bool _listLoaded;
    private bool _reloadRequested = true;
    private DateTime _lastEmptyReload = DateTime.MinValue;
    private int _generation;

    // ---- step queues ----

    private readonly ConcurrentQueue<(int Generation, string Mod, ModFacts Facts)> _factsDone = new();
    private Task? _factsTask;
    private readonly Queue<string> _needItems = new();
    private readonly Queue<string> _needFolder = new();
    private bool _foldersDone;
    private readonly HashSet<string> _detectionAsked = new(StringComparer.OrdinalIgnoreCase);
    private bool _askDirty = true;
    private readonly ConcurrentQueue<(int Generation, string Mod, string Stamp, int Version, ModDetection Detection)> _detected = new();

    /// <summary> Mods whose detection did not finish (cancelled from inside): asked again on the next round. </summary>
    private readonly ConcurrentQueue<string> _askAgain = new();
    private readonly HashSet<string> _reasked = new(StringComparer.OrdinalIgnoreCase);
    private BlockingCollection<(int Generation, string Mod, string Folder, string Stamp, ModHints Hints)> _jobs = new();
    private CancellationTokenSource _cancel = new();
    private readonly List<Thread> _workers = new();
    private readonly CancellationTokenSource _stop = new();
    private OldPictures? _old;

    // ---- what the closet sees ----

    private readonly Dictionary<string, List<LibPart>> _partsByMod = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _partsDirty = new(StringComparer.OrdinalIgnoreCase);
    private PlayerInfo _builtFor = PlayerInfo.Nobody;
    private DateTime _published = DateTime.MinValue;
    private bool _changed = true;

    public int Version { get; private set; }
    public LibraryProgress Progress { get; private set; } = LibraryProgress.Starting;
    private IReadOnlyList<LibPart>? _parts;

    /// <summary> Every part, made on first use after a change (the session asks mod by mod; this is for whole-library questions). </summary>
    public IReadOnlyList<LibPart> Parts
        => _parts ??= _order.Where(_partsByMod.ContainsKey).SelectMany(d => _partsByMod[d]).ToList();

    public IReadOnlyList<LibPart> PartsOf(string mod)
        => _partsByMod.TryGetValue(mod, out var list) ? list : Array.Empty<LibPart>();

    /// <summary> Which mods each published version changed (null: too many to list, or everything). </summary>
    private readonly List<(int Version, HashSet<string>? Mods)> _changeLog = new();
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    private bool _everything = true;

    public IReadOnlyCollection<string>? ChangedSince(int version)
    {
        if (version == Version)
            return Array.Empty<string>();
        var entries = _changeLog.Where(e => e.Version > version).ToList();
        if (entries.Count == 0 || entries[0].Version != version + 1 || entries.Any(e => e.Mods is null))
            return null;
        var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            mods.UnionWith(e.Mods!);
        return mods;
    }
    public string CacheDirectory { get; }

    public IDetector Detector
        => _detector;

    public ModLibrary(Ipc ipc, ItemIndex itemIndex, IPluginLog log, string configDirectory, string pluginConfigs, Func<PlayerInfo> player,
        Func<string> body, IDetector detector)
    {
        _ipc           = ipc;
        _itemIndex     = itemIndex;
        _log           = log;
        _player        = player;
        _body          = body;
        _pluginConfigs = pluginConfigs;
        _detector      = detector;
        CacheDirectory = configDirectory;
        _itemsCache    = new DiskCache<ModItems>(Path.Combine(configDirectory, "library.json"), m => _log.Debug($"Mod Closet 2: {m}"));
        _detectCache   = new DiskCache<DetectionEntry>(Path.Combine(configDirectory, "detection.json"), m => _log.Debug($"Mod Closet 2: {m}"));
        _seen          = new DiskCache<SeenEntry>(Path.Combine(configDirectory, "seen.json"), m => _log.Debug($"Mod Closet 2: {m}"));
        _ipc.ModsChanged += RequestReload;
        _ipc.ModMoved    += OnModMoved;

        for (var i = 0; i < DetectThreads; ++i)
        {
            var thread = new Thread(DetectLoop) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = $"ModCloset2 detect {i + 1}" };
            _workers.Add(thread);
            thread.Start();
        }
    }

    /// <summary> Puts the smart detection in (until then a <see cref="NoDetector"/> stands in). Everything is detected again where the version differs. </summary>
    public void UseDetector(IDetector detector)
    {
        _detector = detector;
        RestartDetection();
    }

    public LibMod? Mod(string directory)
        => _mods.TryGetValue(directory, out var mod) ? mod : null;

    private Dictionary<PartKey, LibPart> _partByKey = new();

    /// <summary> A part as last published (also one that does not fit the player), or null. </summary>
    public LibPart? Part(PartKey key)
        => _partByKey.TryGetValue(key, out var part) ? part : null;

    /// <summary> The body the user picked in the old Mod Closet ("Rue"), "" when none or not read yet. </summary>
    public string OldBody
        => _old?.MyBody ?? string.Empty;

    public void RequestReload()
        => _reloadRequested = true;

    public void Rescan()
    {
        _itemsCache.Clear();
        _detectCache.Clear();
        _mods = new Dictionary<string, LibMod>(StringComparer.OrdinalIgnoreCase);
        _order = new List<string>();
        _partsByMod.Clear();
        _partByKey.Clear();
        _parts      = null;
        _everything = true;
        _listLoaded = false;
        _old        = null;
        RestartDetection();
        _reloadRequested = true;
        _changed         = true;
    }

    public void OpenInPenumbra(string mod, string name)
        => _ipc.OpenMod(mod, name);

    public IReadOnlyList<(string Folder, ModHints Hints)> CensusInput()
        => _order.Select(d => _mods[d]).Where(m => m.Facts is { Folder.Length: > 0 }).Select(m => (m.Facts!.Folder, Hints(m))).ToList();

    private ModHints Hints(LibMod mod)
        => new(mod.Name, mod.SortFolder, mod.Facts?.Tags ?? Array.Empty<string>(), Races.Chain(_player().RaceCode), _body());

    // ======== the loop ========

    /// <summary> Framework thread: reloads when asked, feeds every step a little, and publishes the parts now and then. </summary>
    public void Update()
    {
        var emptyRetry = !_listLoaded && _ipc.PenumbraAvailable && (DateTime.UtcNow - _lastEmptyReload).TotalSeconds > 5;
        if (_reloadRequested || emptyRetry)
        {
            _reloadRequested = false;
            _lastEmptyReload = DateTime.UtcNow;
            Reload();
        }

        TakeFacts();
        ReadItems();
        ReadFolders();
        TakeDetections();
        AskDetections();

        var player = _player();
        if (player.RaceCode != _builtFor.RaceCode || player.Clan != _builtFor.Clan || player.Gender != _builtFor.Gender)
        {
            _builtFor = player;
            foreach (var dir in _order)
                _partsDirty.Add(dir);
            _changed = true;
        }

        if (_changed && DateTime.UtcNow - _published >= (Progress.Detecting && Progress.Ready ? PublishEveryWhileDetecting : PublishEvery))
            Publish();
    }

    private void Reload()
    {
        if (!_ipc.PenumbraAvailable)
            return;

        var root = _ipc.ModDirectory();
        if (root.Length == 0)
            return;
        FrameReport.Step("reading the mod list");
        var list = _ipc.ModList();

        var mods  = new Dictionary<string, LibMod>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>(list.Count);
        var fresh = new List<(string Dir, string Name)>();
        foreach (var (dir, name) in list.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (_mods.TryGetValue(dir, out var existing) && existing.Name == name && root == _root)
            {
                mods[dir] = existing;
            }
            else
            {
                if (_seen.Get(dir) is not { } seen)
                    _seen.Set(dir, seen = new SeenEntry { At = DateTime.UtcNow });
                mods[dir] = new LibMod { Directory = dir, Name = name, FirstSeen = seen.At };
                fresh.Add((dir, name));
                _partsDirty.Add(dir);
            }

            order.Add(dir);
        }

        foreach (var gone in _mods.Keys.Where(k => !mods.ContainsKey(k)).ToList())
        {
            _partsByMod.Remove(gone);
            _removed.Add(gone);
        }

        _seen.Save();
        _root       = root;
        _mods       = mods;
        _order      = order;
        _listLoaded = true;
        _changed    = true;
        _askDirty   = true;

        // every mod's Penumbra folder again (a move between folders does not change the mod)
        _needFolder.Clear();
        foreach (var dir in order)
            _needFolder.Enqueue(dir);
        _foldersDone = false;

        if (fresh.Count > 0)
            StartFacts(fresh.Select(f => f.Dir).ToList(), root);
    }

    // ---- facts (background) ----

    private void StartFacts(List<string> dirs, string root)
    {
        var generation = ++_generation;
        var previous   = _factsTask ?? Task.CompletedTask;
        _factsTask = previous.ContinueWith(_ =>
        {
            _old ??= OldPictures.Read(_pluginConfigs);
            var old = _old;
            foreach (var dir in dirs)
            {
                // a newer reload took over: it reads these mods itself; unloading stops it too
                if (generation != _generation || _stop.IsCancellationRequested)
                    return;
                try
                {
                    var folder = ModFolder.Resolve(root, dir);
                    var facts  = folder is null
                        ? new ModFacts { Broken = true }
                        : ModFolder.ReadFacts(folder, old.IsOldPicture, () => old.Find(folder, dir));
                    _factsDone.Enqueue((generation, dir, facts));
                }
                catch (Exception ex)
                {
                    _log.Debug($"Mod Closet 2: could not read {dir}: {ex.Message}");
                    _factsDone.Enqueue((generation, dir, new ModFacts { Broken = true }));
                }
            }
        }, TaskScheduler.Default);
    }

    private void TakeFacts()
    {
        while (_factsDone.TryDequeue(out var done))
        {
            if (!_mods.TryGetValue(done.Mod, out var mod))
                continue;
            _mods[done.Mod] = mod.With(facts: done.Facts);
            _needItems.Enqueue(done.Mod);
            _partsDirty.Add(done.Mod);
            _changed  = true;
            _askDirty = true;
        }
    }

    // ---- step 1: what Penumbra says a mod changes ----

    private void ReadItems()
    {
        if (!_itemIndex.Ready || _needItems.Count == 0)
            return;

        var calls = 0;
        for (var i = 0; i < CachedItemsPerFrame && calls < IpcItemsPerFrame && _needItems.TryPeek(out var dir); ++i)
        {
            _needItems.Dequeue();
            if (!_mods.TryGetValue(dir, out var mod) || mod.Facts is not { } facts)
                continue;

            ModItems items;
            if (_itemsCache.Get(dir) is { } cached && cached.Stamp == facts.Stamp && facts.Stamp.Length > 0)
            {
                items = cached;
            }
            else
            {
                calls++;
                if (_ipc.ChangedItems(dir) is not { } changed)
                {
                    // Penumbra did not answer (reloading?): try again later, and never cache a failed read.
                    _needItems.Enqueue(dir);
                    break;
                }

                items       = ChangedItems.Classify(changed, _itemIndex.Find);
                items.Stamp = facts.Stamp;
                _itemsCache.Set(dir, items);
            }

            _mods[dir] = mod.With(items: items);
            _partsDirty.Add(dir);
            _changed = true;
        }

        if (_needItems.Count == 0)
            _itemsCache.Save();
    }

    // ---- the user's Penumbra folders (port of the old PenumbraFolders) ----

    private void ReadFolders()
    {
        if (_needFolder.Count == 0 || !_ipc.PenumbraAvailable)
            return;

        for (var i = 0; i < FoldersPerFrame && _needFolder.TryDequeue(out var dir); ++i)
        {
            if (!_mods.TryGetValue(dir, out var mod) || _ipc.ModPath(dir) is not { } full)
                continue;
            // "Clothes/Female/Top/Mod name" → "Clothes/Female/Top"
            var cut    = full.LastIndexOf('/');
            var folder = cut > 0 ? full[..cut] : string.Empty;
            if (folder == mod.SortFolder)
                continue;
            _mods[dir] = mod.With(sortFolder: folder);
            _partsDirty.Add(dir);
            _changed = true;
        }

        _foldersDone = _needFolder.Count == 0;
        _askDirty   |= _foldersDone;
    }

    // ---- step 2: the detector (background, cached) ----

    private void RestartDetection()
    {
        _cancel.Cancel();
        _cancel = new CancellationTokenSource();
        var old = _jobs;
        _jobs = new BlockingCollection<(int, string, string, string, ModHints)>();
        old.CompleteAdding();
        _detectionAsked.Clear();
        _askDirty = true;
        foreach (var (dir, mod) in _mods.ToList())
            if (mod.Detection is not null)
            {
                _mods[dir] = mod.With(clearDetection: true);
                _partsDirty.Add(dir);
            }

        _changed = true;
    }

    /// <summary> Hands mods to the detector once step 1 is done and the folders are known (the folder is one of its hints). </summary>
    private void AskDetections()
    {
        if (!_askDirty || _detector.Version <= 0 || !_listLoaded || _needItems.Count > 0 || !_foldersDone || !_factsDone.IsEmpty)
            return;
        _askDirty = false;

        var generation = _generation;
        foreach (var dir in _order)
        {
            if (_detectionAsked.Contains(dir) || !_mods.TryGetValue(dir, out var mod) || mod.Facts is not { Folder.Length: > 0 } facts || mod.Items is null)
                continue;
            _detectionAsked.Add(dir);

            if (DetectionKey.Valid(_detectCache.Get(dir), facts.Stamp, _detector.Version))
            {
                _mods[dir] = mod.With(detection: _detectCache.Get(dir)!.Detection);
                _partsDirty.Add(dir);
                _changed = true;
                continue;
            }

            try
            {
                _jobs.Add((generation, dir, facts.Folder, facts.Stamp, Hints(mod)));
            }
            catch (InvalidOperationException)
            {
                // the queue was replaced meanwhile (rescan): the next round asks again
                _detectionAsked.Remove(dir);
                _askDirty = true;
            }
        }
    }

    private void DetectLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var jobs = _jobs;
            (int Generation, string Mod, string Folder, string Stamp, ModHints Hints) job;
            try
            {
                if (!jobs.TryTake(out job, 500, _stop.Token))
                    continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var token    = _cancel.Token;
            var detector = _detector;
            if (token.IsCancellationRequested)
                continue;

            ModDetection result;
            try
            {
                result = detector.Detect(job.Folder, job.Hints, token);
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            catch (Exception ex)
            {
                // the detector promises never to throw; if it does, the mod keeps its step-1 parts
                result = new ModDetection { DetectorVersion = detector.Version, Problems = { ex.Message } };
            }

            // a cancelled detection says so with version 0 and no parts: it must never be cached
            if (!token.IsCancellationRequested && result.DetectorVersion == detector.Version)
                _detected.Enqueue((job.Generation, job.Mod, job.Stamp, detector.Version, result));
            else if (!token.IsCancellationRequested)
                _askAgain.Enqueue(job.Mod);
        }
    }

    private DateTime _detectSaved = DateTime.UtcNow;

    private void TakeDetections()
    {
        while (_askAgain.TryDequeue(out var again))
        {
            if (_reasked.Add(again))
            {
                _detectionAsked.Remove(again);
                _askDirty = true;
            }
            else if (_mods.TryGetValue(again, out var gave))
            {
                // twice without an answer: step 1 carries it for this session (nothing cached, the next start tries again)
                _mods[again] = gave.With(detection: new ModDetection { DetectorVersion = _detector.Version });
                _changed     = true;
            }
        }

        while (_detected.TryDequeue(out var done))
        {
            if (done.Version != _detector.Version || !_mods.TryGetValue(done.Mod, out var mod) || mod.Facts?.Stamp != done.Stamp)
                continue;
            if (done.Detection.Problems.Count > 0)
                _log.Debug($"Mod Closet 2: {mod.Name}: {string.Join("; ", done.Detection.Problems.Take(3))}");
            _detectCache.Set(done.Mod, new DetectionEntry { Stamp = done.Stamp, Version = done.Version, Detection = done.Detection });
            _mods[done.Mod] = mod.With(detection: done.Detection);
            _partsDirty.Add(done.Mod);
            _changed = true;
        }

        // write the cache now and then while detecting, and at the end
        if (_detectCache.Dirty && (DateTime.UtcNow - _detectSaved).TotalSeconds > 30 || _detectCache.Dirty && _jobs.Count == 0 && _detected.IsEmpty)
        {
            _detectSaved = DateTime.UtcNow;
            _detectCache.Save();
        }
    }

    // ---- publishing ----

    private void Publish()
    {
        FrameReport.Step("publishing what was read");
        _published = DateTime.UtcNow;
        _changed   = false;

        var changed = new HashSet<string>(_removed, StringComparer.OrdinalIgnoreCase);
        foreach (var dir in _partsDirty)
        {
            if (!_mods.TryGetValue(dir, out var mod))
                continue;
            changed.Add(dir);
            if (_partsByMod.TryGetValue(dir, out var old))
                foreach (var part in old)
                    _partByKey.Remove(part.Key);

            List<LibPart> built;
            try
            {
                built = PartBuilder.Build(mod, _builtFor);
            }
            catch (Exception ex)
            {
                // one mod must never stop the library from updating: it gets no parts, the log says why
                _log.Warning($"Mod Closet 2: could not make the parts of {mod.Name}: {ex.Message}");
                built = new List<LibPart>();
            }

            _partsByMod[dir] = built;
            foreach (var part in built)
                _partByKey[part.Key] = part;
        }

        foreach (var gone in _removed)
            _partByKey.Keys.Where(k => k.Mod.Equals(gone, StringComparison.OrdinalIgnoreCase)).ToList().ForEach(k => _partByKey.Remove(k));
        _partsDirty.Clear();
        _removed.Clear();
        _parts = null;

        var read     = _order.Count(d => _mods[d].Items is not null);
        // a mod without a folder to look at (missing on disk) counts as done: the detector cannot get to it
        var detected = _detector.Version <= 0 ? _order.Count : _order.Count(d => _mods[d] is var m && (m.Detection is not null || m.Facts is { Folder.Length: 0 }));
        Progress   = new LibraryProgress
        {
            Ready     = _listLoaded && read == _order.Count,
            Mods      = _order.Count,
            Read      = read,
            Detected  = detected,
            Detecting = _detector.Version > 0 && detected < _order.Count,
            Problem   = _ipc.PenumbraAvailable ? string.Empty : Loc.T("Penumbra is not running."),
        };
        Version++;
        _changeLog.Add((Version, _everything || changed.Count > MaxListedChanges ? null : changed));
        _everything = false;
        if (_changeLog.Count > 64)
            _changeLog.RemoveAt(0);
    }

    /// <summary> Called by the plugin every second or so: notices Penumbra coming and going. </summary>
    public void CheckProblem()
    {
        var problem = _ipc.PenumbraAvailable ? string.Empty : Loc.T("Penumbra is not running.");
        if (problem != Progress.Problem)
            _changed = true;
    }

    private void OnModMoved(string oldDirectory, string newDirectory)
    {
        _seen.Rename(oldDirectory, newDirectory);
        _itemsCache.Rename(oldDirectory, newDirectory);
        _detectCache.Rename(oldDirectory, newDirectory);
    }

    public void Dispose()
    {
        _ipc.ModsChanged -= RequestReload;
        _ipc.ModMoved    -= OnModMoved;
        _stop.Cancel();
        _cancel.Cancel();
        _jobs.CompleteAdding();
        foreach (var thread in _workers)
            thread.Join(TimeSpan.FromSeconds(2));
        _itemsCache.Save();
        _detectCache.Save();
        _seen.Save();
        _itemsCache.Flush();
        _detectCache.Flush();
        _seen.Flush();
    }
}
