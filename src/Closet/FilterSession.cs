using System.Runtime.CompilerServices;
using ModCloset2.Core;
using ModCloset2.Library;

namespace ModCloset2.Closet;

// The filters over the cards shown (a shelf after the kind chip, or the search results): the slot, the body and the race
// chips. They combine (a card must pass every chosen one); each list of chips is counted over the cards the other two
// chosen chips leave, so every chip offered has cards. A chosen chip that has no card left reads "" again: the newest
// chosen goes first, so the grid is never left empty by the chips.

public sealed partial class ClosetSession
{
    /// <summary> The races in the game's order, by their English names (the race chips' ids). </summary>
    private static readonly string[] RaceIds = { "Hyur", "Elezen", "Lalafell", "Miqo'te", "Roegadyn", "Au Ra", "Hrothgar", "Viera" };

    /// <summary> A race's name, translated. </summary>
    private static string RaceName(int index)
        => index switch
        {
            0 => Loc.T("Hyur"),
            1 => Loc.T("Elezen"),
            2 => Loc.T("Lalafell"),
            3 => Loc.T("Miqo'te"),
            4 => Loc.T("Roegadyn"),
            5 => Loc.T("Au Ra"),
            6 => Loc.T("Hrothgar"),
            7 => Loc.T("Viera"),
            _ => string.Empty,
        };

    /// <summary>
    /// The race of a game race code, as an index into <see cref="RaceIds"/>: male and female (and the children's models)
    /// of a race are one race, Midlander and Highlander are Hyur; -1 for codes of no player race (NPCs).
    /// </summary>
    public static int RaceIndexOf(int code)
        => code <= 0 ? -1 : (code / 100) switch
        {
            1 or 2 or 3 or 4 => 0,
            5 or 6           => 1,
            11 or 12         => 2,
            7 or 8           => 3,
            9 or 10          => 4,
            13 or 14         => 5,
            15 or 16         => 6,
            17 or 18         => 7,
            _                => -1,
        };

    /// <summary>
    /// The races a part's model of one race code is worn by, as bits of <see cref="RaceIds"/>: its own race (male, female
    /// and the children's models together; Highlander is Hyur), and for the Midlander models (101, 201) also Elezen,
    /// Miqo'te and Au Ra, whose bodies are the Midlander one and get it drawn unchanged. Lalafell, Roegadyn, Hrothgar and
    /// Viera wear only their own: the game's last fallback to Midlander does not fit them (a Rue top is not for a Lalafell).
    /// 0 for codes of no player race. The one table the race chips and <see cref="Card.Races"/> go by.
    /// </summary>
    public static int RaceMaskOfCode(int code)
    {
        if (RaceIndexOf(code) is not (var own and >= 0))
            return 0;
        var mask = 1 << own;
        if (code is 101 or 201)
            mask |= 1 << 1 | 1 << 3 | 1 << 5; // Elezen, Miqo'te, Au Ra
        return mask;
    }

    /// <summary> The chip id of cards tied to no body or no race. </summary>
    private const string AnyChip = "any";

    // body names → bits (up to 64 bodies; a library with more leaves the rest out of the body chips)
    private readonly Dictionary<string, int> _bodyBits = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _bodyNames = new();

    private int BodyBit(string body)
    {
        if (_bodyBits.TryGetValue(body, out var bit))
            return bit;
        if (_bodyNames.Count >= 64)
            return -1;
        bit = _bodyNames.Count;
        _bodyNames.Add(body);
        _bodyBits[body] = bit;
        return bit;
    }

    /// <summary> What the filters read of each card of a list: its slot bits, race bits and body bits. </summary>
    private sealed class Facets
    {
        public required int[] Slots;
        public required int[] Races;
        public required ulong[] Bodies;
    }

    private readonly ConditionalWeakTable<List<LibPart>, Facets> _facets = new();

    /// <summary> The filters' view of a card list, made once per list (the lists are new whenever they change). </summary>
    private Facets FacetsOf(List<LibPart> leads)
    {
        if (_facets.TryGetValue(leads, out var made))
            return made;
        made = new Facets { Slots = new int[leads.Count], Races = new int[leads.Count], Bodies = new ulong[leads.Count] };
        for (var i = 0; i < leads.Count; ++i)
        {
            var lead = leads[i];
            made.Slots[i] = SlotMask(lead);
            if (_thingParts.TryGetValue(LeadOf(lead.Key), out var thing))
            {
                foreach (var p in thing)
                {
                    made.Races[i]  |= RaceMask(p);
                    made.Bodies[i] |= BodyMask(p);
                }
            }
            else
            {
                made.Races[i]  = RaceMask(lead);
                made.Bodies[i] = BodyMask(lead);
            }
        }

        _facets.AddOrUpdate(leads, made);
        return made;
    }

    private static int RaceMask(LibPart part)
    {
        var mask = 0;
        foreach (var code in part.Info.RaceCodes)
            mask |= RaceMaskOfCode(code);
        return mask;
    }

    private ulong BodyMask(LibPart part)
    {
        var mask = 0UL;
        foreach (var body in part.Info.Bodies)
            if (body.Length > 0 && BodyBit(body) is var b and >= 0)
                mask |= 1UL << b;
        return mask;
    }

    /// <summary> The races a card's thing is made for, translated, in the game's order (<see cref="Card.Races"/>). </summary>
    private IReadOnlyList<string> RacesOf(IReadOnlyList<LibPart> thing)
    {
        var mask = 0;
        foreach (var p in thing)
            mask |= RaceMask(p);
        if (mask == 0)
            return Array.Empty<string>();
        var names = new List<string>();
        for (var i = 0; i < RaceIds.Length; ++i)
            if ((mask & 1 << i) != 0)
                names.Add(RaceName(i));
        return names;
    }

    /// <summary> The body mods a card's thing is made for, as the detector names them (<see cref="Card.Bodies"/>). </summary>
    private static IReadOnlyList<string> BodiesOf(IReadOnlyList<LibPart> thing)
    {
        if (thing.Count == 1)
            return thing[0].Info.Bodies.Count == 0 ? Array.Empty<string>() : thing[0].Info.Bodies;
        var names = new List<string>();
        foreach (var p in thing)
            foreach (var body in p.Info.Bodies)
                if (body.Length > 0 && !names.Contains(body, StringComparer.OrdinalIgnoreCase))
                    names.Add(body);
        return names;
    }

    // ---- the chosen chips ----

    private string _bodyChip = string.Empty;
    private string _raceChip = string.Empty;
    private IReadOnlyList<KindChip> _bodyChips = Array.Empty<KindChip>();
    private IReadOnlyList<KindChip> _raceChips = Array.Empty<KindChip>();

    /// <summary> The filters in the order they were chosen, newest last (the newest is let go first when nothing is left). </summary>
    private readonly List<char> _chipOrder = new();

    private void Chose(char filter)
    {
        _chipOrder.Remove(filter);
        _chipOrder.Add(filter);
    }

    public IReadOnlyList<KindChip> BodyChips
    {
        get
        {
            Sync();
            return _bodyChips;
        }
    }

    public string BodyChip
    {
        get
        {
            Sync();
            return _bodyChip;
        }
        set
        {
            value ??= string.Empty;
            if (value == _bodyChip)
                return;
            _bodyChip = value;
            Chose('b');
            Changed();
        }
    }

    public IReadOnlyList<KindChip> RaceChips
    {
        get
        {
            Sync();
            return _raceChips;
        }
    }

    public string RaceChip
    {
        get
        {
            Sync();
            return _raceChip;
        }
        set
        {
            value ??= string.Empty;
            if (value == _raceChip)
                return;
            _raceChip = value;
            Chose('r');
            Changed();
        }
    }

    private (List<LibPart> Leads, string Slot, string Body, string Race, int Loc, List<LibPart> Shown)? _filtered;

    /// <summary>
    /// The chips over a card list and the cards the chosen ones leave (made again only when the list, a chosen chip or the
    /// language changes).
    /// </summary>
    private List<LibPart> Filter(List<LibPart> leads)
    {
        if (_filtered is { } f && ReferenceEquals(f.Leads, leads) && f.Slot == _slotChip && f.Body == _bodyChip && f.Race == _raceChip && f.Loc == Loc.Version)
            return f.Shown;

        var facets = FacetsOf(leads);
        int[] slotCounts, raceCounts;
        int raceAny, bodyAny;
        Dictionary<int, int> bodyCounts;
        while (true)
        {
            var slotSel = _slotChip.Length == 0 ? 0 : SlotOrder.Where(b => SlotChipId(b) == _slotChip).Select(b => 1 << b).FirstOrDefault();
            var raceSel = RaceSelection(_raceChip);
            var bodySel = BodySelection(_bodyChip);
            (slotCounts, raceCounts, raceAny, bodyCounts, bodyAny) = Count(facets, slotSel, raceSel, bodySel);

            // a chosen chip with no card under the other chosen ones: let go, the newest first
            var dead = new List<char>();
            if (_slotChip.Length > 0 && (slotSel == 0 || slotCounts[BitIndex(slotSel)] == 0))
                dead.Add('s');
            if (raceSel is { } rs && (rs < 0 || (rs == 0 ? raceAny : raceCounts[BitIndex(rs)]) == 0))
                dead.Add('r');
            if (bodySel is { } bs && (bs == ulong.MaxValue || (bs == 0 ? bodyAny : bodyCounts.GetValueOrDefault(BitIndex(bs))) == 0))
                dead.Add('b');
            if (dead.Count == 0)
                break;
            var go = dead.OrderByDescending(d => _chipOrder.IndexOf(d)).First();
            if (go == 's')
                _slotChip = string.Empty;
            else if (go == 'r')
                _raceChip = string.Empty;
            else
                _bodyChip = string.Empty;
            _chipOrder.Remove(go);
        }

        // the lists of chips
        var slots = SlotOrder.Where(b => slotCounts[b] > 0).ToList();
        _slotChips = slots.Count < 2 ? Array.Empty<KindChip>()
            : slots.Select(b => new KindChip { Id = SlotChipId(b), Name = SlotBitName(b), Count = slotCounts[b] }).ToList();

        var races = new List<KindChip>();
        for (var i = 0; i < RaceIds.Length; ++i)
            if (raceCounts[i] > 0)
                races.Add(new KindChip { Id = RaceIds[i], Name = RaceName(i), Count = raceCounts[i] });
        if (races.Count > 0 && raceAny > 0)
            races.Add(new KindChip { Id = AnyChip, Name = Loc.T("Any race"), Count = raceAny });
        _raceChips = races;

        var bodies = bodyCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => _bodyNames[kv.Key], StringComparer.OrdinalIgnoreCase)
            .Select(kv => new KindChip { Id = _bodyNames[kv.Key], Name = _bodyNames[kv.Key], Count = kv.Value }).ToList();
        if (bodies.Count > 0 && bodyAny > 0)
            bodies.Add(new KindChip { Id = AnyChip, Name = Loc.T("Any body"), Count = bodyAny });
        _bodyChips = bodies;

        // the cards they leave
        List<LibPart> shown;
        if (_slotChip.Length == 0 && _raceChip.Length == 0 && _bodyChip.Length == 0)
        {
            shown = leads;
        }
        else
        {
            var slotSel = _slotChip.Length == 0 ? 0 : SlotOrder.Where(b => SlotChipId(b) == _slotChip).Select(b => 1 << b).FirstOrDefault();
            var raceSel = RaceSelection(_raceChip);
            var bodySel = BodySelection(_bodyChip);
            shown = new List<LibPart>();
            for (var i = 0; i < leads.Count; ++i)
                if (Passes(facets, i, slotSel, raceSel, bodySel))
                    shown.Add(leads[i]);
        }

        _filtered = (leads, _slotChip, _bodyChip, _raceChip, Loc.Version, shown);
        return shown;
    }

    /// <summary> The race chip as bits: null when no chip is chosen (or none matches), 0 for "Any race". </summary>
    private static int? RaceSelection(string chip)
    {
        if (chip.Length == 0)
            return null;
        if (chip == AnyChip)
            return 0;
        var i = Array.IndexOf(RaceIds, chip);
        return i < 0 ? -1 : 1 << i;
    }

    /// <summary> The body chip as bits: null when no chip is chosen, 0 for "Any body", -1 for a body no card has. </summary>
    private ulong? BodySelection(string chip)
    {
        if (chip.Length == 0)
            return null;
        if (chip == AnyChip)
            return 0;
        return _bodyBits.TryGetValue(chip, out var b) ? 1UL << b : ulong.MaxValue;
    }

    private static int BitIndex(int bits)
        => bits <= 0 ? 0 : System.Numerics.BitOperations.TrailingZeroCount(bits);

    private static int BitIndex(ulong bits)
        => bits == 0 || bits == ulong.MaxValue ? 0 : System.Numerics.BitOperations.TrailingZeroCount(bits);

    private static bool PassRace(int races, int? sel)
        => sel is not { } s || (s == 0 ? races == 0 : s > 0 && (races & s) != 0);

    private static bool PassBody(ulong bodies, ulong? sel)
        => sel is not { } s || (s == 0 ? bodies == 0 : s != ulong.MaxValue && (bodies & s) != 0);

    private static bool Passes(Facets f, int i, int slotSel, int? raceSel, ulong? bodySel)
        => (slotSel == 0 || (f.Slots[i] & slotSel) != 0) && PassRace(f.Races[i], raceSel) && PassBody(f.Bodies[i], bodySel);

    /// <summary>
    /// Each filter's counts over the cards the other two chosen filters leave: slot bits, races (and "any"), bodies (and
    /// "any"). A card counts once for each of its slots, races and bodies.
    /// </summary>
    private static (int[] Slots, int[] Races, int RaceAny, Dictionary<int, int> Bodies, int BodyAny) Count(Facets f, int slotSel, int? raceSel, ulong? bodySel)
    {
        var slots  = new int[RingLeftBit + 1];
        var races  = new int[RaceIds.Length];
        var bodies = new Dictionary<int, int>();
        int raceAny = 0, bodyAny = 0;
        for (var i = 0; i < f.Slots.Length; ++i)
        {
            bool slotOk = slotSel == 0 || (f.Slots[i] & slotSel) != 0, raceOk = PassRace(f.Races[i], raceSel), bodyOk = PassBody(f.Bodies[i], bodySel);
            if (raceOk && bodyOk)
                for (int mask = f.Slots[i], s = 0; mask != 0; ++s, mask >>= 1)
                    if ((mask & 1) != 0)
                        slots[s]++;
            if (slotOk && bodyOk)
            {
                if (f.Races[i] == 0)
                    raceAny++;
                for (int mask = f.Races[i], r = 0; mask != 0; ++r, mask >>= 1)
                    if ((mask & 1) != 0)
                        races[r]++;
            }

            if (slotOk && raceOk)
            {
                if (f.Bodies[i] == 0)
                    bodyAny++;
                var b = 0;
                for (var mask = f.Bodies[i]; mask != 0; ++b, mask >>= 1)
                    if ((mask & 1) != 0)
                        bodies[b] = bodies.GetValueOrDefault(b) + 1;
            }
        }

        return (slots, races, raceAny, bodies, bodyAny);
    }
}
