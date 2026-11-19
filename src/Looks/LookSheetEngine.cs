using System.Text;
using ModCloset2.Core;

namespace ModCloset2.Looks;

// The look sheet (second round, docs/briefs/14-looks-2.md): what a look does with each slot, the character's colours and
// what is shown, and how it is worn by itself (a gearset, a job, a Glamourer design). Every change is one undo step.

public sealed partial class LooksEngine
{
    private readonly ILooksGame? _game;
    private readonly ILooksDesigns? _designs;

    public ILookSheet? Sheet
        => this;

    /// <summary> The outfit slots of the sheet, in rail order. </summary>
    private static readonly Socket[] SheetSlots =
    {
        Socket.Head, Socket.Body, Socket.Hands, Socket.Legs, Socket.Feet, Socket.Ears, Socket.Neck, Socket.Wrists, Socket.Rings,
        Socket.MainHand, Socket.OffHand,
    };

    /// <summary> The character's colours, in the sheet's order. </summary>
    public static readonly string[] ColourKeys = { "skin", "hair", "highlights", "eyeLeft", "eyeRight", "lips", "facePaint", "tattoo" };

    /// <summary> What can be shown or hidden, in the sheet's order. </summary>
    public static readonly string[] ShowingKeys = { "weapon", "hat", "visor" };

    private static string SlotId(Socket socket)
        => socket.ToString().ToLowerInvariant();

    private static Socket? SlotOf(string slot)
        => SheetSlots.Cast<Socket?>().FirstOrDefault(s => SlotId(s!.Value) == slot);

    private static string SlotName(Socket socket)
        => socket switch
        {
            Socket.Head     => Loc.T("Head slot"),
            Socket.Body     => Loc.T("Body slot"),
            Socket.Hands    => Loc.T("Hands slot"),
            Socket.Legs     => Loc.T("Legs slot"),
            Socket.Feet     => Loc.T("Feet slot"),
            Socket.Ears     => Loc.T("Ears slot"),
            Socket.Neck     => Loc.T("Neck slot"),
            Socket.Wrists   => Loc.T("Wrist slot"),
            Socket.Rings    => Loc.T("Ring slot"),
            Socket.MainHand => Loc.T("Main hand slot"),
            Socket.OffHand  => Loc.T("Off hand slot"),
            _               => string.Empty,
        };

    private static string ColourName(string key)
        => key switch
        {
            "skin"       => Loc.T("Skin"),
            "hair"       => Loc.T("Hair"),
            "highlights" => Loc.T("Highlights"),
            "eyeLeft"    => Loc.T("Left eye"),
            "eyeRight"   => Loc.T("Right eye"),
            "lips"       => Loc.T("Lips"),
            "facePaint"  => Loc.T("Face paint"),
            "tattoo"     => Loc.T("Tattoo"),
            _            => key,
        };

    private static (string Name, string On, string Off) ShowingWords(string key)
        => key switch
        {
            "weapon" => (Loc.T("Weapon"), Loc.T("Shown"), Loc.T("Sheathed")),
            "hat"    => (Loc.T("Hat"), Loc.T("Shown"), Loc.T("Hidden")),
            "visor"  => (Loc.T("Visor"), Loc.T("Down"), Loc.T("Up")),
            _        => (key, string.Empty, string.Empty),
        };

    private CharacterLook? CharacterNow()
    {
        try
        {
            return _outfit.Character;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Mod Closet 2 looks: the character's colours could not be read: {ex.GetType().Name}");
            return null;
        }
    }

    // ======================================================================= the sheet

    private (Guid Look, int Version, CharacterLook? Character, LookSheet Sheet)? _sheetMade;

    LookSheet? ILookSheet.Sheet(Guid look)
    {
        Refresh();
        if (Find(look) is not { } l)
            return null;
        var character = CharacterNow();
        if (_sheetMade is { } made && made.Look == look && made.Version == _version && ReferenceEquals(made.Character, character))
            return made.Sheet;

        var onNow = _base == l.Id && _now.Any;
        var pieces = new List<SheetPiece>();
        foreach (var socket in SheetSlots)
        {
            var piece = l.Pieces.FirstOrDefault(p => p.Socket == socket && !LookRules.IsBody(_lens.ShelfOf(p)));
            var keep  = piece is not null ? Keep.Saved : l.Empty.Contains(socket) ? Keep.Nothing : Keep.LeaveAlone;
            var part  = piece is null ? null : _library.Part(piece.Key);
            var worn  = _now.In(socket);
            pieces.Add(new SheetPiece
            {
                Slot     = SlotId(socket),
                SlotName = SlotName(socket),
                Keep     = keep,
                Part     = piece?.Key,
                Name     = part?.Name ?? piece?.Print?.Name ?? string.Empty,
                Picture  = part?.Picture,
                Options  = piece is null ? string.Empty : string.Join(" · ", piece.Options.Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value)}")),
                Missing  = piece is not null && Gone(piece),
                Changed  = onNow && keep switch
                {
                    Keep.Saved   => worn is null || worn.Key != piece!.Key,
                    Keep.Nothing => worn is not null,
                    _            => false,
                },
            });
        }

        var colours = ColourKeys.Select(key =>
        {
            var saved = l.MyColours.TryGetValue(key, out var rgb);
            uint now  = 0;
            var known = character?.Colours.TryGetValue(key, out now) == true;
            return new SheetColour
            {
                Key     = key,
                Name    = ColourName(key),
                Keep    = saved ? Keep.Saved : Keep.LeaveAlone,
                Rgb     = saved ? rgb : now,
                Changed = saved && known && now != rgb,
            };
        }).ToList();

        var showing = ShowingKeys.Select(key =>
        {
            var saved = l.Showing.TryGetValue(key, out var on);
            var now   = false;
            if (!saved)
                character?.Showing.TryGetValue(key, out now);
            var words = ShowingWords(key);
            return new SheetShowing { Key = key, Name = words.Name, Keep = saved ? Keep.Saved : Keep.LeaveAlone, On = saved ? on : now, OnWord = words.On, OffWord = words.Off };
        }).ToList();

        var sheet = new LookSheet
        {
            Id         = l.Id,
            Name       = NameOf(l),
            OnNow      = onNow,
            ChangeText = _wearing.Look == l.Id ? _wearing.ChangeText : string.Empty,
            Pieces     = pieces,
            Colours    = colours,
            Showing    = showing,
            Binding    = new SheetBinding
            {
                Gearset         = l.Gearset,
                Job             = l.Job,
                GlamourerDesign = l.GlamourerDesign,
                DesignName      = l.GlamourerDesign ? DesignName(l) : string.Empty,
                DesignProblem   = DesignProblem(),
                // Glamourer gives every design it takes a new identifier: a refresh is a new design (DesignManager.CreateClone)
                DesignNote      = l.GlamourerDesign && l.DesignId is not null
                    ? Loc.T("Each Update makes the design anew; point Glamourer's automation at it again after an Update.")
                    : string.Empty,
            },
        };
        _sheetMade = (look, _version, character, sheet);
        return sheet;
    }

    // ======================================================================= the commands

    public void SetPiece(Guid look, string slot, Keep keep)
    {
        Poll();
        if (Find(look) is not { } l || SlotOf(slot) is not { } socket)
            return;
        var piece = l.Pieces.FirstOrDefault(p => p.Socket == socket);
        var now   = piece is not null ? Keep.Saved : l.Empty.Contains(socket) ? Keep.Nothing : Keep.LeaveAlone;
        if (now == keep && keep != Keep.Saved)
            return;
        if (keep == Keep.Saved && piece is not null)
            return; // the saved piece stays
        var before = Snapshot(l.Id);
        switch (keep)
        {
            case Keep.Saved:
                // the piece that is on in that slot now
                if (_now.In(socket) is not { } worn)
                {
                    Say(Loc.F("Nothing is on in the {0} now: put something on there first.", SlotName(socket)));
                    return;
                }

                l.Pieces.Add(PieceOf(worn));
                l.Empty.Remove(socket);
                break;
            case Keep.Nothing:
                l.Pieces.RemoveAll(p => p.Socket == socket);
                if (!l.Empty.Contains(socket))
                    l.Empty.Add(socket);
                break;
            default:
                l.Pieces.RemoveAll(p => p.Socket == socket);
                l.Empty.Remove(socket);
                break;
        }

        Step(Loc.F("Change {0}", NameOf(l)), before, Snapshot(l.Id));
    }

    public void SetColour(Guid look, string key, Keep keep)
    {
        if (Find(look) is not { } l || !ColourKeys.Contains(key) || keep == Keep.Nothing)
            return;
        var saved = l.MyColours.ContainsKey(key);
        if (saved == (keep == Keep.Saved))
            return;
        uint rgb = 0;
        if (keep == Keep.Saved && CharacterNow()?.Colours.TryGetValue(key, out rgb) != true)
        {
            Say(Loc.T("Glamourer did not say which colours you wear now."));
            return;
        }

        var before = Snapshot(l.Id);
        if (keep == Keep.Saved)
            l.MyColours[key] = rgb & 0xFFFFFF;
        else
            l.MyColours.Remove(key);
        Step(Loc.F("Change {0}", NameOf(l)), before, Snapshot(l.Id));
    }

    public void SetColourValue(Guid look, string key, uint rgb)
    {
        if (Find(look) is not { } l || !ColourKeys.Contains(key))
            return;
        rgb &= 0xFFFFFF;
        if (l.MyColours.TryGetValue(key, out var was) && was == rgb)
            return;
        var before = Snapshot(l.Id);
        l.MyColours[key] = rgb;
        Step(Loc.F("Change {0}", NameOf(l)), before, Snapshot(l.Id));
    }

    public void TakeMineNow(Guid look)
    {
        if (Find(look) is not { } l)
            return;
        var before = Snapshot(l.Id);
        if (!TakeMine(l))
            return;
        Step(Loc.F("Change {0}", NameOf(l)), before, Snapshot(l.Id));
    }

    /// <summary> Every colour and showing the look keeps takes what is worn now; false when nothing changed. </summary>
    private bool TakeMine(LookData l)
    {
        if (CharacterNow() is not { } now)
            return false;
        var changed = false;
        foreach (var key in l.MyColours.Keys.ToList())
            if (now.Colours.TryGetValue(key, out var rgb) && l.MyColours[key] != (rgb & 0xFFFFFF))
            {
                l.MyColours[key] = rgb & 0xFFFFFF;
                changed          = true;
            }

        foreach (var key in l.Showing.Keys.ToList())
            if (now.Showing.TryGetValue(key, out var on) && l.Showing[key] != on)
            {
                l.Showing[key] = on;
                changed        = true;
            }

        return changed;
    }

    public void SetShowing(Guid look, string key, Keep keep, bool on)
    {
        if (Find(look) is not { } l || !ShowingKeys.Contains(key) || keep == Keep.Nothing)
            return;
        var saved = l.Showing.TryGetValue(key, out var was);
        if (keep == Keep.LeaveAlone ? !saved : saved && was == on)
            return;
        var before = Snapshot(l.Id);
        if (keep == Keep.Saved)
            l.Showing[key] = on;
        else
            l.Showing.Remove(key);
        Step(Loc.F("Change {0}", NameOf(l)), before, Snapshot(l.Id));
    }

    /// <summary> The colours and showings the look sets that differ from what is worn now (all of them when that is not known); null: none. </summary>
    private CharacterLook? CharacterToPut(LookData l)
    {
        if (l.MyColours.Count == 0 && l.Showing.Count == 0)
            return null;
        var now     = CharacterNow();
        var colours = l.MyColours.Where(kv => now is null || !now.Colours.TryGetValue(kv.Key, out var c) || c != kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var showing = l.Showing.Where(kv => now is null || !now.Showing.TryGetValue(kv.Key, out var s) || s != kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return colours.Count == 0 && showing.Count == 0 ? null : new CharacterLook(colours, showing);
    }

    // ======================================================================= worn by itself: gearsets and jobs

    public IReadOnlyList<BindChoice> Gearsets
    {
        get
        {
            IReadOnlyList<(int Number, string Name, string Job)> sets;
            try
            {
                sets = _game?.Gearsets ?? Array.Empty<(int, string, string)>();
            }
            catch
            {
                sets = Array.Empty<(int, string, string)>();
            }

            return sets.Select(g => new BindChoice
            {
                Id      = g.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name    = g.Job.Length > 0 ? $"{g.Number} · {g.Name} ({g.Job})" : $"{g.Number} · {g.Name}",
                BoundTo = _looks.FirstOrDefault(l => l.Pinned && l.Gearset == g.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } b ? NameOf(b) : null,
            }).ToList();
        }
    }

    public IReadOnlyList<BindChoice> Jobs
    {
        get
        {
            IReadOnlyList<(string Abbreviation, string Name)> jobs;
            try
            {
                jobs = _game?.Jobs ?? Array.Empty<(string, string)>();
            }
            catch
            {
                jobs = Array.Empty<(string, string)>();
            }

            return jobs.Select(j => new BindChoice
            {
                Id      = j.Abbreviation,
                Name    = j.Name,
                BoundTo = _looks.FirstOrDefault(l => l.Pinned && l.Job == j.Abbreviation) is { } b ? NameOf(b) : null,
            }).ToList();
        }
    }

    public void Bind(Guid look, string? gearset, string? job)
    {
        if (Find(look) is not { } l)
            return;
        gearset = string.IsNullOrWhiteSpace(gearset) ? null : gearset.Trim();
        job     = string.IsNullOrWhiteSpace(job) ? null : job.Trim();
        if (l.Gearset == gearset && l.Job == job)
            return;
        // one look per gearset and per job: the last one bound takes it over
        var others = _looks.Where(o => o != l && (gearset is not null && o.Gearset == gearset || job is not null && o.Job == job)).ToList();
        var before = Snapshot(others.Select(o => o.Id).Append(l.Id));
        foreach (var o in others)
        {
            if (gearset is not null && o.Gearset == gearset)
                o.Gearset = null;
            if (job is not null && o.Job == job)
                o.Job = null;
        }

        l.Gearset = gearset;
        l.Job     = job;
        Step(Loc.F("Change {0}", NameOf(l)), before, Snapshot(others.Select(o => o.Id).Append(l.Id)));
    }

    private int _gearsetSeen;
    private string? _jobSeen;

    /// <summary>
    /// Every frame: when the gearset or the job changed and a look is bound to it, that look is put on and kept (a gearset
    /// binding before a job binding). Never while something is tried: the user is in the middle of something.
    /// </summary>
    private void FollowGame()
    {
        if (_game is null || !_loaded)
            return;
        int gearset;
        string job;
        try
        {
            gearset = _game.CurrentGearset;
            job     = _game.CurrentJob ?? string.Empty;
        }
        catch
        {
            return;
        }

        if (_jobSeen is null)
        {
            // what is on when the closet starts is no change
            _gearsetSeen = gearset;
            _jobSeen     = job;
            return;
        }

        if (gearset == _gearsetSeen && job == _jobSeen)
            return;
        var gearsetChanged = gearset != _gearsetSeen && gearset > 0;
        var jobChanged     = job != _jobSeen && job.Length > 0;
        _gearsetSeen = gearset;
        _jobSeen     = job;
        if (_outfit.Trying || !gearsetChanged && !jobChanged)
            return;

        var id = gearset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string why;
        var bound = gearsetChanged ? _looks.FirstOrDefault(l => l.Pinned && l.Gearset == id) : null;
        if (bound is not null)
        {
            why = Loc.F("Put on {0} for gearset {1}", NameOf(bound), id);
        }
        else
        {
            bound = job.Length > 0 ? _looks.FirstOrDefault(l => l.Pinned && l.Job == job) : null;
            if (bound is null)
                return;
            why = Loc.F("Put on {0} for {1}", NameOf(bound), JobName(job));
        }

        TryColourway(bound.Id, bound.Colourway);
        if (_outfit.Trying && _outfit.TryingLook == bound.Id)
            _outfit.Keep();
        Say(why);
    }

    private string JobName(string abbreviation)
    {
        try
        {
            return _game?.Jobs.FirstOrDefault(j => j.Abbreviation == abbreviation).Name is { Length: > 0 } name ? name : abbreviation;
        }
        catch
        {
            return abbreviation;
        }
    }

    // ======================================================================= a Glamourer design

    private static string DesignName(LookData l, string name)
        => Loc.F("Closet · {0}", name);

    private string DesignName(LookData l)
        => DesignName(l, NameOf(l));

    private string DesignProblem()
    {
        if (_designs is null)
            return Loc.T("Glamourer designs are not available here.");
        try
        {
            return _designs.Problem;
        }
        catch
        {
            return Loc.T("Glamourer did not answer.");
        }
    }

    public void SetGlamourerDesign(Guid look, bool on)
    {
        if (Find(look) is not { } l)
            return;
        if (on && DesignProblem() is { Length: > 0 })
        {
            // no design through Glamourer: the design as text, for Glamourer's own Import button
            if (_designs is not null && _designs.Copy(DesignText(l)))
                Say(Loc.T("Design copied: paste it into Glamourer with its Import button."));
            else
                Say(DesignProblem());
            return;
        }

        if (l.GlamourerDesign == on)
        {
            if (on)
                RefreshDesign(l, asked: true);
            return;
        }

        var before = Snapshot(l.Id);
        l.GlamourerDesign = on;
        if (on && !RefreshDesign(l, asked: true))
            l.GlamourerDesign = false; // Glamourer did not take it: nothing is kept up to date
        if (l.GlamourerDesign)
            Say(Loc.F("Made the Glamourer design “{0}”.", DesignName(l)));
        Step(Loc.F("Change {0}", NameOf(l)), before, Snapshot(l.Id));
    }

    /// <summary>
    /// Makes the look's Glamourer design again (when it keeps one and Glamourer takes designs). Asked for by the user (the
    /// switch) and Glamourer turns out to take none: the design text goes on the clipboard instead. False when not made.
    /// </summary>
    private bool RefreshDesign(LookData l, bool asked = false)
    {
        if (!l.GlamourerDesign || _designs is null || DesignProblem().Length > 0)
            return false;
        try
        {
            if (_designs.Put(DesignName(l), LookDesign.Json(DesignName(l), DesignPieces(l), l.MyColours, l.Showing), l.DesignId) is { } id)
            {
                l.DesignId = id;
                return true;
            }

            if (asked && DesignProblem().Length > 0 && _designs.Copy(DesignText(l)))
                Say(Loc.T("Design copied: paste it into Glamourer with its Import button."));
            else
                Say(Loc.T("Glamourer did not take the design."));
            return false;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Mod Closet 2 looks: the Glamourer design could not be made: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary> The look as a Glamourer design text for its Import button (base64 of the design's JSON). </summary>
    private string DesignText(LookData l)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(LookDesign.Json(DesignName(l), DesignPieces(l), l.MyColours, l.Showing)));

    /// <summary> What the design holds of the look's pieces: the game item of each gear piece, and its mod with the options. </summary>
    private List<LookDesign.Piece> DesignPieces(LookData l)
    {
        var pieces = new List<LookDesign.Piece>();
        foreach (var piece in l.Pieces.Where(p => Array.IndexOf(SheetSlots, p.Socket) >= 0 && !LookRules.IsBody(_lens.ShelfOf(p))))
        {
            var part = _library.Part(piece.Key);
            ulong item;
            try
            {
                item = _library.ItemOf(piece.Key, Math.Max(0, piece.Version));
            }
            catch
            {
                item = 0;
            }

            pieces.Add(new LookDesign.Piece(piece.Socket, item, piece.Mod, part?.Name ?? piece.Print?.Name ?? piece.Mod, piece.Options));
        }

        return pieces;
    }
}
