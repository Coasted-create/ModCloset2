using ModCloset2.Game;

namespace ModCloset2.Wear;

/// <summary>
/// After a Keep or a try: a look at what really shows in each gear slot (port of the old Outfit.SlotCheck). A piece that
/// lost to another mod is put above it when that can't take anything away from the other mod; what can't be fixed is said
/// in one plain sentence.
/// </summary>
public sealed partial class Wardrobe
{
    private sealed class SlotCheck
    {
        /// <summary> Slot name → the mod meant to show there. </summary>
        public required Dictionary<string, DressItem> Wanted { get; init; }
        public required bool Preview { get; init; }
        public required Guid Collection { get; init; }
        public DateTime Due { get; set; }
        public int Attempts { get; set; }
        public List<string> Fixed { get; } = new();
    }

    // A Keep's check and a try's check are kept apart: a try started right after a Keep (the main way the closet is used)
    // must not drop the Keep's check, which is the one that may fix something.
    private SlotCheck? _keepCheck;
    private SlotCheck? _tryCheck;

    /// <summary> Asks for a check a moment after the game has loaded the new models. </summary>
    private void ScheduleSlotCheck(IReadOnlyList<DressItem> items, Guid collection, bool preview)
    {
        var wanted = new Dictionary<string, DressItem>(StringComparer.OrdinalIgnoreCase);
        // the later item is the one meant to show; the slot is the one the item went on (a ring mod's piece says "right
        // ring", but it may have gone on the left hand)
        foreach (var item in items)
            foreach (var (slotId, _) in item.Picks)
                if (GearSlots.ByName(GearSlots.NameOf(slotId) ?? string.Empty) is { } slot)
                    wanted[slot.Name] = item;

        var check = wanted.Count == 0
            ? null
            : new SlotCheck { Wanted = wanted, Preview = preview, Collection = collection, Due = DateTime.UtcNow.AddSeconds(1.8) };
        // a Keep of take-offs only has nothing to check and leaves a pending check alone
        (_keepCheck, _tryCheck) = WearRules.NextChecks(_keepCheck, _tryCheck, check, preview);
    }

    private void UpdateSlotCheck()
    {
        _keepCheck = UpdateCheck(_keepCheck);
        _tryCheck  = UpdateCheck(_tryCheck);
    }

    /// <summary> Runs a check when it is due. Returns the check while it still has work to do, null when done. </summary>
    private SlotCheck? UpdateCheck(SlotCheck? pending)
    {
        if (pending is not { } check || DateTime.UtcNow < check.Due || !_game.LoggedIn)
            return pending;
        // a Keep's check waits for a try to end; a redraw on its way or a look not back yet: look once it has settled
        switch (WearRules.TurnOf(check.Preview, _previewActive, RedrawBusy || _queue.Count > 0 || _pendingRestore is not null))
        {
            case CheckTurn.Drop:
                return null;
            case CheckTurn.Later:
                check.Due = DateTime.UtcNow.AddSeconds(0.5);
                return check;
        }

        // not while the game loads, and not in a frame where another call ran (the next frame then)
        if (!_pace.Take(IpcWork.SlotCheck))
            return check;
        FrameReport.Step("the slot check", check.Wanted.Count);
        if (_ipc.ResourceMap(PlayerIndex) is not { } map)
            return null;

        var truth  = Showing.ModelsBySlot(map, GamePaths.RootFull(_ipc.ModDirectory()));
        var bad    = new List<(string Slot, DressItem Wanted, string? Showing, string Note)>();
        var losers = new List<(string Slot, DressItem Wanted, string ShowingDir)>();
        var bare   = new List<(string Slot, DressItem Wanted)>();
        foreach (var (slot, item) in check.Wanted)
        {
            // nothing drawn there yet: no verdict
            if (!truth.TryGetValue(slot, out var showing))
                continue;
            if (showing is { } dir && dir.Equals(item.Mod, StringComparison.OrdinalIgnoreCase))
                continue;

            bad.Add((slot, item, showing, string.Empty));
            if (showing is { } other)
                losers.Add((slot, item, other));
            else
                bare.Add((slot, item));
        }

        // A try only reports: it was just put on with every piece above everything else, so a redraw and a second try
        // would show the same thing, and each redraw is a second's wait on every flip.
        var mayFix = !check.Preview;

        // the game's own item shows: the mod's item isn't on (something changed it back); put it on again, once
        if (mayFix && bare.Count > 0 && check.Attempts < 1)
        {
            check.Attempts++;
            var again = false;
            foreach (var (slot, item) in bare)
                if (item.Picks.Count > 0)
                {
                    PutOn(item);
                    again = true;
                    if (!check.Fixed.Contains(slot))
                        check.Fixed.Add(slot);
                }

            if (again)
            {
                RedrawPlayer();
                check.Due = DateTime.UtcNow.AddSeconds(1.8);
                return check;
            }
        }

        // put the losers above the mods that beat them, when that takes nothing away from those mods
        if (losers.Count > 0 && check.Attempts < 1)
        {
            check.Attempts++;
            var changed = false;
            foreach (var (slot, wanted, otherDir) in losers)
            {
                // the other mod is meant to show somewhere this mod also has a piece: raising this one would steal that slot
                // (every gear slot of the mod counts, as in the old plugin: the whole mod is on, not only the kept part)
                var otherSlots = check.Wanted.Where(kv => kv.Value.Mod.Equals(otherDir, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
                var mySlots    = SlotsOf(wanted);
                if (otherSlots.Any(mySlots.Contains))
                {
                    var i = bad.FindIndex(b => b.Slot == slot);
                    if (i >= 0)
                        bad[i] = bad[i] with { Note = Loc.F("{0} needs that place for another piece.", NameOf(otherDir)) };
                    continue;
                }

                if (mayFix && Raise(check, wanted, otherDir))
                {
                    changed = true;
                    if (!check.Fixed.Contains(slot))
                        check.Fixed.Add(slot);
                }
            }

            if (changed)
            {
                RedrawPlayer();
                check.Due = DateTime.UtcNow.AddSeconds(1.8);
                return check;
            }
        }

        if (bad.Count == 0)
            return null;

        var first = bad[0];
        var line = first.Showing is { } d
            ? Loc.F("{0} does not show: {1} is in front of it.", first.Wanted.Name, NameOf(d))
            : Loc.F("{0} does not show.", first.Wanted.Name);
        if (first.Note.Length > 0)
            line += " " + first.Note;
        Fail(line);
        return null;
    }

    /// <summary> Every gear slot a mod has a piece in: what Penumbra names, what its files cover, and what was put on. </summary>
    private HashSet<string> SlotsOf(DressItem item)
        => WearRules.SlotsOf(item, _mod(item.Mod));

    private string NameOf(string modDirectory)
        => _mod(modDirectory)?.Name ?? modDirectory;

    /// <summary> Puts a mod above another one (a real priority, or a temporary one during a try). </summary>
    private bool Raise(SlotCheck check, DressItem item, string aboveDir)
    {
        RefreshSettings(force: true);
        var above  = Effective(aboveDir) is { Enabled: true } o ? o.Priority : TopPriority();
        var mine   = Settings.TryGetValue(item.Mod, out var m) ? m.Priority : 0;
        var target = above + 1;
        if (check.Preview)
        {
            // temporary settings sit above everything; among them the order is what counts
            var top      = Math.Max(_settings.TemporaryPriority, TopPriority() + 1) + 100 + check.Attempts * 10 + check.Fixed.Count;
            var settings = item.Options.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
            return _ipc.SetTemporaryMod(PlayerIndex, item.Mod, top, settings) is Ipc.PenumbraSuccess or Ipc.PenumbraNothingChanged;
        }

        // already above it by priority and still losing: a priority can't help (the other is a temporary setting, say)
        if (mine >= target)
            return false;
        _log.Information($"Mod Closet 2: {item.Name} → priority {target} (above {aboveDir}) after a check of what shows");
        return _ipc.TrySetPriority(check.Collection, item.Mod, target) is Ipc.PenumbraSuccess;
    }
}
