using Dalamud.Plugin.Services;
using ModCloset2.Closet;
using ModCloset2.Core;
using ModCloset2.Game;
using ModCloset2.Library;
using Newtonsoft.Json.Linq;

namespace ModCloset2.Wear;

/// <summary> What the wardrobe keeps in the plugin's settings. </summary>
public interface IWearSettings
{
    /// <summary> Mods put on with Keep (mod directory → what to put back on take-off). </summary>
    Dictionary<string, WornRecord> Worn { get; }

    /// <summary> Lowest priority of the temporary settings used for trying on. </summary>
    int TemporaryPriority { get; }
}

/// <summary>
/// Puts parts on the local player and takes them off (port of the old Outfit, with parts instead of whole mods). Framework
/// thread only.
/// - Try on: temporary. The mods are switched on with temporary Penumbra settings above everything else and the tried
///   parts' items / numbers put on over your look; your look is saved first and put back when trying ends.
/// - Keep: permanent. The mods are enabled in your collection above the mods they compete with, the options saved, the
///   items put on. Every Keep is one step in the history.
/// - Take off: puts the mod's earlier settings back and empties what it showed, only where it still shows what Keep put on.
/// </summary>
public sealed partial class Wardrobe : IWardrobe, IPeriodicReader, IDisposable
{
    private const int PlayerIndex = 0;

    private readonly Ipc _ipc;
    private readonly GameState _game;
    private readonly IWearSettings _settings;
    private readonly Action _saveConfig;
    private readonly IPluginLog _log;
    private readonly Journal _journal;
    private readonly Func<PartKey, LibPart?> _part;
    private readonly Func<string, LibMod?> _mod;
    private readonly Func<string> _body;

    /// <summary> When calls may run, frame by frame (quiet while loading, one call per frame): the game state's. </summary>
    private readonly IpcPace _pace;

    /// <summary> The settings and what shows, read every few seconds, one call per frame. </summary>
    private readonly PeriodicReads _reads;

    /// <summary>
    /// The priority a kept mod needs to win in the given sockets (just above what it competes with there), or null when
    /// that is not known (then it goes above every enabled mod, as the old plugin did without its analysis).
    /// Arguments: mod, sockets being kept, its current priority (int.MinValue: only what it needs, int.MinValue when it
    /// competes with nothing), mods not to count as competitors (an upper layer it goes under).
    /// </summary>
    public Func<string, IReadOnlyCollection<Socket>, int, IReadOnlyCollection<string>, int?>? PriorityAdvisor { get; set; }

    /// <summary>
    /// The game's category of a weapon or off-hand item by id ("Gladiator's Arm"), or null: a weapon goes into a hand
    /// only when the job holds that category there now (see <see cref="WearRules.CanHold"/>).
    /// </summary>
    public Func<ulong, (uint Id, string Name)?>? WeaponCategory { get; set; }

    // ---- trying on ----

    private string? _savedLook;
    private string _savedOwner = string.Empty;
    private string? _pendingRestore;
    private string _pendingOwner = string.Empty;
    private int _pendingRefused;
    private DateTime _pendingDue;
    private Guid? _previewCollection;
    private readonly List<string> _previewMods = new();
    private readonly List<string> _previewOff = new();
    private readonly HashSet<Guid> _touched = new();
    private bool _previewActive;

    // ---- the history steps asked for ----

    private readonly Dictionary<Guid, StepState> _steps = new();

    // ---- what is on ----

    private Dictionary<Socket, IReadOnlyList<string>> _kept = new();
    private Dictionary<string, ulong> _keptItems = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> A face is open (the window, On you): what is on and the collection's settings are read every few seconds. </summary>
    public bool Watching { get; set; }

    /// <summary>
    /// With no face open, something still follows what is worn (the colours): what shows is read every few seconds, but
    /// not the collection's settings (thousands of mods), which only a face needs.
    /// </summary>
    public bool Following { get; set; }

    /// <summary> The game item in a gear slot (Glamourer's slot name, "Body") as last read, 0 when unknown or none. </summary>
    public ulong ItemIn(string slot)
        => _keptItems.GetValueOrDefault(slot);

    public Wardrobe(Ipc ipc, GameState game, IWearSettings settings, Action saveConfig, IPluginLog log, Journal journal,
        Func<PartKey, LibPart?> part, Func<string, LibMod?> mod, Func<string> body)
    {
        _ipc        = ipc;
        _game       = game;
        _settings   = settings;
        _saveConfig = saveConfig;
        _log        = log;
        _journal    = journal;
        _part       = part;
        _mod        = mod;
        _body       = body;
        _pace       = game.Pace;
        _reads      = new PeriodicReads(_pace, game.Clock);
        _ipc.Redrawn += OnRedrawn;
    }

    // ---- IWardrobe ----

    public int Version { get; private set; }

    public bool Busy
        => RedrawBusy || _queue.Count > 0;

    public bool Previewing
        => _previewActive || _queue.Any(q => q.Preview);

    public WardrobeMessage Message { get; private set; } = WardrobeMessage.None;

    public IReadOnlyList<string> ShowingIn(Socket socket)
        => Showing.In(_kept, socket);

    /// <summary> Notes a try or a keep for the slow-frame line: how many parts it puts on and takes off. </summary>
    private static void Report(string putOn, string takeOff, IReadOnlyList<TryRequest> requests)
    {
        var off = 0;
        foreach (var r in requests)
            if (r.Nothing)
                off++;
        if (requests.Count > off)
            FrameReport.Step(putOn, requests.Count - off);
        if (off > 0)
            FrameReport.Step(takeOff, off);
    }

    public bool IsOn(PartKey part)
        => part.Socket switch
        {
            Socket.None     => false,
            Socket.Other    => IsEnabled(part.Mod),
            Socket.BodyBase => WearRules.BodyPartOn(_kept.Values.Any(mods => mods.Contains(part.Mod, StringComparer.OrdinalIgnoreCase)),
                _mod(part.Mod)?.Facts?.Files.ContainsKey(Socket.BodyBase) ?? true, IsEnabled(part.Mod)),
            _               => ShowingIn(part.Socket).Contains(part.Mod, StringComparer.OrdinalIgnoreCase),
        };

    public IReadOnlyDictionary<string, List<string>>? SavedOptions(string mod)
        => Settings.TryGetValue(mod, out var s) && s.Options.Count > 0 ? s.Options : null;

    public IReadOnlyDictionary<string, List<string>> CurrentOptions(string mod)
        => OptionPicker.Starting(_mod(mod)?.Groups ?? Array.Empty<GroupInfo>(), SavedOptions(mod), Races.CodesFor(_game.Player().RaceCode), _body());

    public int? CustomizeNow(string key)
        => _keptCustomize.TryGetValue(key, out var value) ? value : null;

    /// <summary> The character's hairstyle, face and tail numbers as last read (with the kept outfit). </summary>
    private Dictionary<string, int> _keptCustomize = new(StringComparer.Ordinal);

    public string? RingOn(Hands hand)
    {
        // the mod whose item is on that finger: the one the closet put there, or one of the part's own items
        var slot = hand == Hands.Left ? "LFinger" : "RFinger";
        var item = _keptItems.GetValueOrDefault(slot);
        var mods = Showing.In(_kept, Socket.Rings);
        if (item == 0 || mods.Count == 0)
            return null;
        foreach (var mod in mods)
        {
            var part = _part(new PartKey(mod, Socket.Rings));
            if (_settings.Worn.TryGetValue(mod, out var record) && record.PutOnItems.TryGetValue(slot, out var put) && put == item
             || part is not null && (part.Items.Any(i => i.Id == item) || part.Versions.Any(i => i.Id == item)))
                return mod;
        }

        return null;
    }

    public IReadOnlyDictionary<string, List<string>> FittedOptions(string mod)
        => OptionPicker.Starting(_mod(mod)?.Groups ?? Array.Empty<GroupInfo>(), null, Races.CodesFor(_game.Player().RaceCode), _body());

    public bool OptionsSaved(string mod)
        => SavedOptions(mod) is not null;

    public int VersionShowing(PartKey part)
    {
        var lib = _part(part);
        if (lib is null)
            return -1;
        if (lib.Versions.Count <= 1)
            return 0;
        foreach (var slot in GearSlots.NamesOf(part.Socket))
            if (_keptItems.TryGetValue(slot, out var item) && lib.VersionOf(item) is >= 0 and var v)
                return v;
        return -1;
    }

    public StepState Step(Guid step)
        => _steps.TryGetValue(step, out var state) ? state : _journal.Has(step) ? StepState.Done : StepState.Unknown;

    // ---- the character's colours and showings (a look's) ----

    private Looks.CharacterLook? _character;
    private Looks.CharacterLook? _characterNow;

    public Looks.CharacterLook? CharacterNow
        => _characterNow;

    public void WithCharacter(Looks.CharacterLook? character)
        => _character = character is { IsEmpty: false } ? character : null;

    /// <summary> Puts the character's colours and showings on through Glamourer (only those keys); "" or a sentence on failure. </summary>
    private string PutOnCharacter(Looks.CharacterLook? character)
    {
        if (character is null)
            return string.Empty;
        var state = GlamourerState.CharacterState(character.Colours, character.Showing, _ipc.GetStateJson(PlayerIndex));
        var r = _ipc.ApplyState(state, PlayerIndex, Ipc.FlagEquipment | Ipc.FlagCustomization | Ipc.FlagOnce);
        if (r is Ipc.GlamourerSuccess or Ipc.GlamourerNothingDone)
            return string.Empty;
        Said($"wearing: Glamourer refused the colours (code {r})");
        return Loc.T("Glamourer did not take the look's colours.");
    }

    public void Preview(IReadOnlyList<TryRequest> parts)
    {
        if (parts.Count == 0 && _character is null)
        {
            PutBack();
            return;
        }

        var copy = parts.ToList();
        var character = _character;
        Enqueue(true, () => PreviewNow(copy, character));
    }

    public void PutBack()
    {
        DropQueuedPreviews();
        if (!_previewActive && _savedLook is null && _queue.Count == 0)
            return;
        Enqueue(false, EndPreview);
    }

    public void Keep(Guid step, string description, IReadOnlyList<TryRequest> parts)
    {
        // Keep makes what is tried permanent: a try still waiting for the redraw is part of it, not something to show first.
        DropQueuedPreviews();
        _steps[step] = StepState.Pending;
        var copy = parts.ToList();
        var character = _character;
        Enqueue(false, () => KeepNow(step, description, copy, character), step);
    }

    public void SetOptions(Guid step, string description, PartKey part, IReadOnlyDictionary<string, List<string>> options)
    {
        _steps[step] = StepState.Pending;
        var copy = options.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        _optionWrites.Add(step);
        Enqueue(false, () => SetOptionsNow(step, description, part, copy), step);
    }

    /// <summary> The steps that are option writes (several waiting in a row are written one per frame, with one redraw after the last). </summary>
    private readonly HashSet<Guid> _optionWrites = new();

    /// <summary> The next thing waiting is another option write: this one's redraw is left to it. </summary>
    private bool OptionWriteNext
        => _queue.Count > 0 && _queue[0].Step is { } next && _optionWrites.Contains(next);

    public void SetVersion(Guid step, string description, PartKey part, int version)
    {
        _steps[step] = StepState.Pending;
        Enqueue(false, () => SetVersionNow(step, description, part, version), step);
    }

    public void Undo(Guid step)
        => Enqueue(false, () => UndoNow(step));

    // ---- one redraw at a time ----
    //
    // The game is still loading the models of the last redraw: changing the mods under it now can hand the loader a file
    // for the wrong path (a crash in ModelResourceHandle.Load). So every change waits for the last redraw to settle; of
    // several tries waiting, only the newest counts (flipping through cards stacks up to the last one). The old plugin
    // gated only its previews; Mod Closet 2 also lets Keep, Put back and Undo wait, since they change mods just the same.

    private DateTime? _redrawAt;
    private DateTime? _redrawSettled;
    private readonly List<(bool Preview, Action Run, Guid? Step)> _queue = new();

    /// <summary> True while the game is still busy with the last redraw we asked for (at most 2 s). </summary>
    private bool RedrawBusy
    {
        get
        {
            if (_redrawAt is not { } at)
                return false;
            var now = DateTime.UtcNow;
            if ((now - at).TotalSeconds > 2 || (_redrawSettled is { } s && now >= s))
            {
                _redrawAt = _redrawSettled = null;
                return false;
            }

            return true;
        }
    }

    private void OnRedrawn(int index)
    {
        if (index != PlayerIndex)
            return;
        if (_redrawAt is not null)
            _redrawSettled ??= DateTime.UtcNow.AddSeconds(0.6); // the new draw object exists; its models load right after
        _reads.TruthNext = _game.Clock() + 1.0;
    }

    /// <summary> Redraws you and remembers that the game is busy with it. </summary>
    private void RedrawPlayer()
    {
        _redrawAt      = DateTime.UtcNow;
        _redrawSettled = null;
        _ipc.Redraw(PlayerIndex);
        _reads.TruthNext = _game.Clock() + 2.5;
    }

    private void Enqueue(bool preview, Action run, Guid? step = null)
    {
        // quiet while the game loads (and one call per frame): the step waits in the queue, Update runs it once settled
        if (_queue.Count == 0 && !RedrawBusy && _pace.May(IpcWork.Step))
        {
            _pace.Took(IpcWork.Step);
            Run(run, step);
            return;
        }

        if (preview)
            DropQueuedPreviews();
        _queue.Add((preview, run, step));
        Version++;
    }

    private void DropQueuedPreviews()
    {
        while (_queue.Count > 0 && _queue[^1].Preview)
            _queue.RemoveAt(_queue.Count - 1);
    }

    private void Run(Action run, Guid? step = null)
    {
        // never leave the window with an exception; the character may be half dressed, Put back still works
        WearRules.RunStep(run, step, _steps, ex =>
        {
            _log.Error(ex, "Mod Closet 2: dressing failed");
            Fail(Loc.T("Something went wrong. Put back still works."));
        });

        Version++;
    }

    // ---- the loop ----

    private bool _wasBusy;

    /// <summary> Framework thread: runs what waits, puts a saved look back once its character is here again, reads what is on. </summary>
    public void Update()
    {
        if (_queue.Count > 0 && !RedrawBusy && _pace.May(IpcWork.Step))
        {
            _pace.Took(IpcWork.Step);
            var (_, run, step) = _queue[0];
            _queue.RemoveAt(0);
            Run(run, step);
        }

        UpdateSlotCheck();
        UpdatePendingRestore();
        // the periodic reads: with no face open and nothing following, nothing needs what is on (Penumbra's settings,
        // thousands of mods, and the resource tree are not read then); Keep and the other steps read the settings
        // themselves, opening a face reads both
        _reads.Tick(this);

        var busy = Busy;
        if (busy != _wasBusy)
        {
            _wasBusy = busy;
            Version++;
        }
    }

    private void UpdatePendingRestore()
    {
        if (_pendingRestore is null || !_game.LoggedIn || _game.GPosing)
            return;

        // Only ever put the look back on the character it came from; another character drops it.
        if (_game.PlayerKey() != _pendingOwner)
        {
            _pendingRestore = null;
            return;
        }

        // Glamourer refusing (unloaded, locked): asked again once a second, not every frame, and given up after a while
        switch (WearRules.RestoreTurnOf(_pendingRefused, DateTime.UtcNow, _pendingDue))
        {
            case RestoreTurn.Wait:
                return;
            case RestoreTurn.GiveUp:
                _pendingRestore = null;
                Fail(Loc.T("Glamourer did not take back your look from before trying; you can put it back in Glamourer."));
                return;
        }

        if (!_pace.Take(IpcWork.Restore))
            return;
        FrameReport.Step("putting a look back");
        var r = _ipc.ApplyState(_pendingRestore, PlayerIndex, Ipc.FlagEquipment | Ipc.FlagCustomization | Ipc.FlagOnce);
        if (r is Ipc.GlamourerSuccess or Ipc.GlamourerNothingDone)
        {
            _pendingRestore = null;
            RedrawPlayer();
            return;
        }

        _pendingRefused++;
        _pendingDue = DateTime.UtcNow + WearRules.RestoreEvery;
    }

    /// <summary> Reads what shows on the player now (Penumbra's resource tree and Glamourer's items). </summary>
    // ---- IPeriodicReader: the reads, one call per frame (PeriodicReads decides when) ----

    bool IPeriodicReader.CanRead
        => _ipc.PenumbraAvailable && _game.LoggedIn;

    bool IPeriodicReader.TruthWanted
        => !_previewActive && _queue.Count == 0 && !RedrawBusy;

    private Dictionary<Socket, IReadOnlyList<string>>? _truthShowing;

    bool IPeriodicReader.ReadTruthFirst()
    {
        FrameReport.Step("reading what shows");
        if (_ipc.ResourceMap(PlayerIndex) is not { } map)
            return false;
        _truthShowing = Showing.Read(map, GamePaths.RootFull(_ipc.ModDirectory()));
        return true;
    }

    void IPeriodicReader.ReadTruthSecond()
    {
        FrameReport.Step("reading what shows");
        if (_truthShowing is not { } showing)
            return;
        _truthShowing = null;
        TakeTruth(showing, _ipc.GetStateJson(PlayerIndex));
    }

    /// <summary> Reads what shows on the player now, both calls in this frame (for a step, which needs it now). </summary>
    private void ReadTruth()
    {
        FrameReport.Step("reading what shows");
        if (_ipc.ResourceMap(PlayerIndex) is not { } map)
            return;
        TakeTruth(Showing.Read(map, GamePaths.RootFull(_ipc.ModDirectory())), _ipc.GetStateJson(PlayerIndex));
    }

    /// <summary> What shows on the player now (Penumbra's resource tree and Glamourer's state, read in two frames). </summary>
    private void TakeTruth(Dictionary<Socket, IReadOnlyList<string>> showing, JObject? state)
    {
        var items   = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in GearSlots.Worn)
            if (GlamourerState.ItemIn(state, slot.Name) is var id and not 0)
                items[slot.Name] = id;

        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in new[] { "Hairstyle", "Face", "TailShape" })
            if (state?["Customize"]?[key]?["Value"] is JValue { Type: JTokenType.Integer } v)
                numbers[key] = Convert.ToInt32(v.Value);

        // the character's colours and showings (a new object only when they changed)
        var (colours, shows) = GlamourerState.CharacterOf(state);
        if (state is not null && (_characterNow is null || !SameCharacter(_characterNow, colours, shows)))
        {
            _characterNow = new Looks.CharacterLook(colours, shows);
            Version++;
        }

        if (Showing.Same(showing, _kept) && items.Count == _keptItems.Count && items.All(kv => _keptItems.GetValueOrDefault(kv.Key) == kv.Value)
         && numbers.Count == _keptCustomize.Count && numbers.All(kv => _keptCustomize.GetValueOrDefault(kv.Key, -1) == kv.Value))
            return;
        _kept          = showing;
        _keptItems     = items;
        _keptCustomize = numbers;
        Version++;
    }

    private static bool SameCharacter(Looks.CharacterLook a, Dictionary<string, uint> colours, Dictionary<string, bool> showing)
        => a.Colours.Count == colours.Count && colours.All(kv => a.Colours.TryGetValue(kv.Key, out var c) && c == kv.Value)
        && a.Showing.Count == showing.Count && showing.All(kv => a.Showing.TryGetValue(kv.Key, out var s) && s == kv.Value);

    // ---- collection settings ----

    /// <summary> Permanent settings in the player's collection (mod directory → enabled, priority, options, inherited). </summary>
    public Dictionary<string, (bool Enabled, int Priority, Dictionary<string, List<string>> Options, bool Inherited)> Settings { get; private set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Re-reads the collection's mod settings now (throttled to every 3 s unless forced), both calls in this frame: for
    /// the steps, which need them fresh. The periodic read goes through <see cref="PeriodicReads"/>, one call per frame.
    /// </summary>
    public void RefreshSettings(bool force = false)
    {
        if (!force && _game.Clock() - _reads.SettingsReadAt < PeriodicReads.SettingsEvery)
            return;
        _reads.SettingsRead();
        if (ReadSettingsFirst())
            ReadSettingsSecond();
    }

    private Guid? _settingsCollection;

    bool IPeriodicReader.ReadSettingsFirst()
        => ReadSettingsFirst();

    void IPeriodicReader.ReadSettingsSecond()
        => ReadSettingsSecond();

    /// <summary> The collection, and the temporary settings other plugins hold in it. False: no collection. </summary>
    private bool ReadSettingsFirst()
    {
        FrameReport.Step("reading the collection's settings");
        _settingsCollection = _ipc.CollectionForObject(PlayerIndex);
        if (_settingsCollection is not { } collection)
            return false;

        // the temporary settings other plugins hold in the collection (a Glamourer design's mods, a sync plugin): they
        // go over the collection's own while they last, also in priority, so what the closet puts on must beat them too
        if (_ipc.AllSettings(collection, ignoreTemporary: false) is { } effective)
            OthersTemporary = effective.Where(kv => kv.Value.Item5 && !_previewMods.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)
                    && !_previewOff.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => (kv.Value.Item1, kv.Value.Item2), StringComparer.OrdinalIgnoreCase);
        return true;
    }

    /// <summary> The collection's own settings (the collection read by <see cref="ReadSettingsFirst"/>). </summary>
    private void ReadSettingsSecond()
    {
        FrameReport.Step("reading the collection's settings");
        if (_settingsCollection is not { } collection)
            return;
        if (_ipc.AllSettings(collection, ignoreTemporary: true) is { } all)
        {
            var fresh = all.ToDictionary(kv => kv.Key, kv => (kv.Value.Item1, kv.Value.Item2, kv.Value.Item3, kv.Value.Item4), StringComparer.OrdinalIgnoreCase);
            // Keep the same dictionary when nothing changed: everything that reads it redoes its work when it is a new one,
            // which made the game stutter every few seconds in the old plugin.
            if (!SameSettings(Settings, fresh))
            {
                Settings = fresh;
                Version++;
            }
        }
    }

    private static bool SameSettings(Dictionary<string, (bool Enabled, int Priority, Dictionary<string, List<string>> Options, bool Inherited)> a,
        Dictionary<string, (bool Enabled, int Priority, Dictionary<string, List<string>> Options, bool Inherited)> b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (var (key, x) in b)
        {
            if (!a.TryGetValue(key, out var y) || x.Enabled != y.Enabled || x.Priority != y.Priority || x.Inherited != y.Inherited
             || x.Options.Count != y.Options.Count)
                return false;
            foreach (var (group, chosen) in x.Options)
                if (!y.Options.TryGetValue(group, out var other) || !chosen.SequenceEqual(other))
                    return false;
        }

        return true;
    }

    public bool IsEnabled(string mod)
        => Settings.TryGetValue(mod, out var s) && s.Enabled;

    /// <summary> Highest priority among the other enabled mods. </summary>
    public int TopPriority(string? except = null, IReadOnlyCollection<string>? alsoExcept = null)
        => WearRules.TopPriority(Settings.Select(kv => (kv.Key, kv.Value.Enabled, kv.Value.Priority)),
            OthersTemporary.Select(kv => (kv.Key, kv.Value.Enabled, kv.Value.Priority)),
            m => except is not null && m.Equals(except, StringComparison.OrdinalIgnoreCase)
              || alsoExcept is not null && alsoExcept.Contains(m, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Temporary settings other plugins hold in the player's collection (mod directory → enabled, priority), not the
    /// closet's own try. While they last they are what counts for those mods.
    /// </summary>
    public Dictionary<string, (bool Enabled, int Priority)> OthersTemporary { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> Whether a mod is on and at which priority as Penumbra uses it now (another plugin's temporary setting first). </summary>
    public (bool Enabled, int Priority) Effective(string mod)
        => OthersTemporary.TryGetValue(mod, out var t) ? t : Settings.TryGetValue(mod, out var s) ? (s.Enabled, s.Priority) : (false, 0);

    // ---- what to put on ----

    /// <summary> The tried or kept parts as mods to put on (one per mod, its sockets together) and parts to take off. </summary>
    private (List<DressItem> Wear, List<TakeOffItem> Off) Resolve(IReadOnlyList<TryRequest> requests)
    {
        var wear   = new List<DressItem>();
        var off    = new List<TakeOffItem>();
        var player = _game.Player();
        var codes  = Races.CodesFor(player.RaceCode);
        var body   = _body();

        JObject? state = null;
        foreach (var group in requests.GroupBy(r => r.Key.Mod, StringComparer.OrdinalIgnoreCase))
        {
            var mod  = _mod(group.Key);
            var name = mod?.Name ?? group.Key;
            foreach (var r in group.Where(r => r.Nothing))
            {
                var part = _part(r.Key);
                var slots = part?.SlotIds ?? GearSlots.NamesOf(r.Key.Socket).Select(n => GearSlots.IdOf(n)!.Value).ToArray();
                // a ring taken off one hand: that finger only
                off.Add(new TakeOffItem(group.Key, name, r.Key.Socket, r.Key.Socket == Socket.Rings ? WearRules.FingerSlots(slots, r.Hand) : slots));
            }

            var tried = group.Where(r => !r.Nothing).ToList();
            if (tried.Count == 0)
                continue;

            var options = tried.FirstOrDefault(r => r.Options is not null)?.Options?.ToDictionary(kv => kv.Key, kv => kv.Value.ToList())
             ?? OptionPicker.Starting(mod?.Groups ?? Array.Empty<GroupInfo>(), SavedOptions(group.Key), codes, body);
            var picks     = new List<(byte, ulong)>();
            var customize = new Dictionary<string, int>();
            var sockets   = new List<Socket>();
            foreach (var r in tried)
            {
                sockets.Add(r.Key.Socket);
                if (_part(r.Key) is not { } part)
                    continue;
                if (part.Versions.Count > 0)
                {
                    var item = part.Versions[Math.Clamp(r.Version, 0, part.Versions.Count - 1)];
                    // a ring goes on the finger asked for (the other hand keeps what it wears)
                    foreach (var slotId in r.Key.Socket == Socket.Rings ? WearRules.FingerSlots(part.SlotIds, r.Hand) : part.SlotIds)
                        picks.Add((slotId, item.Id));
                }

                if (part.Customize.Count > 0)
                {
                    state ??= _ipc.GetStateJson(PlayerIndex);
                    var now = state;
                    foreach (var (key, value) in PartBuilder.PickCustomize(part, k => now?["Customize"]?[k]?["Value"] is JValue { Type: JTokenType.Integer } v ? Convert.ToInt32(v.Value) : null))
                        customize[key] = value;
                }
            }

            var under = tried.SelectMany(r => r.Under ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            wear.Add(new DressItem(group.Key, name, options, picks, customize, sockets, under.Count > 0 ? under : null));
        }

        return (wear, off);
    }

    // ---- trying on ----

    /// <summary> Temporarily puts on parts over your current look (port of the old PreviewMany). </summary>
    private void PreviewNow(IReadOnlyList<TryRequest> requests, Looks.CharacterLook? character)
    {
        Report("trying on", "taking off", requests);
        if (!Ready(out var why))
        {
            Fail(why, "trying on: not ready");
            return;
        }

        if (_savedLook is null || _savedOwner != _game.PlayerKey())
        {
            var code = _ipc.GetState(PlayerIndex, out var look);
            if (code != Ipc.GlamourerSuccess || string.IsNullOrEmpty(look))
            {
                Fail(code == Ipc.GlamourerInvalidKey ? Loc.T("Your Glamourer state is locked by another plugin.") : Loc.T("Glamourer could not read your look."),
                    $"trying on: Glamourer gave no look (code {code})");
                return;
            }

            RemovePreviewSettings();
            _savedLook  = look;
            _savedOwner = _game.PlayerKey();
        }
        else
        {
            // a new try replaces the last one: your own look first, then the new parts over it
            RemovePreviewSettings();
            _ipc.ApplyState(_savedLook, PlayerIndex, Ipc.FlagEquipment | Ipc.FlagCustomization | Ipc.FlagOnce);
        }

        // after your own look is back: a face or hair part keeps your number when the mod has it (see PickCustomize)
        var (wear, off) = Resolve(requests);

        if (_ipc.CollectionForObject(PlayerIndex) is not { } collection)
        {
            EndPreview();
            Fail(Loc.T("Penumbra did not answer. Try again in a moment."));
            return;
        }

        // Above every enabled mod, so the tried mods win all conflicts: the collection's priorities and the temporary ones
        // other plugins hold (a Glamourer design's body, say), read at most a few seconds ago
        RefreshSettings();
        var priority = WearRules.TryPriority(_settings.TemporaryPriority, TopPriority());
        _previewCollection = collection;
        _touched.Add(collection);
        var failed   = new List<string>();
        var problems = new List<string>();
        var notes    = new List<string>();
        foreach (var item in wear)
        {
            var settings = item.Options.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
            var r        = _ipc.SetTemporaryMod(PlayerIndex, item.Mod, priority++, settings);
            if (r is not (Ipc.PenumbraSuccess or Ipc.PenumbraNothingChanged))
            {
                failed.Add(item.Name);
                Said($"trying on: Penumbra refused a mod (code {r})");
                continue;
            }

            _previewMods.Add(item.Mod);
            var put = PutOn(item);
            if (put.Note.Length > 0)
                notes.Add(put.Note);
            if (put.Problem is { Length: > 0 } problem)
            {
                problems.Add(problem);
                Said(item.Customize.Count == 0 && item.Sockets.Any(GearSlots.IsCustomization)
                    ? "trying on: a hair, face or tail part with no number for this character"
                    : "trying on: Glamourer did not take all of it");
            }
        }

        // a slot something tried was just put in keeps it (a ring tried on the hand another ring is taken off from)
        var tried   = wear.Where(w => _previewMods.Contains(w.Mod)).SelectMany(w => w.Picks.Select(p => p.SlotId)).ToHashSet();
        var tookOff = 0;
        foreach (var item in off)
            if (TakeOffForPreview(item, priority++, tried))
                tookOff++;

        // a look's colours and showings, over everything it put on (Put back gives your look back, colours too)
        var characterProblem = PutOnCharacter(character);
        if (characterProblem.Length > 0)
            problems.Add(characterProblem);

        if (_previewMods.Count == 0 && tookOff == 0 && character is null)
        {
            EndPreview();
            Fail(failed.Count > 0 ? Loc.F("Penumbra could not put on {0}.", string.Join(", ", failed)) : Loc.T("That could not be tried on."),
                "trying on: nothing could be tried, the try ended");
            return;
        }

        _previewActive = true;
        RedrawPlayer();
        ScheduleSlotCheck(wear.Where(w => _previewMods.Contains(w.Mod)).ToList(), collection, preview: true);
        if (failed.Count > 0)
            Fail(Loc.F("Penumbra could not put on {0}.", string.Join(", ", failed)));
        else if (problems.Count > 0)
            Fail(problems[0]);
        else if (notes.Count > 0)
            Note(notes[0]);
    }

    /// <summary>
    /// Shows a part taken off while trying: gear by putting Glamourer's "nothing" item in its slots (the mod stays on for
    /// its other parts), anything else by switching the mod off for the moment.
    /// </summary>
    private bool TakeOffForPreview(TakeOffItem item, int priority, IReadOnlySet<byte> tried)
    {
        if (GearSlots.IsGear(item.Socket))
        {
            // only where the mod's own item shows (the kept outfit as read before the try): never another mod's ring on the
            // other hand
            var any = false;
            foreach (var slot in OwnSlots(item.Mod, item.Socket, item.SlotIds))
                if (GearSlots.ByName(slot) is { } gear && !tried.Contains(gear.Id))
                    any |= _ipc.SetItem(PlayerIndex, gear.Id, GearSlots.NothingItem(gear.Id)) is Ipc.GlamourerSuccess or Ipc.GlamourerNothingDone;
            return any;
        }

        // Penumbra switches a mod on or off as a whole: while another part of it is worn, it stays on (the closet does not
        // ask for this; a take-off from elsewhere is left out, and said once)
        if (_kept.Any(kv => kv.Key != item.Socket && kv.Value.Contains(item.Mod, StringComparer.OrdinalIgnoreCase)))
        {
            Said("trying on: a part of a mod with other parts worn was not taken off");
            return false;
        }

        if (_ipc.SetTemporaryModOff(PlayerIndex, item.Mod, priority) is not (Ipc.PenumbraSuccess or Ipc.PenumbraNothingChanged))
            return false;
        _previewOff.Add(item.Mod);
        return true;
    }

    /// <summary> Ends trying on: temporary settings off, your look back (later, if the character is not here now). </summary>
    private void EndPreview()
    {
        if (_previewMods.Count == 0 && _previewOff.Count == 0 && _savedLook is null && !_previewActive)
            return;
        FrameReport.Step("putting back", _previewMods.Count + _previewOff.Count);

        RemovePreviewSettings();
        foreach (var c in _touched)
            _ipc.RemoveAllTemporaryMods(c);
        _touched.Clear();

        if (_savedLook is not null)
        {
            // Only ever put the look back on the character it came from; if that character is not here, do it later.
            // New: when Glamourer does not take it now (reloading, locked), it is tried again later too, not dropped.
            var applied = _game.LoggedIn && _game.PlayerKey() == _savedOwner && !_game.GPosing
             && _ipc.ApplyState(_savedLook, PlayerIndex, Ipc.FlagEquipment | Ipc.FlagCustomization | Ipc.FlagOnce) is Ipc.GlamourerSuccess or Ipc.GlamourerNothingDone;
            if (applied)
            {
                RedrawPlayer();
            }
            else
            {
                _pendingRestore = _savedLook;
                _pendingOwner   = _savedOwner;
                _pendingRefused = 0;
                _pendingDue     = DateTime.UtcNow + WearRules.RestoreEvery;
            }
        }

        _savedLook     = null;
        _previewActive = false;
        _tryCheck      = null;
    }

    private void RemovePreviewSettings()
    {
        if (_previewCollection is { } c)
            foreach (var mod in _previewMods.Concat(_previewOff))
                _ipc.RemoveTemporaryMod(c, mod);
        _previewMods.Clear();
        _previewOff.Clear();
        _previewCollection = null;
    }

    // ---- keep ----

    /// <summary>
    /// Makes parts permanent and takes off the "nothing" ones, as one step in the history (the old WearMany and TakeOffMany
    /// in one, so a Keep of a new top and an empty hat is one undo).
    /// </summary>
    private void KeepNow(Guid step, string description, IReadOnlyList<TryRequest> requests, Looks.CharacterLook? character)
    {
        Report("keeping", "taking off for good", requests);
        if (!Ready(out var why))
        {
            _steps[step] = StepState.Failed;
            Fail(why);
            return;
        }

        var (wear, off) = Resolve(requests);

        // What shows before anything changes (the old TakeOffOne read it first too: afterwards the game's item is loaded
        // there). While trying, the kept outfit read before the try is the right answer already.
        if (!_previewActive)
            ReadTruth();

        // A gear part that does not show is nothing to take off (the closet asks for every other slot of a tried thing,
        // whatever it knew when it asked; only what shows now counts). Its mod is left as it is.
        off = off.Where(o => !GearSlots.IsGear(o.Socket) || Showing.In(_kept, o.Socket).Contains(o.Mod, StringComparer.OrdinalIgnoreCase)).ToList();

        // Ends trying first: its saved look would otherwise put these slots back afterwards.
        EndPreview();
        RefreshSettings(force: true);
        if (_ipc.CollectionForObject(PlayerIndex) is not { } collection)
        {
            _steps[step] = StepState.Failed;
            Fail(Loc.T("Penumbra did not answer. Try again in a moment."));
            return;
        }

        var owner = _game.PlayerKey();
        _ipc.GetState(PlayerIndex, out var lookBefore);
        // an upper layer whose priority may be raised over a kept body is journaled too (one with settings of its own only:
        // an inherited one is never written)
        var raised = wear.SelectMany(w => w.Under ?? Array.Empty<string>())
            .Where(m => Settings.TryGetValue(m, out var u) && u.Enabled && !u.Inherited && !OthersTemporary.ContainsKey(m));
        var dirs  = wear.Select(w => w.Mod).Concat(off.Select(o => o.Mod)).Concat(raised).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var entry = _journal.Begin(step, description, collection, owner, Journal.Snapshot(Settings, dirs), lookBefore, dirs);
        var problems = new List<string>();

        // Reverse order, so several things come off the way they went on.
        foreach (var group in off.GroupBy(o => o.Mod, StringComparer.OrdinalIgnoreCase).Reverse())
            if (TakeOffParts(group.Key, group.ToList(), collection, owner) is { Length: > 0 } problem)
                problems.Add(problem);
        if (off.Count > 0)
            RefreshSettings(force: true);

        var worn  = new List<DressItem>();
        var notes = new List<string>();
        for (var i = 0; i < wear.Count; ++i)
        {
            if (WearOne(wear[i], collection, owner, notes) is { Length: > 0 } problem)
                problems.Add(problem);
            else
                worn.Add(wear[i]);

            // The next mod must see this one's new priority.
            if (i + 1 < wear.Count)
                RefreshSettings(force: true);
        }

        // a look's colours and showings are kept with its parts, in the same step (the look before and after is journaled)
        if (PutOnCharacter(character) is { Length: > 0 } characterProblem)
            problems.Add(characterProblem);

        RedrawPlayer();
        ScheduleSlotCheck(worn, collection, preview: false);
        _saveConfig();
        RefreshSettings(force: true);
        _ipc.GetState(PlayerIndex, out var lookAfter);
        _journal.Commit(entry, Journal.Snapshot(Settings, dirs), lookAfter);
        _steps[step] = StepState.Done;
        Expect(worn, off);

        if (problems.Count > 0)
            Fail(string.Join(" ", problems));
        else if (notes.Count > 0)
            Note(notes[0]);
    }

    /// <summary>
    /// Shows what Keep did right away (the redraw takes a moment before Penumbra can tell): kept parts are on, parts taken
    /// off are not. The next reading of what is on replaces this.
    /// </summary>
    private void Expect(IReadOnlyList<DressItem> worn, IReadOnlyList<TakeOffItem> off)
    {
        var kept = _kept.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        foreach (var o in off)
            if (kept.TryGetValue(o.Socket, out var list))
                list.RemoveAll(m => m.Equals(o.Mod, StringComparison.OrdinalIgnoreCase));
        foreach (var w in worn)
            foreach (var socket in w.Sockets)
            {
                var list = kept.TryGetValue(socket, out var l) ? l : kept[socket] = new List<string>();
                list.RemoveAll(m => m.Equals(w.Mod, StringComparison.OrdinalIgnoreCase));
                if (GearSlots.IsGear(socket) || GearSlots.IsCustomization(socket))
                    list.Clear();
                list.Insert(0, w.Mod);
            }

        foreach (var w in worn)
        {
            foreach (var (slotId, itemId) in w.Picks)
                if (GearSlots.NameOf(slotId) is { } slot)
                    _keptItems[slot] = itemId;
            foreach (var (key, value) in w.Customize)
                _keptCustomize[key] = value;
        }
        _kept = kept.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
        Version++;
    }

    /// <returns> A sentence about what went wrong, "" when it went through. </returns>
    private string WearOne(DressItem item, Guid collection, string owner, List<string> notes)
    {
        // Record the mod's settings and what you wear in its slots before its first Keep (or again if the record is stale:
        // the mod was turned off elsewhere meanwhile, or it was kept on another character). A mod kept part by part keeps
        // its first record and adds what the new part covers to its backup.
        var slots = item.Picks.Select(p => GearSlots.NameOf(p.SlotId)).OfType<string>().Distinct().ToList();
        var current = _ipc.GetStateJson(PlayerIndex);
        var backup  = GlamourerState.Backup(current, slots, item.Customize.Keys);
        if (!_settings.Worn.TryGetValue(item.Mod, out var record) || !IsEnabled(item.Mod) || record.Owner != owner
         || record.Collection is { } recorded && recorded != collection)
        {
            var had = Settings.TryGetValue(item.Mod, out var before);
            record = new WornRecord
            {
                HadSetting   = had,
                WasInherited = had && before.Inherited,
                WasEnabled   = had && before.Enabled,
                OldPriority  = had ? before.Priority : 0,
                OldOptions   = had ? before.Options.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()) : new(),
                Backup       = backup?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty,
                Owner        = owner,
                Collection   = collection,
            };
        }
        else if (backup is not null)
        {
            try
            {
                record.Backup = (record.Backup.Length > 0 ? GlamourerState.Merge(JObject.Parse(record.Backup), backup) : backup)
                    .ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                _log.Warning($"Mod Closet 2: bad backup for {item.Name}: {ex.Message}");
            }
        }

        var r = _ipc.TrySetMod(collection, item.Mod, true);
        if (r is not (Ipc.PenumbraSuccess or Ipc.PenumbraNothingChanged))
            return Loc.F("Penumbra could not turn on {0}.", item.Name);

        var now      = Settings.TryGetValue(item.Mod, out var s) ? s.Priority : 0;
        var under    = item.Under ?? Array.Empty<string>();
        var top      = TopPriority(item.Mod, under);
        var smart    = PriorityAdvisor?.Invoke(item.Mod, item.Sockets, now, under);
        var priority = smart ?? (now > top ? now : top + 1);
        var raise    = new List<(string Mod, int Priority)>();
        if (under.Count > 0)
        {
            // a body kept under the skin that is on (makeup over a face's textures): the skin's settings are not written;
            // only its priority, and only when it has settings of its own and is not above the body already
            var floor = PriorityAdvisor?.Invoke(item.Mod, item.Sockets, int.MinValue, under) ?? top + 1;
            // one held by another plugin's temporary setting counts with that priority, and is left alone like an
            // inherited one: a priority written into the collection would not change what shows
            var over  = under.Where(m => Effective(m).Enabled)
                .Select(m => (m, Effective(m).Priority, OthersTemporary.ContainsKey(m) || Settings.TryGetValue(m, out var u) && u.Inherited)).ToList();
            (priority, raise, var held) = WearRules.UnderLayers(now, floor, over);
            if (held is not null)
            {
                notes.Add(OthersTemporary.ContainsKey(held)
                    ? Loc.F("{0} is kept just under {1}: another plugin holds the settings of {1} for now and they are left as they are, so another mod may still show over {0}.",
                        item.Name, NameOf(held))
                    : Loc.F("{0} is kept just under {1}: the settings of {1} come from another collection and are left as they are, so another mod may still show over {0}.",
                        item.Name, NameOf(held)));
                Said("keeping: a lower layer held under an inherited upper layer");
            }
        }

        if (priority != now)
            _ipc.TrySetPriority(collection, item.Mod, priority);
        foreach (var (mod, to) in raise)
            if (_ipc.TrySetPriority(collection, mod, to) is not (Ipc.PenumbraSuccess or Ipc.PenumbraNothingChanged))
                Said("keeping: Penumbra refused to raise an upper layer");

        foreach (var (group, chosen) in item.Options)
            _ipc.TrySetSettings(collection, item.Mod, group, chosen);

        var (items, customize, problem, note) = PutOn(item);
        if (note.Length > 0)
            notes.Add(note);
        foreach (var (slot, id) in items)
            record.PutOnItems[slot] = id;
        foreach (var (key, value) in customize)
            record.PutOnCustomize[key] = value;
        _settings.Worn[item.Mod] = record;
        return problem;
    }

    // ---- take off ----

    /// <summary>
    /// Takes off some parts of one mod. When another part of the mod still shows, the mod stays on and only these parts'
    /// slots are emptied (hair and the like go back to what was there before); otherwise the whole mod comes off as the old
    /// Take off did.
    /// </summary>
    private string TakeOffParts(string mod, IReadOnlyList<TakeOffItem> parts, Guid collection, string owner)
    {
        // Where it shows comes from the kept outfit as read before trying started (KeepNow reads it again when nothing
        // was being tried); reading now would see the try, whose redraw has not happened yet.
        var sockets = parts.Select(p => p.Socket).ToHashSet();
        var others  = _kept.Where(kv => !sockets.Contains(kv.Key) && kv.Value.Contains(mod, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
        var name    = parts[0].Name;
        if (others.Count == 0)
            return TakeOffOne(mod, name, parts, collection, owner);

        var problem = string.Empty;
        _settings.Worn.TryGetValue(mod, out var record);
        foreach (var part in parts)
        {
            if (GearSlots.IsGear(part.Socket))
            {
                var names = OwnSlots(mod, part.Socket, part.SlotIds);
                EmptySlots(names);
                if (record is not null)
                    foreach (var slot in names)
                        record.PutOnItems.Remove(slot);
            }
            else if (GearSlots.CustomizeKey(part.Socket) is { } key && record is { Backup.Length: > 0 } && record.PutOnCustomize.TryGetValue(key, out var value))
            {
                try
                {
                    var only = new Dictionary<string, int> { [key] = value };
                    if (GlamourerState.FilterBackup(JObject.Parse(record.Backup), _ipc.GetStateJson(PlayerIndex), new Dictionary<string, ulong>(), only) is { } filtered)
                        _ipc.ApplyState(filtered, PlayerIndex, Ipc.FlagCustomization | Ipc.FlagOnce);
                    record.PutOnCustomize.Remove(key);
                }
                catch (Exception ex)
                {
                    _log.Warning($"Mod Closet 2: bad backup for {name}: {ex.Message}");
                }
            }
            else
            {
                problem = Loc.F("{0} cannot come off without the rest of it.", name);
            }
        }

        return problem;
    }

    /// <summary> Takes off a whole mod: its settings as before the first Keep, its slots emptied (port of the old TakeOffOne). </summary>
    private string TakeOffOne(string mod, string name, IReadOnlyList<TakeOffItem> parts, Guid collection, string owner)
    {
        // the gear slots where its pieces show right now, and the ones asked for: only where its own item shows (a ring
        // finger that holds another mod's ring or a plain game ring keeps it)
        var showing = new List<string>();
        foreach (var (socket, mods) in _kept)
            if (GearSlots.IsGear(socket) && mods.Contains(mod, StringComparer.OrdinalIgnoreCase))
                showing.AddRange(OwnSlots(mod, socket, GearSlots.NamesOf(socket).Select(n => GearSlots.IdOf(n)!.Value)));
        foreach (var p in parts)
            showing.AddRange(OwnSlots(mod, p.Socket, p.SlotIds));

        if (!_settings.Worn.TryGetValue(mod, out var record) || record.Owner != owner
         || record.Collection is { } recorded && recorded != collection)
        {
            var d = _ipc.TrySetMod(collection, mod, false);
            if (d is not (Ipc.PenumbraSuccess or Ipc.PenumbraNothingChanged))
                return Loc.F("Penumbra could not take off {0}.", name);
            EmptySlots(showing);
            return string.Empty;
        }

        // 1. The mod's settings as they were before Keep.
        int code;
        if (!record.HadSetting || record.WasInherited)
        {
            code = _ipc.TryInherit(collection, mod, true);
        }
        else
        {
            code = _ipc.TrySetMod(collection, mod, record.WasEnabled);
            _ipc.TrySetPriority(collection, mod, record.OldPriority);
            foreach (var (group, options) in record.OldOptions)
                _ipc.TrySetSettings(collection, mod, group, options);
        }

        if (code is not (Ipc.PenumbraSuccess or Ipc.PenumbraNothingChanged))
            return Loc.F("Penumbra could not take off {0}.", name);

        // 2. The slots: empty, only where the slot still shows what Keep put on (you may have changed it since), and where
        //    the mod's model shows. Hair and the like go back to how they were.
        var current = _ipc.GetStateJson(PlayerIndex);
        var still   = record.PutOnItems
            .Where(kv => current?["Equipment"]?[kv.Key]?["ItemId"] is { Type: JTokenType.Integer } now && Convert.ToUInt64(((JValue)now).Value) == kv.Value)
            .Select(kv => kv.Key)
            .Concat(showing)
            .ToList();
        EmptySlots(still);

        if (record.Backup.Length > 0)
        {
            try
            {
                var filtered = GlamourerState.FilterBackup(JObject.Parse(record.Backup), _ipc.GetStateJson(PlayerIndex), new Dictionary<string, ulong>(), record.PutOnCustomize);
                if (filtered is not null)
                    _ipc.ApplyState(filtered, PlayerIndex, Ipc.FlagEquipment | Ipc.FlagCustomization | Ipc.FlagOnce);
            }
            catch (Exception ex)
            {
                _log.Warning($"Mod Closet 2: bad backup for {name}: {ex.Message}");
            }
        }

        _settings.Worn.Remove(mod);
        return string.Empty;
    }

    /// <summary>
    /// The slots of a part (by its Glamourer slot ids) where the mod's own item shows now, as read last (before a try, the
    /// kept outfit): the ones a take-off of the part empties (<see cref="WearRules.SlotsToEmpty"/>).
    /// </summary>
    private List<string> OwnSlots(string mod, Socket socket, IEnumerable<byte> slotIds)
    {
        var mods   = Showing.In(_kept, socket);
        var shows  = mods.Contains(mod, StringComparer.OrdinalIgnoreCase);
        var first  = mods.Count > 0 && mods[0].Equals(mod, StringComparison.OrdinalIgnoreCase);
        var part   = _part(new PartKey(mod, socket));
        var record = _settings.Worn.GetValueOrDefault(mod);
        var names  = slotIds.Where(id => id is not (GearSlots.MainHandId or GearSlots.OffHandId)).Select(GearSlots.NameOf).OfType<string>();
        return WearRules.SlotsToEmpty(names, shows, first, slot => _keptItems.GetValueOrDefault(slot),
            (slot, item) => record is not null && record.PutOnItems.TryGetValue(slot, out var put) && put == item
             || part is not null && (part.Items.Any(i => i.Id == item) || part.Versions.Any(i => i.Id == item)));
    }

    /// <summary> Empties gear slots, so nothing is worn there (weapons are never emptied). </summary>
    private void EmptySlots(IEnumerable<string> slots)
    {
        foreach (var slot in slots.Distinct(StringComparer.OrdinalIgnoreCase))
            if (GearSlots.ByName(slot) is { } gear)
                _ipc.SetItem(PlayerIndex, gear.Id, GearSlots.NothingItem(gear.Id));
    }

    // ---- options, versions, undo ----

    /// <summary> Changes a kept mod's options as one history step (the old ApplySettingsStep). </summary>
    private void SetOptionsNow(Guid step, string description, PartKey part, Dictionary<string, List<string>> options)
    {
        FrameReport.Step("changing options");
        if (!Ready(out var why) || _ipc.CollectionForObject(PlayerIndex) is not { } collection)
        {
            _steps[step] = StepState.Failed;
            Fail(why.Length > 0 ? why : Loc.T("Penumbra did not answer. Try again in a moment."));
            return;
        }

        RefreshSettings(force: true);
        var dirs  = new[] { part.Mod };
        var entry = _journal.Begin(step, description, collection, _game.PlayerKey(), Journal.Snapshot(Settings, dirs), null, dirs);
        var bad   = 0;
        foreach (var (group, chosen) in options)
            if (_ipc.TrySetSettings(collection, part.Mod, group, chosen) is not (Ipc.PenumbraSuccess or Ipc.PenumbraNothingChanged))
                bad++;
        RefreshSettings(force: true);
        _journal.Commit(entry, Journal.Snapshot(Settings, dirs), null);
        _steps[step] = StepState.Done;
        _optionWrites.Remove(step);
        // toggles clicked in a row while the last redraw settled are written one after another, and the character is
        // redrawn once, after the last (each redraw makes the next change wait for it)
        if (WearRules.RedrawAfterOptions(OptionWriteNext))
            RedrawPlayer();
        if (bad > 0)
            Fail(Loc.T("Penumbra could not change that option."));
    }

    /// <summary> Puts another version (an item of another model) of a kept gear part on, as one history step. </summary>
    private void SetVersionNow(Guid step, string description, PartKey key, int version)
    {
        FrameReport.Step("changing a version");
        if (!Ready(out var why) || _ipc.CollectionForObject(PlayerIndex) is not { } collection)
        {
            _steps[step] = StepState.Failed;
            Fail(why.Length > 0 ? why : Loc.T("Penumbra did not answer. Try again in a moment."));
            return;
        }

        if (_part(key) is not { Versions.Count: > 0 } part)
        {
            _steps[step] = StepState.Failed;
            return;
        }

        // A try's saved look would put the old item back when it ends.
        EndPreview();
        var item  = part.Versions[Math.Clamp(version, 0, part.Versions.Count - 1)];
        var owner = _game.PlayerKey();
        _ipc.GetState(PlayerIndex, out var lookBefore);
        var entry = _journal.Begin(step, description, collection, owner, new List<ModSettingState>(), lookBefore, new[] { key.Mod });
        _settings.Worn.TryGetValue(key.Mod, out var record);
        foreach (var slotId in part.SlotIds)
            if (_ipc.SetItem(PlayerIndex, slotId, item.Id) is Ipc.GlamourerSuccess or Ipc.GlamourerNothingDone && GearSlots.NameOf(slotId) is { } slot)
            {
                _keptItems[slot] = item.Id;
                if (record is not null)
                    record.PutOnItems[slot] = item.Id;
            }

        _saveConfig();
        _ipc.GetState(PlayerIndex, out var lookAfter);
        _journal.Commit(entry, new List<ModSettingState>(), lookAfter);
        _steps[step] = StepState.Done;
        _reads.TruthNext = _game.Clock() + 1.5;
    }

    /// <summary> Takes back a step when it is still the newest one (the old UndoRedo(undo: true)). </summary>
    private void UndoNow(Guid step)
    {
        FrameReport.Step("taking a step back");
        EndPreview();
        if (_journal.NextUndo is not { } entry || entry.Id != step)
        {
            Fail(Loc.T("That can no longer be taken back."));
            return;
        }

        if (!_game.LoggedIn)
        {
            Fail(_game.WhyNot());
            return;
        }

        var problems = _journal.Undo(entry);
        _steps.Remove(step);
        RefreshSettings(force: true);
        if (problems > 0)
            Fail(Loc.T("Not everything could be taken back."));
    }

    /// <summary> Called by the journal when it changed the character. </summary>
    public void RedrawFromJournal()
        => RedrawPlayer();

    // ---- helpers ----

    /// <summary> Puts a dress item's items and customize values on. Returns what went on and a sentence when something did not. </summary>
    private (Dictionary<string, ulong> Items, Dictionary<string, int> Customize, string Problem, string Note) PutOn(DressItem item)
    {
        var items = new Dictionary<string, ulong>();
        var bad   = 0;
        var note  = string.Empty;
        JObject? held = null;
        foreach (var (slotId, itemId) in item.Picks)
        {
            // a weapon only in a hand that holds its class now (the job's): else the mod is on, the hand keeps its own
            if (slotId is GearSlots.MainHandId or GearSlots.OffHandId && WeaponCategory is { } category)
            {
                held ??= _ipc.GetStateJson(PlayerIndex);
                var now  = GlamourerState.ItemIn(held, GearSlots.NameOf(slotId)!);
                var want = category(itemId);
                if (!WearRules.CanHold(want?.Id, now == 0 ? null : category(now)?.Id, slotId == GearSlots.OffHandId))
                {
                    note = Loc.F("Your job cannot hold a {0}; the mod is on for when you do.", want?.Name ?? item.Name);
                    Said("wearing: a weapon of another class than the job holds was left in the mod");
                    continue;
                }
            }

            if (_ipc.SetItem(PlayerIndex, slotId, itemId) is Ipc.GlamourerSuccess or Ipc.GlamourerNothingDone && GearSlots.NameOf(slotId) is { } slot)
                items[slot] = itemId;
            else
                bad++;
        }

        var customize = new Dictionary<string, int>();
        if (item.Customize.Count > 0)
        {
            var r = _ipc.ApplyState(GlamourerState.CustomizeState(item.Customize, _ipc.GetStateJson(PlayerIndex)), PlayerIndex, Ipc.FlagCustomization | Ipc.FlagOnce);
            if (r is Ipc.GlamourerSuccess or Ipc.GlamourerNothingDone)
                foreach (var (key, value) in item.Customize)
                    customize[key] = value;
            else
                bad++;
        }
        else if (item.Sockets.Any(GearSlots.IsCustomization))
        {
            // the old plugin said so too: a hair or face mod with no part for this race, clan and gender
            return (items, customize, Loc.F("{0} has nothing for your character.", item.Name), note);
        }

        return (items, customize, bad > 0 ? Loc.F("Glamourer could not put on all of {0}.", item.Name) : string.Empty, note);
    }

    private bool Ready(out string why)
    {
        why = _game.WhyNot();
        if (why.Length == 0 && !_ipc.PenumbraAvailable)
            why = Loc.T("Penumbra is not running.");
        else if (why.Length == 0 && !_ipc.GlamourerAvailable)
            why = Loc.T("Glamourer is not running.");
        return why.Length == 0;
    }

    /// <summary> A sentence for the status line that is no error (what a Keep could not do the usual way). </summary>
    private void Note(string message)
    {
        Message = new WardrobeMessage(message, false, Message.Serial + 1, DateTime.UtcNow);
        Version++;
    }

    private void Fail(string message, string? kind = null)
    {
        Message = new WardrobeMessage(message, true, Message.Serial + 1, DateTime.UtcNow);
        Version++;
        if (kind is not null)
            Said(kind);
    }

    private readonly HashSet<string> _said = new(StringComparer.Ordinal);

    /// <summary>
    /// A failure into the log file, once a session per kind, in fixed words and Penumbra's or Glamourer's code: never a mod
    /// name (the status line has the whole sentence for the user).
    /// </summary>
    private void Said(string kind)
    {
        if (_said.Add(kind))
            _log.Information($"Mod Closet 2: wardrobe: {kind}");
    }

    /// <summary> Moves remembered data when Penumbra renames a mod directory. </summary>
    public void OnModMoved(string oldDirectory, string newDirectory)
    {
        if (_settings.Worn.Remove(oldDirectory, out var record))
            _settings.Worn[newDirectory] = record;
        _journal.OnModMoved(oldDirectory, newDirectory);
        _saveConfig();
    }

    /// <summary> Puts everything back at once (unloading: no waiting for redraws, the plugin is going away). </summary>
    public void Dispose()
    {
        _ipc.Redrawn -= OnRedrawn;
        _queue.Clear();
        try
        {
            EndPreview();
        }
        catch (Exception ex)
        {
            _log.Warning($"Mod Closet 2: could not put everything back on unload: {ex.Message}");
        }
    }
}
