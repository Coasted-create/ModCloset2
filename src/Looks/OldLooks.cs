using System.Text.Json;
using System.Text.Json.Nodes;
using ModCloset2.Core;

namespace ModCloset2.Looks;

/// <summary>
/// Carries the old Mod Closet's looks over (once): its settings file (pluginConfigs/ModCloset.json, never written) has
/// a "Looks" list of LookDef, each with "Mods" (LookMod: ModDirectory, Options, Items by slot, Slots). Each old look
/// becomes a pinned look holding its mods' worn parts, one per socket, with the options it had. Old looks never said
/// "nothing here", so they know no empty sockets: wearing one leaves every other socket alone.
/// What does not come over: layers, the stored appearance, body shapes, collections, colour versions, notes, the
/// Penumbra collection, the Glamourer design. Pure.
/// </summary>
public static class OldLooks
{
    /// <summary> What came over, and what could not. </summary>
    public sealed record Result(List<LookData> Looks, int Old, int ModsGone, int LooksEmpty);

    /// <summary> Old slot names (LookMod.Items / Slots) → socket. </summary>
    private static readonly Dictionary<string, Socket> Slots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Head"] = Socket.Head, ["Body"] = Socket.Body, ["Hands"] = Socket.Hands, ["Legs"] = Socket.Legs, ["Feet"] = Socket.Feet,
        ["Ears"] = Socket.Ears, ["Neck"] = Socket.Neck, ["Wrists"] = Socket.Wrists, ["RFinger"] = Socket.Rings, ["LFinger"] = Socket.Rings,
    };

    /// <summary> Names the old plugin gave looks the user never named (in either language). </summary>
    private static readonly HashSet<string> DefaultNames = new(StringComparer.OrdinalIgnoreCase) { "New look", "Neuer Look", "Look" };

    /// <summary> Reads the old looks. Never throws: what cannot be read is left out. </summary>
    public static Result Read(string settings, ILooksLibrary library, Func<LookLibraryPart, ModPrint> print, DateTime now)
    {
        var looks = new List<LookData>();
        int old = 0, modsGone = 0, empty = 0;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(settings, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return new Result(looks, 0, 0, 0);
        }

        foreach (var def in Items(root?["Looks"]))
        {
            ++old;
            try
            {
                var look = new LookData
                {
                    Id         = Guid.TryParse(Text(def["Id"]), out var id) && id != Guid.Empty ? id : Guid.NewGuid(),
                    Pinned     = true,
                    CreatedUtc = Date(def["CreatedUtc"]) ?? now,
                };
                look.LastWornUtc = Date(def["LastWornUtc"]) is { } worn && worn > DateTime.MinValue.AddDays(1) ? worn : look.CreatedUtc;
                look.Wears       = Math.Max(0, Number(def["WornCount"]));
                var name = Text(def["Name"]).Trim();
                look.Name = DefaultNames.Contains(name) ? string.Empty : name;

                foreach (var mod in Items(def["Mods"]))
                {
                    var directory = Text(mod["ModDirectory"]);
                    var parts     = directory.Length == 0 ? Array.Empty<LookLibraryPart>() : library.PartsOf(directory);
                    if (parts.Count == 0)
                    {
                        ++modsGone;
                        continue;
                    }

                    var options = Options(mod["Options"]);
                    var items   = ItemsBySlot(mod["Items"]);
                    var only    = Strings(mod["Slots"]).Where(Slots.ContainsKey).Select(s => Slots[s]).ToHashSet();
                    var limited = mod["Slots"] is JsonArray or JsonObject;
                    foreach (var part in parts.OrderBy(p => p.Key.Socket))
                    {
                        // "only these slots" in the old look named gear slots; hair, face, tail and bodies came with the mod
                        if (limited && IsGear(part.Key.Socket) && !only.Contains(part.Key.Socket))
                            continue;
                        // weapons and things not worn are no pieces; the first mod of a socket wins, as the old layer order had it
                        if (!LookRules.Worn(part.Key.Socket) || look.Pieces.Any(p => LookRules.Clash(p.Socket, p.Shelf, part.Key.Socket, part.Shelf)))
                            continue;
                        var version = -1;
                        foreach (var (slot, item) in items)
                            if (Slots.TryGetValue(slot, out var socket) && socket == part.Key.Socket && library.VersionOf(part.Key, item) is >= 0 and var v)
                            {
                                version = v;
                                break;
                            }

                        look.Pieces.Add(new LookPiece
                        {
                            Mod     = part.Key.Mod,
                            Socket  = part.Key.Socket,
                            Shelf   = part.Shelf,
                            Options = options.Where(kv => part.Groups.Contains(kv.Key, StringComparer.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value),
                            Version = version,
                            Print   = print(part),
                        });
                    }
                }

                if (look.Pieces.Count == 0)
                {
                    ++empty;
                    continue;
                }

                // an old look of hair and body only is a whole-character look
                if (look.Pieces.All(p => LookRules.IsBody(p.Shelf)))
                    look.Body = look.Pieces.Select(p => p.Shelf).Distinct().ToList();
                looks.Add(look);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
            {
                ++empty;
            }
        }

        return new Result(looks, old, modsGone, empty);
    }

    private static bool IsGear(Socket socket)
        => socket is >= Socket.Head and <= Socket.OffHand;

    /// <summary> The entries of a list as Dalamud's settings write it: a plain array, or {"$type":…, "$values": [...]}. </summary>
    private static IEnumerable<JsonObject> Items(JsonNode? node)
    {
        var array = node as JsonArray ?? (node as JsonObject)?["$values"] as JsonArray;
        return array?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();
    }

    private static IEnumerable<string> Strings(JsonNode? node)
    {
        var array = node as JsonArray ?? (node as JsonObject)?["$values"] as JsonArray;
        return array?.Select(Text).Where(s => s.Length > 0) ?? Enumerable.Empty<string>();
    }

    private static Dictionary<string, List<string>> Options(JsonNode? node)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (node is JsonObject o)
            foreach (var (group, value) in o)
                if (group != "$type")
                    result[group] = Strings(value).ToList();
        return result;
    }

    private static List<(string Slot, ulong Item)> ItemsBySlot(JsonNode? node)
    {
        var result = new List<(string, ulong)>();
        if (node is JsonObject o)
            foreach (var (slot, value) in o)
                if (slot != "$type" && value is JsonValue v && (v.TryGetValue<ulong>(out var id) || ulong.TryParse(Text(v), out id)))
                    result.Add((slot, id));
        return result;
    }

    private static string Text(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node is JsonValue n ? n.ToJsonString().Trim('"') : string.Empty;

    private static double Number(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    private static DateTime? Date(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<DateTime>(out var d) ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : DateTime.TryParse(Text(node), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var p) ? p : null;
}
