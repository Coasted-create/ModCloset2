using ModCloset2.Core;
using ModCloset2.Library;
using ModCloset2.Looks;

namespace ModCloset2.Closet;

/// <summary> The colours' try layer, as the session needs it for a look's colourway (implemented over the colour engine). </summary>
public interface ITryColours
{
    /// <summary> Shows these colours as part of the try (a second call replaces the first). </summary>
    void TryOn(string snapshot);

    /// <summary> Ends the colour try: kept (the before/after pair for the Keep's undo step) or dropped (as before; null). </summary>
    /// <param name="byUser">
    /// The user ended it (Put back, Keep, another try, Undo). False when the session ends it on its own (the wardrobe
    /// stopped previewing): then nothing is taken over from what was coloured outside the closet.
    /// </param>
    (string Before, string After)? EndTry(bool keep, bool byUser = true);

    /// <summary> Puts colours back as a snapshot says (the undo of a Keep that kept colours). </summary>
    void Restore(string snapshot);
}

/// <summary>
/// The state all faces share (wide window, slim window, later "On you"). It draws its lists from the library and the
/// wardrobe, rebuilds them only when one of those changed, and turns the faces' clicks into library and wardrobe
/// commands. No Dalamud types: the tests drive it with a fake library and a fake wardrobe. Draw / framework thread only.
/// </summary>
public sealed partial class ClosetSession : IClosetSession, ILooksOutfit, IDisposable
{
    private const int MaxUndo = 60;

    /// <summary> A shelf with this many parts and two kinds of five or more gets kind chips (SPEC "Kind chips"). </summary>
    public const int ChipShelfSize = 40;
    public const int ChipKindSize  = 5;

    private readonly ILibrary _library;
    private readonly IWardrobe _wardrobe;
    private readonly ClosetData _data;
    private readonly Action _save;
    private readonly Func<DateTime> _now;
    private readonly Action<string> _applyLanguage;
    private readonly Action<string>? _log;

    /// <summary> The colours (made by the plugin once the session is there), or null: then the faces show none. </summary>
    public IOutfitColours? Colours { get; set; }

    /// <summary> The colours' try layer (a look's colourway shown while trying), or null: looks then leave colours alone. </summary>
    public ITryColours? ColourTry { get; set; }

    /// <summary> The looks (made by the plugin once the session is there), or null: then the faces show none. </summary>
    public ILooks? Looks { get; set; }

    public ClosetSession(ILibrary library, IWardrobe wardrobe, ClosetData data, Action save, Func<DateTime>? now = null,
        Action<string>? applyLanguage = null, Action<string>? log = null)
    {
        _library       = library;
        _wardrobe      = wardrobe;
        _data          = data;
        _save          = save;
        _now           = now ?? (() => DateTime.UtcNow);
        _applyLanguage = applyLanguage ?? (code => Loc.SetLanguage(code.Length > 0 ? code : "en"));
        _log           = log;
        _selected      = data.Selected.Length > 0 ? data.Selected : Pieces.Top;
    }

    // ======== keeping the lists fresh ========

    private int _libraryVersion = -1;
    private int _wardrobeVersion = -1;
    private int _locVersion = -1;
    private int _messageSerial;
    private bool _membershipDirty = true;
    private bool _viewDirty = true;
    private bool _syncing;
    private int _version;

    /// <summary> Bumped whenever something a face shows has changed (reading it picks up what changed). </summary>
    public int Version
    {
        get
        {
            Sync();
            return _version;
        }
    }

    /// <summary> Framework thread, every frame while a face is open: picks up what changed in the library and the wardrobe. </summary>
    public void Update()
    {
        _frame++;
        _faceFrame = _frame;
        Sync();
        WarmSearch();
    }

    /// <summary>
    /// Framework thread, every frame while no face is open: only what must not wait (a failed Keep, a try left on the
    /// character, the census). The lists are rebuilt when a face reads them again; no library work is done meanwhile.
    /// </summary>
    public void Idle()
    {
        _frame++;
        if (_syncing)
            return;
        _syncing = true;
        try
        {
            ReconcileTries();
            UpdateCensus();
            UpdateOwnMods();
            UpdatePhotos();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"session idle failed: {ex}");
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// The session's tries and the wardrobe must say the same:
    /// - a Keep or version change that failed gives the try back (it is still on the character, so it can be put back or
    ///   kept again);
    /// - a try that failed or ended elsewhere leaves nothing showing, so the bar goes;
    /// - something still tried on the character with no try in the session (a failed step whose try is gone) is put back.
    /// </summary>
    private void ReconcileTries()
    {
        if (_resolving is { } r)
        {
            var state = _wardrobe.Step(r.Step);
            if (state == StepState.Failed)
            {
                if (_tries.Count == 0 && r.Tries.Count > 0 && _wardrobe.Previewing)
                {
                    _tries.AddRange(r.Tries);
                    _tryPending = true;
                }

                _resolving = null;
                _viewDirty = true;
            }
            else if (state != StepState.Pending)
            {
                _resolving = null;
            }
        }

        ResolveKeeps();

        SettleMove();

        if (_tries.Count > 0 && _moving is null && !_wardrobe.Previewing && !_wardrobe.Busy && !_tryPending)
        {
            _tries.Clear();
            // the try ended without the user (the wardrobe stopped previewing): not their doing
            EndColourTry(false, byUser: false);
            _viewDirty = true;
        }

        // nothing tried any more (the last piece taken out, a failed try): no look is being tried
        if (_tries.Count == 0 && !_colourTrying && _lookTry is not null)
        {
            _lookTry   = null;
            _viewDirty = true;
        }
        else if (_tries.Count == 0 && _resolving is null && _wardrobe.Previewing && !_wardrobe.Busy)
        {
            _wardrobe.PutBack();
            _viewDirty = true;
        }

        _tryPending = false;
    }

    /// <summary>
    /// A kept thing's slot move. It counts as over when the wardrobe has run its step and what is on says the thing is on
    /// through the new slot and no other, or when the step failed (nothing moved), or at the latest <see cref="MoveShows"/>
    /// after it ran. Until then the tries wait (the Keep ended the wardrobe's try) and a new "Wear on" waits. Then the tries
    /// show again and the waiting pick runs, on what is on now.
    /// </summary>
    private void SettleMove()
    {
        if (_moving is not { } move)
            return;
        var state = _wardrobe.Step(move.Step);
        if (state == StepState.Pending)
            return;

        if (move.RanAt is null)
        {
            move.RanAt = _now();
            if (state != StepState.Done)
            {
                // it did not move: it remembers the slot it is on through
                if (move.SlotBefore is { } before)
                    _data.Slots[move.Thing] = before;
                else
                    _data.Slots.Remove(move.Thing);
                _save();
            }
        }

        var shown = state != StepState.Done
         || _wardrobe.IsOn(move.Target) && !move.Off.Any(_wardrobe.IsOn)
         || _now() - move.RanAt.Value >= MoveShows;
        if (!shown)
            return;

        _moving    = null;
        _viewDirty = true;
        if (_tries.Count > 0 && !_wardrobe.Previewing)
        {
            _wardrobe.Preview(TryRequests());
            _tryPending = true;
        }

        if (_nextMove is { } next)
        {
            _nextMove = null;
            WearOnNow(next.Part, next.Slot);
        }
    }

    /// <summary> A step that ended a try (Keep, a version change): the try comes back if the step fails. </summary>
    private (Guid Step, List<TryEntry> Tries)? _resolving;

    private void Sync()
    {
        if (_syncing)
            return;
        _syncing = true;
        try
        {
            if (_library.Version != _libraryVersion)
            {
                // only the mods the library changed are placed again; everything when it cannot say which
                if (_libraryVersion >= 0 && _library.ChangedSince(_libraryVersion) is { } changed)
                    _libraryDirtyMods.UnionWith(changed);
                else
                    _membershipDirty = true;
                _libraryVersion = _library.Version;
                _fromLibrary    = true;
                _viewDirty      = true;
                _copiesDirty    = true;
            }

            if (Loc.Version != _locVersion)
            {
                _locVersion      = Loc.Version;
                _membershipDirty = true;
            }

            if (_wardrobe.Version != _wardrobeVersion)
            {
                _wardrobeVersion = _wardrobe.Version;
                _viewDirty       = true;
            }

            if (_wardrobe.Message.Serial != _messageSerial)
            {
                _messageSerial = _wardrobe.Message.Serial;
                if (_wardrobe.Message.Text.Length > 0)
                    SetStatus(_wardrobe.Message.Text, _wardrobe.Message.IsError);
            }

            ReconcileTries();
            PruneUndo();
            UpdateCensus();
            UpdateOwnMods();

            if (_searchDue is { } due && _now() >= due)
                _viewDirty = true;

            if (_copiesDirty)
                RefreshCopies();
            if (_membershipDirty || _libraryDirtyMods.Count > 0 || _userDirtyMods.Count > 0)
                RebuildMembership();
            // after placing: photos are only ever asked for parts the session has placed as the library has them now
            UpdatePhotos();
            if (_viewDirty)
                RebuildView();
        }
        catch (Exception ex)
        {
            // a face must never see an exception; keep the last lists
            // and nothing is tried again every frame: what was asked for is dropped (the next change asks again)
            _log?.Invoke($"session update failed: {ex}");
            _membershipDirty = _viewDirty = false;
            _libraryDirtyMods.Clear();
            _userDirtyMods.Clear();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// Something changed. <paramref name="membership"/>: who is on which shelf may have changed for every part (language,
    /// renamed shelves, undo of a sorting step); <paramref name="mods"/>: only the parts of these mods.
    /// </summary>
    private void Changed(bool membership = false, IEnumerable<string>? mods = null)
    {
        _membershipDirty |= membership;
        if (mods is not null)
            _userDirtyMods.UnionWith(mods);
        _viewDirty = true;
        Sync();
    }

    // ======== who is where ========
    //
    // Kept mod by mod: a detection batch, a move or a hide changes only the parts of the mods it touches (a full rebuild of
    // 20,000 parts is 40-50 ms, too much for the framework thread twice a second during a first scan). A shelf is a
    // dictionary (cheap to change) listed on demand.

    private sealed class Bucket
    {
        // stamps are unique across all buckets: a shelf made again (a full rebuild) never repeats an old stamp, so what
        // was cached for the old one is never taken for the new one
        private static int _stamps;

        public readonly Dictionary<PartKey, LibPart> Members = new();

        /// <summary> Kind (lower case) → how many parts of it (the kind chips, without walking the shelf). </summary>
        public readonly Dictionary<string, int> Kinds = new();
        private List<LibPart>? _list;

        /// <summary> Changes on every change (the card order of the shelf is kept until it moves). </summary>
        public int Stamp { get; private set; } = Interlocked.Increment(ref _stamps);

        public IReadOnlyList<LibPart> List
            => _list ??= Members.Values.ToList();

        public void Add(LibPart part)
        {
            if (Members.TryGetValue(part.Key, out var old))
                Count(old, -1);
            Members[part.Key] = part;
            Count(part, 1);
            _list = null;
            Stamp = Interlocked.Increment(ref _stamps);
        }

        public void Remove(PartKey key)
        {
            if (Members.Remove(key, out var old))
            {
                Count(old, -1);
                _list = null;
                Stamp = Interlocked.Increment(ref _stamps);
            }
        }

        private (int Stamp, int Things, Dictionary<string, int> Kinds) _things = (-1, 0, new Dictionary<string, int>());

        /// <summary>
        /// How many things the shelf holds (parts of one mod that are the same thing count once) and how many of each kind,
        /// worked out again only when the shelf changed.
        /// </summary>
        public (int Things, IReadOnlyDictionary<string, int> Kinds) Things(IReadOnlyDictionary<PartKey, List<LibPart>> severalSlots)
        {
            if (_things.Stamp != Stamp)
            {
                // the parts, less the second and later slots of each thing on this shelf (few: only things of several slots)
                var count = Members.Count;
                Dictionary<string, int>? kinds = null;
                foreach (var thing in severalSlots.Values)
                {
                    var here = 0;
                    foreach (var part in thing)
                        if (Members.ContainsKey(part.Key) && ++here > 1)
                        {
                            count--;
                            if (part.Info.Kind.Length > 0)
                            {
                                kinds ??= new Dictionary<string, int>(Kinds);
                                var kind = part.Info.Kind.ToLowerInvariant();
                                if (kinds.TryGetValue(kind, out var n))
                                    kinds[kind] = n - 1;
                            }
                        }
                }

                _things = (Stamp, count, kinds ?? Kinds);
            }

            return (_things.Things, _things.Kinds);
        }

        private void Count(LibPart part, int by)
        {
            if (part.Info.Kind.Length == 0)
                return;
            var kind = part.Info.Kind.ToLowerInvariant();
            var n    = Kinds.GetValueOrDefault(kind) + by;
            if (n > 0)
                Kinds[kind] = n;
            else
                Kinds.Remove(kind);
        }
    }

    private static readonly IReadOnlyList<LibPart> NoParts = Array.Empty<LibPart>();

    private readonly Dictionary<PartKey, LibPart> _byKey = new();
    private readonly Dictionary<PartKey, string> _shelfOf = new();
    private readonly Dictionary<string, Bucket> _buckets = new();
    private readonly Dictionary<string, List<LibPart>> _byMod = new(StringComparer.OrdinalIgnoreCase);
    /// <summary> The library changed since the last placing (its changes may be held while the user looks). </summary>
    private bool _fromLibrary;
    private readonly HashSet<string> _libraryDirtyMods = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _userDirtyMods = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<PartKey, SearchText.Haystack>? _haystacks;

    /// <summary> How many times everything was placed again (the tests count it). </summary>
    public int FullRebuilds { get; private set; }

    /// <summary> How many mods were placed again one by one (the tests count it). </summary>
    public int ModUpdates { get; private set; }

    /// <summary> Unsure parts the user has not placed: they wait under To sort (and stay on their best-guess shelf). </summary>
    private readonly HashSet<PartKey> _waitingSet = new();

    /// <summary> Parts kept in the To sort list while the user looks at it, though the detector became sure meanwhile. </summary>
    private readonly HashSet<PartKey> _waitPins = new();

    /// <summary> The parts not worn (animations, effects…): on when their mod is on. </summary>
    private readonly Dictionary<PartKey, LibPart> _notWorn = new();

    /// <summary> The parts of a shelf; a ring shelf: those for its hand (<see cref="RingView"/>). </summary>
    private IReadOnlyList<LibPart> ShelfParts(string shelf)
        => IsRingShelf(shelf) ? RingView(shelf) : _buckets.TryGetValue(shelf, out var bucket) ? bucket.List : NoParts;

    /// <summary> How many things a shelf holds (the same thing through several slots counts once). </summary>
    private int ShelfCount(string shelf)
        => IsRingShelf(shelf) ? Things(RingView(shelf)).Count : _buckets.TryGetValue(shelf, out var bucket) ? bucket.Things(_thingParts).Things : 0;

    // ======== things: the same thing of one mod through several slots ========
    //
    // Parts of one mod on one shelf that are the same thing (the detection's call) are one thing: one card, one count, one
    // move. A thing is known by its lead, its first part in the game's slot order. The looks, the colours and the wardrobe
    // keep seeing parts.

    /// <summary> Part → the lead of its thing (itself for a thing of one part). </summary>
    private readonly Dictionary<PartKey, PartKey> _leadOf = new();

    /// <summary> Lead → the parts of its thing, in the game's slot order. </summary>
    private readonly Dictionary<PartKey, List<LibPart>> _thingParts = new();

    private PartKey LeadOf(PartKey key)
        => _leadOf.TryGetValue(key, out var lead) ? lead : key;

    /// <summary> The parts of a part's thing (just the part when it is a thing of its own). </summary>
    private IReadOnlyList<LibPart> ThingOf(PartKey key)
        => _thingParts.TryGetValue(LeadOf(key), out var parts) ? parts : _byKey.TryGetValue(key, out var one) ? new[] { one } : NoParts;

    /// <summary> Whether two parts of one mod are the same thing: the detection decides (same mod, look the same). </summary>
    private static bool SameThing(PartInfo a, PartInfo b)
        => Detect.SameThing.Is(a, b);

    /// <summary>
    /// Only the ten gear slots (head … rings) make one card of a thing: an item is put on there. A body part (an ear socket,
    /// a tail) is the mod being on, worn another way, so an "ears and tail" set in the head slot and the ear socket stays
    /// two cards.
    /// </summary>
    private static bool MergesSlots(Socket socket)
        => socket is >= Socket.Head and <= Socket.Rings;

    /// <summary> Groups the parts of one mod into things, shelf by shelf (after its parts were placed). </summary>
    private void GroupThings(string mod)
    {
        if (!_byMod.TryGetValue(mod, out var parts))
            return;
        foreach (var part in parts)
        {
            if (_leadOf.Remove(part.Key, out var oldLead))
                _thingParts.Remove(oldLead);
        }

        foreach (var shelf in parts.GroupBy(p => _shelfOf.GetValueOrDefault(p.Key, string.Empty)))
        {
            var groups = new List<List<LibPart>>();
            foreach (var part in shelf.OrderBy(p => p.Key.Socket))
            {
                if (!MergesSlots(part.Key.Socket) || groups.FirstOrDefault(g => MergesSlots(g[0].Key.Socket)
                        && g.Any(o => SameThing(o.Info, part.Info))) is not { } group)
                    groups.Add(new List<LibPart> { part });
                else
                    group.Add(part);
            }

            foreach (var group in groups)
            {
                var lead = group[0].Key;
                foreach (var part in group)
                    _leadOf[part.Key] = lead;
                if (group.Count > 1)
                    _thingParts[lead] = group;
            }
        }
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<LibPart>, List<LibPart>> _thingLists = new();

    /// <summary> <see cref="OnePerThing"/> of an ordered list, made once per list (the lists are new whenever they change). </summary>
    private List<LibPart> Things(List<LibPart> ordered)
    {
        if (_thingLists.TryGetValue(ordered, out var made))
            return made;
        made = _thingParts.Count == 0 ? ordered : OnePerThing(ordered);
        _thingLists.AddOrUpdate(ordered, made);
        return made;
    }

    /// <summary> Every part a list names once per thing: the first part of each thing that comes, in the list's order. </summary>
    private List<LibPart> OnePerThing(IEnumerable<LibPart> parts)
    {
        var seen = new HashSet<PartKey>();
        var list = new List<LibPart>();
        foreach (var part in parts)
            if (seen.Add(LeadOf(part.Key)))
                list.Add(part);
        return list;
    }

    /// <summary> The keys of every slot of a part's thing (the part alone when it is a thing of its own). </summary>
    private List<PartKey> ThingKeys(PartKey part)
    {
        var keys = ThingOf(part).Select(p => p.Key).ToList();
        if (!keys.Contains(part))
            keys.Add(part);
        return keys;
    }

    /// <summary> What sits in a slot now: what is tried there, else the kept part on there (null: free). </summary>
    private LibPart? Occupant(Socket socket)
    {
        if (_tries.LastOrDefault(t => SameSocket(t.Key.Socket, socket)) is { } tried)
            return tried.Nothing ? null : PartOf(tried.Key);
        foreach (var mod in _wardrobe.ShowingIn(socket))
            if (PartOf(new PartKey(mod, socket)) is { } part && _on.Contains(part.Key))
                return part;
        return null;
    }

    /// <summary>
    /// The slot a click wears a thing through now (brief 9): 1. the slot it is on or tried in; 2. the slot the user picked
    /// last, unless it would take something off and another slot would not; 3. a slot where the same kind of thing of
    /// another mod sits now (like replaces like); 4. a free slot, clothes slots first (the game's order); 5. the first slot.
    /// </summary>
    private LibPart ClickPart(IReadOnlyList<LibPart> thing)
    {
        if (thing.Count == 1)
            return thing[0];
        foreach (var part in thing)
            if (_on.Contains(part.Key) || _tries.Any(t => !t.Nothing && t.Key == part.Key))
                return part;

        var occupants = thing.Select(p => Occupant(p.Key.Socket)).ToList();
        bool Free(int i) => occupants[i] is null;
        if (_data.Slots.TryGetValue(thing[0].KeyText, out var remembered))
        {
            var i = thing.ToList().FindIndex(p => (int)p.Key.Socket == remembered);
            if (i >= 0 && (Free(i) || !occupants.Where((_, j) => j != i).Any(o => o is null)))
                return thing[i];
        }

        var shelf = _shelfOf.GetValueOrDefault(thing[0].Key, string.Empty);
        for (var i = 0; i < thing.Count; ++i)
            if (occupants[i] is { } other && !other.Mod.Directory.Equals(thing[i].Mod.Directory, StringComparison.OrdinalIgnoreCase)
             && _shelfOf.GetValueOrDefault(other.Key) == shelf && other.Info.Kind.Equals(thing[i].Info.Kind, StringComparison.OrdinalIgnoreCase))
                return thing[i];
        for (var i = 0; i < thing.Count; ++i)
            if (Free(i))
                return thing[i];
        return thing[0];
    }

    /// <summary> The slots a card's thing can be worn through, when its mod offers it in several. </summary>
    public IReadOnlyList<SlotChoice> SlotsOf(PartKey part)
    {
        Sync();
        var thing = ThingOf(part);
        if (thing.Count < 2)
            return Array.Empty<SlotChoice>();
        var next = ClickPart(thing);
        return thing.Select(p =>
        {
            var occupant = Occupant(p.Key.Socket);
            var mine     = occupant is not null && LeadOf(occupant.Key) == LeadOf(p.Key);
            return new SlotChoice
            {
                Slot     = p.Key.Socket,
                Name     = KindNames.SlotLabel(p.Key.Socket),
                On       = _on.Contains(p.Key) || _tries.Any(t => !t.Nothing && t.Key == p.Key),
                Next     = p.Key == next.Key,
                Replaces = occupant is null || mine ? string.Empty : occupant.Name,
            };
        }).ToList();
    }

    /// <summary>
    /// Wears a thing through one slot and remembers that slot. Tried: the try moves to that slot. Kept through another
    /// slot: it moves there, one step for Undo. Otherwise it is tried on there, as a click would.
    /// </summary>
    public void WearOn(PartKey part, Socket slot)
    {
        Sync();

        // A move still running (or not yet shown back): what is on is not known yet, so the pick waits for it. Only the
        // latest one waits; it runs once the move has run and what shows says so (see ReconcileTries). The last pick wins.
        if (_moving is not null)
        {
            _nextMove = (part, slot);
            return;
        }

        WearOnNow(part, slot);
    }

    private void WearOnNow(PartKey part, Socket slot)
    {
        var thing = ThingOf(part);
        if (thing.FirstOrDefault(p => p.Key.Socket == slot) is not { } target)
            return;
        var remembered = thing[0].KeyText;
        int? slotBefore = _data.Slots.TryGetValue(remembered, out var was) ? was : null;
        _data.Slots[remembered] = (int)slot;
        _save();

        // what is on now, as the wardrobe has it (the lists follow it a moment later)
        bool On(LibPart p)
            => _wardrobe.IsOn(p.Key);

        var tried = _tries.FirstOrDefault(t => !t.Nothing && thing.Any(p => p.Key == t.Key));
        if (tried is not null || !thing.Any(On))
        {
            _tries.RemoveAll(t => !t.Nothing && thing.Any(p => p.Key == t.Key));
            Replace(target.Key);
            // the try moves with what it was tried with: the options set on it (or named by a look), and its version
            if (tried is not null)
            {
                var entry = _tries[^1];
                entry.Options = tried.Options;
                if (tried.Version >= 0 && tried.Version < target.VersionCount)
                    entry.Version = tried.Version;
            }

            PreviewTries();
            return;
        }

        // on through another slot: it moves at once, as one Keep (the other slots are taken off), one step for Undo. A thing
        // on through two slots from before is down to the one asked for.
        if (On(target) && !thing.Any(p => p.Key != target.Key && On(p)))
            return;
        var requests = new List<TryRequest>();
        if (!On(target))
            requests.Add(Request(new TryEntry { Key = target.Key, Version = _data.Versions.GetValueOrDefault(target.KeyText) }));
        // every other slot: the wardrobe takes off only what of this mod shows there when the step runs
        var off = thing.Where(p => p.Key != target.Key).Select(p => p.Key).ToList();
        requests.AddRange(off.Select(k => new TryRequest(k, true)));

        // Whatever else is tried stays tried (a look's try with its name, its colours). The Keep ends the wardrobe's try, so
        // the tries are shown again once it has run; if it fails, the thing is still where it was and so are the tries. A
        // take-off tried for this thing means nothing once it moved.
        _tries.RemoveAll(t => t.Nothing && thing.Any(p => p.Key == t.Key));
        var step = Guid.NewGuid();
        _wardrobe.Keep(step, Loc.F("Keep {0}", target.Name), requests);
        PushUndo(new UndoEntry { Step = step, What = target.Name, OutfitStep = true });
        _keeping.Add(new PendingKeep(step, null, null));
        _moving = new SlotMove(step, remembered, slotBefore, target.Key, off);
        _orderStamp++;
        Changed();
    }

    /// <summary>
    /// A slot move of a kept thing: its step, the slot the thing remembered before it, the slot it goes to and the ones it
    /// leaves, and when the wardrobe was seen to have run it.
    /// </summary>
    private sealed record SlotMove(Guid Step, string Thing, int? SlotBefore, PartKey Target, IReadOnlyList<PartKey> Off)
    {
        public DateTime? RanAt { get; set; }
    }

    /// <summary> The move running now (until what shows says it has happened). </summary>
    private SlotMove? _moving;

    /// <summary> The latest "Wear on" picked while a move ran: it runs once that move has shown. </summary>
    private (PartKey Part, Socket Slot)? _nextMove;

    /// <summary>
    /// How long after its step has run a move waits at most for what shows to say so. Usually the wardrobe says so at once
    /// (it shows what a Keep did as it runs it); when a part could not be put on or taken off, it never does.
    /// </summary>
    private static readonly TimeSpan MoveShows = TimeSpan.FromSeconds(3);

    private int ShelfStamp(string shelf)
        => _buckets.TryGetValue(shelf == Pieces.RingsLeft ? Pieces.Rings : shelf, out var bucket) ? bucket.Stamp : -1;

    private Bucket BucketOf(string shelf)
        => _buckets.TryGetValue(shelf, out var bucket) ? bucket : _buckets[shelf] = new Bucket();

    /// <summary> What each part can be found by: name, author, Penumbra folder, shelf and kind names (English and translated). </summary>
    private Dictionary<PartKey, SearchText.Haystack> Haystacks()
    {
        var haystacks = _haystacks ??= new Dictionary<PartKey, SearchText.Haystack>(_byKey.Count);
        if (haystacks.Count < _byKey.Count)
            foreach (var part in _byKey.Values)
                if (!haystacks.ContainsKey(part.Key))
                    haystacks[part.Key] = HaystackOf(part);
        return haystacks;
    }

    /// <summary> Search texts made per frame while a face is open and nothing is searched yet. </summary>
    private const int WarmPerFrame = 200;

    /// <summary>
    /// While a face is open, the search texts are made a few hundred a frame, so the first letter typed does not make all
    /// of them at once (20,000 parts: 70-300 ms). Parts placed later get theirs when placed.
    /// </summary>
    private void WarmSearch()
    {
        var haystacks = _haystacks ??= new Dictionary<PartKey, SearchText.Haystack>(_byKey.Count);
        if (haystacks.Count >= _byKey.Count)
            return;
        var made = 0;
        foreach (var part in _byKey.Values)
            if (!haystacks.ContainsKey(part.Key))
            {
                haystacks[part.Key] = HaystackOf(part);
                if (++made >= WarmPerFrame)
                    return;
            }
    }

    private SearchText.Haystack HaystackOf(LibPart part)
    {
        var shelf = _shelfOf[part.Key];
        return new SearchText.Haystack(new[]
        {
            part.Name, part.Mod.Author, part.Mod.SortFolder, ShelfName(shelf), Def(shelf)?.Name ?? string.Empty,
            KindNames.Singular(part.Info.Kind), KindNames.Plural(part.Info.Kind), part.Info.Kind,
        });
    }

    /// <summary> Mod → its clothing and jewellery parts, for mods with two or more (the Outfits shelf). </summary>
    private readonly Dictionary<string, List<LibPart>> _outfits = new(StringComparer.OrdinalIgnoreCase);
    private int _outfitStamp;

    /// <summary> Parts held on the shelf they were on while the user looks at it (SPEC: no jumping under the mouse). </summary>
    private readonly Dictionary<PartKey, string> _pins = new();

    private DateTime _cardsReadAt = DateTime.MinValue;
    private DateTime _hoverAt = DateTime.MinValue;

    /// <summary> Counts framework frames (<see cref="Update"/> and <see cref="Idle"/>). </summary>
    private long _frame;

    /// <summary> The frame of the last GridHovered(true). </summary>
    private long _hoverTrueFrame = long.MinValue / 2;

    /// <summary>
    /// The faces say every frame whether the mouse is over the cards. Several faces may say it in one frame (the window and
    /// On you): one "true" is enough for that frame, whatever the others say, in whatever order they draw. Without any
    /// face saying it, a shelf whose cards were read in the last half second counts as looked at.
    /// </summary>
    public void GridHovered(bool hovered)
    {
        _hoverAt = _now();
        if (hovered)
            _hoverTrueFrame = _frame;
    }

    /// <summary>
    /// The user looks at the selected shelf: a face said the mouse is over its cards in this frame or the one before (the
    /// faces draw after the framework update that reads this), or, from faces that do not say, its cards were read in the
    /// last half second.
    /// </summary>
    private bool Looking
        => (_now() - _hoverAt).TotalSeconds < 0.5 ? _hoverTrueFrame >= _frame - 1 : (_now() - _cardsReadAt).TotalSeconds < 0.5;

    private void RebuildMembership()
    {
        var hold = Looking && _selected != Pieces.Outfits && _search.Trim().Length == 0;
        if (_membershipDirty)
        {
            _membershipDirty = false;
            _libraryDirtyMods.Clear();
            _userDirtyMods.Clear();
            PlaceAll(hold && _fromLibrary);
        }
        else
        {
            // the user's own changes are never held back; the detector's are while the user looks
            foreach (var mod in _libraryDirtyMods.Except(_userDirtyMods, StringComparer.OrdinalIgnoreCase).ToList())
                PlaceModSafely(mod, hold);
            foreach (var mod in _userDirtyMods.ToList())
                PlaceModSafely(mod, false);
            ModUpdates += _libraryDirtyMods.Count + _userDirtyMods.Count;
            _libraryDirtyMods.Clear();
            _userDirtyMods.Clear();
        }

        _fromLibrary = false;

        // a part held here that left the library: forget it
        foreach (var key in _pins.Keys.Where(k => !_byKey.ContainsKey(k)).ToList())
            _pins.Remove(key);

        // tries of parts that are gone (mod deleted, no longer fits) are dropped
        if (_tries.RemoveAll(t => !_byKey.ContainsKey(t.Key)) > 0)
            PreviewTries();

        _viewDirty = true;
    }

    /// <summary> Places every part again (first build, language, renamed shelves, an undone sorting step). </summary>
    private void PlaceAll(bool hold)
    {
        FullRebuilds++;
        Game.FrameReport.Step("placing every mod");
        var before = new Dictionary<PartKey, string>(_shelfOf);
        var waited = new HashSet<PartKey>(_waitingSet);
        _byKey.Clear();
        _shelfOf.Clear();
        _buckets.Clear();
        _byMod.Clear();
        _waitingSet.Clear();
        _notWorn.Clear();
        _aside.Clear();
        _leadOf.Clear();
        _thingParts.Clear();
        _outfits.Clear();
        _haystacks = null;
        _hits      = null;
        foreach (var group in _library.Parts.Where(p => p.FitsMe && !StrayOfCopy(p)).GroupBy(p => p.Mod.Directory, StringComparer.OrdinalIgnoreCase))
        {
            var parts = group.ToList();
            try
            {
                foreach (var part in parts)
                    Place(part, before.TryGetValue(part.Key, out var b) ? b : null, waited.Contains(part.Key), hold);
                _byMod[group.Key] = parts;
                GroupThings(group.Key);
                UpdateOutfit(group.Key);
                _brokenMods.Remove(group.Key);
            }
            catch (Exception ex)
            {
                LeaveOut(group.Key, parts, ex);
            }
        }

        _outfitStamp++;
    }

    /// <summary> Mods whose parts could not be placed (logged once each; they are left out until they change). </summary>
    private readonly HashSet<string> _brokenMods = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> <see cref="PlaceMod"/>, but a mod that cannot be placed is left out and the others go on. </summary>
    private void PlaceModSafely(string mod, bool hold)
    {
        try
        {
            PlaceMod(mod, hold);
            _brokenMods.Remove(mod);
        }
        catch (Exception ex)
        {
            LeaveOut(mod, _library.PartsOf(mod), ex);
        }
    }

    /// <summary> Takes out whatever of a mod got placed before placing it failed, and says so in the log once. </summary>
    private void LeaveOut(string mod, IEnumerable<LibPart> parts, Exception ex)
    {
        var keys = new HashSet<PartKey>(parts.Select(p => p.Key));
        if (_byMod.Remove(mod, out var old))
            keys.UnionWith(old.Select(p => p.Key));
        foreach (var key in keys)
            Unplace(key);
        _outfits.Remove(mod);
        _outfitStamp++;
        if (_brokenMods.Add(mod))
            _log?.Invoke($"the parts of {mod} could not be placed; it is left out until it changes: {ex}");
    }

    /// <summary> Places the parts of one mod again: takes its old parts out, puts its current ones in. </summary>
    private void PlaceMod(string mod, bool hold)
    {
        Game.FrameReport.Step("placing mods", 1);
        var before = new Dictionary<PartKey, string>();
        var waited = new HashSet<PartKey>();
        if (_byMod.Remove(mod, out var old))
            foreach (var part in old)
            {
                if (_shelfOf.TryGetValue(part.Key, out var shelf))
                    before[part.Key] = shelf;
                if (_waitingSet.Contains(part.Key))
                    waited.Add(part.Key);
                Unplace(part.Key);
            }

        var parts = _library.PartsOf(mod).Where(p => p.FitsMe && !StrayOfCopy(p)).ToList();
        foreach (var part in parts)
            Place(part, before.TryGetValue(part.Key, out var b) ? b : null, waited.Contains(part.Key), hold);
        if (parts.Count > 0)
            _byMod[mod] = parts;
        GroupThings(mod);
        UpdateOutfit(mod);
        _outfitStamp++;
    }

    /// <summary> Changes whenever a part is placed or taken out (the name order of all parts is kept until it moves). </summary>
    private int _placeStamp;

    private void Unplace(PartKey key)
    {
        _placeStamp++;
        if (_shelfOf.Remove(key, out var shelf))
            BucketOf(shelf).Remove(key);
        if (_waitingSet.Remove(key))
            BucketOf(Pieces.ToSort).Remove(key);
        _notWorn.Remove(key);
        _aside.Remove(key);
        _byKey.Remove(key);
        if (_leadOf.Remove(key, out var lead))
            _thingParts.Remove(lead);
        _haystacks?.Remove(key);
        if (_hits?.Remove(key) == true)
            _hitsStamp++;
    }

    private void Place(LibPart part, string? before, bool waited, bool hold)
    {
        _placeStamp++;
        var key     = part.KeyText;
        var copied  = CopyShelf(part);
        var natural = NaturalShelf(part);
        var placed  = _data.Moves.ContainsKey(key) || _data.Hidden.Contains(key);
        string shelf;
        if (!placed && _pins.TryGetValue(part.Key, out var pin))
        {
            shelf = pin;
        }
        else if (hold && !placed && before is not null && before != natural && (before == _selected || natural == _selected))
        {
            // the detector moved it while the user looks at this shelf: it waits until the shelf changes
            _pins[part.Key] = before;
            shelf           = before;
        }
        else
        {
            shelf = natural;
        }

        // a copy the closet made, not read to the end yet: on the shelf its card was on for now
        if (copied is not null && !placed)
            shelf = copied;

        _byKey[part.Key]   = part;
        _shelfOf[part.Key] = shelf;

        // a thing that is a mod of its own now: no card of its own here (unless it is on or tried on: see AsideShown)
        if (_copiedTo.ContainsKey(part.Key))
        {
            _aside[part.Key] = part;
            if (_haystacks is not null)
                _haystacks[part.Key] = HaystackOf(part);
            return;
        }

        BucketOf(shelf).Add(part);
        // asked one by one whether they are on: parts not worn, and body parts (a body mod's bare-skin models show only where
        // nothing is worn, so what shows cannot always say)
        if (part.Key.Socket is Socket.Other or Socket.BodyBase)
            _notWorn[part.Key] = part;

        // unsure and not placed by the user: also waits under To sort (while the user looks at To sort, a part the
        // detector became sure about stays in the list until the shelf changes). To sort is a view: its parts are also on
        // their best-guess shelf, so nothing is ever out of reach (SPEC).
        // a part on the shelf of its kind does not wait: the user said where that kind goes
        var waits = copied is null && !placed && KindShelf(part) is null && (part.Info.Confidence < DetectorThresholds.Unsure || _waitPins.Contains(part.Key));
        if (!waits && !placed && hold && _selected == Pieces.ToSort && waited)
        {
            _waitPins.Add(part.Key);
            waits = true;
        }

        if (waits)
        {
            _waitingSet.Add(part.Key);
            BucketOf(Pieces.ToSort).Add(part);
        }

        if (_haystacks is not null)
            _haystacks[part.Key] = HaystackOf(part);
        if (_hits is not null && Hit(part))
        {
            _hits.Add(part.Key);
            _hitsStamp++;
        }
    }

    /// <summary>
    /// A mod is an outfit when two or more of its parts sit on clothes shelves, jewellery shelves or shelves of the user's
    /// own (after the user's moves; an unsure part on its best guess). Body shelves, Everything else and Hidden never count,
    /// and its Outfits card tries on only the parts that count.
    /// </summary>
    private void UpdateOutfit(string mod)
    {
        // things, not parts: the same tights through three slots are one thing (one per thing, its lead first)
        var clothing = _byMod.TryGetValue(mod, out var parts)
            ? OnePerThing(parts.Where(p => _shelfOf.TryGetValue(p.Key, out var shelf) && IsClothing(shelf)).OrderBy(p => p.Key.Socket))
            : new List<LibPart>();
        if (clothing.Count >= 2)
            _outfits[mod] = clothing;
        else
            _outfits.Remove(mod);
    }

    /// <summary> "jewellery set" when every counting part is on a jewellery shelf, else "outfit" (the Outfits chips). </summary>
    private string OutfitKind(IReadOnlyList<LibPart> parts)
        => parts.All(p => _shelfOf.TryGetValue(p.Key, out var shelf) && Def(shelf)?.Group == PieceGroup.Jewellery) ? "jewellery set" : "outfit";

    /// <summary>
    /// The shelf a part belongs on: hidden, the user's move, else the detector's best guess (an unsure part too: it also
    /// waits under To sort, but stays where it can be found).
    /// </summary>
    private string NaturalShelf(LibPart part)
    {
        var key = part.Key.ToString();
        if (_data.Hidden.Contains(key))
            return Pieces.Hidden;
        if (_data.Moves.TryGetValue(key, out var moved) && IsHome(moved))
            return moved;
        if (KindShelf(part) is { } mine)
            return mine.Id;
        return IsHome(part.Info.Piece) ? part.Info.Piece : Pieces.Other;
    }

    /// <summary> The user's shelf that belongs to the part's kind, or null. </summary>
    private UserShelf? KindShelf(LibPart part)
    {
        var kind = part.Info.Kind;
        if (kind.Length == 0)
            return null;
        foreach (var shelf in _data.Shelves)
            if (shelf.Kind.Length > 0 && shelf.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                return shelf;
        return null;
    }

    /// <summary> A shelf a part can live on: a built-in one that is not a view, or an existing shelf of the user's. </summary>
    private bool IsHome(string id)
        => Pieces.IsUser(id) ? _data.Shelves.Any(s => s.Id == id) : id != Pieces.Hidden && !Pieces.IsVirtual(id) && Pieces.BuiltIn.Any(p => p.Id == id);

    private static bool IsClothing(string shelf)
        => Pieces.IsOutfitPiece(shelf) || Pieces.IsUser(shelf);

    // ======== shelves ========

    private PieceDef? Def(string id)
    {
        if (Pieces.IsUser(id))
            return _data.Shelves.FirstOrDefault(s => s.Id == id) is { } user ? UserDef(user) : null;
        return Pieces.BuiltIn.FirstOrDefault(p => p.Id == id);
    }

    private static PieceDef UserDef(UserShelf shelf)
        => new(shelf.Id, shelf.Name, PieceGroup.Mine, "mine");

    /// <summary> A shelf's name for people (built-in ones translated, the user's as they typed it). </summary>
    private string ShelfName(string id)
        => Def(id) is { } def ? def.Group == PieceGroup.Mine ? def.Name : Loc.T(def.Name) : id;

    private string ShelfIcon(string id)
        => Def(id)?.Icon ?? Pieces.Other;

    /// <summary> Every shelf in rail order: the built-in ones with the user's own after the body shelves. </summary>
    private List<PieceDef> AllShelves()
    {
        var list = new List<PieceDef>();
        foreach (var def in Pieces.BuiltIn)
        {
            if (def.Group == PieceGroup.Bottom && list.All(d => d.Group != PieceGroup.Mine))
                list.AddRange(_data.Shelves.Select(UserDef));
            list.Add(def);
        }

        return list;
    }

    // ======== the view ========

    private IReadOnlyList<RailRow> _rail = Array.Empty<RailRow>();
    private IReadOnlyList<PieceDef> _moveTargets = Array.Empty<PieceDef>();
    private IReadOnlyList<Card> _cards = Array.Empty<Card>();
    private IReadOnlyList<KindChip> _kinds = Array.Empty<KindChip>();
    private WearingInfo? _wearing;
    private IReadOnlyList<WearingInfo> _wearingAll = Array.Empty<WearingInfo>();
    private TryState _trying = TryState.Nothing;
    private IReadOnlyList<SortItem> _toSort = Array.Empty<SortItem>();
    private ScanStatus _scan = new();
    private HashSet<PartKey> _on = new();
    private bool _toSortDirty = true;
    private (int Shelf, int Order, int Loc, int Shelves) _toSortFor = (-1, -1, -1, -1);
    private List<LibPart> _toSortOrdered = new();
    private int _toSortOrderStamp = -1;
    private readonly Dictionary<LibPart, (SortItem Item, int Loc, int Shelves, string Proposal)> _sortItems = new(ReferenceEqualityComparer.Instance);

    /// <summary> Bumped when the card order may change (who is where, what is on, what was kept). </summary>
    private int _orderStamp;
    private (int Stamp, string Shelf, string Kind, int ShelfStamp) _orderedFor = (-1, string.Empty, string.Empty, -1);
    private (string Shelf, int Stamp, int Loc) _chipsFor = (string.Empty, -1, -1);
    private Dictionary<string, List<LibPart>> _onByShelf = new();
    private List<LibPart> _ordered = new();

    private static readonly Socket[] ShowingSockets = Enum.GetValues<Socket>().Where(s => s is not (Socket.None or Socket.Other)).ToArray();

    /// <summary>
    /// The parts that are on: asked socket by socket from what shows (a few mods each), and for parts not worn from the
    /// mod being on. Never a question per part of the library.
    /// </summary>
    private HashSet<PartKey> OnNow()
    {
        // (kept as a set; the rail finds what is on per shelf from it, never by walking a shelf)
        var on = new HashSet<PartKey>();
        foreach (var socket in ShowingSockets)
            foreach (var mod in _wardrobe.ShowingIn(socket))
                if (_byMod.TryGetValue(mod, out var parts) && parts.FirstOrDefault(p => p.Key.Socket == socket) is { } part && _wardrobe.IsOn(part.Key)
                 && NumberShows(part))
                    on.Add(part.Key);
        foreach (var key in _notWorn.Keys)
            if (_wardrobe.IsOn(key))
                on.Add(key);
        return on;
    }

    /// <summary>
    /// A part picked by a number (hair, face, tail, ears) is on only when the character has a number its mod replaces: a
    /// mod that is on for its other parts (a head piece), or files of a tail number the character does not wear, show
    /// nothing of it. Its card then tries it (with its number) instead of saying "on".
    /// </summary>
    private bool NumberShows(LibPart part)
    {
        if (Game.GearSlots.CustomizeKey(part.Key.Socket) is not { } key)
            return true;
        IReadOnlyCollection<int>? numbers = part.CustomizeChoices.TryGetValue(key, out var all) ? all.ToList()
            : part.Customize.TryGetValue(key, out var one) ? new[] { one } : null;
        return Wear.WearRules.NumberShows(numbers, _wardrobe.CustomizeNow(key));
    }

    /// <summary>
    /// Another part of this part's mod is on (Penumbra switches a mod on or off as a whole: this part cannot come off
    /// alone unless it is gear, whose slot can be emptied). The other part's shelf, or null.
    /// </summary>
    private string? WornWith(PartKey part, IReadOnlyCollection<PartKey>? alsoOff = null)
    {
        if (GearIsSocket(part.Socket) || !_byMod.TryGetValue(part.Mod, out var parts))
            return null;
        foreach (var other in parts)
            if (other.Key != part && _on.Contains(other.Key) && alsoOff?.Contains(other.Key) != true)
                return _shelfOf.GetValueOrDefault(other.Key, Pieces.Other);
        return null;
    }

    private void RebuildView()
    {
        Game.FrameReport.Step("making the lists");
        ViewBuilds++;
        _viewDirty   = false;
        _toSortDirty = true;
        var now = _now();

        var on = OnNow();
        if (!on.SetEquals(_on))
        {
            _on = on;
            _orderStamp++;
        }

        _onByShelf = new Dictionary<string, List<LibPart>>();
        foreach (var key in _on)
            if (_byKey.TryGetValue(key, out var onPart) && _shelfOf.TryGetValue(key, out var onShelf))
            {
                if (!_onByShelf.TryGetValue(onShelf, out var list))
                    _onByShelf[onShelf] = list = new List<LibPart>();
                list.Add(onPart);
            }

        // rail
        var rail = new List<RailRow>();
        foreach (var def in AllShelves())
        {
            var count = def.Id == Pieces.Outfits ? _outfits.Count : ShelfCount(def.Id);
            if (count == 0)
                continue;
            var onShelf = def.Id is Pieces.Outfits or Pieces.ToSort or Pieces.Hidden ? null : OnIn(def.Id);
            var worn    = onShelf?.FirstOrDefault();
            rail.Add(new RailRow
            {
                Id          = def.Id,
                Name        = ShelfName(def.Id),
                Group       = def.Group,
                Icon        = def.Icon,
                Count       = count,
                WornName    = worn?.Name ?? string.Empty,
                WornPicture = worn is null ? null : PictureOf(worn),
                WornMore    = Math.Max(0, (onShelf?.Count ?? 0) - 1),
                Badge       = def.Id == Pieces.ToSort ? count : 0,
                IsUser      = def.Group == PieceGroup.Mine,
            });
        }

        _rail        = rail;
        _moveTargets = AllShelves().Where(d => !Pieces.IsVirtual(d.Id) && d.Id != Pieces.Hidden).ToList();

        // kind chips of the selected shelf
        var searching = _search.Trim().Length > 0;
        var shelfParts = searching ? NoParts : ShelfParts(_selected);
        if (searching || _selected is Pieces.ToSort or Pieces.Hidden)
            _kinds = Array.Empty<KindChip>();
        else if (_selected == Pieces.Outfits)
        {
            if (_chipsFor != (_selected, _outfitStamp, Loc.Version))
            {
                var counts = _outfits.Values.GroupBy(OutfitKind).ToDictionary(g => g.Key, g => g.Count());
                _kinds    = Chips(_outfits.Count, counts);
                _chipsFor = (_selected, _outfitStamp, Loc.Version);
            }
        }
        else if (_chipsFor != (_selected, ShelfStamp(_selected), Loc.Version))
        {
            if (IsRingShelf(_selected))
            {
                var ringThings = Things(RingView(_selected));
                _kinds = Chips(ringThings.Count, ringThings.Where(p => p.Info.Kind.Length > 0).GroupBy(p => p.Info.Kind.ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.Count()));
            }
            else
            {
                _kinds = _buckets.TryGetValue(_selected, out var chipBucket) && chipBucket.Things(_thingParts) is var things
                    ? Chips(things.Things, things.Kinds)
                    : Array.Empty<KindChip>();
            }

            _chipsFor = (_selected, ShelfStamp(_selected), Loc.Version);
        }

        var kind = _kinds.Count > 0 ? _kind : string.Empty;

        // cards: the order is kept until something it depends on changes (a try only changes the marks)
        if (searching)
        {
            if (_searchDue is { } due && now < due)
            {
                // still typing in a very large library: the last results stay until the typing pauses
            }
            else
            {
                _searchDue = null;
                _cards     = SearchCards(now);
            }
        }
        else if (_selected == Pieces.Outfits)
        {
            _cards = OutfitCards(now, kind);
        }
        else
        {
            // a thing whose copy is a mod of its own shows here only while it is on or tried on (few, ordered each time)
            if (AsideShown(_selected) is { Count: > 0 } aside)
            {
                _ordered    = Order(shelfParts.Concat(aside).Where(p => kind.Length == 0 || p.Info.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)), now).ToList();
                _orderedFor = (-1, _selected, kind, -1);
            }
            else if (_orderedFor != (_orderStamp, _selected, kind, ShelfStamp(_selected))
                && !(_orderedFor.Stamp == _orderStamp && _orderedFor.Shelf == _selected && _orderedFor.Kind == kind && Reorder(shelfParts, kind, now)))
            {
                _ordered    = Order(shelfParts.Where(p => kind.Length == 0 || p.Info.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)), now).ToList();
                _orderedFor = (_orderStamp, _selected, kind, ShelfStamp(_selected));
            }

            _cards = new LazyCards(Things(_ordered), p => MakeCard(p, now));
        }

        var onHere = searching || _selected is Pieces.Outfits or Pieces.ToSort or Pieces.Hidden ? new List<LibPart>() : OnIn(_selected);
        // what is tried on this shelf comes first: the strip is about it (its options change the try) until the try ends; a
        // worn part it stands in for (the same socket) is left out meanwhile
        var triedHere = new List<(LibPart Part, TryEntry Try)>();
        if (!searching && _selected is not (Pieces.Outfits or Pieces.ToSort or Pieces.Hidden))
            foreach (var t in _tries)
                if (!t.Nothing && _byKey.TryGetValue(t.Key, out var tp) && _shelfOf.TryGetValue(t.Key, out var ts) && TriedOn(t, ts, _selected))
                    triedHere.Add((tp, t));
        var wearing = triedHere.Select(t => MakeWearing(t.Part, t.Try)!).ToList();
        foreach (var p in onHere)
            if (!triedHere.Any(t => t.Part.Key == p.Key || SameSocket(t.Part.Key.Socket, p.Key.Socket) && (p.Key.Socket != Socket.Rings || IsRingShelf(_selected))))
                wearing.Add(MakeWearing(p)!);
        _wearingAll = wearing;
        _wearing    = _wearingAll.FirstOrDefault();
        _trying  = MakeTrying();
        _scan    = MakeScan();
        _version++;
    }

    /// <summary>
    /// The kept parts that are on in a shelf, the one to name first: the part whose files win its socket first (a part that
    /// only partly shows comes later), then by name. Several can be on one shelf (a top, and a jacket in the wrist slot).
    /// </summary>
    private List<LibPart> OnIn(string shelf)
    {
        // a ring shelf: what is on its finger
        if (!_onByShelf.TryGetValue(IsRingShelf(shelf) ? Pieces.Rings : shelf, out var parts))
            return new List<LibPart>();
        if (IsRingShelf(shelf))
        {
            var hand = HandOfShelf(shelf);
            parts = parts.Where(p => p.Key.Socket != Socket.Rings ? shelf == Pieces.Rings : OnFinger(p, hand)).ToList();
        }

        int Rank(LibPart part)
        {
            var showing = _wardrobe.ShowingIn(part.Key.Socket);
            for (var rank = 0; rank < showing.Count; ++rank)
                if (showing[rank].Equals(part.Mod.Directory, StringComparison.OrdinalIgnoreCase))
                    return rank;
            return showing.Count;
        }

        return parts.OrderBy(Rank).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key.Socket).ToList();
    }

    private PartUse? Use(PartKey key)
        => _data.Use.TryGetValue(key.ToString(), out var u) ? u : null;

    private PartUse? Use(LibPart part)
        => _data.Use.TryGetValue(part.KeyText, out var u) ? u : null;

    /// <summary>
    /// The cards of a shelf, made when the window asks for them: a shelf of thousands shows a few dozen at a time, and a
    /// detection batch must not make every card again. The list stands for the moment it was made; a change makes a new one.
    /// </summary>
    private sealed class LazyCards : IReadOnlyList<Card>
    {
        private readonly IReadOnlyList<LibPart> _parts;
        private readonly Func<LibPart, Card> _make;
        private readonly Card?[] _made;

        public LazyCards(IReadOnlyList<LibPart> parts, Func<LibPart, Card> make)
        {
            _parts = parts;
            _make  = make;
            _made  = new Card?[parts.Count];
        }

        public int Count
            => _made.Length;

        public Card this[int index]
            => _made[index] ??= _make(_parts[index]);

        public IEnumerator<Card> GetEnumerator()
        {
            for (var i = 0; i < _made.Length; ++i)
                yield return this[i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

    /// <summary>
    /// Keeps the card order when only a few parts of the shelf came or went (a detection batch): those leave the order and
    /// the new ones are put in their place in it. False when too much changed (then the shelf is ordered again).
    /// </summary>
    private bool Reorder(IReadOnlyList<LibPart> shelfParts, string kind, DateTime now)
    {
        var wanted = kind.Length == 0 ? shelfParts : shelfParts.Where(p => p.Info.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)).ToList();
        if (Reordered(_ordered, wanted, OrderComparer(now)) is not { } ordered)
            return false;
        _ordered    = ordered;
        _orderedFor = (_orderStamp, _selected, kind, ShelfStamp(_selected));
        return true;
    }

    /// <summary> <paramref name="ordered"/> with what left <paramref name="wanted"/> taken out and what came put in place; null when much changed. </summary>
    private static List<LibPart>? Reordered(List<LibPart> ordered, IReadOnlyList<LibPart> wanted, IComparer<LibPart> compare)
    {
        var had   = new HashSet<LibPart>(ordered, ReferenceEqualityComparer.Instance);
        var added = wanted.Where(p => !had.Contains(p)).ToList();
        var gone  = ordered.Count - (wanted.Count - added.Count);
        if (added.Count + gone > Math.Max(64, wanted.Count / 8))
            return null;

        var still   = new HashSet<LibPart>(wanted, ReferenceEqualityComparer.Instance);
        var result  = gone == 0 ? new List<LibPart>(ordered) : ordered.Where(still.Contains).ToList();
        foreach (var part in added)
        {
            var at = result.BinarySearch(part, compare);
            result.Insert(at < 0 ? ~at : at, part);
        }

        return result;
    }

    /// <summary> The card order (<see cref="Order"/>) as a comparer, for putting single parts in place. </summary>
    private IComparer<LibPart> OrderComparer(DateTime now)
        => Comparer<LibPart>.Create((a, b) =>
        {
            int c;
            if ((c = _on.Contains(b.Key).CompareTo(_on.Contains(a.Key))) != 0)
                return c;
            if ((c = Usage.Weight(Use(b), now).CompareTo(Usage.Weight(Use(a), now))) != 0)
                return c;
            if ((c = Usage.IsNew(b.Mod.Since, Use(b), now).CompareTo(Usage.IsNew(a.Mod.Since, Use(a), now))) != 0)
                return c;
            return ByNameComparer.Compare(a, b);
        });

    /// <summary> The last keys of the card order: name, socket, mod folder (so that every order is the same every time). </summary>
    private static readonly IComparer<LibPart> ByNameComparer = Comparer<LibPart>.Create((a, b) =>
    {
        int c;
        if ((c = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name)) != 0)
            return c;
        if ((c = a.Key.Socket.CompareTo(b.Key.Socket)) != 0)
            return c;
        return StringComparer.OrdinalIgnoreCase.Compare(a.Key.Mod, b.Key.Mod);
    });

    /// <summary> Card order: on now, then most kept (recent keeps weigh more), then new, then by name. </summary>
    private IEnumerable<LibPart> Order(IEnumerable<LibPart> parts, DateTime now)
        => parts.Select(p => (Part: p, On: _on.Contains(p.Key), Weight: Usage.Weight(Use(p), now), New: Usage.IsNew(p.Mod.Since, Use(p), now)))
            .OrderByDescending(x => x.On)
            .ThenByDescending(x => x.Weight)
            .ThenByDescending(x => x.New)
            // ordinal: culture-aware comparing is many times slower, and a shelf can hold thousands of cards
            .ThenBy(x => x.Part.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Part.Key.Socket)
            .ThenBy(x => x.Part.Key.Mod, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Part);

    /// <summary> The order key of the card order formula, for the tests. </summary>
    public static (bool On, double Weight, bool New, string Name) OrderKey(bool on, PartUse? use, DateTime added, string name, DateTime now)
        => (on, Usage.Weight(use, now), Usage.IsNew(added, use, now), name);

    /// <summary>
    /// The card of a part's thing: its key is the slot a click would take now (brief 9), "n slots" when its mod offers it
    /// through several, On or Trying when any of its slots is.
    /// </summary>
    private Card MakeCard(LibPart part, DateTime now)
    {
        var shelf = _shelfOf.GetValueOrDefault(part.Key, Pieces.Other);
        var thing = ThingOf(part.Key);
        if (thing.Count == 0)
            thing = new[] { part };
        var click = ClickPart(thing);
        var lead  = LeadOf(part.Key);
        return new Card
        {
            Key        = click.Key,
            Name       = part.Name,
            Author     = part.Mod.Author,
            Picture    = PhotoOfThing(click, thing) ?? part.Mod.Picture,
            Piece      = shelf,
            PieceName  = ShelfName(shelf),
            Icon       = ShelfIcon(shelf),
            KindName   = Label(part, shelf),
            State      = CardStateOf(thing),
            IsNew      = Usage.IsNew(click.Mod.Since, Use(click), now),
            Versions   = click.VersionCount,
            Slots      = thing.Count,
            PartsInMod = _outfits.TryGetValue(part.Mod.Directory, out var set) && set.Any(p => LeadOf(p.Key) == lead) ? set.Count : 1,
            Moved      = thing.Any(p => _data.Moves.TryGetValue(p.KeyText, out var moved) && moved != p.Info.Piece && IsHome(moved)),
            Unsure     = thing.Any(p => _waitingSet.Contains(p.Key)),
            OwnMod     = OffersOwnMod(thing),
            OneHand    = thing.Count == 1 && thing[0].Key.Socket == Socket.Rings ? HandsOf(thing[0]) : Hands.Both,
            CopyOf     = CopyName(part.Mod.Directory),
        };
    }

    /// <summary>
    /// Tried, on or neither. On a ring shelf a ring counts for that shelf's finger only (a ring on the right hand is not on
    /// the left hand's shelf).
    /// </summary>
    private CardState CardStateOf(IReadOnlyList<LibPart> thing)
    {
        var hand = _search.Trim().Length == 0 ? HandOfShelf(_selected) : Hands.Both;
        if (thing.Any(p => _tries.Any(t => !t.Nothing && t.Key == p.Key && (p.Key.Socket != Socket.Rings || HasHand(t.Hand, hand)))))
            return CardState.Trying;
        return thing.Any(p => p.Key.Socket == Socket.Rings ? OnFinger(p, hand) : _on.Contains(p.Key)) ? CardState.On : CardState.None;
    }

    /// <summary> The card's quiet label: what tells two same-named mods apart, and what tells a mod's things apart. </summary>
    private string Label(LibPart part, string shelf)
    {
        var kind  = KindLabel(part, shelf);
        var twins = TwinLabel(part, shelf);
        return twins.Length == 0 ? kind : kind.Length == 0 ? twins : Loc.F("{0} · {1}", twins, kind);
    }

    private readonly Dictionary<string, (int Stamp, Dictionary<string, List<LibMod>> Names)> _namesOn = new();

    /// <summary>
    /// Two different mods of the same name on one shelf (one mod installed twice): the user's Penumbra folder (its last
    /// part) when those differ, else the mod's own folder name. "" when the name is the only one of its kind there.
    /// </summary>
    private string TwinLabel(LibPart part, string shelf)
    {
        var stamp = ShelfStamp(shelf);
        if (!_namesOn.TryGetValue(shelf, out var known) || known.Stamp != stamp)
        {
            var names = new Dictionary<string, List<LibMod>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in ShelfParts(shelf))
            {
                if (!names.TryGetValue(p.Name, out var mods))
                    names[p.Name] = mods = new List<LibMod>();
                if (!mods.Any(m => m.Directory.Equals(p.Mod.Directory, StringComparison.OrdinalIgnoreCase)))
                    mods.Add(p.Mod);
            }

            _namesOn[shelf] = known = (stamp, names);
        }

        if (!known.Names.TryGetValue(part.Name, out var same) || same.Count < 2)
            return string.Empty;
        static string Last(string folder)
            => folder.Replace('\\', '/').TrimEnd('/') is var f && f.LastIndexOf('/') is var i and >= 0 ? f[(i + 1)..] : folder;
        var folders = same.Select(m => Last(m.SortFolder)).ToList();
        return folders.All(f => f.Length > 0) && folders.Distinct(StringComparer.CurrentCultureIgnoreCase).Count() == folders.Count
            ? Last(part.Mod.SortFolder)
            : part.Mod.Directory;
    }

    /// <summary>
    /// What tells a card apart: on Everything else its kind; elsewhere, when its mod has more cards on the same shelf, its
    /// kind when theirs differ ("Jacket", "Top"), else how it is worn ("Wrist slot"); "" otherwise.
    /// </summary>
    private string KindLabel(LibPart part, string shelf)
    {
        if (shelf == Pieces.Other)
            return KindNames.Singular(part.Info.Kind);
        if (!_byMod.TryGetValue(part.Mod.Directory, out var siblings) || siblings.Count < 2)
            return string.Empty;
        // different things of the mod on this shelf (the same thing through several slots is one)
        var here = OnePerThing(siblings.Where(p => _shelfOf.TryGetValue(p.Key, out var s) && s == shelf).OrderBy(p => p.Key.Socket));
        if (here.Count < 2)
            return string.Empty;
        var kinds = here.Select(p => p.Info.Kind.ToLowerInvariant()).Distinct().Count();
        var lead  = LeadOf(part.Key);
        return kinds == here.Count && part.Info.Kind.Length > 0 ? KindNames.Singular(part.Info.Kind) : KindNames.SlotLabel(lead.Socket);
    }

    private IReadOnlyList<Card> OutfitCards(DateTime now, string kind = "")
        => _outfits.Where(kv => kind.Length == 0 || OutfitKind(kv.Value) == kind)
            .Select(kv => (Mod: kv.Key, Parts: kv.Value, On: kv.Value.Any(p => ThingOf(p.Key).Any(q => _on.Contains(q.Key))),
                Weight: kv.Value.Sum(p => Usage.Weight(Use(p), now)), New: kv.Value.Any(p => Usage.IsNew(p.Mod.Since, Use(p), now))))
            .OrderByDescending(x => x.On).ThenByDescending(x => x.Weight).ThenByDescending(x => x.New)
            .ThenBy(x => x.Parts[0].Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new Card
            {
                Key        = new PartKey(x.Mod, Socket.None),
                Name       = x.Parts[0].Name,
                Author     = x.Parts[0].Mod.Author,
                Picture    = OutfitPicture(x.Parts),
                Piece      = Pieces.Outfits,
                PieceName  = ShelfName(Pieces.Outfits),
                Icon       = Pieces.Outfits,
                State      = x.Parts.Any(p => ThingOf(p.Key).Any(q => _tries.Any(t => !t.Nothing && t.Key == q.Key))) ? CardState.Trying
                    : x.Parts.All(p => ThingOf(p.Key).Any(q => _on.Contains(q.Key))) ? CardState.On : CardState.None,
                IsNew      = x.New,
                PartsInMod = x.Parts.Count,
            }).ToList();

    // ---- search ----
    //
    // The parts found for the current words are kept, and kept up to date as mods change (a detection batch tests only
    // the parts it changed); their order is kept until something it depends on changes. When finding takes longer than a
    // frame (a very large library), a new search waits until the typing pauses for 150 ms.

    private string[] _hitsWords = Array.Empty<string>();
    private HashSet<PartKey>? _hits;
    private int _hitsStamp;
    private (int Hits, int Order, int Place) _searchOrderedFor = (-1, -1, -1);
    private List<LibPart> _byName = new();
    private int _byNameFor = -1;

    /// <summary> Not on, never kept, not new: such a part's place in the card order is by its name alone. </summary>
    private bool IsPlain(LibPart part, DateTime now)
    {
        if (_on.Contains(part.Key))
            return false;
        var use = Use(part);
        return Usage.Weight(use, now) <= 0 && !Usage.IsNew(part.Mod.Since, use, now);
    }

    /// <summary> Every placed part by name, kept while only a few parts come and go. </summary>
    private List<LibPart> ByName()
    {
        if (_byNameFor == _placeStamp)
            return _byName;
        var all = _byKey.Values.ToList();
        _byName    = (_byNameFor >= 0 ? Reordered(_byName, all, ByNameComparer) : null) ?? all.OrderBy(p => p, ByNameComparer).ToList();
        _byNameFor = _placeStamp;
        return _byName;
    }
    private List<LibPart> _searchOrdered = new();
    private DateTime? _searchDue;

    /// <summary> Parts from which a search may wait for the typing to pause. </summary>
    private const int LargeLibrary = 2000;
    private double _lastSearchMs;

    /// <summary> How long the last full search took, in milliseconds (the tests read it). </summary>
    public double LastSearchMs
        => _lastSearchMs;

    private bool Hit(LibPart part)
        => !_aside.ContainsKey(part.Key) && Matches(part);

    private bool Matches(LibPart part)
        => _shelfOf.TryGetValue(part.Key, out var shelf) && shelf != Pieces.Hidden && SearchText.Matches(_hitsWords, Haystacks()[part.Key]);

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<LibPart, SearchText.NameKey> _nameKeys = new();

    /// <summary> A part's name as the search ranking reads it, made once per part. </summary>
    private SearchText.NameKey NameKeyOf(LibPart part)
        => _nameKeys.GetValue(part, static p => SearchText.KeyOf(p.Name));

    private IReadOnlyList<Card> SearchCards(DateTime now)
    {
        var query = SearchText.Words(_search);
        if (query.Length == 0)
            return Array.Empty<Card>();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var work  = false;
        if (_hits is null || !query.SequenceEqual(_hitsWords))
        {
            work       = true;
            _hitsWords = query;
            var hits = new HashSet<PartKey>();
            foreach (var part in _byKey.Values)
                if (Hit(part))
                    hits.Add(part.Key);
            _hits = hits;
            _hitsStamp++;
        }

        if (_searchOrderedFor != (_hitsStamp, _orderStamp, _placeStamp))
        {
            // What has every word in its name first, each half in the card order. Most parts were never kept, are not on
            // and are not new: in the card order they come after the others, by name. So only the few others are ordered
            // here; the rest are taken from the name order of all parts, which is kept (sorting 20,000 found parts on
            // every search cost 30-110 ms).
            work = true;
            bool InName(LibPart p)
                => query.All(w => p.NameLower.Contains(w, StringComparison.Ordinal));
            var special = new List<LibPart>();
            foreach (var key in _hits)
                if (_byKey.TryGetValue(key, out var part) && !IsPlain(part, now))
                    special.Add(part);
            var specialSet = new HashSet<LibPart>(special, ReferenceEqualityComparer.Instance);
            var plainNamed = new List<LibPart>();
            var plainRest  = new List<LibPart>();
            foreach (var part in ByName())
                if (_hits.Contains(part.Key) && !specialSet.Contains(part))
                    (InName(part) ? plainNamed : plainRest).Add(part);

            var result = new List<LibPart>(_hits.Count);
            result.AddRange(Order(special.Where(InName), now));
            result.AddRange(plainNamed);
            result.AddRange(Order(special.Where(p => !InName(p)), now));
            result.AddRange(plainRest);

            // a name that is the query, or starts with it, comes first; then names with the query as whole words; each
            // tier in the order above ("rue+" finds the body mod Rue+ before the 234 "… for Rue+")
            var squashed = SearchText.Squash(_search);
            var tiers    = new List<LibPart>[3] { new(), new(), new() };
            var rest     = new List<LibPart>(result.Count);
            foreach (var part in result)
            {
                var tier = InName(part) ? SearchText.NameRank(NameKeyOf(part), squashed, query) : 3;
                (tier < 3 ? tiers[tier] : rest).Add(part);
            }

            if (rest.Count != result.Count)
            {
                result = new List<LibPart>(result.Count);
                foreach (var tier in tiers)
                    result.AddRange(tier);
                result.AddRange(rest);
            }

            _searchOrdered    = result;
            _searchOrderedFor = (_hitsStamp, _orderStamp, _placeStamp);
        }

        if (work)
            _lastSearchMs = watch.Elapsed.TotalMilliseconds;

        // a thing whose copy is a mod of its own is found only while it is on or tried on
        if (AsideShown(null).Where(Matches).ToList() is { Count: > 0 } aside)
            return new LazyCards(Things(Order(aside, now).Concat(_searchOrdered).ToList()), p => MakeCard(p, now));
        return new LazyCards(Things(_searchOrdered), p => MakeCard(p, now));
    }

    /// <summary> Kind chips: a shelf with 40 or more parts and at least two kinds with 5 or more each (SPEC). </summary>
    private static IReadOnlyList<KindChip> Chips(int count, IReadOnlyDictionary<string, int> counts)
    {
        if (count < ChipShelfSize)
            return Array.Empty<KindChip>();
        var kinds = counts.Select(kv => (Kind: kv.Key, Count: kv.Value)).Where(k => k.Count >= ChipKindSize)
            .OrderByDescending(k => k.Count).ThenBy(k => k.Kind, StringComparer.Ordinal).ToList();
        if (kinds.Count < 2)
            return Array.Empty<KindChip>();
        var chips = new List<KindChip> { new() { Id = string.Empty, Name = Loc.T("All"), Count = count } };
        chips.AddRange(kinds.Select(k => new KindChip { Id = k.Kind, Name = KindNames.Plural(k.Kind), Count = k.Count }));
        return chips;
    }

    private WearingInfo? MakeWearing(LibPart? part, TryEntry? tried = null)
    {
        if (part is null)
            return null;

        // a try: the options it is sent with (number picks included); else what the mod shows with now
        var current = tried is not null ? RequestOptions(tried) ?? OptionsNow(part.Mod.Directory) : OptionsNow(part.Mod.Directory);
        var hand    = HandChoices(part.Mod.Directory);
        var options = new List<OptionChip>();
        foreach (var group in OfferedGroups(part))
        {
            var fits = IsFit(group);
            options.Add(new OptionChip
            {
                Group     = group.Name,
                Choices   = group.Options.Select(o => o.Name).ToList(),
                On        = current.TryGetValue(group.Name, out var on) ? on : group.DefaultChoice(),
                Multi     = group.Multi,
                Fits      = fits,
                Automatic = fits && !hand.ContainsKey(group.Name),
            });
        }

        var version = tried?.Version ?? _wardrobe.VersionShowing(part.Key);
        if (version < 0)
            version = _data.Versions.GetValueOrDefault(part.Key.ToString());
        return new WearingInfo
        {
            Trying   = tried is not null,
            Key      = part.Key,
            Name     = part.Name,
            Picture  = PictureOf(part),
            Icon     = ShelfIcon(_shelfOf.GetValueOrDefault(part.Key, Pieces.Other)),
            Options  = options,
            Version  = Math.Clamp(version, 0, part.VersionCount - 1),
            Versions = part.VersionCount,
        };
    }

    private TryState MakeTrying()
    {
        if (_tries.Count == 0)
        {
            // only a look's colours are tried (its pieces are on already): still a try, with Keep and Put back
            if (_colourTrying && _lookTry is { } colourOnly)
                return new TryState { Active = true, Busy = _wardrobe.Busy, Lead = Loc.T("Trying on"), Name = colourOnly.Name, Note = colourOnly.Note };
            return _wardrobe.Busy ? new TryState { Busy = true } : TryState.Nothing;
        }

        var items = _tries.Select(t => new TryItem { Key = t.Key, Name = NameOf(t.Key), Piece = _shelfOf.GetValueOrDefault(t.Key, Pieces.Other), TakingOff = t.Nothing }).ToList();
        var mods  = _tries.Select(t => t.Key.Mod).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var name  = mods == 1 ? items[0].Name : Loc.F("{0} things", _tries.Count);
        var lead  = _tries.All(t => t.Nothing) ? Loc.T("Taking off") : Loc.T("Trying on");

        // the newest thing tried: what it replaces, and what else its mod has
        var note   = string.Empty;
        var extras = new List<TryItem>();
        if (_tries.LastOrDefault(t => !t.Nothing) is { } newest && _byKey.TryGetValue(newest.Key, out var part))
        {
            // in a layered socket a part of the other layer stays: skin textures go over the body, the body under the skin
            if (Replaced(newest.Key) is { } replaced)
                note = Layered(part.Key.Socket) && LayerOf(replaced.Key) != LayerOf(part.Key)
                    ? OverLayer(LayerOf(part.Key)) ? Loc.F("goes over {0}", replaced.Name) : Loc.F("goes under {0}", replaced.Name)
                    : _shelfOf.GetValueOrDefault(replaced.Key) == _shelfOf.GetValueOrDefault(part.Key)
                        ? Loc.F("replaces {0}", replaced.Name)
                        : Loc.F("takes the place of {0} (same slot)", replaced.Name);

            if (_byMod.TryGetValue(part.Mod.Directory, out var siblings))
                foreach (var other in siblings)
                {
                    var shelf = _shelfOf.GetValueOrDefault(other.Key, Pieces.Other);
                    if (other.Key == part.Key || shelf is Pieces.Hidden or Pieces.Other || _on.Contains(other.Key)
                     || _tries.Any(t => t.Key == other.Key || SameSocket(t.Key.Socket, other.Key.Socket)))
                        continue;
                    extras.Add(new TryItem { Key = other.Key, Name = ShelfName(shelf), Piece = shelf });
                }
        }
        else if (_tries.Count == 1 && _byKey.TryGetValue(_tries[0].Key, out var off))
        {
            name = off.Name;
        }

        // a try that started as a look keeps the look's name and note, also while the user swaps pieces
        if (_lookTry is { } look)
        {
            name = look.Name;
            note = look.Note;
            lead = Loc.T("Trying on");
        }

        return new TryState
        {
            Active = true,
            Busy   = _wardrobe.Busy,
            Lead   = lead,
            Name   = name,
            Note   = note,
            Items  = items,
            Extras = extras,
        };
    }

    /// <summary>
    /// The kept part that a tried part takes the place of (another mod's part on in the same socket), or null. In a layered
    /// socket the one on the tried part's own shelf first; else one of the other layer, which stays (the note says over or
    /// under).
    /// </summary>
    private LibPart? Replaced(PartKey tried)
    {
        LibPart? other = null;
        foreach (var mod in _wardrobe.ShowingIn(tried.Socket))
        {
            if (mod.Equals(tried.Mod, StringComparison.OrdinalIgnoreCase))
                continue;
            if (_byMod.TryGetValue(mod, out var parts) && parts.FirstOrDefault(p => p.Key.Socket == tried.Socket) is { } part)
            {
                if (!Layered(tried.Socket) || SameLayer(part.Key, tried))
                    return part;
                other ??= part;
            }
        }

        return other;
    }

    private string NameOf(PartKey key)
        => _byKey.TryGetValue(key, out var part) ? part.Name : _library.Mod(key.Mod)?.Name ?? key.Mod;

    private static bool SameSocket(Socket a, Socket b)
        => a == b && a != Socket.None;

    /// <summary>
    /// Layered sockets: one part per shelf is on at once, each showing its own files: in the body a body mod (Bodies) and
    /// skin textures (Skin) over it; in the face's textures the face's own (Face) and makeup (Makeup) over them. Two parts
    /// of one shelf still replace each other; elsewhere one mod wins the socket.
    /// </summary>
    private static bool Layered(Socket socket)
        => socket is Socket.BodyBase or Socket.FaceTextures;

    /// <summary> The upper layer of a layered socket (skin textures over a body, makeup over a face's textures). </summary>
    private static bool OverLayer(string shelf)
        => shelf is Pieces.Skin or Pieces.Makeup;

    /// <summary> The shelf a part is on (after the user's moves), placed or not. </summary>
    private string LayerOf(PartKey key)
        => _shelfOf.TryGetValue(key, out var shelf) ? shelf : PartOf(key) is { } part ? NaturalShelf(part) : Pieces.Other;

    /// <summary> Two parts take each other's place in a try: the same socket, and in a layered socket the same shelf too. </summary>
    private bool SameLayer(PartKey a, PartKey b)
        => SameSocket(a.Socket, b.Socket) && (!Layered(a.Socket) || LayerOf(a) == LayerOf(b));

    // ---- To sort ----

    private IReadOnlyList<SortItem> MakeToSort()
    {
        var parts = ShelfParts(Pieces.ToSort);
        if (parts.Count == 0)
            return Array.Empty<SortItem>();

        // "New shelf: Wings": the detection's proposals (several unsure parts sharing a shelf-less kind and a place)
        var proposals = new Dictionary<PartKey, string>();
        foreach (var proposal in Detect.ShelfProposals.Propose(parts.Select(p => p.Info), _data.Shelves.Select(s => s.Name)))
            foreach (var key in proposal.Parts)
                proposals.TryAdd(key, proposal.Name);

        // an item is made again only when its part, the language, the user's shelves or its proposal changed (a detection
        // batch changes a few of a thousand)
        var now     = _now();
        var loc     = Loc.Version;
        var shelves = ShelvesStamp();
        if (_sortItems.Count > 2 * parts.Count + 64)
        {
            var live = new HashSet<LibPart>(parts, ReferenceEqualityComparer.Instance);
            foreach (var gone in _sortItems.Keys.Where(k => !live.Contains(k)).ToList())
                _sortItems.Remove(gone);
        }

        // the order is kept while only a few parts come and go (as for the cards)
        _toSortOrdered = _toSortOrderStamp == _orderStamp && Reordered(_toSortOrdered, parts, OrderComparer(now)) is { } kept ? kept : Order(parts, now).ToList();
        _toSortOrderStamp = _orderStamp;
        return Things(_toSortOrdered).Select(p =>
        {
            var proposal = proposals.GetValueOrDefault(p.Key) ?? string.Empty;
            if (_sortItems.TryGetValue(p, out var made) && made.Loc == loc && made.Shelves == shelves && made.Proposal == proposal)
                return made.Item;
            var item = MakeSortItem(p, proposal);
            _sortItems[p] = (item, loc, shelves, proposal);
            return item;
        }).ToList();
    }

    /// <summary> Changes when the user's shelves change (names or which there are). </summary>
    private int ShelvesStamp()
    {
        var hash = new HashCode();
        foreach (var shelf in _data.Shelves)
        {
            hash.Add(shelf.Id);
            hash.Add(shelf.Name);
        }

        return hash.ToHashCode();
    }

    private SortItem MakeSortItem(LibPart p, string proposal)
    {
        {
            var guesses = new List<PieceDef>();
            foreach (var g in p.Info.Guesses.OrderByDescending(g => g.Score))
                if (IsHome(g.Piece) && Def(g.Piece) is { } def && guesses.All(d => d.Id != def.Id))
                    guesses.Add(def);
            // once the user has that shelf, a part of its kind gets it as a guess (the detection proposes only new ones)
            if (ShelfFor(p.Info.Kind) is { } mine)
                guesses.Insert(Math.Min(1, guesses.Count), UserDef(mine));
            else if (proposal.Length > 0)
                guesses.Insert(Math.Min(1, guesses.Count), new PieceDef(string.Empty, proposal, PieceGroup.Mine, "mine"));

            return new SortItem
            {
                Key       = p.Key,
                Name      = p.Name,
                Picture   = PictureOf(p),
                Icon      = ShelfIcon(IsHome(p.Info.Piece) ? p.Info.Piece : Pieces.Other),
                SlotText  = Witness(p.Info, EvidenceKind.Slot),
                SitsText  = Witness(p.Info, EvidenceKind.Body),
                WordsText = Witness(p.Info, EvidenceKind.Words),
                Covers    = p.Info.Covers,
                Guesses   = guesses.Take(2).ToList(),
            };
        }
    }

    private static string Witness(PartInfo info, EvidenceKind kind)
        => info.Evidence.Where(e => e.Kind == kind && e.Value.Length > 0).OrderByDescending(e => e.Weight).FirstOrDefault() is { } e
            ? Translate(e)
            : Loc.T("no hint");

    /// <summary> A witness phrase for people: fixed phrases through Loc.T, phrases with a word ("says “horns”") around the word. </summary>
    public static string Translate(Evidence e)
    {
        if (e.Word.Length == 0 || !e.Value.Contains(e.Word, StringComparison.Ordinal))
            return Loc.T(e.Value);

        // "a game headwear model": the word is one of the closet's own slot phrases, so it is translated too; words quoted
        // from mods ("says “horns”", "your folder “ears”") stay as the mod has them
        var word = e.Value.StartsWith("a game ", StringComparison.Ordinal) ? Loc.T(e.Word) : e.Word;
        return Loc.F(e.Value.Replace(e.Word, "{0}"), word);
    }

    /// <summary> The user's shelf for a shelf-less kind ("horns" → their "Horns"), by the detection's name for it. </summary>
    private UserShelf? ShelfFor(string kind)
        => Detect.ShelfProposals.Names.TryGetValue(kind, out var english)
            ? _data.Shelves.FirstOrDefault(s => s.Name.Equals(english, StringComparison.CurrentCultureIgnoreCase) || s.Name.Equals(Loc.T(english), StringComparison.CurrentCultureIgnoreCase))
            : null;

    private ScanStatus MakeScan()
    {
        var p = _library.Progress;
        var text = !p.Ready
            ? p.Mods == 0 ? Loc.T("Reading your mods…") : Loc.F("Reading your mods… {0} of {1}", SearchText.Number(p.Read), SearchText.Number(p.Mods))
            : p.Detecting
                ? Loc.F("Getting to know your mods… {0} of {1}", SearchText.Number(p.Detected), SearchText.Number(p.Mods))
                : string.Empty;
        return new ScanStatus
        {
            Ready   = p.Ready && p.Problem.Length == 0,
            Done    = p.Ready ? p.Detected : p.Read,
            Total   = p.Mods,
            Text    = text,
            Problem = p.Problem,
        };
    }

    // ======== IClosetSession: reading ========

    public ScanStatus Scan
    {
        get
        {
            Sync();
            var p = _library.Progress;
            if (p.Ready != _scan.Ready || p.Problem != _scan.Problem || (p.Ready ? p.Detected : p.Read) != _scan.Done || p.Mods != _scan.Total)
            {
                _scan = MakeScan();
                _version++;
            }

            return _scan;
        }
    }

    public IReadOnlyList<RailRow> Rail
    {
        get
        {
            Sync();
            return _rail;
        }
    }

    public IReadOnlyList<PieceDef> MoveTargets
    {
        get
        {
            Sync();
            return _moveTargets;
        }
    }

    private string _selected;

    public string Selected
        => _selected;

    public void Select(string piece)
    {
        if (piece == _selected || piece.Length == 0)
            return;
        _selected = piece;
        _kind     = string.Empty;

        // parts held for the last shelf go where they belong now (only their mods are placed again)
        var held = _pins.Keys.Concat(_waitPins).Select(k => k.Mod).ToList();
        _pins.Clear();
        _waitPins.Clear();
        _data.Selected = piece;
        _save();
        Changed(mods: held);
    }

    private string _search = string.Empty;

    public string Search
    {
        get => _search;
        set
        {
            value ??= string.Empty;
            if (value == _search)
                return;
            _search    = value;
            // in a large library, a search that took longer than half a frame (and the first one) waits for the typing to
            // pause; a small library always searches at once
            var slow = _byKey.Count >= LargeLibrary && (_lastSearchMs == 0 || _lastSearchMs > 8);
            _searchDue = value.Trim().Length > 0 && slow ? _now().AddSeconds(0.15) : null;
            Changed();
        }
    }

    public IReadOnlyList<KindChip> Kinds
    {
        get
        {
            Sync();
            return _kinds;
        }
    }

    private string _kind = string.Empty;

    public string Kind
    {
        get => _kind;
        set
        {
            value ??= string.Empty;
            if (value == _kind)
                return;
            _kind = value;
            Changed();
        }
    }

    public IReadOnlyList<Card> Cards
    {
        get
        {
            _cardsReadAt = _now();
            Sync();
            return _cards;
        }
    }

    // ---- one shelf for another face (On you) ----

    private readonly Dictionary<string, (int Order, int Shelf, List<LibPart> Ordered)> _shelfOrders = new();
    private (string Piece, int Version, IReadOnlyList<Card> Cards)? _cardsOf;

    /// <summary>
    /// The cards of one shelf in the closet's order, whatever the window has selected, searched or filtered. Its order is
    /// kept like the window's (patched while only a few parts come and go); the cards are made when asked for.
    /// </summary>
    public IReadOnlyList<Card> CardsOf(string piece)
    {
        Sync();
        if (_cardsOf is { } last && last.Piece == piece && last.Version == _version)
            return last.Cards;

        var now = _now();
        IReadOnlyList<Card> cards;
        if (piece == Pieces.Outfits)
        {
            cards = OutfitCards(now);
        }
        else
        {
            var parts = ShelfParts(piece);
            var stamp = ShelfStamp(piece);
            if (AsideShown(piece) is { Count: > 0 } aside)
            {
                // a thing whose copy is a mod of its own, on or tried on: ordered with the shelf each time (rare)
                cards = new LazyCards(Things(Order(parts.Concat(aside), now).ToList()), p => MakeCard(p, now));
                _cardsOf = (piece, _version, cards);
                return cards;
            }

            if (!_shelfOrders.TryGetValue(piece, out var kept) || kept.Order != _orderStamp || kept.Shelf != stamp)
            {
                var ordered = _shelfOrders.ContainsKey(piece) && kept.Order == _orderStamp && Reordered(kept.Ordered, parts, OrderComparer(now)) is { } patched
                    ? patched
                    : Order(parts, now).ToList();
                // a handful of shelves at most (On you flips through a few); the oldest goes when there are many
                if (_shelfOrders.Count > 8 && !_shelfOrders.ContainsKey(piece))
                    _shelfOrders.Remove(_shelfOrders.Keys.First());
                _shelfOrders[piece] = kept = (_orderStamp, stamp, ordered);
            }

            cards = new LazyCards(Things(kept.Ordered), p => MakeCard(p, now));
        }

        _cardsOf = (piece, _version, cards);
        return cards;
    }

    // ---- what shows in each gear slot (for the colours) ----

    /// <summary> What shows in one gear slot: the part (null for the game's own item), its shelf, and whether it is tried. </summary>
    public sealed record GearShowing(int Slot, PartKey? Key, string Piece, string PieceName, string Icon, string Name, string? Picture, bool Trying);

    /// <summary>
    /// Changes when what <see cref="ShowingNow"/> says may have changed on the session's side (a try, a part moved to
    /// another shelf). Cheap: read every frame. Kept outfit changes come from the wardrobe's version.
    /// </summary>
    public int ShowingStamp
    {
        get
        {
            var hash = new HashCode();
            hash.Add(_placeStamp);
            hash.Add(_library.Version);
            hash.Add(_sortStamp);
            hash.Add(Loc.Version);
            foreach (var t in _tries)
            {
                hash.Add(t.Key);
                hash.Add(t.Nothing);
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Per gear slot (Glamourer's order: 0 head … 9 left ring) what shows now: what is tried there first, else the mod
    /// that wins the slot in the kept outfit, else the game's own item (<paramref name="gameItem"/> names it; null for an
    /// empty slot). Works without a sync, so it costs nothing while the window is closed.
    /// </summary>
    public IReadOnlyList<GearShowing> ShowingNow(Func<int, string?> gameItem)
    {
        var list = new List<GearShowing>();
        for (var slot = 0; slot < Game.GearSlots.Worn.Length; ++slot)
        {
            var gear   = Game.GearSlots.Worn[slot];
            var socket = gear.Socket;
            LibPart? part = null;
            var trying = false;
            // a ring tried on one finger shows on that finger only
            var finger = socket != Socket.Rings ? Hands.Both : gear.Id == Wear.WearRules.LeftRingId ? Hands.Left : Hands.Right;
            if (_tries.LastOrDefault(t => SameSocket(t.Key.Socket, socket) && (socket != Socket.Rings || HasHand(t.Hand, finger))) is { } tried)
            {
                trying = true;
                part   = tried.Nothing ? null : PartOf(tried.Key);
            }
            else
            {
                foreach (var mod in _wardrobe.ShowingIn(socket))
                    if (PartOf(new PartKey(mod, socket)) is { } kept)
                    {
                        part = kept;
                        break;
                    }
            }

            // a ring goes on the hands its item goes on (the right one when nothing says); a kept one is where the wardrobe
            // says it is, when it can tell
            if (part is not null && socket == Socket.Rings && !trying)
            {
                if (_wardrobe.RingOn(finger) is { } ringMod)
                    part = PartOf(new PartKey(ringMod, Socket.Rings));
                else if (!OnHand(part, gear.Id, slot))
                    part = null;
            }
            else if (part is not null && socket == Socket.Rings && !OnHand(part, gear.Id, slot) && HandOfTry(part.Key) == Hands.Both)
            {
                part = null;
            }

            if (part is not null)
            {
                var shelf = NaturalShelf(part);
                list.Add(new GearShowing(slot, part.Key, shelf, ShelfName(shelf), ShelfIcon(shelf), part.Name, PictureOf(part), trying));
            }
            else if (gameItem(slot) is { Length: > 0 } item)
            {
                var shelf = PartBuilder.ShelfOf(socket);
                list.Add(new GearShowing(slot, null, shelf, ShelfName(shelf), ShelfIcon(shelf), item, null, trying));
            }
        }

        return list;
    }

    /// <summary> The shelf a part belongs on after the user's moves (no sync; never a shelf held under the mouse). </summary>
    public string ShelfOfPart(LibPart part)
        => NaturalShelf(part);

    /// <summary> A shelf's name, translated. </summary>
    public string NameOfShelf(string shelf)
        => ShelfName(shelf);

    /// <summary> A shelf's icon key. </summary>
    public string IconOfShelf(string shelf)
        => ShelfIcon(shelf);

    /// <summary> The parts on a shelf as last placed (no sync). </summary>
    public IReadOnlyList<LibPart> PartsOnShelf(string shelf)
        => ShelfParts(shelf);

    /// <summary>
    /// The library's part as it is now (not the session's last placing, which can be minutes old with no face open during a
    /// scan: a part the detection replaced or dropped must not show as worn). A few parts per mod: cheap.
    /// </summary>
    private LibPart? PartOf(PartKey key)
    {
        foreach (var part in _library.PartsOf(key.Mod))
            if (part.Key == key)
                return part;
        return null;
    }

    /// <summary> The finger a part is tried on (<see cref="Hands.Both"/> when it is not tried, or not on one finger). </summary>
    private Hands HandOfTry(PartKey key)
        => _tries.FirstOrDefault(t => !t.Nothing && t.Key == key)?.Hand ?? Hands.Both;

    private static bool OnHand(LibPart ring, byte slotId, int slot)
        => ring.SlotIds.Count > 0 ? ring.SlotIds.Contains(slotId) : slot == 8;

    public WearingInfo? Wearing
    {
        get
        {
            Sync();
            return _wearing;
        }
    }

    /// <summary> Every kept part on in the selected shelf, <see cref="Wearing"/> first (a top, and a jacket in the wrist slot). </summary>
    public IReadOnlyList<WearingInfo> WearingAll
    {
        get
        {
            Sync();
            return _wearingAll;
        }
    }

    public TryState Trying
    {
        get
        {
            Sync();
            if (_trying.Busy != _wardrobe.Busy && (_trying.Active || _wardrobe.Busy))
            {
                _trying = MakeTrying();
                _version++;
            }

            return _trying;
        }
    }

    public IReadOnlyList<SortItem> ToSort
    {
        get
        {
            // the To sort shelf shows this list instead of the cards: reading it counts as looking at the shelf
            if (_selected == Pieces.ToSort)
                _cardsReadAt = _now();
            Sync();
            var stamps = (ShelfStamp(Pieces.ToSort), _orderStamp, Loc.Version, ShelvesStamp());
            if (_toSortDirty && stamps != _toSortFor)
            {
                _toSort    = MakeToSort();
                _toSortFor = stamps;
            }

            _toSortDirty = false;

            return _toSort;
        }
    }

    public MoveSuggestion? Suggestion
    {
        get
        {
            Sync();
            return _suggestion?.Shown;
        }
    }

    // ======== trying on ========

    private sealed class TryEntry
    {
        public PartKey Key { get; init; }
        public bool Nothing { get; init; }
        public Dictionary<string, List<string>>? Options { get; set; }
        public int Version { get; set; }

        /// <summary> A ring: the finger it is tried on (or taken off); <see cref="Hands.Both"/> for anything else. </summary>
        public Hands Hand { get; set; }
    }

    private readonly List<TryEntry> _tries = new();

    /// <summary> A try was just sent to the wardrobe: the next sync must not take "not previewing" for a failure yet. </summary>
    private bool _tryPending;

    public void Try(PartKey part)
    {
        Sync();
        if (part.Socket == Socket.None)
        {
            // an Outfits card: every thing of the mod once, each through the slot a click on its card would take
            if (!_outfits.TryGetValue(part.Mod, out var set))
                return;
            var added = false;
            foreach (var p in set)
            {
                var thing = ThingOf(p.Key);
                if (thing.Count == 0 || thing.Any(q => _tries.Any(t => !t.Nothing && t.Key == q.Key)))
                    continue;
                Replace(ClickPart(thing).Key);
                added = true;
            }

            if (added)
                PreviewTries();
            return;
        }

        if (!_byKey.ContainsKey(part))
            return;

        // a thing its mod offers through several slots: the slot the rule picks now (brief 9)
        if (ThingOf(part) is { Count: > 1 } several)
            part = ClickPart(several).Key;

        // clicking the card being tried, or the card that is on, does nothing (SPEC "Trying on"); the face takes a tried
        // card out with StopTrying. A ring tried on one hand and clicked on the other hand's shelf moves to that hand.
        var hand = _byKey.TryGetValue(part, out var clicked) ? TryHand(clicked) : Hands.Both;
        if (_tries.FirstOrDefault(t => !t.Nothing && t.Key == part) is { } already)
        {
            if (part.Socket == Socket.Rings && already.Hand != hand)
            {
                _tries.RemoveAll(t => t != already && t.Key.Socket == Socket.Rings && HasHand(t.Hand, hand));
                already.Hand = hand;
                PreviewTries();
            }

            return;
        }

        if (part.Socket == Socket.Rings ? clicked is not null && OnFinger(clicked, hand) : _on.Contains(part))
        {
            // the kept part while something else is tried in its place: back to the kept one
            if (_tries.RemoveAll(t => SameSocket(t.Key.Socket, part.Socket)) > 0)
                PreviewTries();
            return;
        }

        Replace(part);
        PreviewTries();

        // a gear part Penumbra named no item for: its mod is on now, but nothing is put on that would show it
        if (_byKey.TryGetValue(part, out var lib) && GearIsSocket(part.Socket) && lib.Versions.Count == 0)
            SetStatus(Loc.F("{0} is on for the moment, but the closet does not know which item shows it.", lib.Name), false);
    }

    private static bool GearIsSocket(Socket socket)
        => socket is >= Socket.Head and <= Socket.OffHand;

    /// <summary> Puts a part in the try list, swapping out whatever was tried in the same socket. </summary>
    private void Replace(PartKey part)
    {
        // what is tried in its slot, and the same thing tried through another slot, make room
        var lead = LeadOf(part);
        // a ring makes room on its own finger only: the other hand keeps what is tried there
        var hand = part.Socket == Socket.Rings && _byKey.TryGetValue(part, out var ring) ? TryHand(ring) : Hands.Both;
        _tries.RemoveAll(t => SameLayer(t.Key, part) && (part.Socket != Socket.Rings || HasHand(t.Hand, hand))
         || !t.Nothing && t.Key != part && LeadOf(t.Key) == lead);
        _tries.Add(new TryEntry { Key = part, Version = _data.Versions.GetValueOrDefault(part.ToString()), Hand = hand });
        UseOf(part).TriedUtc = _now();
    }

    private PartUse UseOf(PartKey part)
    {
        var key = part.ToString();
        if (!_data.Use.TryGetValue(key, out var use))
            _data.Use[key] = use = new PartUse();
        return use;
    }

    public void StopTrying(PartKey part)
    {
        Sync();
        var lead    = LeadOf(part);
        var removed = part.Socket == Socket.None
            ? _tries.RemoveAll(t => t.Key.Mod.Equals(part.Mod, StringComparison.OrdinalIgnoreCase))
            : _tries.RemoveAll(t => t.Key == part || !t.Nothing && LeadOf(t.Key) == lead);
        if (removed == 0)
            return;
        PreviewTries();
    }

    public void TryNothing(string piece)
    {
        Sync();

        // The body and its skin are not taken off with one click: that is the body mod (Rue, Bibo…) that every outfit is
        // made for, and it would go for every character using the collection.
        if (piece is Pieces.Skin or Pieces.Bodies)
        {
            SetStatus(Loc.T("The body cannot be taken off here. Turn it off in Penumbra if you mean to."), false);
            return;
        }

        var parts = ShelfParts(piece);
        // a ring shelf: what is on its finger only (the other hand keeps its ring)
        var hand = HandOfShelf(piece);
        var on = parts.Where(p => (p.Key.Socket == Socket.Rings ? OnFinger(p, hand) : _on.Contains(p.Key)) && p.Key.Socket != Socket.BodyBase).ToList();
        if (on.Count == 0)
        {
            SetStatus(Loc.T("Nothing is on here."), false);
            return;
        }

        // a part that is not gear, whose mod is also worn for another part, stays: Penumbra turns a mod on or off as a whole
        var stays = on.Where(p => WornWith(p.Key) is not null).ToList();
        if (stays.Count > 0)
        {
            on.RemoveAll(p => stays.Contains(p));
            var first = stays[0];
            SetStatus(Loc.F("{0} stays on: its mod is also worn on {1}, and Penumbra turns a mod on or off only as a whole.", first.Name,
                ShelfName(WornWith(first.Key)!)), false);
            if (on.Count == 0)
                return;
        }

        foreach (var p in on)
        {
            var finger = p.Key.Socket == Socket.Rings ? hand : Hands.Both;
            _tries.RemoveAll(t => SameLayer(t.Key, p.Key) && (p.Key.Socket != Socket.Rings || HasHand(t.Hand, finger)));
            _tries.Add(new TryEntry { Key = p.Key, Nothing = true, Hand = finger });
        }

        PreviewTries();
    }

    /// <summary> Sends the whole try list to the wardrobe (it replaces what was tried before). </summary>
    private void PreviewTries()
    {
        if (_tries.Count == 0)
            _wardrobe.PutBack();
        else
            _wardrobe.Preview(TryRequests());
        _tryPending = true;
        // no settings save for a try (that was a write of the whole settings file within 2 s of every click): when a part
        // was tried is saved with the next Keep, move or window change
        Changed();
    }

    private static TryRequest Request(TryEntry t)
        => new(t.Key, t.Nothing, t.Options, t.Version, t.Hand);

    /// <summary>
    /// What a try or a Keep sends to the wardrobe: the tries, and for each tried thing a take-off of every other slot of it,
    /// in the same step. A thing is never on through two slots: in the try it shows through the tried slot only, Put back
    /// brings the old slot back, and Keep takes the old slot off in the same step (one Undo takes it all back).
    /// - Every other slot is asked for, on or not as far as the session knows: what is on can still change under a step
    ///   that has not run (a move, a Keep). The wardrobe takes off only what of the mod shows there when the step runs, and
    ///   only the places its own item is in (<see cref="ModCloset2.Wear.WearRules.SlotsToEmpty"/>).
    /// - A slot another tried part is put in is left to that part, except the ring slot: its two fingers are two places, and
    ///   a ring tried on one hand does not take the thing off the other (in a try the tried ring keeps its finger).
    /// </summary>
    private List<TryRequest> TryRequests(bool keep = false)
    {
        // a mod with nothing saved is put on with the closet's pick; body and race choices made by hand go over it
        // (and a hair, face, tail or ear part is put on with the choice that replaces the number it shows with)
        var list = _tries.Select(t => Request(t) with { Options = RequestOptions(t) }).ToList();

        // layered sockets: what goes on is put on in layer order (a body before skin textures, a face's textures before
        // makeup): later is higher in Penumbra. A body tried must not end up above the skin the user wears (when no other
        // skin is tried with it): in a try that skin goes on again after the body (temporary settings, gone after the
        // try); at Keep the body carries it as Under, and the wardrobe puts the body under it without keeping the skin
        var lower = list.Where(r => !r.Nothing && Layered(r.Key.Socket) && !OverLayer(LayerOf(r.Key))).ToList();
        if (lower.Count > 0)
        {
            var overTried = list.Where(r => !r.Nothing && Layered(r.Key.Socket) && OverLayer(LayerOf(r.Key))).Select(r => r.Key.Socket).ToHashSet();
            foreach (var r in lower.Where(r => !overTried.Contains(r.Key.Socket)))
            {
                var overs = _on.Where(k => k.Socket == r.Key.Socket && OverLayer(LayerOf(k)) && list.All(q => q.Key != k))
                    .OrderBy(k => k.Mod, StringComparer.OrdinalIgnoreCase).ToList();
                if (overs.Count == 0)
                    continue;
                if (keep)
                    list[list.IndexOf(r)] = r with { Under = overs.Select(k => k.Mod).ToList() };
                else
                    list.AddRange(overs.Select(k => new TryRequest(k)));
            }

            list = list.OrderBy(r => !r.Nothing && Layered(r.Key.Socket) && OverLayer(LayerOf(r.Key)) ? 1 : 0).ToList();
        }
        foreach (var t in _tries)
        {
            if (t.Nothing)
                continue;
            foreach (var p in ThingOf(t.Key))
                if (p.Key != t.Key && !list.Any(r => r.Key == p.Key) && (p.Key.Socket == Socket.Rings || !_tries.Any(o => !o.Nothing && SameSocket(o.Key.Socket, p.Key.Socket))))
                    list.Add(new TryRequest(p.Key, true));
        }

        return list;
    }

    /// <param name="on">
    /// The choices to have on; null for a body or race group: "automatic", the closet's own pick for the player's body and
    /// race again (the hand choice is forgotten).
    /// </param>
    public void SetOption(PartKey part, string group, IReadOnlyList<string>? on)
    {
        Sync();
        if (on is null || _byKey.TryGetValue(part, out var lib) && FitGroup(lib.Mod, group) is not null)
        {
            if (!_byKey.TryGetValue(part, out var fitted) || FitGroup(fitted.Mod, group) is null)
                return;
            RememberFit(fitted, group, on);
            on ??= NumberPicks(fitted).TryGetValue(group, out var number) ? number
                : _wardrobe.FittedOptions(part.Mod).TryGetValue(group, out var pick) ? pick : FitGroup(fitted.Mod, group)!.DefaultChoice();
        }

        if (_tries.FirstOrDefault(t => !t.Nothing && t.Key.Mod.Equals(part.Mod, StringComparison.OrdinalIgnoreCase)) is { } tried)
        {
            // the mod is being tried: the option changes the try (every tried part of the mod shares the options)
            var options = Copy(RequestOptions(tried) ?? OptionsNow(part.Mod));
            options[group] = on.ToList();
            foreach (var t in _tries.Where(t => !t.Nothing && t.Key.Mod.Equals(part.Mod, StringComparison.OrdinalIgnoreCase)))
                t.Options = options;
            PreviewTries();
            return;
        }

        if (!_on.Contains(part))
        {
            // a hand choice for a part that is neither on nor tried is remembered for its next try; the lists show it
            Changed();
            return;
        }

        var kept = Copy(OptionsNow(part.Mod));
        kept[group] = on.ToList();
        var step = Guid.NewGuid();
        _wardrobe.SetOptions(step, Loc.F("Options of {0}", NameOf(part)), part, kept);
        PushUndo(new UndoEntry { Step = step, What = NameOf(part), OutfitStep = true });
        _steps++;
        Changed();
    }

    private static Dictionary<string, List<string>> Copy(IReadOnlyDictionary<string, List<string>> options)
        => options.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());

    // ======== options: the mod's own, and the body and race choices the closet makes ========

    /// <summary> A body or race group of the mod (one the closet picks to fit the player), or null. </summary>
    private static GroupInfo? FitGroup(LibMod mod, string group)
        => mod.Groups.FirstOrDefault(g => g.Name == group && IsFit(g));

    /// <summary> A group the closet picks for the player: body, race, or which hair, face, tail or ear number the mod replaces. </summary>
    private static bool IsFit(GroupInfo group)
        => group.IsBody || group.IsRace || NumberSocket(group) is not null;

    private static readonly Socket[] NumberSockets = { Socket.Hair, Socket.Face, Socket.Tail, Socket.EarShape };

    /// <summary>
    /// A single-choice group whose choices replace different hair, face, tail or ear numbers ("Face 1 / Face 2 / Face 3"):
    /// the socket whose number it picks, else null.
    /// </summary>
    private static Socket? NumberSocket(GroupInfo group)
    {
        if (group.Multi || group.Options.Count < 2)
            return null;
        foreach (var socket in NumberSockets)
        {
            var sets = group.Options.Select(o => o.Numbers.TryGetValue(socket, out var n) ? n : null).Where(n => n is { Count: > 0 }).ToList();
            if (sets.Count >= 2 && sets.Select(n => string.Join(",", n!.Order())).Distinct().Count() >= 2)
                return socket;
        }

        return null;
    }

    /// <summary> The number socket of a part (makeup counts for the face it is on). </summary>
    private static Socket? NumberSocketOf(Socket socket)
        => socket switch
        {
            Socket.Hair or Socket.Face or Socket.Tail or Socket.EarShape => socket,
            Socket.FaceTextures                                         => Socket.Face,
            _                                                           => null,
        };

    /// <summary>
    /// The number a part shows with once tried: the character's own when the mod replaces it (the try keeps it), else the
    /// mod's first for the player (the try sets it): as the wardrobe's <see cref="PartBuilder.PickCustomize"/>.
    /// </summary>
    private int? NumberTarget(LibPart part, Socket socket)
    {
        if (Game.GearSlots.CustomizeKey(socket) is not { } key)
            return null;
        var now  = _wardrobe.CustomizeNow(key);
        var mine = part.CustomizeChoices.TryGetValue(key, out var all) ? all : null;
        if (now is { } n && (mine is null || mine.Contains(n) || !part.Customize.ContainsKey(key)))
            return n;
        return part.Customize.TryGetValue(key, out var first) ? first : now;
    }

    /// <summary>
    /// For each group of the part's mod that picks the part's number and has no hand choice: the choice that replaces the
    /// number the part will show with (the face the character wears, say), when there is one. Empty: nothing to pick.
    /// </summary>
    private Dictionary<string, List<string>> NumberPicks(LibPart part)
    {
        var picks = new Dictionary<string, List<string>>();
        if (NumberSocketOf(part.Key.Socket) is not { } socket || NumberTarget(part, socket) is not { } target)
            return picks;
        var hand = HandChoices(part.Mod.Directory);
        foreach (var group in part.Mod.Groups)
            if (NumberSocket(group) == socket && !hand.ContainsKey(group.Name)
             && group.Options.FirstOrDefault(o => o.Numbers.TryGetValue(socket, out var n) && n.Contains(target)) is { } fit)
                picks[group.Name] = new List<string> { fit.Name };
        return picks;
    }

    /// <summary> The options a try is sent with: its own, else the hand choices over what is saved, with the number picks over both. </summary>
    private IReadOnlyDictionary<string, List<string>>? RequestOptions(TryEntry t)
    {
        if (t.Nothing)
            return t.Options;
        var options = t.Options ?? (IReadOnlyDictionary<string, List<string>>?)HandOver(t.Key.Mod);
        if (!_byKey.TryGetValue(t.Key, out var part) || NumberPicks(part) is not { Count: > 0 } picks)
            return options;
        var result = Copy(options ?? _wardrobe.CurrentOptions(t.Key.Mod));
        foreach (var (group, on) in picks)
            result[group] = on;
        return result;
    }

    /// <summary>
    /// A part picked by a number kept with options that replace another number than the one it shows with: it is on, but
    /// nothing of it shows (a sculpt for face 2 on a character with face 3).
    /// </summary>
    private bool ShowsWith(LibPart part, IReadOnlyDictionary<string, List<string>> options)
    {
        if (NumberSocketOf(part.Key.Socket) is not { } socket || NumberTarget(part, socket) is not { } target)
            return true;
        foreach (var group in part.Mod.Groups)
            if (NumberSocket(group) == socket && options.TryGetValue(group.Name, out var on)
             && !group.Options.Where(o => on.Contains(o.Name)).Any(o => o.Numbers.TryGetValue(socket, out var n) && n.Contains(target)))
                return false;
        return true;
    }

    /// <summary> A group worth offering: two choices or more, or several at once. </summary>
    private static bool Offerable(GroupInfo group)
        => group.Name.Length > 0 && (group.Options.Count >= 2 || group.Multi);

    /// <summary>
    /// The option groups the wearing strip offers for a part. A body mod (its body part): every group, body and race
    /// groups included, in the mod's order: they are its main choices (sizes, body type, variants per race). Anything
    /// else: the part's own groups first, then the body and race groups the closet picks to fit the player (marked so).
    /// </summary>
    private static List<GroupInfo> OfferedGroups(LibPart part)
    {
        var groups = part.Mod.Groups;
        if (part.Key.Socket == Socket.BodyBase)
            return groups.Where(Offerable).ToList();

        var own = new List<GroupInfo>();
        foreach (var name in part.Info.Groups)
            if (groups.FirstOrDefault(g => g.Name == name) is { } group && !IsFit(group) && Offerable(group) && !own.Contains(group))
                own.Add(group);
        foreach (var group in groups)
            if (IsFit(group) && Offerable(group) && !own.Contains(group) && Touches(group, part))
                own.Add(group);
        return own;
    }

    /// <summary> A group changes this part: the part's groups name it, or its files are for the part's socket. </summary>
    private static bool Touches(GroupInfo group, LibPart part)
        => part.Info.Groups.Contains(group.Name) || group.Sockets.Any(s => s is not (Socket.Other or Socket.None) && Game.GamePaths.SameFamily(s, part.Key.Socket));

    /// <summary>
    /// A body or race group of an offered option: <c>Fits</c> (the closet picks it to fit the player's body and race),
    /// and <c>Automatic</c> (no hand choice is remembered: the closet's pick is used); the same as the strip's
    /// <see cref="OptionChip.Fits"/> and <see cref="OptionChip.Automatic"/>, for any group asked about.
    /// </summary>
    public (bool Fits, bool Automatic) OptionState(PartKey part, string group)
    {
        Sync();
        if (!_byKey.TryGetValue(part, out var lib) || FitGroup(lib.Mod, group) is null)
            return (false, false);
        return (true, !HandChoices(lib.Mod.Directory).ContainsKey(group));
    }

    /// <summary> The body and race choices made by hand for a mod (over all its parts), group → choice. </summary>
    private Dictionary<string, List<string>> HandChoices(string mod)
    {
        var result = new Dictionary<string, List<string>>();
        if (_data.FitChoices.Count == 0 || !_byMod.TryGetValue(mod, out var parts))
            return result;
        foreach (var part in parts)
            if (_data.FitChoices.TryGetValue(part.KeyText, out var choices))
                foreach (var (group, on) in choices)
                    if (FitGroup(part.Mod, group) is { } g && on.All(c => g.Options.Any(o => o.Name == c)))
                        result[group] = on;
        return result;
    }

    /// <summary> Remembers a hand choice for a body or race group (on the part), or forgets it everywhere in the mod (null). </summary>
    private void RememberFit(LibPart part, string group, IReadOnlyList<string>? on)
    {
        if (_byMod.TryGetValue(part.Mod.Directory, out var parts))
            foreach (var p in parts)
                if (_data.FitChoices.TryGetValue(p.KeyText, out var choices) && choices.Remove(group) && choices.Count == 0)
                    _data.FitChoices.Remove(p.KeyText);
        if (on is not null)
        {
            if (!_data.FitChoices.TryGetValue(part.KeyText, out var mine))
                _data.FitChoices[part.KeyText] = mine = new Dictionary<string, List<string>>();
            mine[group] = on.ToList();
        }

        _save();
    }

    /// <summary>
    /// The options a mod shows with now: a try's own, else what is saved, else the closet's pick with the hand choices over
    /// it (a body or race choice made by hand wins over the closet's pick until "automatic").
    /// </summary>
    private IReadOnlyDictionary<string, List<string>> OptionsNow(string mod)
    {
        if (_tries.FirstOrDefault(t => !t.Nothing && t.Options is not null && t.Key.Mod.Equals(mod, StringComparison.OrdinalIgnoreCase)) is { Options: { } tried })
            return tried;
        return HandOver(mod) ?? _wardrobe.CurrentOptions(mod);
    }

    /// <summary> The closet's pick with the hand choices over it, when the mod has nothing saved and hand choices exist; else null. </summary>
    private Dictionary<string, List<string>>? HandOver(string mod)
    {
        var hand = HandChoices(mod);
        if (hand.Count == 0 || _wardrobe.OptionsSaved(mod))
            return null;
        var options = Copy(_wardrobe.CurrentOptions(mod));
        foreach (var (group, on) in hand)
            options[group] = on.ToList();
        return options;
    }

    public void SetVersion(PartKey part, int index)
    {
        Sync();
        if (!_byKey.TryGetValue(part, out var lib) || index < 0 || index >= lib.VersionCount)
            return;
        _data.Versions[part.ToString()] = index;
        _save();

        if (_tries.FirstOrDefault(t => !t.Nothing && t.Key == part) is { } tried)
        {
            tried.Version = index;
            PreviewTries();
            return;
        }

        if (!_on.Contains(part))
            return;

        // putting another item on for good ends any try (its saved look would put the old item back)
        var step = Guid.NewGuid();
        _resolving = (step, _tries.ToList());
        _tries.Clear();
        _wardrobe.SetVersion(step, Loc.F("Version of {0}", lib.Name), part, index);
        PushUndo(new UndoEntry { Step = step, What = lib.Name, OutfitStep = true });
        _steps++;
        Changed();
    }

    public void Keep()
    {
        Sync();
        // a "Wear on" waiting for a move is not made after a Keep (it would try or move something after it)
        _nextMove = null;
        if (_tries.Count == 0)
        {
            // a look's other colourway with the same pieces: only colours were tried
            if (_colourTrying)
                KeepColours();
            return;
        }

        var now  = _now();
        var name = _trying.Name.Length > 0 ? _trying.Name : NameOf(_tries[0].Key);
        var step = Guid.NewGuid();
        var requests = TryRequests(keep: true);
        _wardrobe.Keep(step, Loc.F("Keep {0}", name), requests);
        _resolving = (step, _tries.ToList());
        // a hair, face, tail or ear part kept with options for another number than the one it shows with: it is on, and
        // stays on, but nothing of it shows; the status line says so instead of "Kept"
        var unseen = requests.Where(r => !r.Nothing && _byKey.TryGetValue(r.Key, out var kp)
            && !ShowsWith(kp, r.Options ?? _wardrobe.CurrentOptions(r.Key.Mod))).Select(r => NameOf(r.Key)).FirstOrDefault();
        foreach (var t in _tries.Where(t => !t.Nothing))
            Usage.Kept(UseOf(t.Key), now);
        _orderStamp++;

        // a look's colours are kept with its pieces, in the same step: one Undo takes both back. Whether the Keep worked is
        // known when the wardrobe has run its step: then it counts (LastKept, Steps); if it failed, the try is whole again
        var colours = EndColourTry(true);
        PushUndo(new UndoEntry { Step = step, What = name, OutfitStep = true, Restore = RestoreColours(colours) });
        _keeping.Add(new PendingKeep(step, _lookTry, colours));
        _lookTry = null;
        SetStatus(unseen is not null ? Loc.F("{0} is on, but it does not show on your character with the options chosen: pick another option above.", unseen)
            : _tries.All(t => t.Nothing) ? Loc.F("Took off {0}.", name) : Loc.F("Kept {0}.", name), false);
        _tries.Clear();
        _save();
        Changed();
    }

    /// <summary> Keep of a try that is only colours (another colourway of a look whose pieces are on). </summary>
    private void KeepColours()
    {
        var name    = _trying.Name;
        var colours = EndColourTry(true);
        if (colours is not null)
            PushUndo(new UndoEntry { Restore = RestoreColours(colours), EndsColourTry = true, OutfitStep = true, What = name });
        KeptLook();
        SetStatus(Loc.F("Kept {0}.", name), false);
        Changed();
    }

    private Action? RestoreColours((string Before, string After)? pair)
        => pair is { } p && ColourTry is { } colours ? () => colours.Restore(p.Before) : null;

    /// <summary> A Keep the wardrobe has not run yet: the look it was, and the colours it kept with it. </summary>
    private sealed record PendingKeep(Guid Step, (Guid Look, int Colourway, string Name, string Note)? Look, (string Before, string After)? Colours);

    private readonly List<PendingKeep> _keeping = new();

    /// <summary>
    /// Keeps whose step has run: one that worked counts (a look's becomes <see cref="LastKept"/>, and it is a step of the
    /// outfit); one that failed leaves the user exactly where they were before pressing Keep: the pieces back on the try
    /// bar (above), the look's colours put back and shown as a try again, the look's try with its name.
    /// </summary>
    private void ResolveKeeps()
    {
        for (var i = 0; i < _keeping.Count; ++i)
        {
            var keep  = _keeping[i];
            var state = _wardrobe.Step(keep.Step);
            if (state == StepState.Pending)
                continue;
            _keeping.RemoveAt(i--);
            if (state == StepState.Done)
            {
                if (keep.Look is { } look)
                    _lastKept = new LookKept(look.Look, look.Colourway, (_lastKept?.Serial ?? 0) + 1);
                _steps++;
                continue;
            }

            if (keep.Colours is { } colours && ColourTry is { } colourTry)
            {
                try
                {
                    colourTry.Restore(colours.Before);
                    colourTry.TryOn(colours.After);
                    _colourTrying = true;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"giving a failed Keep's colours back to the try failed: {ex}");
                }
            }

            if (keep.Look is { } tried && (_tries.Count > 0 || _colourTrying))
                _lookTry = tried;
            _viewDirty = true;
        }
    }

    /// <summary> After a Keep: a look's try becomes <see cref="LastKept"/>; it counts as a step of the outfit. </summary>
    private void KeptLook()
    {
        if (_lookTry is { } look)
            _lastKept = new LookKept(look.Look, look.Colourway, (_lastKept?.Serial ?? 0) + 1);
        _lookTry = null;
        _steps++;
    }

    public void PutBack()
    {
        Sync();
        // a "Wear on" waiting for a move is not made after the user put everything back
        _nextMove = null;
        EndColourTry(false);
        _lookTry = null;
        _tries.Clear();
        _wardrobe.PutBack();
        Changed();
    }

    /// <summary>
    /// Ends a colour try: kept (the pair for the undo step) or dropped (as before). Null when none was on. Every caller is
    /// a user's act (Put back, Keep, trying a look, Undo) but the one in the frame that finds the wardrobe no longer
    /// previewing, which passes <paramref name="byUser"/> false.
    /// </summary>
    private (string Before, string After)? EndColourTry(bool keep, bool byUser = true)
    {
        if (!_colourTrying)
            return null;
        _colourTrying = false;
        try
        {
            return ColourTry?.EndTry(keep, byUser);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"ending a colour try failed: {ex}");
            return null;
        }
    }

    // ======== the looks' side (ILooksOutfit) ========

    /// <summary> The look the current try started as, with its bar's name and note; kept while the user swaps pieces. </summary>
    private (Guid Look, int Colourway, string Name, string Note)? _lookTry;

    /// <summary> A look's colours are being shown with the try (the colour engine's try layer). </summary>
    private bool _colourTrying;
    private LookKept? _lastKept;
    private int _steps;
    private int _outfitVersion;
    private (int Wardrobe, int Library, int Place, int Sort) _keptFor = (int.MinValue, 0, 0, 0);
    private (int Steps, bool Trying, Guid? Look, int Kept) _outfitSeen;
    private IReadOnlyList<WornPart> _kept = Array.Empty<WornPart>();

    int ILooksOutfit.Version
    {
        get
        {
            RefreshOutfit();
            return _outfitVersion;
        }
    }

    /// <summary> Every kept part that is on, one per socket (the one whose files win it). A new list only when it changed. </summary>
    public IReadOnlyList<WornPart> Kept
    {
        get
        {
            RefreshOutfit();
            return _kept;
        }
    }

    /// <summary> +1 for every Keep, take-off, option or version change of a kept part, and the Undo of one. Never a try. </summary>
    public int Steps
        => _steps;

    bool ILooksOutfit.Trying
        => _tries.Count > 0 || _colourTrying;

    public Guid? TryingLook
        => _lookTry?.Look;

    public LookKept? LastKept
        => _lastKept;

    /// <summary> A sentence of the looks (what a save did) on the status line, as the closet's own. </summary>
    public void Say(string text)
        => SetStatus(text, false);

    /// <summary> Works without a sync (the looks follow the outfit with no face open). </summary>
    private void RefreshOutfit()
    {
        // the library's version too: with no face open the session does not place, but the parts are the library's own
        var key = (_wardrobe.Version, _library.Version, _placeStamp, _sortStamp);
        if (key != _keptFor)
        {
            _keptFor = key;
            var now = KeptNow();
            if (!SameKept(now, _kept))
            {
                _kept = now;
                _outfitVersion++;
            }
        }

        var seen = (_steps, _tries.Count > 0 || _colourTrying, _lookTry?.Look, _lastKept?.Serial ?? 0);
        if (seen != _outfitSeen)
        {
            _outfitSeen = seen;
            _outfitVersion++;
        }
    }

    private List<WornPart> KeptNow()
    {
        var list = new List<WornPart>();
        foreach (var socket in Enum.GetValues<Socket>())
        {
            if (socket is Socket.None or Socket.Other or Socket.MainHand or Socket.OffHand)
                continue;

            // a body mod's model in the body or legs slot (nothing worn there) is no piece of that slot: only a part of its
            // own socket counts, and a body mod has none there. Where several mods are on together (a body and skin, several
            // face paints), every one of them; elsewhere the one that shows.
            var layered = Layered(socket);
            var found   = false;
            var shelves = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mod in _wardrobe.ShowingIn(socket))
                if (PartOf(new PartKey(mod, socket)) is { FitsMe: true } part && !StrayOfCopy(part))
                {
                    // the one that shows first wins its shelf (in a layered socket each shelf has its own)
                    var shelf = NaturalShelf(part);
                    if (layered && !shelves.Add(shelf))
                        continue;
                    list.Add(new WornPart(part.Key, shelf, Copy(_wardrobe.CurrentOptions(mod)), _wardrobe.VersionShowing(part.Key)));
                    found = true;
                    if (!layered)
                        break;
                }

            // a body mod whose files cannot show now (dressed) is still on: the first one that is, on each shelf still free
            if ((layered || !found) && socket == Socket.BodyBase)
                foreach (var key in _notWorn.Keys.Where(k => k.Socket == Socket.BodyBase).OrderBy(k => k.Mod, StringComparer.OrdinalIgnoreCase))
                    if (list.All(w => w.Key != key) && _wardrobe.IsOn(key) && PartOf(key) is { FitsMe: true } body && shelves.Add(NaturalShelf(body)))
                        list.Add(new WornPart(body.Key, NaturalShelf(body), Copy(_wardrobe.CurrentOptions(body.Mod.Directory)), -1));
        }

        return list;
    }

    private static bool SameKept(IReadOnlyList<WornPart> a, IReadOnlyList<WornPart> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; ++i)
        {
            var (x, y) = (a[i], b[i]);
            if (x.Key != y.Key || x.Shelf != y.Shelf || x.Version != y.Version || x.Options.Count != y.Options.Count)
                return false;
            foreach (var (group, on) in x.Options)
                if (!y.Options.TryGetValue(group, out var other) || !on.SequenceEqual(other))
                    return false;
        }

        return true;
    }

    /// <summary>
    /// Tries a look on: its parts (each in its socket, with the options it names over the ones a card click would use, and
    /// its version), its take-offs, and its colours. It replaces whatever was tried; the bar takes the look's name and note.
    /// A look of only colours (another colourway of what is on) is a try too.
    /// </summary>
    public void TryLook(LookTry look)
    {
        Sync();
        // a look replaces whatever was tried, a "Wear on" waiting for a move too
        _nextMove = null;
        EndColourTry(false);
        _tries.Clear();
        foreach (var p in look.Parts)
        {
            if (PartOf(p.Key) is null)
                continue;
            // a look that names one thing through two slots (saved while it was on through both) tries it through the first
            var lead = LeadOf(p.Key);
            if (_tries.Any(t => !t.Nothing && t.Key != p.Key && LeadOf(t.Key) == lead))
                continue;
            Dictionary<string, List<string>>? options = null;
            if (p.Options is { Count: > 0 } named)
            {
                options = Copy(_wardrobe.CurrentOptions(p.Key.Mod));
                foreach (var (group, on) in named)
                    options[group] = on.ToList();
            }

            // in a layered socket a part takes the place of a tried part of its own shelf only (a body and skin together)
            _tries.RemoveAll(t => t.Key == p.Key || SameLayer(t.Key, p.Key));
            _tries.Add(new TryEntry { Key = p.Key, Options = options, Version = p.Version >= 0 ? p.Version : _data.Versions.GetValueOrDefault(p.Key.ToString()) });
            UseOf(p.Key).TriedUtc = _now();
        }

        // what the look wants empty comes off as the Nothing tile does it (never the body itself)
        // (a look's take-off of a part whose mod is also worn for another part is left out, as for "Nothing")
        foreach (var off in look.TakeOff)
            if (off.Socket != Socket.BodyBase && !_tries.Any(t => SameLayer(t.Key, off)) && WornWith(off, look.TakeOff.ToList()) is null)
                _tries.Add(new TryEntry { Key = off, Nothing = true });

        if (look.Colours is { } colours && ColourTry is { } colourTry)
        {
            try
            {
                colourTry.TryOn(colours);
                _colourTrying = true;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"trying a look's colours failed: {ex}");
            }
        }

        _lookTry = _tries.Count > 0 || _colourTrying ? (look.Look, look.Colourway, look.Name, look.Note) : null;
        PreviewTries();
    }

    // ======== undo ========

    private sealed class UndoEntry
    {
        /// <summary> A wardrobe step (Keep, take-off, options, version). </summary>
        public Guid? Step { get; init; }

        /// <summary> A sorting step: the sorting as it was before. </summary>
        public SortingSnapshot? Sorting { get; init; }

        /// <summary>
        /// Puts things back as they were before: a colour or looks step, or, beside <see cref="Step"/>, the colours a
        /// look's Keep kept with its pieces.
        /// </summary>
        public Action? Restore { get; init; }

        /// <summary> Taking it back ends a colour try first (the colour engine's Restore drops one anyway). </summary>
        public bool EndsColourTry { get; init; }

        /// <summary> A step that changed the kept outfit (Keep, take-off, option, version): its Undo is one too (<see cref="Steps"/>). </summary>
        public bool OutfitStep { get; init; }

        public string What { get; init; } = string.Empty;
    }

    private sealed record SortingSnapshot(Dictionary<string, string> Moves, HashSet<string> Hidden, List<UserShelf> Shelves);

    private readonly List<UndoEntry> _undo = new();

    private void PushUndo(UndoEntry entry)
    {
        _undo.Add(entry);
        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);
    }

    /// <summary> Wardrobe steps that did not go through cannot be taken back: they leave the history. </summary>
    private void PruneUndo()
    {
        while (_undo.Count > 0 && _undo[^1].Step is { } step && _wardrobe.Step(step) is StepState.Failed or StepState.Unknown)
            _undo.RemoveAt(_undo.Count - 1);
    }

    public bool CanUndo
    {
        get
        {
            Sync();
            return _undo.Count > 0 && (_undo[^1].Step is not { } step || _wardrobe.Step(step) == StepState.Done);
        }
    }

    public void Undo()
    {
        if (!CanUndo)
            return;
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        // a pick waiting for a move is not made after the user took a step back
        _nextMove = null;
        if (entry.Step is { } step)
        {
            // taking a step back ends any try (the wardrobe puts your look back first)
            EndColourTry(false);
            _lookTry = null;
            _tries.Clear();
            _wardrobe.Undo(step);
            // a look's Keep took its colours along: they go back with the pieces
            Quietly(entry.Restore);
            _steps++;
            SetStatus(Loc.F("Took back: {0}.", entry.What), false);
            Changed();
            return;
        }

        if (entry.Sorting is { } s)
        {
            RestoreSorting(s);
            SetStatus(Loc.F("Took back: {0}.", entry.What), false);
            _save();
            Changed(membership: true);
            return;
        }

        if (entry.Restore is { } restore)
        {
            if (entry.EndsColourTry)
                EndColourTry(false);
            Quietly(restore);
            if (entry.OutfitStep)
            {
                _lookTry = null;
                _steps++;
            }

            SetStatus(Loc.F("Took back: {0}.", entry.What), false);
            Changed();
        }
    }

    private void Quietly(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"taking a step back failed: {ex}");
        }
    }

    /// <summary>
    /// A colour change as one step of the same history as everything else (Undo walks all of them in order).
    /// <paramref name="restore"/> puts the colours back as they were before.
    /// </summary>
    public void RecordColourStep(string description, Action restore)
    {
        PushUndo(new UndoEntry { Restore = restore, EndsColourTry = true, What = description });
        _version++;
    }

    /// <summary> A looks command (pin, rename, remove, update …) as one step of the same history; <paramref name="restore"/> takes it back. </summary>
    public void RecordLooksStep(string description, Action restore)
    {
        PushUndo(new UndoEntry { Restore = restore, What = description });
        _version++;
    }

    private SortingSnapshot TakeSorting()
        => new(new Dictionary<string, string>(_data.Moves), new HashSet<string>(_data.Hidden),
            _data.Shelves.Select(s => new UserShelf { Id = s.Id, Name = s.Name, Kind = s.Kind }).ToList());

    private void RestoreSorting(SortingSnapshot s)
    {
        _sortStamp++;
        _data.Moves.Clear();
        foreach (var (k, v) in s.Moves)
            _data.Moves[k] = v;
        _data.Hidden.Clear();
        _data.Hidden.UnionWith(s.Hidden);
        _data.Shelves.Clear();
        _data.Shelves.AddRange(s.Shelves);
    }

    /// <summary> Starts a sorting step: what the sorting was is kept for Undo. </summary>
    private void SortingStep(string what)
    {
        PushUndo(new UndoEntry { Sorting = TakeSorting(), What = what });
        _sortStamp++;
    }

    /// <summary> Changes whenever the user's sorting may have changed (moves, hiding, shelves): the looks re-read shelves then. </summary>
    private int _sortStamp;

    public int SortStamp
        => _sortStamp;

    // ======== sorting ========

    private sealed class PendingSuggestion
    {
        public required MoveSuggestion Shown { get; init; }
        public required List<PartKey> Keys { get; init; }
        public required string Target { get; init; }
    }

    private PendingSuggestion? _suggestion;

    public void Move(PartKey part, string piece)
    {
        Sync();
        // the left hand's rings are a view of the rings' shelf
        if (piece == Pieces.RingsLeft)
            piece = Pieces.Rings;
        if (piece == Pieces.Hidden)
        {
            Hide(part);
            return;
        }

        if (part.Socket == Socket.None || !_byKey.TryGetValue(part, out var lib) || !IsHome(piece))
            return;
        var old = _shelfOf.GetValueOrDefault(part, Pieces.Other);
        if (old == piece && _data.Moves.ContainsKey(part.ToString()))
            return;

        // already on the shelf of its kind (just made from "New shelf: …"): it is where it belongs, nothing to remember
        if (old == piece && KindShelf(lib)?.Id == piece && !_data.Hidden.Contains(part.ToString()))
            return;

        // placing a part that waited under To sort (also on the shelf it is on: that confirms it)
        var from = _waitingSet.Contains(part) ? Pieces.ToSort : old;
        _waitPins.Remove(part);

        SortingStep(Loc.F("moving {0}", lib.Name));
        // a thing moves with all its slots
        foreach (var p in ThingKeys(part))
        {
            _data.Moves[p.ToString()] = piece;
            _data.Hidden.Remove(p.ToString());
            _pins.Remove(p);
            _waitPins.Remove(p);
        }

        _save();
        Changed(mods: new[] { part.Mod });
        SetStatus(Loc.F("Moved {0} to {1}.", lib.Name, ShelfName(piece)), false);
        Suggest(lib, from, piece);
    }

    /// <summary> After a move: parts that look the same and are still on the old shelf, offered once (SPEC "It learns"). </summary>
    private void Suggest(LibPart moved, string from, string to)
    {
        var left = ShelfParts(from);
        if (from == to || left.Count == 0)
            return;
        var offer = $"{moved.Key}>{to}";
        if (_data.Offered.Contains(offer))
            return;

        var candidates = left.Where(p => p.Key != moved.Key && p.Key.Socket == moved.Key.Socket && !_data.Moves.ContainsKey(p.Key.ToString())).ToList();
        if (candidates.Count == 0)
            return;

        IReadOnlyList<PartInfo> alike;
        try
        {
            alike = _library.Detector.LookAlikes(moved.Info, candidates.Select(p => p.Info));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"look-alikes failed: {ex.Message}");
            return;
        }

        var keys = alike.Select(i => new PartKey(i.Mod, i.Socket)).Where(k => candidates.Any(c => c.Key == k)).Distinct().ToList();
        if (keys.Count == 0)
            return;

        _data.Offered.Add(offer);
        _save();
        _suggestion = new PendingSuggestion
        {
            Keys   = keys,
            Target = to,
            Shown  = new MoveSuggestion
            {
                Name      = moved.Name,
                FromPiece = from,
                ToPiece   = to,
                ToIcon    = ShelfIcon(to),
                Text     = Loc.F("You moved {0} from {1} to {2}.", moved.Name, ShelfName(from), ShelfName(to)),
                Detail   = keys.Count == 1 ? Loc.T("1 more looks the same.") : Loc.F("{0} more look the same.", keys.Count),
                Count    = keys.Count,
                Pictures = keys.Take(4).Select(k => _byKey.TryGetValue(k, out var p) ? PictureOf(p) : null).ToList(),
            },
        };
        Changed();
    }

    public void AcceptSuggestion()
    {
        Sync();
        if (_suggestion is not { } s)
            return;
        _suggestion = null;
        SortingStep(Loc.F("moving {0} more", s.Keys.Count));
        foreach (var key in s.Keys)
        {
            _data.Moves[key.ToString()] = s.Target;
            _data.Hidden.Remove(key.ToString());
            _pins.Remove(key);
            _waitPins.Remove(key);
        }

        _save();
        SetStatus(Loc.F("Moved {0} more to {1}.", s.Keys.Count, ShelfName(s.Target)), false);
        Changed(mods: s.Keys.Select(k => k.Mod));
    }

    public void DismissSuggestion()
    {
        if (_suggestion is null)
            return;
        _suggestion = null;
        Changed();
    }

    public void PutBackWhereDetected(PartKey part)
    {
        Sync();
        // a thing is moved when any of its slots is (the card's key can be a slot detection placed there)
        if (!ThingKeys(part).Any(k => _data.Moves.ContainsKey(k.ToString())))
            return;
        SortingStep(Loc.F("moving {0}", NameOf(part)));
        foreach (var p in ThingKeys(part))
        {
            _data.Moves.Remove(p.ToString());
            _pins.Remove(p);
        }

        _save();
        Changed(mods: new[] { part.Mod });
        SetStatus(Loc.F("{0} is back where the closet had it.", NameOf(part)), false);
    }

    public void Hide(PartKey part)
    {
        Sync();
        if (part.Socket == Socket.None || !_byKey.ContainsKey(part) || _data.Hidden.Contains(part.ToString()))
            return;
        SortingStep(Loc.F("hiding {0}", NameOf(part)));
        foreach (var p in ThingKeys(part))
        {
            _data.Hidden.Add(p.ToString());
            _pins.Remove(p);
        }

        _save();
        Changed(mods: new[] { part.Mod });
        SetStatus(Loc.F("{0} is hidden. It can be shown again from Hidden.", NameOf(part)), false);
    }

    public void Unhide(PartKey part)
    {
        Sync();
        if (!_data.Hidden.Contains(part.ToString()))
            return;
        SortingStep(Loc.F("showing {0} again", NameOf(part)));
        foreach (var p in ThingKeys(part))
            _data.Hidden.Remove(p.ToString());
        _save();
        Changed(mods: new[] { part.Mod });
    }

    public string AddPiece(string name)
    {
        Sync();
        name = name.Trim();
        if (name.Length == 0)
            return string.Empty;
        var kind = KindOfName(name);
        if (_data.Shelves.FirstOrDefault(s => s.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)) is { } existing)
        {
            // a shelf of that name made before it could belong to a kind: it takes the kind now
            if (existing.Kind.Length == 0 && kind.Length > 0)
                GiveKind(existing, kind, Loc.F("making {0}", existing.Name));
            return existing.Id;
        }

        var id = Pieces.UserPrefix + Guid.NewGuid().ToString("N");
        if (kind.Length > 0)
        {
            // a shelf for a kind fills itself: one step for Undo (taking it away again puts everything back)
            SortingStep(Loc.F("making {0}", name));
            _data.Shelves.Add(new UserShelf { Id = id, Name = name, Kind = kind });
            _save();
            Changed(membership: true);
            SayFilled(id, name);
            return id;
        }

        // an empty shelf does not show, so making one is no step of its own: the move onto it is
        _data.Shelves.Add(new UserShelf { Id = id, Name = name });
        _save();
        Changed(membership: true);
        return id;
    }

    /// <summary>
    /// The kind a shelf name stands for ("Piercings" → piercing), "" for none, or when another shelf of the user's already
    /// belongs to that kind.
    /// </summary>
    private string KindOfName(string name)
    {
        var kind = KindNames.KindOf(name, Pieces.BuiltIn.SelectMany(p => new[] { p.Name, Loc.T(p.Name) }));
        return kind.Length > 0 && _data.Shelves.Any(s => s.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)) ? string.Empty : kind;
    }

    private void GiveKind(UserShelf shelf, string kind, string step)
    {
        SortingStep(step);
        shelf.Kind = kind;
        _save();
        Changed(membership: true);
        SayFilled(shelf.Id, shelf.Name);
    }

    /// <summary> "Piercings: 37 things moved here." (nothing said when nothing came). </summary>
    private void SayFilled(string id, string name)
    {
        var count = ShelfCount(id);
        if (count == 1)
            SetStatus(Loc.F("{0}: 1 thing moved here.", name), false);
        else if (count > 1)
            SetStatus(Loc.F("{0}: {1} things moved here.", name, count), false);
    }

    public void RenamePiece(string id, string name)
    {
        Sync();
        name = name.Trim();
        if (name.Length == 0 || _data.Shelves.FirstOrDefault(s => s.Id == id) is not { } shelf || shelf.Name == name)
            return;
        SortingStep(Loc.F("renaming {0}", shelf.Name));
        shelf.Name = name;

        // renamed to a kind's name: it belongs to that kind from now on; renamed to anything else: it keeps its kind
        var kind = KindOfName(name);
        var gained = kind.Length > 0 && !kind.Equals(shelf.Kind, StringComparison.OrdinalIgnoreCase);
        if (gained)
            shelf.Kind = kind;
        _save();
        Changed(membership: true);
        if (gained)
            SayFilled(shelf.Id, shelf.Name);
    }

    public void RemovePiece(string id)
    {
        Sync();
        if (_data.Shelves.FirstOrDefault(s => s.Id == id) is not { } shelf)
            return;
        SortingStep(Loc.F("removing {0}", shelf.Name));
        _data.Shelves.Remove(shelf);
        foreach (var key in _data.Moves.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList())
            _data.Moves.Remove(key);
        if (_selected == id)
            Select(Pieces.Top);
        _save();
        Changed(membership: true);
    }

    public IReadOnlyList<Evidence> WhyHere(PartKey part)
    {
        Sync();
        if (!_byKey.TryGetValue(part, out var lib))
            return Array.Empty<Evidence>();

        var list = new List<Evidence>();
        if (_data.Moves.TryGetValue(part.ToString(), out var moved) && IsHome(moved))
            list.Add(new Evidence { Kind = EvidenceKind.Learned, Value = Loc.T("you put it here"), Piece = moved, Weight = 1 });
        if (_data.Hidden.Contains(part.ToString()))
            list.Add(new Evidence { Kind = EvidenceKind.Learned, Value = Loc.T("you hid it"), Piece = Pieces.Hidden, Weight = 1 });
        foreach (var kind in new[] { EvidenceKind.Slot, EvidenceKind.Body, EvidenceKind.Words })
            if (!lib.Info.Evidence.Any(e => e.Kind == kind))
                list.Add(new Evidence { Kind = kind, Value = Loc.T("no hint"), Piece = string.Empty });
        list.AddRange(lib.Info.Evidence.Select(e => new Evidence { Kind = e.Kind, Value = Translate(e), Word = e.Word, Piece = e.Piece, Weight = e.Weight }));
        var ordered = list.OrderBy(e => e.Kind == EvidenceKind.Learned ? 0 : 1).ThenBy(e => e.Kind).ToList();
        // a copy the closet made: that comes first
        if (CopyName(part.Mod) is { Length: > 0 } from)
            ordered.Insert(0, new Evidence { Kind = EvidenceKind.Learned, Value = Loc.F("A copy the closet made of {0}.", from), Piece = string.Empty, Weight = 1 });
        return ordered;
    }

    // ======== the rest ========

    private (string Text, bool Error, DateTime At, int Id)? _status;
    private int _statusId;

    private void SetStatus(string text, bool error)
    {
        _status = (text, error, _now(), ++_statusId);
        _version++;
    }

    public StatusLine? Status
    {
        get
        {
            Sync();
            return _status is { } s ? new StatusLine { Id = s.Id, Text = s.Text, IsError = s.Error, Age = (_now() - s.At).TotalSeconds } : null;
        }
    }

    public void OpenInPenumbra(PartKey part)
    {
        if (_library.Mod(part.Mod) is { } mod)
            _library.OpenInPenumbra(mod.Directory, mod.Name);
    }

    public void Rescan()
    {
        _library.Rescan();
        SetStatus(Loc.T("Reading your mods again."), false);
        Changed(membership: true);
    }

    // ---- census ----

    // The census reads every mod's files again (seconds to minutes): on a thread of its own at low priority, never on the
    // game's. One at a time; a second request while one runs only says so. Progress shows in Status.

    private Thread? _census;
    private CancellationTokenSource? _censusCancel;
    private volatile int _censusDone;
    private volatile int _censusResult = -1;
    private volatile string? _censusError;
    private int _censusTotal;
    private string _censusFile = string.Empty;
    private int _censusShown = -1;
    private DateTime _censusShownAt = DateTime.MinValue;

    /// <summary> Where the census goes: pluginConfigs/ModCloset2/census.jsonl.gz. </summary>
    public string CensusFile
        => Path.Combine(_library.CacheDirectory, "census.jsonl.gz");

    /// <summary> True while the census is being written. </summary>
    public bool WritingCensus
        => _census is not null;

    public void WriteCensus()
    {
        if (_census is not null)
        {
            SetStatus(Loc.F("The census file is still being written… {0} of {1}", SearchText.Number(_censusDone), SearchText.Number(_censusTotal)), false);
            return;
        }

        // the same mods and hints the detection gets; the list is made here (cheap), the reading on the census thread
        var input    = _library.CensusInput();
        var detector = _library.Detector;
        var file     = CensusFile;
        var cancel   = new CancellationTokenSource();
        _censusFile   = file;
        _censusTotal  = input.Count;
        _censusDone   = 0;
        _censusResult = -1;
        _censusError  = null;
        _censusShown  = 0;
        _censusCancel = cancel;
        _census = new Thread(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                _censusResult = detector.WriteCensus(file, input, n => _censusDone = n, cancel.Token);
            }
            catch (Exception ex)
            {
                _censusError  = ex.GetBaseException().Message;
                _censusResult = 0;
            }
        })
        {
            IsBackground = true,
            Priority     = ThreadPriority.BelowNormal,
            Name         = "ModCloset2 census",
        };
        _census.Start();
        SetStatus(Loc.F("Writing the census file… {0} of {1}", 0, SearchText.Number(_censusTotal)), false);
    }

    private void UpdateCensus()
    {
        if (_census is null)
            return;
        if (_censusResult >= 0)
        {
            _census       = null;
            _censusCancel = null;
            if (_censusError is { } error)
            {
                _log?.Invoke($"census failed: {error}");
                SetStatus(Loc.T("The census file could not be written."), true);
            }
            else if (_censusResult == 0)
            {
                // nothing to look at (no mods, or a detector that reads nothing): say so rather than point at no file
                SetStatus(Loc.T("No census was written: there were no mods to look at."), false);
            }
            else
            {
                SetStatus(Loc.F("The census file is ready: {0}", _censusFile), false);
            }

            return;
        }

        // progress at most twice a second
        var done = _censusDone;
        if (done != _censusShown && (_now() - _censusShownAt).TotalSeconds >= 0.5)
        {
            _censusShown   = done;
            _censusShownAt = _now();
            SetStatus(Loc.F("Writing the census file… {0} of {1}", SearchText.Number(done), SearchText.Number(_censusTotal)), false);
        }
    }

    /// <summary> On unload: stops a census that is still being written (the detector writes to a temporary file first). </summary>
    public void Dispose()
    {
        StopOwnMods();
        StopPhotos();
        _censusCancel?.Cancel();
        if (_census is { } thread && !thread.Join(TimeSpan.FromSeconds(3)))
            _log?.Invoke("the census did not stop in time");
    }

    public string Language
    {
        get => _data.Language;
        set
        {
            value ??= string.Empty;
            if (value == _data.Language)
                return;
            _data.Language = value;
            _applyLanguage(value);
            _save();
            Changed(membership: true);
        }
    }

    public string Remembered(string key)
        => _data.Remembered.TryGetValue(key, out var value) ? value : string.Empty;

    /// <summary> The remembered keys that start with <paramref name="prefix"/> (for moving them to a file of their own). </summary>
    public IReadOnlyList<string> RememberedKeys(string prefix)
        => _data.Remembered.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    /// <summary> Forgets a remembered key (it moved elsewhere). </summary>
    public void Forget(string key)
    {
        if (_data.Remembered.Remove(key))
            _save();
    }

    public void Remember(string key, string value)
    {
        if (_data.Remembered.TryGetValue(key, out var old) && old == value)
            return;
        _data.Remembered[key] = value;
        _save();
    }
}
