using System.Text.RegularExpressions;
using ModCloset2.Game;

namespace ModCloset2.Library;

/// <summary>
/// Step 1 of knowing a mod: what Penumbra's "changed items" say it changes (port of the old Catalog.Classify). Gear is
/// recognised by item name, hair / face / tail / ears by Penumbra's "Customization: …" names, the rest by its kind.
/// No Dalamud types: the item list comes in as a lookup.
/// </summary>
public static partial class ChangedItems
{
    // "Customization: <Race> [<Clan>] <Gender> <Part> [(Marker)] <N>"; a clan alone also counts as the race (older names).
    [GeneratedRegex(@"^\s*(?:Customization\s*:\s*)?(?<race>Hyur|Midlander|Highlander|Elezen|Lalafell|Miqo'?te|Roegadyn|Au\s*Ra|Hrothgar|Viera|Wildwood|Duskwight|Plainsfolk|Dunesfolk|Seekers?\s+of\s+the\s+Sun|Keepers?\s+of\s+the\s+Moon|Sea\s*Wolf|Hellsguard|Raen|Xaela|Helions?|The\s+Lost|Rava|Veena)(?:\s+(?<clan>Midlander|Highlander|Wildwood|Duskwight|Plainsfolk|Dunesfolk|Seekers?\s+of\s+the\s+Sun|Keepers?\s+of\s+the\s+Moon|Sea\s*Wolf|Hellsguard|Raen|Xaela|Helions?|The\s+Lost|Rava|Veena))?\s+(?<gender>Male|Female)\s+(?<kind>Hairstyle|Hair|Face|Tail|Ears?|Body)\s*(?:\((?<marker>[^)]*)\))?\s*(?<n>\d+)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CustomizationRegex();

    private enum Kind { Hair, Face, Tail, Ears, Body, Animation, Effect, Sound, Interface, MountOrMinion, Other }

    /// <summary> Classifies one mod's changed items. <paramref name="findItem"/> looks a gear item up by name (null: not gear). </summary>
    public static ModItems Classify(IReadOnlyDictionary<string, object?> changed, Func<string, ItemRef?> findItem)
    {
        var result = new ModItems { ChangedCount = changed.Count };
        var bySlot = new Dictionary<string, SlotPieces>();
        var counts = new Dictionary<Kind, int>();

        foreach (var (name, value) in changed)
        {
            var typeName = value?.GetType().Name ?? string.Empty;

            var m = CustomizationRegex().Match(name);
            if (m.Success)
            {
                var marker = m.Groups["marker"].Success ? m.Groups["marker"].Value.Trim() : string.Empty;
                var kind = marker.ToLowerInvariant() switch
                {
                    // A marker names the real part ("Face (Tail) 3" is a tail, "Body (Ears) 2" are ears).
                    "tail"          => Kind.Tail,
                    "ear" or "ears" => Kind.Ears,
                    _ => m.Groups["kind"].Value.ToLowerInvariant() switch
                    {
                        "hair" or "hairstyle" => Kind.Hair,
                        "face"                => Kind.Face,
                        "tail"                => Kind.Tail,
                        "ear" or "ears"       => Kind.Ears,
                        _                     => Kind.Body,
                    },
                };

                var race   = Races.RaceName(m.Groups["race"].Value, m.Groups["clan"].Success ? m.Groups["clan"].Value : null);
                var gender = m.Groups["gender"].Value.Equals("Female", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (kind == Kind.Body)
                {
                    var body = $"{race}|{gender}";
                    if (!result.Bodies.Contains(body))
                        result.Bodies.Add(body);
                    continue;
                }

                var key = kind switch
                {
                    Kind.Hair => "Hairstyle",
                    Kind.Face => "Face",
                    _         => "TailShape",
                };
                if (m.Groups["n"].Success && int.TryParse(m.Groups["n"].Value, out var number)
                 && !marker.Contains("iris", StringComparison.OrdinalIgnoreCase) && !marker.Contains("eye", StringComparison.OrdinalIgnoreCase))
                {
                    // Faces: a race's second clan uses face files 101+ but the same 1..n face numbers in the game.
                    // (Hyur clans are separate model races, so they need no clan rule.)
                    var clan = 0;
                    if (key == "Face" && race is not ("Midlander" or "Highlander"))
                    {
                        clan = number > 100 ? 2 : 1;
                        if (number > 100)
                            number -= 100;
                    }

                    // Ears and tails share the customize key; the kind decides the socket (see PartBuilder).
                    var part = new CustomizationPart(race, gender, kind == Kind.Ears ? "Ears" : key, number, clan);
                    if (!result.Customizations.Any(c => c.Race == part.Race && c.Gender == part.Gender && c.Key == part.Key && c.Clan == part.Clan))
                        result.Customizations.Add(part);
                }

                continue;
            }

            if (name.StartsWith("Emote", StringComparison.OrdinalIgnoreCase) || typeName.Contains("Emote")
             || name.StartsWith("Action", StringComparison.OrdinalIgnoreCase) || typeName.Contains("Action"))
            {
                Count(Kind.Animation);
                continue;
            }

            if (typeName.Contains("Companion") || typeName.Contains("Minion") || name.StartsWith("Minion", StringComparison.OrdinalIgnoreCase)
             || typeName.Contains("Mount") || name.StartsWith("Mount", StringComparison.OrdinalIgnoreCase))
            {
                Count(Kind.MountOrMinion);
                continue;
            }

            if (findItem(name) is { } item && GearSlots.IdOf(item.Slot) is { } slotId)
            {
                if (!bySlot.TryGetValue(item.Slot, out var pieces))
                    bySlot[item.Slot] = pieces = new SlotPieces { Slot = item.Slot, SlotId = slotId };
                if (pieces.Items.All(i => i.Id != item.Id))
                    pieces.Items.Add(new ItemChoice(item.Id, item.Name, item.Model));
                continue;
            }

            var lower = name.ToLowerInvariant();
            if (lower.Contains("vfx") || lower.EndsWith(".avfx") || lower.EndsWith(".atex"))
                Count(Kind.Effect);
            else if (lower.EndsWith(".scd") || lower.Contains("sound"))
                Count(Kind.Sound);
            else if (lower.StartsWith("ui/") || lower.Contains("interface") || lower.Contains("icon"))
                Count(Kind.Interface);
            else if (lower.EndsWith(".pap") || lower.EndsWith(".tmb") || lower.Contains("animation"))
                Count(Kind.Animation);
            else
                Count(Kind.Other);
        }

        // Best item first: the current version of an item before its "Dated" one, then short names (the plain item
        // before "… of Healing" variants), then by name.
        foreach (var pieces in bySlot.Values)
            pieces.Items = pieces.Items
                .OrderBy(i => i.Name.StartsWith("Dated ", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(i => i.Name.Length)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        result.Slots = bySlot.Values.OrderBy(p => p.SlotId).ToList();

        result.OtherKind = counts.Where(kv => kv.Key != Kind.Other).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                .Select(kv => KindId(kv.Key)).FirstOrDefault() ?? string.Empty;
        return result;

        void Count(Kind c)
            => counts[c] = counts.GetValueOrDefault(c) + 1;
    }

    private static string KindId(Kind kind)
        => kind switch
        {
            Kind.Animation     => "animation",
            Kind.Effect        => "effect",
            Kind.Sound         => "sound",
            Kind.Interface     => "interface",
            Kind.MountOrMinion => "mount or minion",
            _                  => string.Empty,
        };
}
