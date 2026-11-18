using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModCloset2.Looks;

/// <summary>
/// Tells colour snapshots apart: two snapshots with the same key dress the same pieces in the same colours. The snapshot
/// is the colour engine's undo snapshot of some parts (src/Colours/ColourMemory.Snapshot):
/// <c>{"p":"RRGGBB RRGGBB RRGGBB RRGGBB","d":bool,"c":{"part":{"u":n,"a":{"area":"m|s|a|x|o|#RRGGBB"}} | null}}</c>.
/// The key leaves out what does not change the colours: the use counters, parts without choices, and the outfit colours
/// nothing follows. A snapshot in another form is compared as it is. Pure.
/// </summary>
public static class ColourKey
{
    /// <summary> The key of a snapshot; "" for none. </summary>
    public static string Of(string? snapshot)
    {
        if (string.IsNullOrEmpty(snapshot))
            return string.Empty;
        if (Read(snapshot) is not { } read)
            return "raw:" + snapshot;

        var key = new StringBuilder();
        key.Append(read.Dress ? "d1" : "d0");
        // an outfit colour counts only when something follows it: all four with "dress everything", else the ones chosen
        var roles = read.Dress ? "msax" : new string("msax".Where(r => read.Choices.Values.Any(a => a.Values.Contains(r.ToString()))).ToArray());
        for (var i = 0; i < 4; ++i)
            if (roles.Contains("msax"[i]))
                key.Append('|').Append("msax"[i]).Append('=').Append(read.Palette[i]);
        foreach (var (part, areas) in read.Choices.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (areas.Count == 0)
                continue;
            key.Append("|[").Append(part).Append(']');
            foreach (var (area, choice) in areas.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                key.Append(area).Append('=').Append(choice).Append(';');
        }

        return key.ToString();
    }

    /// <summary>
    /// The main colour a snapshot dresses in, when it says one: the outfit's main colour if anything follows it, else the
    /// first colour given by hand to <paramref name="mainPart"/>. Null when the pieces keep their own colours.
    /// </summary>
    public static uint? MainOf(string? snapshot, string? mainPart)
    {
        if (string.IsNullOrEmpty(snapshot) || Read(snapshot) is not { } read)
            return null;
        if (read.Dress || read.Choices.Values.Any(a => a.Values.Contains("m")))
            return Hex(read.Palette[0]);
        if (mainPart is not null && read.Choices.TryGetValue(mainPart, out var areas))
            foreach (var choice in areas.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value))
                if (choice.StartsWith('#') && Hex(choice[1..]) is { } hand)
                    return hand;
        return null;
    }

    private sealed record Snapshot(string[] Palette, bool Dress, Dictionary<string, Dictionary<string, string>> Choices);

    private static Snapshot? Read(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root || root["p"] is not JsonValue p || root["c"] is not (JsonObject or null))
                return null;
            var palette = (p.GetValue<string>() ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (palette.Length != 4 || palette.Any(c => Hex(c) is null))
                return null;
            palette = palette.Select(c => Hex(c)!.Value.ToString("X6", CultureInfo.InvariantCulture)).ToArray();
            var dress   = root["d"] is JsonValue d && d.TryGetValue<bool>(out var on) && on;
            var choices = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            if (root["c"] is JsonObject parts)
                foreach (var (part, node) in parts)
                {
                    var areas = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (node is JsonObject o && o["a"] is JsonObject a)
                        foreach (var (area, value) in a)
                            if (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
                                areas[area] = s.StartsWith('#') && Hex(s[1..]) is { } rgb ? "#" + rgb.ToString("X6", CultureInfo.InvariantCulture) : s;
                    choices[part] = areas;
                }

            return new Snapshot(palette, dress, choices);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static uint? Hex(string text)
        => uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v & 0xFFFFFF : null;
}
