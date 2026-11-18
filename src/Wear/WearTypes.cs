using ModCloset2.Core;

namespace ModCloset2.Wear;

/// <summary> Everything taking off needs to undo a Keep of a mod (port of the old WornRecord). </summary>
[Serializable]
public sealed class WornRecord
{
    /// <summary> The mod had its own (or inherited) settings in the collection before the first Keep. </summary>
    public bool HadSetting { get; set; }
    public bool WasInherited { get; set; }
    public bool WasEnabled { get; set; }
    public int OldPriority { get; set; }
    public Dictionary<string, List<string>> OldOptions { get; set; } = new();

    /// <summary> Glamourer state (JSON) with what the slots / hair held before the mod's parts were kept. </summary>
    public string Backup { get; set; } = string.Empty;

    /// <summary> What Keep put on (slot → item id, customize key → value); only slots still showing these are restored. </summary>
    public Dictionary<string, ulong> PutOnItems { get; set; } = new();
    public Dictionary<string, int> PutOnCustomize { get; set; } = new();

    /// <summary> The character it was kept on (name@world). </summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary> The Penumbra collection the old settings belong to. </summary>
    public Guid? Collection { get; set; }
}

/// <summary> How a mod stood in a collection: the thing undo puts back. </summary>
public sealed class ModSettingState
{
    public string ModDirectory { get; set; } = string.Empty;

    /// <summary> False: the mod had no own settings (it inherits or is simply off). </summary>
    public bool HasOwn { get; set; }
    public bool Enabled { get; set; }
    public int Priority { get; set; }
    public Dictionary<string, List<string>> Options { get; set; } = new();
}

/// <summary> One step in the history (a Keep, a take-off, an option change). </summary>
public sealed class JournalEntry
{
    /// <summary> The session's id for the step (so undo takes back exactly that step). </summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public string Description { get; set; } = string.Empty;
    public Guid Collection { get; set; }
    public string Owner { get; set; } = string.Empty;

    /// <summary> Written before the step is applied and set once it went through; an unfinished entry means a crash mid-step. </summary>
    public bool Committed { get; set; }
    public bool Undone { get; set; }

    public List<ModSettingState> Before { get; set; } = new();
    public List<ModSettingState> After { get; set; } = new();

    /// <summary> Full Glamourer state (base64) before / after, when the step changed the look. </summary>
    public string? LookBefore { get; set; }
    public string? LookAfter { get; set; }

    /// <summary> Keep records before / after (mod directory → record, null = none). </summary>
    public Dictionary<string, WornRecord?> WornBefore { get; set; } = new();
    public Dictionary<string, WornRecord?> WornAfter { get; set; } = new();
}

/// <summary> One mod to put on, with the options, items and customize values to use (the old DressItem). </summary>
/// <param name="Sockets"> The sockets of this mod being tried or kept (its other parts stay as they are). </param>
/// <param name="Picks"> Glamourer slot id → item id to put on. </param>
/// <param name="Customize"> Glamourer customize key → value (hairstyle, face, tail shape). </param>
/// <param name="Under"> Mods of an upper layer that must stay above this one (<see cref="ModCloset2.Closet.TryRequest.Under"/>). </param>
public sealed record DressItem(string Mod, string Name, Dictionary<string, List<string>> Options, IReadOnlyList<(byte SlotId, ulong ItemId)> Picks,
    IReadOnlyDictionary<string, int> Customize, IReadOnlyList<Socket> Sockets, IReadOnlyList<string>? Under = null);

/// <summary> One part to take off: the mod and the socket it shows in. </summary>
public sealed record TakeOffItem(string Mod, string Name, Socket Socket, IReadOnlyList<byte> SlotIds);

/// <summary> What a due slot check does this frame. </summary>
public enum CheckTurn
{
    Run,
    Later,
    Drop,
}

/// <summary> What a refused look does this frame. </summary>
public enum RestoreTurn
{
    Wait,
    Try,
    GiveUp,
}

/// <summary> The wardrobe's rules that need no game: kept apart so the tests can hold them to their word. </summary>
public static class WearRules
{
    /// <summary>
    /// The highest priority among the mods that are on, as Penumbra uses them: a mod another plugin holds a temporary
    /// setting for counts with that setting (on or off, its priority), every other one with the collection's. 0 when none.
    /// </summary>
    public static int TopPriority(IEnumerable<(string Mod, bool Enabled, int Priority)> collection,
        IEnumerable<(string Mod, bool Enabled, int Priority)> othersTemporary, Func<string, bool> skip)
    {
        var temporary = othersTemporary.ToDictionary(t => t.Mod, StringComparer.OrdinalIgnoreCase);
        var top       = (int?)null;
        foreach (var (mod, enabled, priority) in collection.Where(c => !temporary.ContainsKey(c.Mod)).Concat(temporary.Values))
            if (enabled && !skip(mod) && (top is null || priority > top))
                top = priority;
        return top ?? 0;
    }

    /// <summary> The first temporary priority of a try: above every mod that is on (<see cref="TopPriority"/>), at least the setting's. </summary>
    public static int TryPriority(int temporaryPriority, int top)
        => Math.Max(temporaryPriority, top + 1);

    /// <summary>
    /// The priority for a lower layer kept (a body, a face's own textures) under the upper layer that is on (skin
    /// textures, makeup), touching the upper layer as little as possible:
    /// - the kept part needs at least <paramref name="floor"/> to win its socket (int.MinValue: nothing to win against),
    ///   and keeps its own priority <paramref name="now"/> when that is higher, as any kept part does;
    /// - when every upper-layer mod is above that floor, the kept part goes just under the lowest of them (or stays where
    ///   it is), and nothing of the upper layer changes;
    /// - else an upper-layer mod with settings of its own that is not above the kept part is raised just above it (its
    ///   priority only);
    /// - an upper-layer mod whose settings come from a parent collection is never written: the kept part goes just under
    ///   it instead, even below its floor, and <c>Held</c> names that mod so the user can be told.
    /// </summary>
    /// <param name="over"> The upper-layer mods that are on: directory, priority, inherited from a parent collection. </param>
    public static (int Priority, List<(string Mod, int Priority)> Raise, string? Held) UnderLayers(int now, int floor,
        IReadOnlyList<(string Mod, int Priority, bool Inherited)> over)
    {
        var want  = Math.Max(now, floor);
        var raise = new List<(string Mod, int Priority)>();
        if (over.Count == 0)
            return (want, raise, null);

        var lowest = over.Min(o => o.Priority);
        if (floor == int.MinValue || lowest - 1 >= floor)
            return (Math.Min(want, lowest - 1), raise, null);

        // some of the upper layer is at or under the floor: the kept part takes its floor, under the inherited ones
        var priority = floor;
        string? held = null;
        foreach (var o in over.Where(o => o.Inherited).OrderBy(o => o.Priority))
            if (o.Priority - 1 < priority)
            {
                priority = o.Priority - 1;
                held   ??= o.Mod;
            }

        foreach (var o in over.Where(o => !o.Inherited && o.Priority <= priority).OrderBy(o => o.Mod, StringComparer.OrdinalIgnoreCase))
            raise.Add((o.Mod, priority + 1));
        return (priority, raise, held);
    }

    /// <summary>
    /// A part picked by a number (hair, face, tail, ears) shows only when the character has a number the mod replaces:
    /// the numbers the part has for the player (<paramref name="numbers"/>, from Penumbra's changed items). Unknown (no
    /// number known now, or none named for the part): it shows as far as anyone can tell.
    /// </summary>
    public static bool NumberShows(IReadOnlyCollection<int>? numbers, int? now)
        => now is not { } n || numbers is not { Count: > 0 } || numbers.Contains(n);

    /// <summary> Glamourer's slots of the right and the left ring finger. </summary>
    public const byte RightRingId = 12;
    public const byte LeftRingId  = 14;

    /// <summary>
    /// The finger slots a ring-slot part goes on (or comes off) for a hand: the right or the left finger alone, or every
    /// finger it has (<see cref="Hands.Both"/>). A hand the part has no slot for gives its own slots, as before.
    /// </summary>
    public static IReadOnlyList<byte> FingerSlots(IReadOnlyList<byte> slotIds, Hands hand)
    {
        if (hand == Hands.Both)
            return slotIds;
        var id = hand == Hands.Left ? LeftRingId : RightRingId;
        return slotIds.Contains(id) ? new[] { id } : slotIds;
    }

    /// <summary>
    /// Every gear slot a mod has a piece in (the slots it was put on in, the slots its items cover, the slots its files
    /// change): the whole mod is on, not only the kept part. Both rings count as one: a ring piece takes either hand.
    /// </summary>
    public static HashSet<string> SlotsOf(DressItem item, Library.LibMod? mod)
    {
        var slots = item.Picks.Select(p => Game.GearSlots.NameOf(p.SlotId)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mod is not null)
        {
            foreach (var pieces in mod.Items?.Slots ?? new List<Library.SlotPieces>())
                slots.Add(pieces.Slot);
            slots.UnionWith(mod.Facts?.GearSlots ?? new HashSet<string>());
        }

        if (slots.Contains("RFinger") || slots.Contains("LFinger"))
            slots.UnionWith(new[] { "RFinger", "LFinger" });
        return slots;
    }

    /// <summary>
    /// Which slot checks wait after a new one is asked for: a try's check replaces the last try's; a Keep's replaces the
    /// last Keep's only when it has something to look at. A try right after a Keep never drops the Keep's check.
    /// </summary>
    public static (T? Keep, T? Try) NextChecks<T>(T? keep, T? tryCheck, T? next, bool preview) where T : class
        => preview ? (keep, next) : (next ?? keep, tryCheck);

    /// <summary>
    /// Whether a due slot check looks now. A try's check runs while its try is on and goes when the try ends. A Keep's
    /// check never judges a try (the tried mods sit above everything for the moment): it waits until the try has ended,
    /// kept or put back. Both wait while the game redraws, a step waits, or the look from before a try is not back yet.
    /// </summary>
    public static CheckTurn TurnOf(bool tryCheck, bool tryOn, bool busy)
        => tryCheck
            ? !tryOn ? CheckTurn.Drop : busy ? CheckTurn.Later : CheckTurn.Run
            : tryOn || busy ? CheckTurn.Later : CheckTurn.Run;

    /// <summary>
    /// The gear slots that taking off one part of a mod empties. Where it can tell, only the places where that mod's own item
    /// shows now, never a slot that holds another mod's item or a plain game item:
    /// - nothing when the mod does not show in the part's socket at all;
    /// - a slot whose item is the part's own (one of its items, or the one the closet put on there for this mod);
    /// - in a socket of one slot, also the slot when the mod supplies what shows there (its model comes first).
    /// The two ring fingers share one socket, so a finger counts only by its item: tights worn through the right ring
    /// finger never take another mod's ring off the left hand with them.
    /// When the mod shows there but no slot is recognised as its own, every slot of the part is emptied, as before: so a
    /// slot of a one-slot socket is emptied whenever the mod shows there, and the fingers are told apart whenever they can be.
    /// </summary>
    /// <param name="slots"> The part's slot names ("Legs", "RFinger"…). </param>
    /// <param name="modShows"> The mod's files show in the part's socket. </param>
    /// <param name="modFirst"> The mod supplies the model there (first of the mods showing). </param>
    /// <param name="itemIn"> The item worn in a slot now, 0 for none known. </param>
    /// <param name="isOwn"> Whether an item in a slot is the part's own. </param>
    public static List<string> SlotsToEmpty(IEnumerable<string> slots, bool modShows, bool modFirst, Func<string, ulong> itemIn, Func<string, ulong, bool> isOwn)
    {
        var empty = new List<string>();
        if (!modShows)
            return empty;
        var all = slots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var slot in all)
        {
            var item   = itemIn(slot);
            var finger = slot.Equals("RFinger", StringComparison.OrdinalIgnoreCase) || slot.Equals("LFinger", StringComparison.OrdinalIgnoreCase);
            if (item != 0 && isOwn(slot, item) || !finger && modFirst)
                empty.Add(slot);
        }

        // the mod shows there but no place is recognised as its own (no item known, or none of them its): which place shows
        // it cannot be told, so every slot of the part is emptied, as before this rule. The rule never does less than that.
        return empty.Count > 0 ? empty : all;
    }

    /// <summary> How often a look Glamourer refused is offered again, and how many times before giving up. </summary>
    public static readonly TimeSpan RestoreEvery = TimeSpan.FromSeconds(1);
    public const int RestoreAttempts = 30;

    /// <summary>
    /// Whether a body part (socket BodyBase: skin, and since detector 3 a body mod's bare-skin models) is on: when any of
    /// its mod's files show on the character; and when it has no skin files of its own (its models show only where the
    /// character wears nothing, so under clothes none of them can show), when its mod is on in the collection.
    /// </summary>
    public static bool BodyPartOn(bool showsAnywhere, bool hasSkinFiles, bool enabled)
        => showsAnywhere || !hasSkinFiles && enabled;

    /// <summary> What to do with a look Glamourer has refused <paramref name="refused"/> times; the next try is due at <paramref name="due"/>. </summary>
    public static RestoreTurn RestoreTurnOf(int refused, DateTime now, DateTime due)
        => refused >= RestoreAttempts ? RestoreTurn.GiveUp : now < due ? RestoreTurn.Wait : RestoreTurn.Try;

    /// <summary>
    /// Runs one queued step. An exception never leaves the frame; the step it belongs to is marked Failed when it was
    /// still waiting, so it never stays "in progress" (that kept Undo off for good). False when it broke.
    /// </summary>
    public static bool RunStep(Action run, Guid? step, IDictionary<Guid, Closet.StepState> steps, Action<Exception> failed)
    {
        try
        {
            run();
            return true;
        }
        catch (Exception ex)
        {
            failed(ex);
            if (step is { } broken && steps.TryGetValue(broken, out var state) && state == Closet.StepState.Pending)
                steps[broken] = Closet.StepState.Failed;
            return false;
        }
    }
}
