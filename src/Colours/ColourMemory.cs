using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModCloset2.Core;

namespace ModCloset2.Colours;

// Pure code (no game, no Dalamud): linked into tests/ColourTests.

/// <summary> What the user chose for one area: follow an outfit colour, keep the mod's own colour, or a colour by hand. </summary>
/// <param name="Flat"> A hand colour given to the whole piece at once: every row takes it as it is, as a game dye does (no shading kept). </param>
public readonly record struct AreaChoice(ColourRole Follows, uint? Hand, bool Flat = false)
{
    /// <summary> The user said "keep this one": a hand colour, or the mod's own colour on purpose. </summary>
    public bool Pinned
        => Follows == ColourRole.Own;

    public static AreaChoice Own
        => new(ColourRole.Own, null);

    public static AreaChoice ByHand(uint rgb, bool flat = false)
        => new(ColourRole.Own, rgb & 0xFFFFFF, flat);

    public static AreaChoice Follow(ColourRole role)
        => new(role, null);

    public string Text
        => Hand is { } h ? (Flat ? "=" : string.Empty) + $"#{h:X6}" : Follows switch
        {
            ColourRole.Main   => "m",
            ColourRole.Second => "s",
            ColourRole.Accent => "a",
            ColourRole.Metal  => "x",
            _                 => "o",
        };

    public static AreaChoice? Parse(string? text)
        => text switch
        {
            "m" => Follow(ColourRole.Main),
            "s" => Follow(ColourRole.Second),
            "a" => Follow(ColourRole.Accent),
            "x" => Follow(ColourRole.Metal),
            "o" => Own,
            ['#', ..] when uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, null, out var v) => ByHand(v),
            ['=', '#', ..] when uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, null, out var v) => ByHand(v, flat: true),
            _ => null,
        };
}

/// <summary>
/// Everything the colours remember: the outfit colours, the switch, saved palettes, and per part (by its memory key) and
/// area (by its identity) what the user chose. Strings in the store (<see cref="IColourStore"/>), so wearing a mod again
/// brings its colours back, also after a restart.
/// </summary>
public sealed class ColourMemory
{
    public const string KeyPalette = "colours.palette";
    public const string KeyDress   = "colours.dress";
    public const string KeySaved   = "colours.saved";
    public const string KeyParts   = "colours.parts";

    /// <summary> Parts remembered at most; the ones used longest ago are forgotten first. </summary>
    public const int MaxParts = 3000;
    public const int MaxSaved = 12;

    /// <summary> The colours a first start begins with (the mockup's: black, white, rose, gold). </summary>
    public static readonly OutfitPalette Default = new() { Main = 0x25202C, Second = 0xE9E3EA, Accent = 0xC8668A, Metal = 0xEBC98E };

    /// <summary> The choices of a part made under one set of game dyes ("stain,stain2"; "" = made before dyes were remembered: any dyes). </summary>
    private sealed class DyeSet
    {
        public string Dyes { get; set; } = string.Empty;
        public Dictionary<string, AreaChoice> Areas { get; } = new(StringComparer.Ordinal);

        public DyeSet Clone()
        {
            var c = new DyeSet { Dyes = Dyes };
            foreach (var (id, a) in Areas)
                c.Areas[id] = a;
            return c;
        }
    }

    /// <summary>
    /// What the user chose for a part, per set of game dyes the piece wore (another outfit that dyes it otherwise has
    /// choices of its own, or none), most recently used first. At most <see cref="MaxDyeSets"/> sets per part.
    /// </summary>
    private sealed class PartEntry
    {
        public List<DyeSet> Sets { get; } = new();
        public long Used { get; set; }

        public DyeSet? Exact(string dyes)
            => Sets.FirstOrDefault(s => s.Dyes == dyes);

        /// <summary> The set that holds under these dyes: their own, else the one made before dyes were remembered. Unknown dyes: the newest. </summary>
        public DyeSet? For(string dyes)
            => dyes.Length > 0 ? Exact(dyes) ?? Exact(string.Empty) : Exact(string.Empty) ?? Sets.FirstOrDefault();

        public PartEntry Clone()
        {
            var e = new PartEntry { Used = Used };
            e.Sets.AddRange(Sets.Select(s => s.Clone()));
            return e;
        }

        public bool HasChoices
            => Sets.Any(s => s.Areas.Count > 0);
    }

    /// <summary> Dye sets kept per part (outfits that dye the piece differently); the one used longest ago goes first. </summary>
    public const int MaxDyeSets = 4;

    private readonly Dictionary<string, PartEntry> _parts = new(StringComparer.Ordinal);
    private long _clock;

    public OutfitPalette Palette { get; set; } = Default;
    public bool DressEverything { get; set; }
    public List<OutfitPalette> Saved { get; } = new();

    // ---- areas ----

    /// <summary>
    /// The choice for an area of a part under the game dyes the piece wears (<paramref name="dyes"/>, "" not known), or
    /// null when the user never chose there (then the defaults apply).
    /// </summary>
    public AreaChoice? Choice(string part, AreaDef area, string dyes = "")
    {
        if (!_parts.TryGetValue(part, out var entry) || entry.For(dyes) is not { } set)
            return null;
        if (set.Areas.TryGetValue(area.Id, out var exact))
            return exact;
        // the grouping may have moved a little (another anchor row): any of the area's rows will do, heaviest first
        foreach (var row in area.Rows)
            if (set.Areas.TryGetValue(row.Id, out var c))
                return c;
        return null;
    }

    /// <summary>
    /// The part has choices, but none that hold under these dyes (they were made under other dyes: another outfit). Choices
    /// made before dyes were remembered hold under any dyes and are never dormant.
    /// </summary>
    public bool Dormant(string part, string dyes)
        => dyes.Length > 0 && _parts.TryGetValue(part, out var entry) && entry.HasChoices && entry.For(dyes) is null;

    /// <param name="dyes">
    /// The game dyes the piece wears now ("" not known). The choice goes into the set for these dyes; the sets for other
    /// dyes stay as they are. Choices made before dyes were remembered are copied into the new set first (and stay, for
    /// every other set of dyes).
    /// </param>
    public void Choose(string part, AreaDef area, AreaChoice? choice, string dyes = "")
    {
        if (!_parts.TryGetValue(part, out var entry))
        {
            if (choice is null)
                return;
            _parts[part] = entry = new PartEntry();
        }

        var set = dyes.Length > 0 ? entry.Exact(dyes) : entry.For(dyes);
        if (set is null && dyes.Length > 0 && entry.Exact(string.Empty) is { } any)
        {
            // colours from before dyes were remembered: copied into a set for these dyes and changed there; the old set
            // stays as it is and keeps showing under every other set of dyes
            set      = any.Clone();
            set.Dyes = dyes;
        }

        if (set is null)
            entry.Sets.Insert(0, set = new DyeSet { Dyes = dyes });
        entry.Sets.Remove(set);
        entry.Sets.Insert(0, set);

        // one entry per area: drop entries stored under other rows of the same area
        foreach (var row in area.Rows)
            set.Areas.Remove(row.Id);
        set.Areas.Remove(area.Id);
        if (choice is { } c)
            set.Areas[area.Id] = c;
        if (set.Areas.Count == 0)
            entry.Sets.Remove(set);
        while (entry.Sets.Count > MaxDyeSets)
            entry.Sets.Remove(entry.Sets.Last(s => s.Dyes.Length > 0 && s != set)); // the any-dyes set stays: it shows everywhere else
        entry.Used = ++_clock;
        if (!entry.HasChoices)
            _parts.Remove(part);
        Trim();
    }

    /// <summary> Marks a part as worn just now (so it is not forgotten). </summary>
    public void Touch(string part)
    {
        if (_parts.TryGetValue(part, out var entry))
            entry.Used = ++_clock;
    }

    public bool Knows(string part)
        => _parts.ContainsKey(part);

    private void Trim()
    {
        if (_parts.Count <= MaxParts)
            return;
        foreach (var key in _parts.OrderBy(kv => kv.Value.Used).Take(_parts.Count - MaxParts).Select(kv => kv.Key).ToList())
            _parts.Remove(key);
    }

    // ---- renames ----

    /// <summary>
    /// Penumbra renamed a mod's folder: the parts of that mod are remembered under the new name. Returns whether anything
    /// moved. A part already remembered under the new name keeps the moved choices.
    /// </summary>
    public bool RenameMod(string oldDirectory, string newDirectory)
    {
        var moved = false;
        foreach (var key in _parts.Keys.ToList())
        {
            if (!PartKey.TryParse(key, out var part) || !part.Mod.Equals(oldDirectory, StringComparison.OrdinalIgnoreCase))
                continue;
            var renamed = (part with { Mod = newDirectory }).ToString();
            if (renamed == key)
                continue;
            _parts[renamed] = _parts[key];
            _parts.Remove(key);
            moved = true;
        }

        return moved;
    }

    /// <summary>
    /// Gives <paramref name="to"/> the choices of <paramref name="from"/> as they are (hand colours, "its own", what
    /// follows which outfit colour), when <paramref name="to"/> has none and <paramref name="from"/> has some. Returns
    /// whether anything was copied. Area names need no mapping: they are "material index:material file name:row", and
    /// a copy of a mod has the same models and the same material file names (only the mod folder differs).
    /// </summary>
    public bool CopyPart(string from, string to)
    {
        if (from == to || !_parts.TryGetValue(from, out var source) || !source.HasChoices || _parts.ContainsKey(to))
            return false;
        var copy = source.Clone();
        copy.Used = ++_clock;
        _parts[to] = copy;
        Trim();
        return true;
    }

    // ---- copies (a colour try works on a copy and drops or keeps it) ----

    /// <summary> A full copy: palette, switch, saved palettes and every part's choices. </summary>
    public ColourMemory Copy()
    {
        var copy = new ColourMemory { Palette = Palette, DressEverything = DressEverything, _clock = _clock };
        copy.Saved.AddRange(Saved);
        foreach (var (key, entry) in _parts)
            copy._parts[key] = entry.Clone();

        return copy;
    }

    /// <summary> Takes palette, switch and choices from <paramref name="other"/> (the saved palettes stay as they are). </summary>
    public void Assign(ColourMemory other)
    {
        Palette         = other.Palette;
        DressEverything = other.DressEverything;
        _clock          = Math.Max(_clock, other._clock);
        _parts.Clear();
        foreach (var (key, entry) in other.Copy()._parts)
            _parts[key] = entry;
    }

    /// <summary> The parts whose choices differ between two memories (when they were last used does not count). </summary>
    public static IEnumerable<string> Differing(ColourMemory a, ColourMemory b)
    {
        foreach (var key in a._parts.Keys.Union(b._parts.Keys, StringComparer.Ordinal))
        {
            a._parts.TryGetValue(key, out var x);
            b._parts.TryGetValue(key, out var y);
            if (x is null || y is null || Choices(x).ToJsonString() != Choices(y).ToJsonString())
                yield return key;
        }
    }

    // ---- saved palettes ----

    public static bool Near(OutfitPalette a, OutfitPalette b)
        => Close(a.Main, b.Main) && Close(a.Second, b.Second) && Close(a.Accent, b.Accent) && Close(a.Metal, b.Metal);

    private static bool Close(uint a, uint b)
        => ColourMath.OkDistance(Rgb.FromHex(a), Rgb.FromHex(b)) < 1.5f;

    /// <summary> Saves the palette in use (once; the oldest goes when there are too many). </summary>
    public bool SaveCurrent()
    {
        if (Saved.Any(s => Near(s, Palette)))
            return false;
        Saved.Add(Palette);
        while (Saved.Count > MaxSaved)
            Saved.RemoveAt(0);
        return true;
    }

    // ---- the store ----

    public static string Format(OutfitPalette p)
        => $"{p.Main:X6} {p.Second:X6} {p.Accent:X6} {p.Metal:X6}";

    public static OutfitPalette? ParsePalette(string? text)
    {
        var parts = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
            return null;
        var v = new uint[4];
        for (var i = 0; i < 4; ++i)
            if (!uint.TryParse(parts[i], NumberStyles.HexNumber, null, out v[i]))
                return null;
        return new OutfitPalette { Main = v[0] & 0xFFFFFF, Second = v[1] & 0xFFFFFF, Accent = v[2] & 0xFFFFFF, Metal = v[3] & 0xFFFFFF };
    }

    public void Load(IColourStore store)
    {
        Palette         = ParsePalette(Safe(store, KeyPalette)) ?? Default;
        DressEverything = Safe(store, KeyDress) == "1";
        Saved.Clear();
        foreach (var s in Safe(store, KeySaved).Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (ParsePalette(s) is { } p)
                Saved.Add(p);
        _parts.Clear();
        ReadParts(Safe(store, KeyParts));
    }

    public void Save(IColourStore store)
    {
        try
        {
            store.Set(KeyPalette, Format(Palette));
            store.Set(KeyDress, DressEverything ? "1" : string.Empty);
            store.Set(KeySaved, string.Join(';', Saved.Select(Format)));
            store.Set(KeyParts, PartsJson(_parts.Keys));
        }
        catch
        {
            // the store is the foundation's; a failure there must not break colouring
        }
    }

    private static string Safe(IColourStore store, string key)
    {
        try
        {
            return store.Get(key) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// {"part": {"u": used, "a": {"area": "m" | "s" | "a" | "x" | "o" | "#RRGGBB" | "=#RRGGBB"}, "d": "stain,stain2",
    /// "o": {"stain,stain2": {"area": …}}}}: "a" and "d" are the dye set used last ("d" left out: any dyes), "o" the sets
    /// for other dyes, newest first.
    /// </summary>
    private string PartsJson(IEnumerable<string> keys)
    {
        var root = new JsonObject();
        foreach (var key in keys)
        {
            if (!_parts.TryGetValue(key, out var entry))
            {
                root[key] = null; // known to be without choices (for undo snapshots)
                continue;
            }

            var o = Choices(entry);
            o.Insert(0, "u", entry.Used);
            root[key] = o;
        }

        return root.ToJsonString();
    }

    private static JsonObject Choices(PartEntry entry)
    {
        static JsonObject Areas(DyeSet set)
        {
            var areas = new JsonObject();
            foreach (var (id, c) in set.Areas.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                areas[id] = c.Text;
            return areas;
        }

        var o = new JsonObject();
        if (entry.Sets.Count == 0)
            return o;
        o["a"] = Areas(entry.Sets[0]);
        if (entry.Sets[0].Dyes.Length > 0)
            o["d"] = entry.Sets[0].Dyes;
        if (entry.Sets.Count > 1)
        {
            var others = new JsonObject();
            foreach (var set in entry.Sets.Skip(1))
                others[set.Dyes] = Areas(set);
            o["o"] = others;
        }

        return o;
    }

    private void ReadParts(string json, bool replace = false)
    {
        if (json.Length == 0)
            return;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
                return;
            foreach (var (key, node) in root)
            {
                if (node is not JsonObject o)
                {
                    if (replace)
                        _parts.Remove(key);
                    continue;
                }

                var entry = new PartEntry { Used = o["u"]?.GetValue<long>() ?? 0 };
                void Read(string dyes, JsonNode? areasNode)
                {
                    if (areasNode is not JsonObject areas || entry.Exact(dyes) is not null || entry.Sets.Count >= MaxDyeSets)
                        return;
                    var set = new DyeSet { Dyes = dyes };
                    foreach (var (id, value) in areas)
                        if (AreaChoice.Parse(value?.GetValue<string>()) is { } c)
                            set.Areas[id] = c;
                    if (set.Areas.Count > 0)
                        entry.Sets.Add(set);
                }

                Read(o["d"]?.GetValue<string>() ?? string.Empty, o["a"]);
                if (o["o"] is JsonObject others)
                    foreach (var (dyes, set) in others)
                        Read(dyes, set);
                if (entry.HasChoices)
                    _parts[key] = entry;
                else if (replace)
                    _parts.Remove(key);
                _clock = Math.Max(_clock, entry.Used);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            // a damaged memory: start with what could be read
        }
    }

    // ---- undo snapshots ----

    /// <summary>
    /// The outfit colours, the switch and the choices of the given parts, as one string (what an undo step holds: a colour
    /// step only ever changes those).
    /// </summary>
    public string Snapshot(IEnumerable<string> parts)
    {
        var o = new JsonObject
        {
            ["p"] = Format(Palette),
            ["d"] = DressEverything,
            ["c"] = JsonNode.Parse(PartsJson(parts.Distinct(StringComparer.Ordinal))),
        };
        return o.ToJsonString();
    }

    /// <summary> Puts a snapshot back. Returns false when it cannot be read. </summary>
    public bool Restore(string snapshot)
    {
        try
        {
            if (JsonNode.Parse(snapshot) is not JsonObject o)
                return false;
            if (ParsePalette(o["p"]?.GetValue<string>()) is { } p)
                Palette = p;
            DressEverything = o["d"]?.GetValue<bool>() ?? DressEverything;
            if (o["c"] is JsonObject c)
                ReadParts(c.ToJsonString(), replace: true);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
