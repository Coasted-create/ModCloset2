using System.Text.Json;
using System.Text.Json.Serialization;
using ModCloset2.Core;

namespace ModCloset2.Looks;

/// <summary>
/// The looks: the outfits the closet remembers on its own (Lately), the ones the user pinned, which of them is on, and
/// how to wear one. A look is the outfit, kept: every part that was on, one per socket (the game's truth: a top in the
/// body slot, a jacket in the wrist slot and a corset in the ring slot are three pieces, all on Tops), with options and
/// version, and the gear sockets that were empty. A piece's shelf only says how it is shown and whether it is outfit or
/// body (body pieces are worn only with "with body"). Wearing goes through the session's shared try-on, so Keep and Put
/// back work as for anything else. See docs/LOOKS.md and docs/reports/looks.md.
/// </summary>
/// <remarks>
/// Threading: everything on the framework / draw thread. <see cref="Tick"/> every frame (it only compares a few numbers
/// unless something changed); the lists are rebuilt when something they show changed, never every frame. No Dalamud
/// types: the game comes in through the seams (Seams.cs).
/// </remarks>
public sealed class LooksEngine : ILooks
{
    private readonly ILooksOutfit _outfit;
    private readonly ILooksLibrary _library;
    private readonly ILooksColours? _colours;
    private readonly ILooksStore _store;
    private readonly ILooksUndo _undo;
    private readonly ILooksClock _clock;
    private readonly IDetector? _detector;
    private readonly Action<string>? _log;

    /// <summary> Every look, pinned and lately, in no particular order (the lists are ordered when they are built). </summary>
    private readonly List<LookData> _looks = new();
    private readonly Dictionary<Guid, LookData> _byId = new();
    private bool _importedOld;

    /// <summary> Pinned looks the user removed, kept (not shown) for <see cref="LookRules.RemovedKeep"/>. </summary>
    private readonly List<LookData> _removed = new();
    private DateTime _nextPrune;

    /// <summary>
    /// The looks file has been read (or there is none). Until then nothing is saved and nothing is imported: a failed
    /// read must never lead to the file being written over. Read again every <see cref="LookRules.LoadRetry"/>.
    /// </summary>
    private bool _loaded;
    private DateTime _nextLoad;
    private bool _loadFailedSaid;

    /// <summary> Replacement cards per look and shelf, for one state of the library, the looks and the outfit. </summary>
    private readonly Dictionary<(Guid Look, string Shelf), IReadOnlyList<Card>> _replacements = new();
    private (int Library, int Revision, int Outfit) _replacementsFor = (-1, -1, -1);

    /// <summary> The pinned look the outfit is, or started from. </summary>
    private Guid? _base;

    // what was seen of the world last time
    private bool _started;
    private int _outfitSeen = int.MinValue, _stepsSeen, _keptSerial, _librarySeen = int.MinValue, _modsSeen = int.MinValue, _coloursSeen = int.MinValue;
    private Guid? _tryingSeen;
    private OutfitNow _now = OutfitNow.Empty;

    /// <summary> Since when the kept outfit has been changed by the user without being remembered yet. </summary>
    private DateTime? _unsettledSince;

    /// <summary> Until when the look that was just kept (or the one from the last session) stays the outfit's look whatever is read. </summary>
    private DateTime _graceUntil;

    /// <summary> The outfit or the looks changed: which look the outfit is (or started from) is worked out again on the next poll. </summary>
    private bool _baseDirty = true;

    /// <summary> Once the grace ends, the outfit's look is checked again (it may have drifted meanwhile). </summary>
    private bool _graceCheck = true;

    private bool _viewDirty = true;

    /// <summary> Goes up with every change of the looks themselves (what the cards' fixed parts depend on). </summary>
    private int _revision;
    private bool _saveDirty;
    private DateTime _saveDue;

    // the view
    private int _version;
    private IReadOnlyList<LookCard> _pinned = Array.Empty<LookCard>();
    private IReadOnlyList<LookCard> _lately = Array.Empty<LookCard>();
    private WearingNow _wearing = new();

    /// <summary> Colour keys of colour snapshots (so a snapshot is read once). </summary>
    private readonly Dictionary<string, string> _keys = new(StringComparer.Ordinal);

    /// <summary> Colour keys of the colours now, by the parts asked about; emptied when colours or the outfit change. </summary>
    private readonly Dictionary<string, string?> _keysNow = new(StringComparer.Ordinal);

    /// <summary> How the comparisons see the library (option groups, shelves now); made once, passed for every look on every change. </summary>
    private readonly Lens _lens;

    private Healing.Index? _healIndex;
    private bool _fullHealDue;
    private DateTime _nextFullHeal = DateTime.MinValue;
    private readonly Dictionary<string, string[]> _words = new(StringComparer.Ordinal);
    private int _healIndexFor = int.MinValue;

    public LooksEngine(ILooksOutfit outfit, ILooksLibrary library, ILooksColours? colours, ILooksStore store, ILooksUndo undo, ILooksClock? clock = null,
        IDetector? detector = null, Action<string>? log = null)
    {
        _outfit   = outfit;
        _library  = library;
        _colours  = colours;
        _store    = store;
        _undo     = undo;
        _clock    = clock ?? new SystemLooksClock();
        _detector = detector;
        _log      = log;
        _lens     = new Lens(GroupsOf, key => _library.Part(key)?.Shelf);
        Load();
        // the look the outfit started from is kept over a restart; what is on may not be read yet at first
        _graceUntil = _clock.UtcNow + LookRules.KeepGrace;
    }

    // ======================================================================= ILooks: what the faces read

    public int Version
    {
        get
        {
            Refresh();
            return _version;
        }
    }

    public IReadOnlyList<LookCard> Pinned
    {
        get
        {
            Refresh();
            return _pinned;
        }
    }

    public IReadOnlyList<LookCard> Lately
    {
        get
        {
            Refresh();
            return _lately;
        }
    }

    public WearingNow Now
    {
        get
        {
            Refresh();
            return _wearing;
        }
    }

    // ======================================================================= for the plugin

    /// <summary> Every frame: follows the outfit and the library, remembers a settled outfit, saves. Cheap when nothing changed. </summary>
    public void Tick()
    {
        try
        {
            Poll();
            var now = _clock.UtcNow;
            if (_unsettledSince is { } since && !_outfit.Trying && now - since >= LookRules.QuietTime)
                Settle(now);
            if (_removed.Count > 0 && now >= _nextPrune)
                Prune(now);
            if (_saveDirty && _loaded && now >= _saveDue)
                Save();
        }
        catch (Exception ex)
        {
            // never break the frame: one line in the log, and carry on next frame
            _log?.Invoke($"Mod Closet 2 looks: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary> The closet window was closed (or the plugin unloads): an outfit changed since the last one is remembered now. </summary>
    public void ClosetClosed()
    {
        Poll();
        if (_unsettledSince is not null)
            Settle(_clock.UtcNow);
        Flush();
    }

    /// <summary> Penumbra moved (renamed) a mod's folder: every piece of it follows at once. </summary>
    public void ModMoved(string oldDirectory, string newDirectory)
    {
        if (oldDirectory.Length == 0 || newDirectory.Length == 0 || oldDirectory.Equals(newDirectory, StringComparison.Ordinal))
            return;
        var changed = false;
        foreach (var piece in _looks.SelectMany(l => l.Pieces))
            if (piece.Mod.Equals(oldDirectory, StringComparison.OrdinalIgnoreCase))
            {
                piece.Mod = newDirectory;
                changed   = true;
            }

        if (changed)
            Changed();
    }

    /// <summary> Puts the looks an undo step touched back as they were (the session calls it with the step's "before"). </summary>
    public void Restore(string snapshot)
    {
        UndoSnapshot? snap;
        try
        {
            snap = JsonSerializer.Deserialize<UndoSnapshot>(snapshot);
        }
        catch (JsonException)
        {
            return;
        }

        if (snap is null)
            return;
        foreach (var entry in snap.Entries)
        {
            if (_byId.TryGetValue(entry.Id, out var current))
                RemoveLook(current);
            if (entry.Look is { } look)
            {
                // a removal taken back: it is in use again, not kept aside
                _removed.RemoveAll(r => r.Id == look.Id);
                var back = look.Copy();
                back.RemovedUtc = null;
                AddLook(back);
            }
        }

        _base = snap.Base is { } b && _byId.ContainsKey(b) ? b : null;
        Changed();
    }

    /// <summary> Writes what changed now (the plugin calls it on unload). Never before the looks file could be read. </summary>
    public void Flush()
    {
        if (_saveDirty && _loaded)
            Save();
    }

    // ======================================================================= ILooks: commands

    public void Try(Guid look)
    {
        if (Find(look) is { } l)
            TryColourway(look, l.Colourway);
    }

    public void TryColourway(Guid look, int colourway)
    {
        Poll();
        if (Find(look) is not { } l)
            return;
        var parts   = new List<LookTryPart>();
        var off     = new List<PartKey>();
        var missing = 0;
        foreach (var piece in Matching.Worn(l, _lens))
        {
            if (Gone(piece))
            {
                ++missing;
                continue;
            }

            if (_now.ByKey.TryGetValue(piece.Key, out var worn) && Matching.SameWear(piece, worn, _lens))
                continue; // on already as the look has it
            parts.Add(new LookTryPart(piece.Key, piece.Options.Count == 0 ? null : piece.Options, piece.Version));
        }

        // sockets the look knows as empty: what is on there comes off (a body piece stays, unless the look is with body)
        foreach (var socket in l.Empty)
            if (LookRules.CanTakeOff(socket) && _now.In(socket) is { } worn && (l.WithBody || !LookRules.IsBody(worn.Shelf)))
                off.Add(worn.Key);

        string? colours = null;
        var way = colourway;
        if (_colours is not null && colourway >= 0 && colourway < l.Colourways.Count)
        {
            colours = l.Colourways[colourway].Colours;
            if (KeyNow(WornKeys(l)) is { } now && now == KeyOf(colours))
                colours = null; // already in these colours
        }
        else
        {
            way = Math.Clamp(l.Colourway, 0, Math.Max(0, l.Colourways.Count - 1));
        }

        if (parts.Count == 0 && off.Count == 0 && colours is null)
            return; // it is on already, exactly
        _outfit.TryLook(new LookTry(l.Id, way, NameOf(l), LookTexts.TryNote(Matching.Count(l, _lens), missing, l.WithBody), parts, off, colours));
    }

    public void Pin(Guid look)
    {
        Poll();
        if (Find(look) is not { Pinned: false } l)
            return;
        var name = NameOf(l);
        if (_looks.FirstOrDefault(p => p.Pinned && Matching.SamePieces(p, l, _lens)) is { } existing)
        {
            // the same pieces are a look already: these colours become one of its colourways
            var before = Snapshot(l.Id, existing.Id);
            foreach (var way in l.Colourways)
                existing.Colourway = AddColourway(existing, way);
            existing.Wears       = Weight(existing, l.LastWornUtc) + Weight(l, l.LastWornUtc);
            existing.LastWornUtc = l.LastWornUtc > existing.LastWornUtc ? l.LastWornUtc : existing.LastWornUtc;
            RemoveLook(l);
            Step(Loc.F("Pin {0}", name), before, Snapshot(l.Id, existing.Id));
            return;
        }

        var start = Snapshot(l.Id);
        l.Pinned     = true;
        l.CreatedUtc = _clock.UtcNow;
        Step(Loc.F("Pin {0}", name), start, Snapshot(l.Id));
    }

    public Guid PinNow()
    {
        Poll();
        if (!_now.AnyClothes)
            return Guid.Empty;
        var now     = _clock.UtcNow;
        var made    = Capture(_now, now);
        var colours = ColourwayNow(made);

        // the lately entry of this very outfit is pinned with it (it is not listed twice)
        var lately = _looks.Where(l => !l.Pinned && Matching.Same(l, _now, _lens) && SameColours(l, colours)).Select(l => l.Id).ToList();

        if (_looks.FirstOrDefault(p => p.Pinned && Matching.Same(p, _now, _lens)) is { } existing)
        {
            var known = colours is null || existing.Colourways.Any(c => KeyOf(c.Colours) == KeyOf(colours.Colours));
            if (known && lately.Count == 0)
            {
                // exactly a look already: nothing to pin
                if (colours is not null)
                    existing.Colourway = IndexOf(existing, colours);
                _base = existing.Id;
                Changed();
                return existing.Id;
            }

            var before = Snapshot(lately.Append(existing.Id));
            if (colours is not null)
                existing.Colourway = AddColourway(existing, colours);
            foreach (var id in lately)
                RemoveLook(_byId[id]);
            _base = existing.Id;
            Step(Loc.F("Pin {0}", NameOf(existing)), before, Snapshot(lately.Append(existing.Id)));
            return existing.Id;
        }

        var start = Snapshot(lately.Append(made.Id));
        made.Pinned      = true;
        made.CreatedUtc  = now;
        made.LastWornUtc = now;
        made.Wears       = 1 + lately.Sum(id => Weight(_byId[id], now));
        if (colours is not null)
            made.Colourways.Add(colours);
        foreach (var id in lately)
            RemoveLook(_byId[id]);
        AddLook(made);
        _base = made.Id;
        Step(Loc.F("Pin {0}", NameOf(made)), start, Snapshot(lately.Append(made.Id)));
        return made.Id;
    }

    public void UpdateFromNow(Guid look)
    {
        Poll();
        if (Find(look) is not { } l || !_now.AnyClothes)
            return;
        var before = Snapshot(l.Id);
        var made   = Capture(_now, _clock.UtcNow);
        l.Pieces = made.Pieces;
        l.Empty  = made.Empty;
        if (ColourwayNow(l) is { } colours)
        {
            if (l.Colourways.Count == 0)
            {
                l.Colourways.Add(colours);
                l.Colourway = 0;
            }
            else
            {
                // the colourway that was worn takes the colours now; another one with the very same colours goes
                var at = Math.Clamp(l.Colourway, 0, l.Colourways.Count - 1);
                l.Colourways[at] = colours;
                for (var i = l.Colourways.Count - 1; i >= 0; --i)
                    if (i != at && KeyOf(l.Colourways[i].Colours) == KeyOf(colours.Colours))
                    {
                        l.Colourways.RemoveAt(i);
                        if (i < at)
                            --at;
                    }

                l.Colourway = at;
            }
        }

        if (l.Pinned)
            _base = l.Id;
        Step(Loc.F("Update {0}", NameOf(l)), before, Snapshot(l.Id));
    }

    public void Rename(Guid look, string name)
    {
        if (Find(look) is not { } l)
            return;
        name = (name ?? string.Empty).Trim();
        // the name it would make itself is no name of its own: it keeps following the main piece
        if (name == AutoName(l))
            name = string.Empty;
        if (name == l.Name)
            return;
        var before = Snapshot(l.Id);
        var old    = NameOf(l);
        l.Name = name;
        Step(Loc.F("Rename {0}", old), before, Snapshot(l.Id));
    }

    public void Remove(Guid look)
    {
        if (Find(look) is not { } l)
            return;
        var before = Snapshot(l.Id);
        var text   = l.Pinned ? Loc.F("Remove {0}", NameOf(l)) : Loc.T("Forget an outfit");
        if (l.Pinned)
        {
            // kept (not shown) for a while: the undo history does not survive a restart
            var kept = l.Copy();
            kept.RemovedUtc = _clock.UtcNow;
            _removed.RemoveAll(r => r.Id == l.Id);
            _removed.Add(kept);
        }

        RemoveLook(l);
        Step(text, before, Snapshot(l.Id));
    }

    public void SetWithBody(Guid look, bool withBody)
    {
        Poll();
        if (Find(look) is not { } l || l.WithBody == withBody)
            return;
        var before = Snapshot(l.Id);
        l.WithBody = withBody;
        // switched on while it is the outfit's look, or never seen with a body: the hair and body that are on now (in the
        // sockets its outfit pieces leave free)
        if (withBody && (_base == l.Id || !l.Pieces.Any(_lens.IsBody)))
        {
            l.Pieces.RemoveAll(_lens.IsBody);
            foreach (var piece in Capture(_now, _clock.UtcNow).Pieces.Where(_lens.IsBody))
                if (l.Pieces.All(p => p.Socket != piece.Socket))
                {
                    l.Pieces.Add(piece);
                    l.Empty.Remove(piece.Socket);
                }
        }
        Step(withBody ? Loc.F("Let {0} change hair and body", NameOf(l)) : Loc.F("Let {0} leave hair and body alone", NameOf(l)), before, Snapshot(l.Id));
    }

    public void RemoveColourway(Guid look, int colourway)
    {
        if (Find(look) is not { } l || l.Colourways.Count < 2 || colourway < 0 || colourway >= l.Colourways.Count)
            return;
        var before = Snapshot(l.Id);
        l.Colourways.RemoveAt(colourway);
        if (l.Colourway > colourway || l.Colourway >= l.Colourways.Count)
            l.Colourway = Math.Max(0, l.Colourway - 1);
        Step(Loc.F("Remove a colourway of {0}", NameOf(l)), before, Snapshot(l.Id));
    }

    /// <summary>
    /// Kept per look and shelf while the library, the looks and the outfit stay as they are: the window asks for every
    /// missing piece every frame while its panel is open, and a shelf can have hundreds of parts.
    /// </summary>
    public IReadOnlyList<Card> ReplacementsFor(Guid look, string piece)
    {
        Poll();
        var state = (_librarySeen, _revision, _outfitSeen);
        if (_replacementsFor != state)
        {
            _replacements.Clear();
            _replacementsFor = state;
        }

        if (_replacements.TryGetValue((look, piece), out var cards))
            return cards;
        if (Find(look) is not { } l || PieceOn(l, piece, null) is not { } p)
        {
            cards = Array.Empty<Card>();
        }
        else
        {
            var taken = Matching.Keys(l);
            var shelf = _library.PartsOn(piece).Where(c => !taken.Contains(c.Key)).ToList();
            cards = Healing.Replacements(p, shelf, _detector, LookRules.Replacements).Select(MakeCard).ToList();
        }

        if (_replacements.Count > 64)
            _replacements.Clear();
        _replacements[(look, piece)] = cards;
        return cards;
    }

    public void Replace(Guid look, string piece, PartKey with)
    {
        Poll();
        if (Find(look) is not { } l || _library.Part(with) is not { } part || !LookRules.Worn(with.Socket))
            return;
        var before = Snapshot(l.Id);
        var old    = PieceOn(l, piece, with.Socket);
        if (old is not null)
            l.Pieces.Remove(old);
        // at most one piece per socket: what the look held in the new part's socket makes room
        l.Pieces.RemoveAll(p => p.Socket == with.Socket);
        l.Empty.Remove(with.Socket);
        l.Pieces.Add(new LookPiece { Mod = with.Mod, Socket = with.Socket, Shelf = part.Shelf, Version = -1, Print = PrintOf(part) });
        Step(Loc.F("Replace a piece of {0}", NameOf(l)), before, Snapshot(l.Id));
    }

    /// <summary>
    /// The look's piece a face means by a shelf: a missing one first (the one in <paramref name="socket"/> when there are
    /// several), else one that is there (again the one in that socket first); null when the look has nothing on that shelf.
    /// </summary>
    private LookPiece? PieceOn(LookData look, string shelf, Socket? socket)
    {
        var onShelf = look.Pieces.Where(p => _lens.ShelfOf(p) == shelf).OrderBy(p => p.Socket).ToList();
        var missing = onShelf.Where(Gone).ToList();
        var pool    = missing.Count > 0 ? missing : onShelf;
        return pool.FirstOrDefault(p => p.Socket == socket) ?? pool.FirstOrDefault();
    }

    // ======================================================================= following the world

    /// <summary> Picks up what changed in the outfit, the library and the colours (a few number comparisons when nothing did). </summary>
    private void Poll()
    {
        var now = _clock.UtcNow;
        if (!_loaded && now >= _nextLoad)
            Load();
        if (!_started)
        {
            // what the user did before the looks were there does not count as a change
            _started    = true;
            _stepsSeen  = _outfit.Steps;
            _keptSerial = _outfit.LastKept?.Serial ?? 0;
        }

        var libraryChanged = _library.Version != _librarySeen;
        _librarySeen = _library.Version;
        if (_library.Ready && _library.ModsVersion != _modsSeen)
        {
            _modsSeen      = _library.ModsVersion;
            _fullHealDue   = true;
            libraryChanged = true;
            ImportOld();
        }

        if (_fullHealDue && _library.Ready && now >= _nextFullHeal)
        {
            // mods came, went or moved: look for missing pieces everywhere
            _fullHealDue  = false;
            _nextFullHeal = now + LookRules.HealPause;
            Heal(full: true);
        }
        else if (libraryChanged && _library.Ready)
        {
            // detection moved parts between sockets or shelves: only the quick look inside the same mod
            Heal(full: false);
        }

        if (libraryChanged)
        {
            _keysNow.Clear();
            _viewDirty = true;
        }

        if (_outfit.Version != _outfitSeen)
        {
            _outfitSeen = _outfit.Version;
            _now        = new OutfitNow(_outfit.Kept);
            _keysNow.Clear();
            _viewDirty = true;
            _baseDirty = true;
        }

        if (_outfit.Steps != _stepsSeen)
        {
            _stepsSeen      = _outfit.Steps;
            _unsettledSince = now;
            _viewDirty      = true;
        }

        if (_outfit.LastKept is { } kept && kept.Serial != _keptSerial)
        {
            _keptSerial = kept.Serial;
            if (Find(kept.Look) is { } l)
            {
                // the look that was just kept is the outfit's look, even before what is on was read again
                if (l.Colourways.Count > 0)
                    l.Colourway = Math.Clamp(kept.Colourway, 0, l.Colourways.Count - 1);
                if (l.Pinned)
                {
                    _base       = l.Id;
                    _graceUntil = now + LookRules.KeepGrace;
                    _graceCheck = true;
                }

                Changed();
            }
        }

        if (_outfit.TryingLook != _tryingSeen)
        {
            _tryingSeen = _outfit.TryingLook;
            _viewDirty  = true;
        }

        if (_colours is not null && _colours.Version != _coloursSeen)
        {
            _coloursSeen = _colours.Version;
            _keysNow.Clear();
            _viewDirty = true;
        }

        if (_graceCheck && now >= _graceUntil)
        {
            _graceCheck = false;
            _baseDirty  = true;
        }

        FollowBase(now);
    }

    /// <summary>
    /// Works out which look the outfit is after a change: on every poll and at the end of every command, not only when a
    /// face reads (a pin and a change of shoes before the next read must still say "you changed the shoes").
    /// </summary>
    private void FollowBase(DateTime now)
    {
        if (!_baseDirty)
            return;
        _baseDirty = false;
        var before = _base;
        UpdateBase(now);
        if (_base != before)
        {
            _viewDirty = true;
            _saveDirty = true;
        }
    }

    /// <summary> Brings the lists up to date for a face. Never throws (faces read in Draw): on a failure the last lists stay. </summary>
    private void Refresh()
    {
        try
        {
            Poll();
            // "Today" becomes "Yesterday" at midnight
            if (_viewDirty || _fixedFor.Day != _clock.ToLocal(_clock.UtcNow).Date)
                Rebuild();
        }
        catch (Exception ex)
        {
            _viewDirty = false;
            if (_lastError != ex.Message)
                _log?.Invoke($"Mod Closet 2 looks: {ex.GetType().Name}: {ex.Message}");
            _lastError = ex.Message;
        }
    }

    private string _lastError = string.Empty;

    /// <summary> The outfit has settled: remember it (or count a wear of the look or remembered outfit it is). </summary>
    private void Settle(DateTime now)
    {
        _unsettledSince = null;
        if (!_now.AnyClothes)
            return;
        var made    = Capture(_now, now);
        var colours = ColourwayNow(made);

        // exactly a pinned look: a wear of it (in the colourway it is in; colours it never had make a new outfit)
        foreach (var look in _looks.Where(l => l.Pinned && Matching.Same(l, _now, _lens)).OrderByDescending(l => l.WithBody))
        {
            if (colours is not null && look.Colourways.Count > 0 && !look.Colourways.Any(c => KeyOf(c.Colours) == KeyOf(colours.Colours)))
                continue;
            if (colours is not null)
                look.Colourway = AddColourway(look, colours);
            Wear(look, now);
            _base = look.Id;
            Changed();
            return;
        }

        // the same outfit again: it moves to the front
        if (_looks.FirstOrDefault(l => !l.Pinned && Matching.Same(l, _now, _lens) && SameColours(l, colours)) is { } again)
        {
            // the same outfit: its pieces are the same, the body is the one that is on now
            again.Pieces = made.Pieces;
            again.Empty  = made.Empty;
            Wear(again, now);
            Changed();
            return;
        }

        made.CreatedUtc  = now;
        made.LastWornUtc = now;
        made.Wears       = 1;
        if (colours is not null)
            made.Colourways.Add(colours);
        AddLook(made);
        foreach (var old in _looks.Where(l => !l.Pinned).OrderByDescending(l => l.LastWornUtc).Skip(LookRules.LatelyMax).ToList())
            RemoveLook(old);
        Changed();
    }

    private static void Wear(LookData look, DateTime now)
    {
        look.Wears       = Weight(look, now) + 1;
        look.LastWornUtc = now;
    }

    /// <summary> How much a look's wears weigh now (each halves after <see cref="LookRules.WearHalfLifeDays"/> days). </summary>
    private static double Weight(LookData look, DateTime now)
    {
        if (look.Wears <= 0)
            return 0;
        var days = Math.Max(0, (now - look.LastWornUtc).TotalDays);
        return look.Wears * Math.Pow(0.5, days / LookRules.WearHalfLifeDays);
    }

    /// <summary>
    /// The outfit as a look (not pinned, not added): every kept part that is on, one per socket (outfit and body), and the
    /// gear sockets with nothing on.
    /// </summary>
    private LookData Capture(OutfitNow outfit, DateTime now)
    {
        var look = new LookData { CreatedUtc = now, LastWornUtc = now };
        foreach (var worn in outfit.All)
            look.Pieces.Add(PieceOf(worn));
        foreach (var socket in LookRules.GearSockets)
            if (outfit.In(socket) is null)
                look.Empty.Add(socket);
        return look;
    }

    private LookPiece PieceOf(WornPart worn)
    {
        var part   = _library.Part(worn.Key);
        var groups = part?.Groups ?? Array.Empty<string>();
        return new LookPiece
        {
            Mod     = worn.Key.Mod,
            Socket  = worn.Key.Socket,
            Shelf   = worn.Shelf.Length > 0 ? worn.Shelf : part?.Shelf ?? string.Empty,
            Options = worn.Options.Where(kv => groups.Contains(kv.Key, StringComparer.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
            Version = worn.Version,
            Print   = part is null ? null : PrintOf(part),
        };
    }

    private ModPrint PrintOf(LookLibraryPart part)
    {
        IReadOnlyList<string> files;
        try
        {
            files = _library.FilesOf(part.Key);
        }
        catch
        {
            files = Array.Empty<string>();
        }

        return new ModPrint
        {
            Name      = part.Name,
            Author    = part.Author,
            Files     = Healing.PrintFiles(files),
            Signature = part.Info.Signature.Select(f => MathF.Round(f, 3)).ToArray(),
        };
    }

    private IReadOnlyList<string>? GroupsOf(PartKey key)
        => _library.Part(key)?.Groups;

    /// <summary> The piece's mod is gone (the library knows all mods and has no such part). </summary>
    private bool Gone(LookPiece piece)
        => _library.Ready && _library.Part(piece.Key) is null;

    /// <summary>
    /// Finds pieces again whose mod's folder changed (<paramref name="full"/>; else only inside the same mod, which is
    /// cheap); refreshes the names kept for the rest.
    /// </summary>
    private void Heal(bool full)
    {
        var changed = false;
        foreach (var look in _looks)
        {
            var taken = Matching.Keys(look);
            foreach (var piece in look.Pieces)
            {
                if (_library.Part(piece.Key) is { } part)
                {
                    if (piece.Print is null)
                    {
                        // kept before the library knew the part: what finds it again later
                        piece.Print = PrintOf(part);
                        changed     = true;
                    }
                    else if (piece.Print.Name != part.Name || piece.Print.Author != part.Author)
                    {
                        piece.Print.Name   = part.Name;
                        piece.Print.Author = part.Author;
                        changed            = true;
                    }

                    // the shelf it was last seen on (counts if its mod goes)
                    if (part.Shelf.Length > 0 && piece.Shelf != part.Shelf)
                    {
                        piece.Shelf = part.Shelf;
                        changed     = true;
                    }

                    continue;
                }

                if (Healing.Find(piece, piece.Shelf, _library, full ? HealIndex : null, taken) is not { } found)
                    continue;
                // at most one piece per socket: a part in a socket another piece holds is no answer
                if (found.Key.Socket != piece.Socket && look.Pieces.Any(p => p != piece && p.Socket == found.Key.Socket))
                    continue;
                _log?.Invoke($"Mod Closet 2 looks: found {piece.Mod} again as {found.Key.Mod}.");
                taken.Remove(piece.Key);
                piece.Mod    = found.Key.Mod;
                piece.Socket = found.Key.Socket;
                piece.Shelf  = found.Shelf;
                piece.Print  = PrintOf(found);
                look.Empty.Remove(found.Key.Socket);
                // its options may not exist any more; groups it still has keep the look's choices
                foreach (var group in piece.Options.Keys.Where(g => !found.Groups.Contains(g, StringComparer.Ordinal)).ToList())
                    piece.Options.Remove(group);
                taken.Add(found.Key);
                changed = true;
            }
        }

        if (changed)
            Changed();
    }

    private Healing.Index HealIndex()
    {
        if (_healIndex is null || _healIndexFor != _library.ModsVersion)
        {
            if (_words.Count > 50000)
                _words.Clear();
            _healIndex    = new Healing.Index(_library.Parts, _words);
            _healIndexFor = _library.ModsVersion;
        }

        return _healIndex;
    }

    /// <summary> Carries the old Mod Closet's looks over, once, as soon as the library knows its mods. </summary>
    private void ImportOld()
    {
        // before the looks file was read, "imported already" is not known: importing could duplicate or, worse, be saved
        // over the user's looks
        if (_importedOld || !_loaded)
            return;
        string? text;
        try
        {
            text = _store.OldSettings();
        }
        catch
        {
            text = null;
        }

        if (text is null)
            return; // there, but not readable now: next time
        _importedOld = true;
        if (text.Length > 0)
        {
            var result = OldLooks.Read(text, _library, PrintOf, _clock.UtcNow);
            var added  = 0;
            foreach (var look in result.Looks.Where(l => !_byId.ContainsKey(l.Id)))
            {
                AddLook(look);
                ++added;
            }

            _log?.Invoke($"Mod Closet 2 looks: carried over {added} of {result.Old} old looks ({result.ModsGone} mods gone, {result.LooksEmpty} looks with nothing left).");
        }

        Changed();
    }

    // ======================================================================= colours

    /// <summary> The colours now of a look's parts as a colourway, null when there are no colours. </summary>
    private Colourway? ColourwayNow(LookData look)
    {
        if (_colours is null)
            return null;
        var keys = WornKeys(look).ToList();
        string? snapshot;
        try
        {
            snapshot = _colours.Snapshot(keys);
        }
        catch
        {
            snapshot = null;
        }

        if (snapshot is null)
            return null;
        var main = MainPiece(look);
        uint? showing = null;
        try
        {
            showing = main is null ? null : _colours.ShowingMain(main.Key);
        }
        catch
        {
            // a colour not known now: the snapshot may still say one
        }

        return new Colourway { Colours = snapshot, Main = showing ?? ColourKey.MainOf(snapshot, main?.Key.ToString()) ?? 0 };
    }

    private string KeyOf(string snapshot)
    {
        if (!_keys.TryGetValue(snapshot, out var key))
        {
            if (_keys.Count > 4096)
                _keys.Clear();
            _keys[snapshot] = key = ColourKey.Of(snapshot);
        }

        return key;
    }

    /// <summary> The colour key of these parts now (null: colours unknown). </summary>
    private string? KeyNow(IReadOnlyCollection<PartKey> parts)
    {
        if (_colours is null)
            return null;
        var id = string.Join('\n', parts.Select(p => p.ToString()).Order(StringComparer.Ordinal));
        if (!_keysNow.TryGetValue(id, out var key))
        {
            string? snapshot;
            try
            {
                snapshot = _colours.Snapshot(parts);
            }
            catch
            {
                snapshot = null;
            }

            _keysNow[id] = key = snapshot is null ? null : KeyOf(snapshot);
        }

        return key;
    }

    /// <summary> The look's colourway the colours now are, -1 when none (or colours do not count). </summary>
    private int ColourwayOn(LookData look)
    {
        if (_colours is null || look.Colourways.Count == 0)
            return -1;
        if (KeyNow(WornKeys(look)) is not { } now)
            return -1;
        for (var i = 0; i < look.Colourways.Count; ++i)
            if (KeyOf(look.Colourways[i].Colours) == now)
                return i;
        return -1;
    }

    /// <summary> The colours do not say against the look being on (no colours, no colourways, or one of them). </summary>
    private bool ColoursOn(LookData look)
        => _colours is null || look.Colourways.Count == 0 || KeyNow(WornKeys(look)) is null || ColourwayOn(look) >= 0;

    /// <summary> The parts worn with the look (what its colours are about). </summary>
    private HashSet<PartKey> WornKeys(LookData look)
        => look.Pieces.Where(p => _lens.InScope(look, p)).Select(p => p.Key).ToHashSet();

    private bool SameColours(LookData look, Colourway? colours)
        => colours is null || look.Colourways.Count == 0 || look.Colourways.Any(c => KeyOf(c.Colours) == KeyOf(colours.Colours));

    /// <summary> Adds a colourway unless one with the same colours is there; returns its index. </summary>
    private int AddColourway(LookData look, Colourway way)
    {
        var at = IndexOf(look, way);
        if (at >= 0)
            return at;
        look.Colourways.Add(way.Copy());
        return look.Colourways.Count - 1;
    }

    private int IndexOf(LookData look, Colourway way)
        => look.Colourways.FindIndex(c => KeyOf(c.Colours) == KeyOf(way.Colours));

    // ======================================================================= the view

    /// <summary> What a card shows that does not depend on the outfit: made again only when the looks, the library, the language or the day changed. </summary>
    private sealed record Fixed(string Name, IReadOnlyList<LookTile> Tiles, string Meta, string When, int Missing, IReadOnlyList<uint> Colourways);

    private readonly Dictionary<Guid, Fixed> _fixed = new();
    private (int Revision, int Library, int Language, DateTime Day) _fixedFor = (-1, -1, -1, DateTime.MinValue);
    private List<LookData> _pinnedOrder = new();
    private List<LookData> _latelyOrder = new();
    private Dictionary<Guid, string> _names = new();

    private void Rebuild()
    {
        _viewDirty = false;
        var now = _clock.UtcNow;
        var day = _clock.ToLocal(now).Date;
        if (_fixedFor != (_revision, _librarySeen, Loc.Version, day))
        {
            _fixedFor = (_revision, _librarySeen, Loc.Version, day);
            _names    = Names();
            _fixed.Clear();
            foreach (var look in _looks)
                _fixed[look.Id] = MakeFixed(look, now);
            // the order of decayed wears does not change as time passes (all halve alike), only when a look is worn
            _pinnedOrder = _looks.Where(l => l.Pinned).OrderByDescending(l => Weight(l, now)).ThenByDescending(l => l.CreatedUtc).ThenBy(l => l.Id).ToList();
            _latelyOrder = _looks.Where(l => !l.Pinned).OrderByDescending(l => l.LastWornUtc).ThenBy(l => l.Id).ToList();
        }

        _pinned  = _pinnedOrder.Select(MakeCard).ToList();
        _lately  = _latelyOrder.Select(MakeCard).ToList();
        _wearing = MakeWearing(_names);
        _version++;
    }

    /// <summary> Which pinned look the outfit is, or started from (and whether it drifted too far from it). </summary>
    private void UpdateBase(DateTime now)
    {
        LookData? current = _base is { } id && _byId.TryGetValue(id, out var b) && b.Pinned ? b : null;
        if (current is not null && Matching.Same(current, _now, _lens))
            return;

        // the outfit is exactly a pinned look: that one (also over an old look that holds only some of its shelves)
        if (Best(_looks.Where(l => l.Pinned && Matching.Count(l, _lens) > 0 && Matching.Same(l, _now, _lens))) is { } exact)
        {
            _base = exact.Id;
            return;
        }

        // the look it started from stays while it is on, or less than half of it changed (or while what was just kept is
        // not read yet)
        if (current is not null
         && (now < _graceUntil || Matching.Covers(current, _now, _lens) || !Matching.TooFar(current, Matching.Diff(current, _now, _lens).Count, _lens)))
            return;

        // else a pinned look that is on (an old look that holds only some shelves), or none
        _base = Best(_looks.Where(l => l.Pinned && Matching.Count(l, _lens) > 0 && Matching.Covers(l, _now, _lens)))?.Id;
    }

    /// <summary> Of several looks that are on: the one holding the most pieces, then with body, then the last worn. </summary>
    private LookData? Best(IEnumerable<LookData> looks)
        => looks.OrderByDescending(l => Matching.Count(l, _lens)).ThenByDescending(l => l.WithBody).ThenByDescending(l => l.LastWornUtc).ThenBy(l => l.Id).FirstOrDefault();

    /// <summary> The names to show: the user's own, else the main piece's; pinned looks with the same made name get a number. </summary>
    private Dictionary<Guid, string> Names()
    {
        var names = new Dictionary<Guid, string>();
        foreach (var look in _looks)
            names[look.Id] = NameOf(look);
        foreach (var group in _looks.Where(l => l.Pinned && !l.OwnName).GroupBy(l => names[l.Id], StringComparer.OrdinalIgnoreCase))
        {
            var taken = _looks.Count(l => l.Pinned && l.OwnName && names[l.Id].Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            var n     = taken;
            foreach (var look in group.OrderBy(l => l.CreatedUtc).ThenBy(l => l.Id))
            {
                if (++n > 1)
                    names[look.Id] = $"{group.Key} {n}";
            }
        }

        return names;
    }

    private string NameOf(LookData look)
        => look.OwnName ? look.Name : AutoName(look);

    /// <summary> The name a look makes itself: its main piece's (the top or dress, else the next telling piece). </summary>
    private string AutoName(LookData look)
    {
        if (MainPiece(look) is not { } piece)
            return Loc.T("An outfit");
        var name = _library.Part(piece.Key)?.Name ?? piece.Print?.Name ?? piece.Mod;
        return LookTexts.CleanName(name);
    }

    /// <summary>
    /// The main piece: of the outfit pieces (or, for a look of body only, of all), the first by shelf (top, bottoms,
    /// legwear, shoes …) and then by socket (on Tops the body slot's top before a jacket in the wrist slot).
    /// </summary>
    private LookPiece? MainPiece(LookData look)
    {
        var outfit = look.Pieces.Where(p => !_lens.IsBody(p)).ToList();
        var pool   = outfit.Count > 0 ? outfit : look.Pieces;
        return pool.OrderBy(p => LookRules.Telling(_lens.ShelfOf(p))).ThenBy(p => p.Socket).FirstOrDefault();
    }

    /// <summary> The pieces for the collage: the main one of each shelf in telling order first, then the others; at most four. </summary>
    private List<LookPiece> CollagePieces(LookData look)
    {
        var worn  = look.Pieces.Where(p => _lens.InScope(look, p)).OrderBy(p => LookRules.Telling(_lens.ShelfOf(p))).ThenBy(p => p.Socket).ToList();
        var first = worn.GroupBy(p => _lens.ShelfOf(p)).Select(g => g.First()).ToList();
        return first.Concat(worn.Except(first)).Take(LookRules.CardTiles).ToList();
    }

    private Fixed MakeFixed(LookData look, DateTime now)
    {
        return new Fixed(
            _names.GetValueOrDefault(look.Id) ?? NameOf(look),
            CollagePieces(look).Select(p => Tile(p, false)).ToList(),
            LookTexts.Meta(Matching.Count(look, _lens), look.WithBody),
            look.Pinned ? string.Empty : LookTexts.When(_clock.ToLocal(look.LastWornUtc), _clock.ToLocal(now)),
            look.Pieces.Count(p => _lens.InScope(look, p) && Gone(p)),
            look.Colourways.Select(c => c.Main).ToList());
    }

    private LookCard MakeCard(LookData look)
    {
        var f     = _fixed[look.Id];
        var state = CardState.None;
        var way   = look.Colourway;
        if (_outfit.TryingLook == look.Id)
        {
            state = CardState.Trying;
        }
        else if (f.Missing == 0 && Matching.Covers(look, _now, _lens) && Matching.Count(look, _lens) > 0 && ColoursOn(look))
        {
            state = CardState.On;
            if (ColourwayOn(look) is >= 0 and var on)
                way = on;
        }

        return new LookCard
        {
            Id         = look.Id,
            Name       = f.Name,
            Pinned     = look.Pinned,
            Picture    = null,
            Tiles      = f.Tiles,
            Meta       = f.Meta,
            When       = f.When,
            State      = state,
            Missing    = f.Missing,
            Colourways = f.Colourways,
            Colourway  = look.Colourways.Count == 0 ? 0 : Math.Clamp(way, 0, look.Colourways.Count - 1),
            WithBody   = look.WithBody,
        };
    }

    /// <summary> A tile of a look's piece, on the shelf the part is on now (or was, when its mod is gone). </summary>
    private LookTile Tile(LookPiece piece, bool changed)
    {
        var part  = _library.Part(piece.Key);
        var shelf = _lens.ShelfOf(piece);
        return new LookTile
        {
            Piece   = shelf,
            Icon    = _library.ShelfIcon(shelf),
            Name    = part?.Name ?? piece.Print?.Name ?? piece.Mod,
            Picture = part?.Picture,
            Changed = changed,
            Missing = Gone(piece),
        };
    }

    /// <summary> A tile of what is on (no look piece behind it). </summary>
    private LookTile Tile(WornPart worn, bool changed)
    {
        var part = _library.Part(worn.Key);
        return new LookTile
        {
            Piece   = worn.Shelf,
            Icon    = _library.ShelfIcon(worn.Shelf),
            Name    = part?.Name ?? worn.Key.Mod,
            Picture = part?.Picture,
            Changed = changed,
        };
    }

    private WearingNow MakeWearing(Dictionary<Guid, string> names)
    {
        LookData? look = _base is { } id && _byId.TryGetValue(id, out var b) ? b : null;
        var diffs   = look is null ? new List<PieceDiff>() : Matching.Diff(look, _now, _lens);
        var colours = look is not null && diffs.Count == 0 && !ColoursOn(look);
        var changed = diffs.Where(d => d.Now is not null).Select(d => d.Socket).ToHashSet();

        // every outfit piece that is on, one tile each, in outfit order
        var tiles = _now.All.Where(w => !LookRules.IsBody(w.Shelf))
            .OrderBy(w => LookRules.Order(w.Shelf)).ThenBy(w => w.Key.Socket)
            .Select(w => Tile(w, changed.Contains(w.Key.Socket)))
            .ToList();

        var exactly = _looks.Any(l => l.Pinned && Matching.Same(l, _now, _lens) && ColoursOn(l));
        return new WearingNow
        {
            Look       = look?.Id,
            Name       = look is null ? string.Empty : names.GetValueOrDefault(look.Id) ?? NameOf(look),
            ChangeText = look is null ? string.Empty : LookTexts.Change(diffs.Select(d => (d.Kind, Thing(look, d))).ToList(), colours),
            Tiles      = tiles,
            CanPin     = _now.AnyClothes && !exactly,
            CanUpdate  = look is not null && _now.AnyClothes && (diffs.Count > 0 || colours),
        };
    }

    /// <summary>
    /// How a change names the thing: "the shoes" when it is the only thing on its shelf (in the look and on the
    /// character), else by its own name ("Denim jacket"): with a top, a jacket and a corset on Tops, "the top" would not say which.
    /// </summary>
    private string Thing(LookData look, PieceDiff d)
    {
        var alone = LookTexts.HasNoun(d.Shelf)
                 && look.Pieces.Count(p => _lens.ShelfOf(p) == d.Shelf) <= 1
                 && _now.All.Count(w => w.Shelf == d.Shelf) <= 1;
        if (alone)
            return LookTexts.Noun(d.Shelf);
        var key = d.Kind == DiffKind.Added ? d.Now : d.Was;
        if (key is not { } k)
            return LookTexts.Noun(d.Shelf);
        var name = _library.Part(k)?.Name ?? look.Pieces.FirstOrDefault(p => p.Key == k)?.Print?.Name ?? k.Mod;
        return LookTexts.CleanName(name);
    }

    private Card MakeCard(LookLibraryPart part)
        => new()
        {
            Key       = part.Key,
            Name      = part.Name,
            Author    = part.Author,
            Picture   = part.Picture,
            Piece     = part.Shelf,
            PieceName = _library.ShelfName(part.Shelf),
            Icon      = _library.ShelfIcon(part.Shelf),
            KindName  = string.Empty,
            State     = _now.ByKey.ContainsKey(part.Key) ? CardState.On : CardState.None,
        };

    // ======================================================================= the list, steps and the store

    private LookData? Find(Guid id)
        => _byId.GetValueOrDefault(id);

    private void AddLook(LookData look)
    {
        if (_byId.TryGetValue(look.Id, out var old))
            _looks.Remove(old);
        _looks.Add(look);
        _byId[look.Id] = look;
    }

    private void RemoveLook(LookData look)
    {
        _looks.Remove(look);
        _byId.Remove(look.Id);
        if (_base == look.Id)
            _base = null;
    }

    private void Changed()
    {
        _viewDirty = true;
        _baseDirty = true;
        _revision++;
        if (!_saveDirty)
            _saveDue = _clock.UtcNow + LookRules.SaveDelay;
        _saveDirty = true;
    }

    /// <summary> One undo step of a command (the looks it touched, before and after). </summary>
    private void Step(string description, string before, string after)
    {
        Changed();
        FollowBase(_clock.UtcNow);
        try
        {
            _undo.Record(description, before, after);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Mod Closet 2 looks: the undo step could not be kept: {ex.Message}");
        }
    }

    private sealed class UndoEntry
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary> The look as it was; null: there was none. </summary>
        [JsonPropertyName("look")]
        public LookData? Look { get; set; }
    }

    private sealed class UndoSnapshot
    {
        [JsonPropertyName("e")]
        public List<UndoEntry> Entries { get; set; } = new();

        [JsonPropertyName("base")]
        public Guid? Base { get; set; }
    }

    private string Snapshot(params Guid[] ids)
        => Snapshot((IEnumerable<Guid>)ids);

    private string Snapshot(IEnumerable<Guid> ids)
    {
        var snap = new UndoSnapshot { Base = _base };
        foreach (var id in ids.Distinct())
            snap.Entries.Add(new UndoEntry { Id = id, Look = _byId.GetValueOrDefault(id)?.Copy() });
        return JsonSerializer.Serialize(snap);
    }

    /// <summary>
    /// Reads the looks file. When it is there but cannot be read now, nothing changes and it is tried again in a few
    /// seconds; meanwhile nothing is saved. Looks made meanwhile (an outfit remembered, a pin) are kept and join the ones
    /// read.
    /// </summary>
    private void Load()
    {
        string? text;
        try
        {
            text = _store.Read();
        }
        catch (Exception ex)
        {
            if (!_loadFailedSaid)
                _log?.Invoke($"Mod Closet 2 looks: the looks could not be read: {ex.Message}");
            text = null;
        }

        if (text is null)
        {
            if (!_loadFailedSaid)
                _log?.Invoke("Mod Closet 2 looks: the looks file cannot be read right now; asking again in a moment (nothing is saved until then).");
            _loadFailedSaid = true;
            _nextLoad       = _clock.UtcNow + LookRules.LoadRetry;
            return;
        }

        var madeMeanwhile = _looks.Count > 0;
        _loaded = true;
        if (_loadFailedSaid)
            _log?.Invoke("Mod Closet 2 looks: the looks file could be read.");

        var file = LooksFile.FromJson(text, out var problem);
        if (problem.Length > 0)
        {
            // a damaged file is kept aside before anything is written over it
            _log?.Invoke($"Mod Closet 2 looks: the looks file could not be read ({problem}); it is kept aside.");
            try
            {
                _store.SetAside(text);
            }
            catch
            {
                // nothing more to do than say so
            }
        }

        foreach (var look in file.Looks.Where(l => !_byId.ContainsKey(l.Id)))
            AddLook(look);
        _removed.AddRange(file.Removed.Where(r => !_byId.ContainsKey(r.Id) && _removed.All(k => k.Id != r.Id)));
        _importedOld =   file.ImportedOld;
        _base        ??= file.Base is { } b && _byId.ContainsKey(b) ? b : null;
        // what waited for the file (the old looks, the search for missing pieces) runs on the next poll
        _modsSeen  = int.MinValue;
        _viewDirty = true;
        _baseDirty = true;
        if (madeMeanwhile)
        {
            // keep only the newest Lately entries, and write the looks made meanwhile together with the ones read
            foreach (var old in _looks.Where(l => !l.Pinned).OrderByDescending(l => l.LastWornUtc).Skip(LookRules.LatelyMax).ToList())
                RemoveLook(old);
            Changed();
        }
    }

    /// <summary> Removed looks older than <see cref="LookRules.RemovedKeep"/> go for good. </summary>
    private void Prune(DateTime now)
    {
        _nextPrune = now + TimeSpan.FromMinutes(1);
        if (_removed.RemoveAll(r => r.RemovedUtc is not { } at || now - at > LookRules.RemovedKeep) > 0)
            Changed();
    }

    private void Save()
    {
        if (!_loaded)
            return; // a file that could not be read is never written over
        _saveDirty = false;
        var file = new LooksFile { ImportedOld = _importedOld, Base = _base, Looks = _looks, Removed = _removed };
        try
        {
            _store.Write(file.ToJson());
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Mod Closet 2 looks: the looks could not be saved: {ex.Message}");
        }
    }
}
