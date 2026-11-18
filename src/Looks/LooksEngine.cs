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

    /// <summary>
    /// The base was the closet's guess (one look fit better than every other), not the user's word (a Keep of its try, a
    /// click on its tile, a pin, an update). A guess gives way to a look that fits strictly better; the user's word holds
    /// while the outfit still fits it, whatever else fits too.
    /// </summary>
    private bool _baseGuessed;

    /// <summary>
    /// Pinned looks the outfit is equally (twins: the same pieces, nothing that tells them apart). The closet does not
    /// pick between them: "Wearing now" names them all and asks, until the user clicks one.
    /// </summary>
    private readonly List<Guid> _maybe = new();

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

        _base        = snap.Base is { } b && _byId.ContainsKey(b) ? b : null;
        _baseGuessed = false;
        _maybe.Clear();
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
            if (LookRules.CanTakeOff(socket) && _now.In(socket) is { } worn && (!LookRules.IsBody(worn.Shelf) || _lens.Wears(l, worn.Shelf)))
                off.Add(worn.Key);

        // another layer on a body shelf the look wears (other makeup) comes off; the body socket is never emptied, a body
        // put on wins over the one there by priority (the session's business)
        foreach (var worn in _lens.ExtraLayers(l, _now))
            if (LookRules.CanTakeOff(worn.Key.Socket) && parts.All(p => !LookRules.Clash(p.Key.Socket, ShelfNow(p.Key), worn.Key.Socket, worn.Shelf)) && !off.Contains(worn.Key))
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

        // a click on a look's tile says "this one": a twin that is on already as it is needs no try, and among looks the
        // closet could not tell apart the one clicked is the one meant (also if its try is put back)
        if (l.Pinned && (_maybe.Contains(l.Id) || parts.Count == 0 && off.Count == 0 && colours is null))
        {
            Choose(l.Id);
            if (ColourwayOn(l) is >= 0 and var on)
                l.Colourway = on;
            Changed();
        }

        if (parts.Count == 0 && off.Count == 0 && colours is null)
            return; // it is on already, exactly
        _outfit.TryLook(new LookTry(l.Id, way, NameOf(l), LookTexts.TryNote(Matching.Count(l, _lens), missing, l.Body, _lens.BodyOnly(l)), parts, off, colours));
    }

    public void Pin(Guid look)
    {
        Poll();
        if (Find(look) is not { Pinned: false } l)
            return;
        var name = NameOf(l);
        if (Twin(l) is { } existing)
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
        => Save(null, null, sheet: false);

    /// <summary>
    /// Saves what is on as a look named <paramref name="name"/>: the clothes and jewellery that are on, and of the body
    /// shelves exactly <paramref name="bodyShelves"/>. One undo step, one sentence on the status line.
    /// </summary>
    public Guid SaveNow(string name, IReadOnlyCollection<string> bodyShelves)
        => Save(name ?? string.Empty, bodyShelves ?? Array.Empty<string>(), sheet: true);

    /// <summary>
    /// Pin now and Save look. <paramref name="body"/> null: the body shelves a pin takes (none with clothes on, all of
    /// them when only body things are on). A pin is "exactly a look already" for any look the outfit is; a save from the
    /// sheet only for one that also wears the same body shelves (a save with the hair is another look than one without).
    /// </summary>
    private Guid Save(string? name, IReadOnlyCollection<string>? body, bool sheet)
    {
        Poll();
        if (!_now.Any)
            return Guid.Empty;
        var now  = _clock.UtcNow;
        var made = Capture(_now, now, body);
        if (Matching.Count(made, _lens) == 0)
            return Guid.Empty; // body things only, and none of them chosen
        var colours = ColourwayNow(made);

        // the lately entry of this very outfit is pinned with it (it is not listed twice)
        var lately = _looks.Where(l => !l.Pinned && Matching.Same(l, _now, _lens) && Matching.SameBody(l, made) && SameColours(l, colours))
            .Select(l => l.Id).ToList();

        var existing = sheet ? SameSave(made) : Worn() ?? TheSame();
        if (existing is not null)
        {
            var known = colours is null || existing.Colourways.Any(c => KeyOf(c.Colours) == KeyOf(colours.Colours));
            if (known && lately.Count == 0)
            {
                // exactly a look already: nothing to save
                if (colours is not null)
                    existing.Colourway = IndexOf(existing, colours);
                Choose(existing.Id);
                Changed();
                if (sheet)
                    Say(Loc.F("This is already “{0}”.", NameOf(existing)));
                return existing.Id;
            }

            var before = Snapshot(lately.Append(existing.Id));
            if (colours is not null)
                existing.Colourway = AddColourway(existing, colours);
            foreach (var id in lately)
                RemoveLook(_byId[id]);
            Choose(existing.Id);
            Step(sheet ? Loc.F("Save {0}", NameOf(existing)) : Loc.F("Pin {0}", NameOf(existing)), before, Snapshot(lately.Append(existing.Id)));
            if (sheet)
                Say(known ? Loc.F("Saved as “{0}”.", NameOf(existing)) : Loc.F("Saved as a colourway of “{0}”.", NameOf(existing)));
            return existing.Id;
        }

        var start = Snapshot(lately.Append(made.Id));
        made.Pinned      = true;
        made.CreatedUtc  = now;
        made.LastWornUtc = now;
        made.Wears       = 1 + lately.Sum(id => Weight(_byId[id], now));
        if (colours is not null)
            made.Colourways.Add(colours);
        // a name of its own, unless it is the one the look would make itself (then it keeps following its main piece)
        var own = (name ?? string.Empty).Trim();
        made.Name = own == AutoName(made) ? string.Empty : own;
        foreach (var id in lately)
            RemoveLook(_byId[id]);
        AddLook(made);
        Choose(made.Id);
        Step(sheet ? Loc.F("Save {0}", NameOf(made)) : Loc.F("Pin {0}", NameOf(made)), start, Snapshot(lately.Append(made.Id)));
        if (sheet)
            Say(Loc.F("Saved as “{0}”.", NameOf(made)));
        return made.Id;
    }

    /// <summary>
    /// For Save look: the pinned look that is exactly what would be saved (the outfit, and the same body shelves): the
    /// look being worn if it is one, else the one in these colours, else the only one; null when there is none, or
    /// several equal ones (then a new look is saved rather than a colourway given to the first).
    /// </summary>
    private LookData? SameSave(LookData made)
    {
        var same = _looks.Where(l => l.Pinned && Matching.Count(l, _lens) > 0 && Matching.Same(l, _now, _lens) && Matching.SameBody(l, made)).ToList();
        if (_base is { } id && same.FirstOrDefault(l => l.Id == id) is { } chosen)
            return chosen;
        var coloured = Top(same.Where(Fits).ToList());
        if (coloured.Count == 1)
            return coloured[0];
        return same.Count == 1 ? same[0] : null;
    }

    private void Say(string text)
    {
        try
        {
            _outfit.Say(text);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Mod Closet 2 looks: the status line did not take a sentence: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes a look what is on now. The body shelves it wears stay the same (the pieces on them are taken as they are
    /// now; a shelf with nothing on any more is dropped); clothes go as they are now. A look of body things only stays one
    /// (clothes that are on are not taken into it). A clothes look updated with no clothes on becomes a look of the body
    /// things that are on. The body shelves are changed with <see cref="SetBody"/> (or "Also change hair and body").
    /// </summary>
    public void UpdateFromNow(Guid look)
    {
        Poll();
        if (Find(look) is not { } l || !_now.Any)
            return;
        var before   = Snapshot(l.Id);
        var bodyOnly = _lens.BodyOnly(l) || !_now.AnyClothes;
        var made     = Capture(_now, _clock.UtcNow, bodyOnly && !_lens.BodyOnly(l) ? null : l.Body);
        if (bodyOnly)
        {
            made.Pieces.RemoveAll(p => !_lens.IsBody(p));
            made.Empty.Clear();
        }

        l.Pieces = made.Pieces;
        l.Empty  = made.Empty;
        l.Body   = made.Body;
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
            Choose(l.Id);
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

    /// <summary>
    /// "Also change hair and body": on, the look wears every body shelf it holds a piece on (taking the body that is on now
    /// when it is the outfit's look or holds none); off, it wears none. A look of body things only cannot be switched off
    /// (nothing would be left).
    /// </summary>
    public void SetWithBody(Guid look, bool withBody)
    {
        Poll();
        if (Find(look) is not { } l || l.WithBody == withBody || !withBody && _lens.BodyOnly(l))
            return;
        var before = Snapshot(l.Id);
        if (withBody)
        {
            // switched on while it is the outfit's look, or never seen with a body: the body that is on now (in the
            // sockets its outfit pieces leave free)
            if (_base == l.Id || !l.Pieces.Any(_lens.IsBody))
                TakeBody(l, null);
            l.Body = BodyShelvesOf(l.Pieces.Where(_lens.IsBody).Select(_lens.ShelfOf));
        }
        else
        {
            l.Body.Clear();
        }

        Step(withBody ? Loc.F("Let {0} change hair and body", NameOf(l)) : Loc.F("Let {0} leave hair and body alone", NameOf(l)), before, Snapshot(l.Id));
    }

    /// <summary>
    /// Chooses the body shelves a look wears, one by one (not in <see cref="ILooks"/> yet). A shelf it holds no piece on
    /// takes what is on there now. A look of body things only keeps at least one.
    /// </summary>
    public void SetBody(Guid look, IReadOnlyCollection<string> shelves)
    {
        Poll();
        if (Find(look) is not { } l)
            return;
        var want = BodyShelvesOf(shelves.Where(LookRules.IsBody));
        if (want.Count == 0 && _lens.BodyOnly(l))
            return;
        var missing = want.Where(s => !l.Pieces.Any(p => _lens.ShelfOf(p) == s)).ToList();
        if (Matching.SameBody(l, new LookData { Body = want }) && missing.Count == 0)
            return;
        var before = Snapshot(l.Id);
        if (missing.Count > 0)
            TakeBody(l, missing);
        l.Body = BodyShelvesOf(want.Where(s => l.Pieces.Any(p => _lens.ShelfOf(p) == s)));
        Step(Loc.F("Choose what {0} changes of hair and body", NameOf(l)), before, Snapshot(l.Id));
    }

    /// <summary> <see cref="ILooks.SetBodyShelves"/>: which body shelves a look puts on when worn (see <see cref="SetBody"/>). </summary>
    public void SetBodyShelves(Guid look, IReadOnlyCollection<string> bodyShelves)
        => SetBody(look, bodyShelves ?? Array.Empty<string>());

    /// <summary> The shelf a part is on now ("" when the library does not know it). </summary>
    private string ShelfNow(PartKey key)
        => _library.Part(key)?.Shelf ?? string.Empty;

    /// <summary> Puts the body pieces that are on now (of these shelves, or all) into a look, in sockets its other pieces leave free. </summary>
    private void TakeBody(LookData look, IReadOnlyCollection<string>? shelves)
    {
        var now = Capture(_now, _clock.UtcNow, null).Pieces.Where(_lens.IsBody)
            .Where(p => shelves is null || shelves.Contains(_lens.ShelfOf(p))).ToList();
        look.Pieces.RemoveAll(p => _lens.IsBody(p) && (shelves is null || shelves.Contains(_lens.ShelfOf(p))));
        foreach (var piece in now)
            if (look.Pieces.All(p => !LookRules.Clash(p.Socket, _lens.ShelfOf(p), piece.Socket, _lens.ShelfOf(piece))))
            {
                look.Pieces.Add(piece);
                look.Empty.Remove(piece.Socket);
            }
    }

    /// <summary> Body shelves, once each, in rail order. </summary>
    private static List<string> BodyShelvesOf(IEnumerable<string> shelves)
        => shelves.Where(LookRules.IsBody).Distinct().OrderBy(LookRules.BodyIndex).ToList();

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
            // a body kept when body mods sat on Skin is offered bodies from Bodies too (and the other way round)
            var shelf = LookRules.Kin(piece).SelectMany(_library.PartsOn).Where(c => !taken.Contains(c.Key)).ToList();
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
        l.Pieces.RemoveAll(p => LookRules.Clash(p.Socket, _lens.ShelfOf(p), with.Socket, part.Shelf));
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
        if (onShelf.Count == 0) // a body kept on Skin, asked for as Bodies (or the other way round)
            onShelf = look.Pieces.Where(p => LookRules.SameShelf(_lens.ShelfOf(p), shelf)).OrderBy(p => p.Socket).ToList();
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
                    Choose(l.Id);
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
        if (!_now.Any)
            return; // nothing on at all (body things alone are an outfit too)
        var made    = Capture(_now, now, null);
        var colours = ColourwayNow(made);

        // exactly a pinned look (in one of its colourways, or one without any): a wear of the look being worn. The quiet
        // minute never decides which look that is: it used to take the first in the list that matched, which took the
        // outfit away from the look the user had just put on. Among twins nobody picked, no wear is counted (and no
        // Lately entry: it is a pinned look)
        if (Worn() is { } look)
        {
            if (colours is not null)
                look.Colourway = AddColourway(look, colours);
            Wear(look, now);
            Changed();
            return;
        }

        if (_looks.Any(l => l.Pinned && Matching.Count(l, _lens) > 0 && Matching.Same(l, _now, _lens) && ColoursOn(l)))
            return;

        // the same outfit again: it moves to the front (the latest of them, should colours not tell two apart)
        if (_looks.Where(l => !l.Pinned && Matching.Same(l, _now, _lens) && SameColours(l, colours)).OrderByDescending(l => l.LastWornUtc).FirstOrDefault() is { } again)
        {
            // the same outfit: its pieces are the same, the body is the one that is on now
            again.Pieces = made.Pieces;
            again.Empty  = made.Empty;
            again.Body   = made.Body;
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
    /// <summary>
    /// <paramref name="body"/>: the body shelves it wears (only those with something on count); null: a pin's choice,
    /// none with clothes on, every body shelf with something on when there are only body things. A look of body things
    /// only knows no empty sockets: it says nothing about clothes.
    /// </summary>
    /// <param name="prints"> False: without the fingerprints (no file names read), for a name only. </param>
    private LookData Capture(OutfitNow outfit, DateTime now, IReadOnlyCollection<string>? body, bool prints = true)
    {
        var look = new LookData { CreatedUtc = now, LastWornUtc = now };
        foreach (var worn in outfit.All)
            look.Pieces.Add(PieceOf(worn, prints));
        var on = outfit.All.Where(w => LookRules.IsBody(w.Shelf)).Select(w => w.Shelf).ToList();
        if (!outfit.AnyClothes)
        {
            look.Body = BodyShelvesOf(body ?? on).Where(on.Contains).ToList();
            return look;
        }

        foreach (var socket in LookRules.GearSockets)
            if (outfit.In(socket) is null)
                look.Empty.Add(socket);
        look.Body = BodyShelvesOf(body ?? Array.Empty<string>()).Where(on.Contains).ToList();
        return look;
    }

    private LookPiece PieceOf(WornPart worn, bool prints = true)
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
            Print   = part is null || !prints ? null : PrintOf(part),
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
                if (look.Pieces.Any(p => p != piece && (p.Key == found.Key || LookRules.Clash(p.Socket, _lens.ShelfOf(p), found.Key.Socket, found.Shelf))))
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

        // a chosen body shelf none of its pieces is on any more, while a kin shelf has them (a body chosen as Skin before
        // Bodies was split off, now on Bodies): the choice follows the pieces
        foreach (var look in _looks)
            for (var i = 0; i < look.Body.Count; ++i)
            {
                var chosen = look.Body[i];
                if (look.Pieces.Any(p => _lens.ShelfOf(p) == chosen))
                    continue;
                var kin = LookRules.Kin(chosen).FirstOrDefault(k => k != chosen && !look.Body.Contains(k) && look.Pieces.Any(p => _lens.ShelfOf(p) == k));
                if (kin is null)
                    continue;
                look.Body[i] = kin;
                changed      = true;
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

    /// <summary>
    /// Which pinned look the outfit is, or started from. The rule, in order:
    /// <list type="number">
    /// <item>The look the user named (put on through the closet, clicked, pinned, updated) stays while the outfit still
    /// fits it, whatever else fits too. A look the closet only guessed stays while nothing fits strictly better.</item>
    /// <item>Right after a Keep of a look (<see cref="LookRules.KeepGrace"/>) it stays whatever is read.</item>
    /// <item>Else the looks that fit are ranked by everything that tells them apart (<see cref="Rank"/>); one best is the
    /// outfit's look; several equal ones are named together and the closet asks (<see cref="_maybe"/>), never picks.</item>
    /// <item>Nothing fits: the look stays while no more than half of it changed (the "you changed …" case); so do equal
    /// looks the closet was asking about.</item>
    /// </list>
    /// </summary>
    private void UpdateBase(DateTime now)
    {
        LookData? current = _base is { } id && _byId.TryGetValue(id, out var b) && b.Pinned ? b : null;
        var fits = _looks.Where(l => l.Pinned && Fits(l)).ToList();
        if (current is not null && Fits(current))
        {
            if (!_baseGuessed || Top(fits).Contains(current))
            {
                _maybe.Clear();
                return;
            }
        }
        else if (current is not null && now < _graceUntil)
        {
            return; // just kept: what is on may not be read yet
        }

        if (fits.Count > 0)
        {
            var top = Top(fits);
            _maybe.Clear();
            if (top.Count == 1)
            {
                _base        = top[0].Id;
                _baseGuessed = true;
            }
            else
            {
                _base = null;
                _maybe.AddRange(top.Select(l => l.Id));
            }

            return;
        }

        if (current is not null && !Matching.TooFar(current, Matching.Diff(current, _now, _lens).Count, _lens))
            return;
        _base = null;
        _maybe.RemoveAll(m => !_byId.TryGetValue(m, out var l) || Matching.TooFar(l, Matching.Diff(l, _now, _lens).Count, _lens));
    }

    /// <summary> The user named this look (see <see cref="_baseGuessed"/>). </summary>
    private void Choose(Guid look)
    {
        _base        = look;
        _baseGuessed = false;
        _maybe.Clear();
    }

    /// <summary> The look is on: its pieces as it has them (<see cref="Matching.Covers"/>) and its colours (or colours that cannot tell). </summary>
    private bool Fits(LookData look)
        => Matching.Count(look, _lens) > 0 && Matching.Covers(look, _now, _lens) && ColoursOn(look);

    /// <summary>
    /// How well a look that fits says what is on, for telling looks apart: nothing else on that it would wear (exact), its
    /// colours known to be these (a colourway, not just "colours cannot tell"), how many pieces, the body too, and how
    /// many options and versions it names. Not when it was worn or made: that says nothing about what is on.
    /// </summary>
    private (int Exact, int Colours, int Pieces, int Body, int Named) Rank(LookData look)
        => (Matching.Same(look, _now, _lens) ? 1 : 0,
            ColourwayOn(look) >= 0 ? 1 : 0,
            Matching.Count(look, _lens),
            look.Body.Count,
            look.Pieces.Where(p => _lens.InScope(look, p)).Sum(p => p.Options.Count + (p.Version >= 0 ? 1 : 0)));

    /// <summary> The looks of the best rank (several when they are equal). </summary>
    private List<LookData> Top(List<LookData> looks)
    {
        if (looks.Count <= 1)
            return looks;
        var ranked = looks.Select(l => (Look: l, Rank: Rank(l))).ToList();
        var best   = ranked.Max(r => r.Rank);
        return ranked.Where(r => r.Rank == best).Select(r => r.Look).ToList();
    }

    /// <summary>
    /// The pinned look the outfit is exactly, in its colours: the outfit's look when it is one, else the one best (null
    /// when none, or several equal ones nobody picked).
    /// </summary>
    private LookData? Worn()
    {
        var exact = _looks.Where(l => l.Pinned && Fits(l) && Matching.Same(l, _now, _lens)).ToList();
        if (_base is { } id && exact.FirstOrDefault(l => l.Id == id) is { } chosen)
            return chosen;
        var top = Top(exact);
        return top.Count == 1 ? top[0] : null;
    }

    /// <summary>
    /// The pinned look with the outfit's pieces when its colours differ (for a new colourway): the outfit's look if it is
    /// one of them, else the only one; null when there are several and none was named.
    /// </summary>
    private LookData? TheSame()
    {
        var same = _looks.Where(l => l.Pinned && Matching.Count(l, _lens) > 0 && Matching.Same(l, _now, _lens)).ToList();
        if (_base is { } id && same.FirstOrDefault(l => l.Id == id) is { } chosen)
            return chosen;
        return same.Count == 1 ? same[0] : null;
    }

    /// <summary>
    /// The pinned look a remembered outfit is (the same pieces), for a pin that adds a colourway: the one that has its
    /// colours already, else the outfit's look, else the only one; null when several are equally it (then it is pinned as
    /// a look of its own rather than merged into the first that matches).
    /// </summary>
    private LookData? Twin(LookData lately)
    {
        var same = _looks.Where(p => p.Pinned && Matching.SamePieces(p, lately, _lens)).ToList();
        if (same.Count <= 1)
            return same.FirstOrDefault();
        var keys    = lately.Colourways.Select(c => KeyOf(c.Colours)).ToHashSet();
        var coloured = same.Where(p => keys.Count > 0 && p.Colourways.Any(c => keys.Contains(KeyOf(c.Colours)))).ToList();
        if (coloured.Count == 1)
            return coloured[0];
        if (_base is { } id && same.FirstOrDefault(p => p.Id == id) is { } chosen)
            return chosen;
        return null;
    }

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
            LookTexts.Meta(Matching.Count(look, _lens), look.WithBody, _lens.BodyOnly(look)),
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
        else if (f.Missing == 0 && Fits(look) && !TwinOfWorn(look))
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
            WithBody    = look.WithBody,
            BodyShelves = look.Body.ToList(),
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

    /// <summary>
    /// The look holds the same pieces as the look being worn (which the user put on, or the closet is sure of), and that
    /// one is on: only that one is marked On, so two cards never both say On for one outfit.
    /// </summary>
    private bool TwinOfWorn(LookData look)
        => _base is { } id && id != look.Id && _byId.TryGetValue(id, out var worn) && worn.Pinned && Fits(worn)
        && Matching.SamePieces(worn, look, _lens);

    private WearingNow MakeWearing(Dictionary<Guid, string> names)
    {
        LookData? look = _base is { } id && _byId.TryGetValue(id, out var b) ? b : null;
        if (look is null && _maybe.Count > 0)
            return MakeMaybe(names);
        var diffs   = look is null ? new List<PieceDiff>() : Matching.Diff(look, _now, _lens);
        var colours = look is not null && diffs.Count == 0 && !ColoursOn(look);
        var changed = diffs.Where(d => d.Now is not null).Select(d => d.Socket).ToHashSet();

        var exactly = _looks.Any(l => l.Pinned && Matching.Same(l, _now, _lens) && ColoursOn(l));
        return new WearingNow
        {
            Look         = look?.Id,
            Name         = look is null ? string.Empty : names.GetValueOrDefault(look.Id) ?? NameOf(look),
            ChangeText   = look is null ? string.Empty : LookTexts.Change(diffs.Select(d => (d.Kind, Thing(look, d))).ToList(), colours),
            Tiles        = NowTiles(changed),
            CanPin       = _now.Any && !exactly,
            CanUpdate    = look is not null && _now.Any && (diffs.Count > 0 || colours),
            ProposedName = ProposedName(names),
            Saves        = !_now.Any ? SaveWhat.Nothing : _now.AnyClothes ? SaveWhat.Clothes : SaveWhat.BodyOnly,
            BodyShelves  = SaveShelves(look),
        };
    }

    /// <summary>
    /// The pieces that are on, one tile each, in outfit order: the clothes and jewellery; when only body things are on,
    /// those (so "Wearing now" never says nothing is on while a body is).
    /// </summary>
    private List<LookTile> NowTiles(HashSet<Socket> changed)
    {
        var shown = _now.AnyClothes ? _now.All.Where(w => !LookRules.IsBody(w.Shelf)) : _now.All;
        return shown.OrderBy(w => LookRules.Order(w.Shelf)).ThenBy(w => w.Key.Socket).Select(w => Tile(w, changed.Contains(w.Key.Socket))).ToList();
    }

    /// <summary>
    /// The name a save of what is on would get: its main piece's (as a pin names it), with a number when a pinned look is
    /// called that already. "" when there is nothing to save.
    /// </summary>
    private string ProposedName(Dictionary<Guid, string> names)
    {
        if (!_now.Any)
            return string.Empty;
        var made = Capture(_now, _clock.UtcNow, null, prints: false);
        var name = AutoName(made);
        var taken = _looks.Where(l => l.Pinned).Select(l => names.GetValueOrDefault(l.Id) ?? NameOf(l)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
            return name;
        for (var n = 2; ; ++n)
            if (!taken.Contains($"{name} {n}"))
                return $"{name} {n}";
    }

    /// <summary>
    /// The body shelves with something on, in rail order, for the save sheet's ticks: what is on there, and whether a save
    /// takes it unless unticked (as the look being worn has it; all of them when only body things are on; else none).
    /// </summary>
    private List<SaveBodyShelf> SaveShelves(LookData? worn)
        => _now.All.Where(w => LookRules.IsBody(w.Shelf))
            .GroupBy(w => w.Shelf)
            .OrderBy(g => LookRules.BodyIndex(g.Key))
            .Select(g => new SaveBodyShelf
            {
                Shelf   = g.Key,
                Name    = _library.ShelfName(g.Key),
                Worn    = string.Join(", ", g.OrderBy(w => w.Key.Socket).Select(w => _library.Part(w.Key)?.Name ?? w.Key.Mod)),
                Default = worn is not null ? _lens.Wears(worn, g.Key) : !_now.AnyClothes,
            })
            .ToList();

    /// <summary>
    /// "Wearing now" when several looks are equally the outfit: all of them named ("Gala night or Beach day"), what
    /// changed, or a line that asks; no look to update until the user clicks the one they are wearing.
    /// </summary>
    private WearingNow MakeMaybe(Dictionary<Guid, string> names)
    {
        var looks = _maybe.Select(Find).OfType<LookData>().ToList();
        var said  = looks.Select(l => names.GetValueOrDefault(l.Id) ?? NameOf(l)).ToList();
        var name  = said.Count < 2 ? said.FirstOrDefault() ?? string.Empty : Loc.F("{0} or {1}", string.Join(", ", said.Take(said.Count - 1)), said[^1]);
        var diffs = looks.Count == 0 ? new List<PieceDiff>() : Matching.Diff(looks[0], _now, _lens);
        var changed = diffs.Where(d => d.Now is not null).Select(d => d.Socket).ToHashSet();
        var exactly = _looks.Any(l => l.Pinned && Matching.Same(l, _now, _lens) && ColoursOn(l));
        return new WearingNow
        {
            Look       = null,
            Name       = name,
            ChangeText = diffs.Count > 0 ? LookTexts.Change(diffs.Select(d => (d.Kind, Thing(looks[0], d))).ToList(), false)
                                         : Loc.T("These looks are the same. Click the one you are wearing."),
            Tiles        = NowTiles(changed),
            CanPin       = _now.Any && !exactly,
            CanUpdate    = false,
            ProposedName = ProposedName(names),
            Saves        = !_now.Any ? SaveWhat.Nothing : _now.AnyClothes ? SaveWhat.Clothes : SaveWhat.BodyOnly,
            BodyShelves  = SaveShelves(null),
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
        _maybe.Remove(look.Id);
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
        if (_base is null && file.Base is { } b && _byId.ContainsKey(b))
        {
            // the last session's word, or its guess: it holds while nothing fits strictly better
            _base        = b;
            _baseGuessed = true;
        }
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
        foreach (var look in _looks.Concat(_removed))
            look.LegacyWithBody = look.WithBody; // for an older build reading this file
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
