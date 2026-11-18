using ModCloset2.Core;
using ModCloset2.Library;

namespace ModCloset2.Closet;

// "Make it its own mod" (docs/briefs/10-own-mod.md): a card's thing copied out of its mod into a new Penumbra mod. The
// file work is the maker's (IOwnModMaker, on threads of its own); the session plans per card, writes one at a time, has
// Penumbra take the new folder in on the game's thread, remembers the copy and gives the new mod what the closet knew of
// the card. Nothing on the character changes, and the mod it comes from is not touched.

public sealed partial class ClosetSession
{
    /// <summary> The file work behind "Make it its own mod", or null: then no card offers it. </summary>
    public IOwnModMaker? OwnModMaker { get; set; }

    /// <summary> Penumbra's side of it (where its mods are, taking a new one in), or null: then no card offers it. </summary>
    public IPenumbraMods? PenumbraMods { get; set; }

    private bool OwnModsOn
        => OwnModMaker is not null && PenumbraMods is not null;

    /// <summary> At most this long after Penumbra took the new mod in, the status line waits for its card. </summary>
    private static readonly TimeSpan OwnModShows = TimeSpan.FromSeconds(4);

    /// <summary> "Mod Closet 2 0.4.0", for the note inside a new mod. </summary>
    private static string MadeBy
        => $"Mod Closet 2 {typeof(ClosetSession).Assembly.GetName().Version?.ToString(3) ?? string.Empty}".TrimEnd();

    // ======== one card per thing ========

    /// <summary> Parts of a mod whose thing has a copy in the library → the copy's folder. </summary>
    private Dictionary<PartKey, string> _copiedTo = new();

    /// <summary> Those parts as placed: known (worn, looked for, on outfit cards) but on no shelf's list. </summary>
    private readonly Dictionary<PartKey, LibPart> _aside = new();

    /// <summary> The copies or the library changed: <see cref="RefreshCopies"/> before the next placing. </summary>
    private bool _copiesDirty = true;

    /// <summary> A note is dropped only when its mod has been missing in two reads of the library this far apart. </summary>
    private static readonly TimeSpan CopyGoneAfter = TimeSpan.FromSeconds(5);

    /// <summary> Notes whose copy or source was missing at a read: when that was first seen, and at which library version. </summary>
    private readonly Dictionary<string, (DateTime At, int Version)> _copyMissing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Works out which things have a copy in the library, and drops the notes of copies that are gone. A note goes only when
    /// the library has read every mod, says nothing is wrong and holds mods at all (one empty read of Penumbra's list is
    /// no reason), and the mod it came from, or the copy that was there once, has been missing in two reads at least
    /// <see cref="CopyGoneAfter"/> apart (this runs when the library has read again, so time alone drops nothing). A copy
    /// just made that the library has not read yet stays noted. The mods whose
    /// things gain or lose their card are placed again.
    /// </summary>
    private void RefreshCopies()
    {
        _copiesDirty = false;
        var progress = _library.Progress;
        var sure     = progress.Ready && progress.Problem.Length == 0 && _library.Parts.Count > 0;
        var changed  = false;
        var now      = _now();
        foreach (var (dir, copy) in _data.Copies.ToList())
        {
            var there = _library.Mod(dir) is not null;
            if (there && !copy.Seen)
            {
                copy.Seen = true;
                changed   = true;
            }

            if (!(sure && (_library.Mod(copy.From) is null || copy.Seen && !there)))
            {
                _copyMissing.Remove(dir);
                continue;
            }

            if (!_copyMissing.TryGetValue(dir, out var first))
            {
                _copyMissing[dir] = (now, _library.Version);
            }
            else if (now - first.At >= CopyGoneAfter && _library.Version != first.Version)
            {
                _copyMissing.Remove(dir);
                _data.Copies.Remove(dir);
                changed = true;
            }
        }

        foreach (var dir in _copyMissing.Keys.Where(d => !_data.Copies.ContainsKey(d)).ToList())
            _copyMissing.Remove(dir);
        if (changed)
            _save();

        // only the slots the copy has a part in now: a piece is never without a card
        var map = new Dictionary<PartKey, string>();
        foreach (var (dir, copy) in _data.Copies)
        {
            // a ring made for the other hand stands in for nothing: the ring it came from keeps its card
            if (copy.Hand != Hands.Both)
                continue;
            var parts = _library.PartsOf(dir);
            if (parts.Count == 0)
                continue;
            foreach (var slot in copy.Slots)
                if (parts.Any(p => p.FitsMe && (int)p.Key.Socket == slot))
                    map[new PartKey(copy.From, (Socket)slot)] = dir;
        }

        foreach (var key in map.Keys.Concat(_copiedTo.Keys))
            if (map.GetValueOrDefault(key) != _copiedTo.GetValueOrDefault(key))
                _userDirtyMods.Add(key.Mod);
        _copiedTo = map;

        // a copy noted or no longer noted: which of its parts are pieces changes
        var folders = new HashSet<string>(_data.Copies.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var dir in folders.Concat(_copyFolders))
            if (folders.Contains(dir) != _copyFolders.Contains(dir) && _library.PartsOf(dir).Count > 0)
                _userDirtyMods.Add(dir);
        _copyFolders = folders;
    }

    /// <summary>
    /// A part of a copy the closet made that is not one of the slots it was made for: the copied model names another
    /// slot's material (trousers whose model uses the socks' material), so the detection finds a texture-only part of that
    /// slot. It is no piece of the copy: it is not placed (no card on any shelf, in the search, the counts, To sort, On
    /// you or Outfits) and the looks do not see it as worn.
    /// </summary>
    private bool StrayOfCopy(LibPart part)
        => _data.Copies.Count > 0 && _data.Copies.TryGetValue(part.Mod.Directory, out var copy) && !copy.Slots.Contains((int)part.Key.Socket);

    /// <summary> The copies noted when the copies were last looked at (their mods are placed again when that changes). </summary>
    private HashSet<string> _copyFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> The parts kept aside that are on or tried on now, on one shelf (null: all). Few; usually none. </summary>
    private List<LibPart> AsideShown(string? shelf)
    {
        var list = new List<LibPart>();
        if (_aside.Count == 0)
            return list;
        foreach (var (key, part) in _aside)
            if ((shelf is null || _shelfOf.GetValueOrDefault(key) == shelf)
             && (_on.Contains(key) || _tries.Any(t => !t.Nothing && t.Key == key)))
                list.Add(part);
        return list;
    }

    /// <summary>
    /// The shelf a part of a copy the closet made goes on, once: the shelf its card was on. When the detection has read the
    /// new mod, the shelf becomes the user's move only where the closet would not put it there anyway (or would be unsure);
    /// until then the part sits there for now (null: nothing to do, the part is placed as any other).
    /// </summary>
    private string? CopyShelf(LibPart part)
    {
        if (_data.Copies.Count == 0 || !_data.Copies.TryGetValue(part.Mod.Directory, out var copy))
            return null;
        var socket = (int)part.Key.Socket;
        if (copy.Shelf.Length == 0 || !copy.Slots.Contains(socket) || copy.Shelved.Contains(socket) || !IsHome(copy.Shelf))
            return null;
        var key = part.KeyText;
        if (_data.Moves.ContainsKey(key) || _data.Hidden.Contains(key))
            return null;
        if (part.Mod.Detection is null)
            return copy.Shelf;

        copy.Shelved.Add(socket);
        var unsure = KindShelf(part) is null && part.Info.Confidence < DetectorThresholds.Unsure;
        if (NaturalShelf(part) != copy.Shelf || unsure)
            _data.Moves[key] = copy.Shelf;
        _save();
        return null;
    }

    /// <summary> The name of the mod a copy came out of ("" when this mod is no copy the closet made). </summary>
    private string CopyName(string mod)
        => _data.Copies.Count > 0 && _data.Copies.TryGetValue(mod, out var copy)
            ? _library.Mod(copy.From)?.Name ?? copy.FromName
            : string.Empty;

    /// <summary>
    /// The copy already made of a thing (any of its slots), or null. A copy that was in the library and is not now does
    /// not count (its note waits for a second read before it goes).
    /// </summary>
    private (string Folder, OwnCopy Copy)? CopyOfThing(string mod, IReadOnlyList<LibPart> thing, Hands hand = Hands.Both)
    {
        foreach (var (dir, copy) in _data.Copies)
            if (copy.Hand == hand && copy.From.Equals(mod, StringComparison.OrdinalIgnoreCase) && thing.Any(p => copy.Slots.Contains((int)p.Key.Socket))
             && (!copy.Seen || _library.Mod(dir) is not null))
                return (dir, copy);
        return null;
    }

    /// <summary> "Make it its own mod…" on a card: gear, and its mod holds more than this thing. Cheap. </summary>
    private bool OffersOwnMod(IReadOnlyList<LibPart> thing)
        => OwnModsOn && thing.Count > 0 && thing.All(p => MergesSlots(p.Key.Socket))
         && _byMod.TryGetValue(thing[0].Mod.Directory, out var parts) && parts.Count > thing.Count;

    // ======== the plan ========

    private sealed class OwnPlanEntry
    {
        public PartKey Lead { get; init; }
        public Hands ToHand { get; init; }
        public LibMod Mod { get; init; } = null!;
        public Dictionary<string, List<string>> Chosen { get; init; } = new();
        public List<Socket> Sockets { get; init; } = new();
        public string Name { get; init; } = string.Empty;
        public string Piece { get; init; } = string.Empty;
        public List<string> Stays { get; init; } = new();
        public bool Started;
        public volatile bool Finished;
        public volatile OwnModContent? Content;
        public volatile string? Crash;
        public OwnModPlan? Plan;
    }

    /// <summary> Plans per card (by its thing's lead), kept until the mod or the options that are on for the thing change. </summary>
    private readonly Dictionary<(PartKey Lead, Hands Hand), OwnPlanEntry> _ownPlans = new();

    /// <summary> The plan being worked out (one at a time), and the card asked for last. </summary>
    private OwnPlanEntry? _planRunning;
    private Thread? _planThread;
    private (PartKey Lead, Hands Hand)? _planWanted;

    public OwnModPlan? OwnModOf(PartKey part)
        => OwnModOfCore(part, Hands.Both);

    /// <summary> A copy of a ring with files for one hand, made for the other hand (<paramref name="to"/>). </summary>
    private OwnModPlan? OwnModOfHand(PartKey part, Hands to)
        => OwnModOfCore(part, to);

    private OwnModPlan? OwnModOfCore(PartKey part, Hands to)
    {
        if (!OwnModsOn)
            return null;
        Sync();
        var thing = ThingOf(part);
        if (thing.Count == 0)
            return No(Loc.T("This piece is not in the closet any more."));
        var lead = LeadOf(thing[0].Key);
        if (_ownWrite is { } writing && writing.Entry.Lead == lead && writing.Entry.ToHand == to)
            return Making(writing);
        if (!thing.All(p => MergesSlots(p.Key.Socket)))
            return No(Loc.T("Only gear can be made a mod of its own for now."));
        if (to != Hands.Both)
        {
            // a ring for the other hand: only a ring with files for one hand, and only for the hand it lacks
            if (thing.Count != 1 || thing[0].Key.Socket != Socket.Rings)
                return No(Loc.T("Only a ring can be made for the other ring slot."));
            var has = HandsOf(thing[0]);
            if (has == Hands.Both)
                return No(Loc.T("This ring is made for both ring slots already."));
            if (has == to)
                return No(to == Hands.Left ? Loc.T("This ring is made for the left ring slot already.") : Loc.T("This ring is made for the right ring slot already."));
        }

        var mod = thing[0].Mod;
        if (CopyOfThing(mod.Directory, thing, to) is { } made)
            return No(to == Hands.Both
                ? Loc.F("This piece already is a mod of its own: {0}.", _library.Mod(made.Folder)?.Name ?? made.Copy.Name)
                : Loc.F("There is a copy of this ring for the other ring slot already: {0}.", _library.Mod(made.Folder)?.Name ?? made.Copy.Name));
        if (mod.Facts is not { Folder.Length: > 0 })
            return No(Loc.T("The closet has not read this mod's files yet."));

        var chosen = ChosenFor(thing);
        if (!_ownPlans.TryGetValue((lead, to), out var entry) || !ReferenceEquals(entry.Mod, mod) || !SameOptions(entry.Chosen, chosen))
        {
            var shelf = _shelfOf.GetValueOrDefault(lead, Pieces.Other);
            var piece = PieceName(thing[0], shelf);
            // the proposed name says the slot ("Silver band · left ring slot")
            var name  = to switch
            {
                Hands.Left  => Loc.F("{0} · {1}", mod.Name, Loc.T("left ring slot")),
                Hands.Right => Loc.F("{0} · {1}", mod.Name, Loc.T("right ring slot")),
                _           => Loc.F("{0} · {1}", mod.Name, piece),
            };
            _ownPlans[(lead, to)] = entry = new OwnPlanEntry
            {
                Lead    = lead,
                ToHand  = to,
                Mod     = mod,
                Chosen  = chosen,
                Sockets = thing.Select(p => p.Key.Socket).ToList(),
                Name    = FreeName(name),
                Piece   = piece,
                Stays   = to == Hands.Both ? StaysOf(mod.Directory, lead) : new List<string>(),
            };
        }

        _planWanted = (lead, to);
        StartPlan();
        return PlanOf(entry);
    }

    /// <summary> Starts working out the plan asked for last, unless one is being worked out (it starts when that is done). </summary>
    private void StartPlan()
    {
        if (_planRunning is { Finished: false })
            return;
        _planRunning = null;
        _planThread  = null;
        if (_planWanted is not { } wanted || !_ownPlans.TryGetValue(wanted, out var entry) || entry.Started || OwnModMaker is not { } maker)
            return;

        entry.Started = true;
        var ask = new OwnModAsk
        {
            ModFolder = entry.Mod.Facts?.Folder ?? string.Empty,
            Sockets   = entry.Sockets,
            Chosen    = entry.Chosen.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList()),
            ToHand    = entry.ToHand,
        };
        _planRunning = entry;
        _planThread = new Thread(() =>
        {
            try
            {
                entry.Content = maker.Plan(ask);
            }
            catch (Exception ex)
            {
                entry.Crash = ex.Message;
            }
            finally
            {
                entry.Finished = true;
            }
        })
        {
            IsBackground = true,
            Priority     = ThreadPriority.BelowNormal,
            Name         = "ModCloset2 own mod plan",
        };
        _planThread.Start();
    }

    private OwnModPlan PlanOf(OwnPlanEntry entry)
    {
        if (entry.Plan is { } done)
            return done;
        if (!entry.Finished)
            return new OwnModPlan { State = OwnModState.Working, Name = entry.Name, From = entry.Mod.Name, Stays = entry.Stays };

        OwnModPlan plan;
        if (entry.Crash is { } crash || entry.Content is not { } content)
        {
            _log?.Invoke($"planning a mod of its own failed: {entry.Crash}");
            plan = No(Loc.T("The closet could not read this mod's files."));
        }
        else if (!content.Possible)
        {
            plan = No(content.Problem.Length > 0 ? content.Problem : Loc.T("The closet could not read this mod's files."));
        }
        else if (!content.ModHasMore && entry.ToHand == Hands.Both)
        {
            plan = No(Loc.T("This mod is only this piece."));
        }
        else
        {
            var notes = new List<string>();
            if (content.AuthorsLine.Length > 0)
                notes.Add(content.AuthorsLine);
            if (content.LeftBehind > 0)
                notes.Add(Loc.F("{0} changes of the mod belong to no single piece and stay behind.", content.LeftBehind));
            plan = new OwnModPlan
            {
                State   = OwnModState.Ready,
                Name    = entry.Name,
                From    = content.ModName.Length > 0 ? content.ModName : entry.Mod.Name,
                Files   = content.Files,
                Bytes   = content.Bytes,
                Options = content.Groups,
                Shared  = content.Shared,
                Stays   = entry.Stays,
                Notes   = notes,
            };
        }

        entry.Plan = plan;
        return plan;
    }

    private static OwnModPlan No(string why)
        => new() { State = OwnModState.No, Why = why };

    private static OwnModPlan Making(OwnWrite job)
        => new()
        {
            State = OwnModState.Making,
            Name  = job.Name,
            From  = job.Entry.Plan?.From ?? job.Entry.Mod.Name,
            Files = job.Total,
            Done  = Math.Min(job.Done, job.Total),
            Bytes = job.Entry.Plan?.Bytes ?? 0,
            Stays = job.Entry.Stays,
        };

    /// <summary>
    /// The options that are on for a thing now: those it is tried with when it is tried with options of its own (a look's,
    /// or set on the try), else the mod's as Penumbra has them for the player (the saved ones, else the defaults picked for
    /// the race and body: what a try or a Keep would use).
    /// </summary>
    private Dictionary<string, List<string>> ChosenFor(IReadOnlyList<LibPart> thing)
    {
        var tried = _tries.FirstOrDefault(t => !t.Nothing && t.Options is not null && thing.Any(p => p.Key == t.Key));
        return Copy(tried?.Options ?? _wardrobe.CurrentOptions(thing[0].Mod.Directory));
    }

    private static bool SameOptions(Dictionary<string, List<string>> a, Dictionary<string, List<string>> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && kv.Value.SequenceEqual(other));

    /// <summary>
    /// What a thing is, for the new mod's name: its kind when the closet knows one, else its shelf's name, in the singular
    /// where a built-in shelf's name is a plural of single things ("Top", not "Tops"); pairs stay ("Gloves", "Earrings").
    /// </summary>
    private string PieceName(LibPart part, string shelf)
        => part.Info.Kind.Length > 0
            ? KindNames.Singular(part.Info.Kind)
            : shelf switch
            {
                Pieces.Top      => Loc.T("Top"),
                Pieces.Necklace => Loc.T("Necklace"),
                Pieces.Rings    => Loc.T("Ring"),
                _               => ShelfName(shelf),
            };

    /// <summary> The name, or with " (2)", " (3)"… when a mod of that name is there already. </summary>
    private string FreeName(string name)
    {
        var taken = TakenNames();
        if (!taken.Contains(name))
            return name;
        for (var n = 2;; ++n)
            if (!taken.Contains($"{name} ({n})"))
                return $"{name} ({n})";
    }

    private bool NameTaken(string name)
        => TakenNames().Contains(name);

    /// <summary> The names of the mods in the library and of the copies made (one may not be read yet). </summary>
    private HashSet<string> TakenNames()
    {
        var taken = new HashSet<string>(_library.Parts.Select(p => p.Mod.Name), StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(_data.Copies.Values.Select(c => c.Name));
        return taken;
    }

    /// <summary> What else the mod holds and keeps, one name per thing ("Top", "Skirt"). </summary>
    private List<string> StaysOf(string mod, PartKey lead)
    {
        if (!_byMod.TryGetValue(mod, out var parts))
            return new List<string>();
        return OnePerThing(parts.Where(p => LeadOf(p.Key) != lead).OrderBy(p => p.Key.Socket))
            .Select(p => PieceName(p, _shelfOf.GetValueOrDefault(p.Key, Pieces.Other)))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ======== making it ========

    private sealed class OwnWrite
    {
        public OwnPlanEntry Entry { get; init; } = null!;
        public string Name { get; init; } = string.Empty;
        public string Shelf { get; init; } = string.Empty;
        public List<LibPart> Thing { get; init; } = new();
        public CancellationTokenSource Stop { get; } = new();
        public Thread? Thread;
        public volatile int Done;
        public volatile int Total;
        public volatile string? Folder;
        public volatile string? Error;
        public volatile bool Stopped;
        public volatile bool Over;
    }

    /// <summary> The new mod being written (one at a time). </summary>
    private OwnWrite? _ownWrite;

    /// <summary> A new mod Penumbra took in: the status line says so when its card is there, or after a few seconds. </summary>
    private (string Folder, string Name, DateTime Until)? _ownAnnounce;

    public void MakeOwnMod(PartKey part, string name)
        => MakeOwnModCore(part, name, Hands.Both);

    /// <summary> Makes the copy of a one-hand ring for the other hand (<paramref name="to"/>). </summary>
    private void MakeOwnModFor(PartKey part, string name, Hands to)
        => MakeOwnModCore(part, name, to);

    private void MakeOwnModCore(PartKey part, string name, Hands to)
    {
        if (!OwnModsOn)
            return;
        Sync();

        // Whenever no copy starts, the status line says why (the sheet closes when the plan does not turn to Making).
        // One at a time: a second click while one is written changes nothing.
        if (_ownWrite is not null)
        {
            SetStatus(Loc.T("A mod of its own is still being made. Wait until it is done."), false);
            return;
        }

        var thing = ThingOf(part);
        if (thing.Count == 0)
        {
            SetStatus(Loc.T("This piece is not in the closet any more."), true);
            return;
        }

        var lead = LeadOf(thing[0].Key);
        if (!_ownPlans.TryGetValue((lead, to), out var entry) || PlanOf(entry) is not { State: OwnModState.Ready } plan || entry.Content is not { } content)
        {
            var now = OwnModOfCore(part, to);
            SetStatus(now is { State: OwnModState.No } no ? no.Why : Loc.T("The closet is still looking at the mod's files. Try again in a moment."), now?.State == OwnModState.No);
            return;
        }

        // the plan must still be the one for the mod and the options as they are now
        if (!ReferenceEquals(entry.Mod, thing[0].Mod) || !SameOptions(entry.Chosen, ChosenFor(thing)))
        {
            SetStatus(Loc.T("The mod or its options changed meanwhile. Try again in a moment."), false);
            return;
        }

        name = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0)
            name = plan.Name;
        if (!name.Equals(plan.Name, StringComparison.OrdinalIgnoreCase) && NameTaken(name))
        {
            SetStatus(Loc.F("There is a mod called “{0}” already. Pick another name.", name), true);
            return;
        }

        var root = PenumbraMods!.ModRoot();
        if (root.Length == 0)
        {
            SetStatus(Loc.T("Penumbra did not say where its mods are. Nothing was changed."), true);
            return;
        }

        var job = new OwnWrite
        {
            Entry = entry,
            Name  = name,
            Shelf = _shelfOf.GetValueOrDefault(lead, string.Empty),
            Thing = thing.ToList(),
            Total = content.Files,
        };
        var how   = new OwnModWrite { ModRoot = root, Name = name, Piece = entry.Piece, MadeBy = MadeBy };
        var maker = OwnModMaker!;
        job.Thread = new Thread(() =>
        {
            try
            {
                job.Folder = maker.Write(content, how, (done, all) =>
                {
                    job.Done  = done;
                    job.Total = all;
                }, job.Stop.Token);
            }
            catch (OperationCanceledException) when (job.Stop.IsCancellationRequested)
            {
                job.Stopped = true;
            }
            catch (Exception ex)
            {
                job.Error = ex.Message.Trim();
            }
            finally
            {
                job.Over = true;
            }
        })
        {
            IsBackground = true,
            Priority     = ThreadPriority.BelowNormal,
            Name         = "ModCloset2 own mod",
        };
        _ownWrite = job;
        job.Thread.Start();
        Changed();
    }

    /// <summary> Game's thread: takes what the writing gave back, and says when the new card is there. </summary>
    private void UpdateOwnMods()
    {
        if (_ownWrite is { Over: true } job)
        {
            _ownWrite = null;
            Finish(job);
        }

        if (_ownAnnounce is { } a && (_byMod.ContainsKey(a.Folder) || _now() >= a.Until))
        {
            _ownAnnounce = null;
            SetStatus(Loc.F("“{0}” is a mod of its own now. It is switched off until you wear it.", a.Name), false);
        }
    }

    private void Finish(OwnWrite job)
    {
        if (job.Stopped || job.Stop.IsCancellationRequested)
            return;
        var entry = job.Entry;
        _ownPlans.Remove((entry.Lead, entry.ToHand));
        _viewDirty = true;
        if (job.Error is not null || job.Folder is not { Length: > 0 } folder)
        {
            var why = job.Error is { Length: > 0 } error ? error : Loc.T("The new mod could not be written.");
            SetStatus(Loc.F("{0} Nothing was changed.", why), true);
            return;
        }

        // Penumbra takes the whole folder in; it sits beside the mod it came from in the user's Penumbra folders
        var old   = entry.Mod;
        var added = false;
        try
        {
            added = PenumbraMods!.AddMod(folder);
            if (added && old.SortFolder.Length > 0)
                PenumbraMods.SetModPath(folder, $"{old.SortFolder}/{job.Name.Replace('/', '-').Replace('\\', '-')}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Penumbra calls for a new mod failed: {ex.Message}");
        }

        Remember(folder, job);
        if (added)
            _ownAnnounce = (folder, job.Name, _now() + OwnModShows);
        else
            SetStatus(Loc.F("“{0}” was written, but Penumbra did not take it in. Press “Rediscover mods” in Penumbra to see it.", job.Name), true);
        Changed();
    }

    /// <summary>
    /// Notes the copy and gives the new mod's parts what the closet remembers of the card: its shelf (when they are first
    /// placed), the slot picked last, the version picked last, and the colours. The options came along in the copy (its
    /// defaults are the ones that were on). Nothing about the mod it came from changes.
    /// </summary>
    private void Remember(string folder, OwnWrite job)
    {
        var old = job.Entry.Mod.Directory;
        _data.Copies[folder] = new OwnCopy
        {
            From     = old,
            FromName = job.Entry.Mod.Name,
            Name     = job.Name,
            Slots    = job.Thing.Select(p => (int)p.Key.Socket).ToList(),
            Shelf    = job.Shelf,
            Hand     = job.Entry.ToHand,
            MadeUtc  = _now(),
        };

        if (job.Thing.Count > 0 && _data.Slots.TryGetValue(job.Thing[0].KeyText, out var slot))
            _data.Slots[new PartKey(folder, job.Thing[0].Key.Socket).ToString()] = slot;
        foreach (var part in job.Thing)
        {
            var to = new PartKey(folder, part.Key.Socket);
            if (_data.Versions.TryGetValue(part.KeyText, out var version))
                _data.Versions[to.ToString()] = version;
            if (_data.FitChoices.TryGetValue(part.KeyText, out var fit))
                _data.FitChoices[to.ToString()] = fit.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
            try
            {
                Colours?.CopyMemory(part.Key, to);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"copying colours to a new mod failed: {ex.Message}");
            }
        }

        _copiesDirty = true;
        _save();
    }

    /// <summary> On unload: stops a write (the maker takes its own unfinished folder away) and waits for it a moment. </summary>
    private void StopOwnMods()
    {
        if (_ownWrite is { } job)
        {
            job.Stop.Cancel();
            if (job.Thread is { } thread && !thread.Join(TimeSpan.FromSeconds(5)))
                _log?.Invoke("writing a new mod did not stop in time");
        }

        if (_planThread is { } plan && !plan.Join(TimeSpan.FromSeconds(2)))
            _log?.Invoke("reading a mod for a new one did not stop in time");
    }
}
