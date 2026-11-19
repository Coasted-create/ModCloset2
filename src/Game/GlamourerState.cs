using Newtonsoft.Json.Linq;

namespace ModCloset2.Game;

/// <summary>
/// Glamourer state snippets (port of the old Look.cs). Glamourer's JSON state is the "design base" layout: FileVersion 1,
/// Equipment / Bonus / Customize / Parameters / Materials. No Dalamud types.
/// </summary>
public static class GlamourerState
{
    private static readonly string[] Identity = { "Race", "Clan", "Gender", "BodyType" };

    /// <summary>
    /// The part of a Keep backup that may still be put back: only slots / customize keys that still show exactly what
    /// Keep put on (anything changed since is left alone). Null when nothing is left to restore.
    /// </summary>
    public static JObject? FilterBackup(JObject backup, JObject? current, IReadOnlyDictionary<string, ulong> putOnItems, IReadOnlyDictionary<string, int> putOnCustomize)
    {
        if (current is null)
            return null;

        var equipment = new JObject();
        if (backup["Equipment"] is JObject be && current["Equipment"] is JObject ce)
            foreach (var (slot, item) in putOnItems)
                if (be[slot] is JObject entry && ce[slot]?["ItemId"] is { Type: JTokenType.Integer } now && now.Value<ulong>() == item)
                    equipment[slot] = entry.DeepClone();

        var customize = new JObject();
        var any       = false;
        if (backup["Customize"] is JObject bc && current["Customize"] is JObject cc)
        {
            foreach (var key in new[] { "ModelId", "Race", "Clan", "Gender", "BodyType" })
                if (bc[key] is { } token)
                    customize[key] = token.DeepClone();
            foreach (var (key, value) in putOnCustomize)
                if (bc[key] is JObject entry && cc[key]?["Value"] is { Type: JTokenType.Integer } now && now.Value<int>() == value)
                {
                    customize[key] = entry.DeepClone();
                    any            = true;
                }
        }

        return equipment.Count == 0 && !any ? null : Wrap(equipment, any ? customize : new JObject());
    }

    /// <summary>
    /// Customization-only Glamourer state (FileVersion 1, the layout its state loader reads), with race / clan / gender /
    /// body type copied unapplied so Glamourer validates the value against the right clan.
    /// </summary>
    public static JObject CustomizeState(IReadOnlyDictionary<string, int> values, JObject? current)
    {
        var customize = new JObject();
        var baseline  = current?["Customize"] as JObject;
        if (baseline?["ModelId"] is { Type: JTokenType.Integer } model)
            customize["ModelId"] = model.Value<uint>();

        foreach (var key in Identity)
            if (!values.ContainsKey(key) && baseline?[key]?["Value"] is { Type: JTokenType.Integer } v)
                customize[key] = new JObject { ["Value"] = v.Value<int>(), ["Apply"] = false };

        foreach (var (key, value) in values)
            customize[key] = new JObject { ["Value"] = value, ["Apply"] = true };

        return Wrap(new JObject(), customize);
    }

    /// <summary>
    /// A Glamourer state that puts back exactly what <paramref name="current"/> has in the given equipment slots (items and
    /// dyes) and customize keys; nothing else is applied. Null when nothing could be read.
    /// </summary>
    public static JObject? Backup(JObject? current, IEnumerable<string> slots, IEnumerable<string> customizeKeys)
    {
        if (current is null)
            return null;

        var equipment = new JObject();
        if (current["Equipment"] is JObject eq)
            foreach (var slot in slots)
                if (eq[slot] is JObject entry)
                {
                    var copy = (JObject)entry.DeepClone();
                    copy["Apply"]      = true;
                    copy["ApplyStain"] = true;
                    equipment[slot]    = copy;
                }

        var keys      = customizeKeys.ToList();
        var customize = new JObject();
        if (keys.Count > 0 && current["Customize"] is JObject cu)
        {
            if (cu["ModelId"] is { Type: JTokenType.Integer } model)
                customize["ModelId"] = model.Value<uint>();
            foreach (var key in Identity)
                if (cu[key]?["Value"] is { Type: JTokenType.Integer } v)
                    customize[key] = new JObject { ["Value"] = v.Value<int>(), ["Apply"] = false };
            foreach (var key in keys)
                if (cu[key]?["Value"] is { Type: JTokenType.Integer } v)
                    customize[key] = new JObject { ["Value"] = v.Value<int>(), ["Apply"] = true };
        }

        return equipment.Count == 0 && keys.Count == 0 ? null : Wrap(equipment, customize);
    }

    /// <summary>
    /// Adds the slots and customize keys of <paramref name="addition"/> that <paramref name="existing"/> does not have yet.
    /// New in Mod Closet 2: a mod can be kept part by part, and each part's first Keep must still be able to go back to
    /// what was there before it (the old plugin kept whole mods only, so one backup per mod was enough).
    /// </summary>
    public static JObject Merge(JObject existing, JObject? addition)
    {
        if (addition is null)
            return existing;

        var merged = (JObject)existing.DeepClone();
        foreach (var section in new[] { "Equipment", "Customize" })
        {
            if (addition[section] is not JObject add)
                continue;
            if (merged[section] is not JObject into)
                merged[section] = into = new JObject();
            foreach (var (key, value) in add)
                if (into[key] is null && value is not null)
                    into[key] = value.DeepClone();
        }

        return merged;
    }

    /// <summary> Item id in a slot of a Glamourer JSON state, or 0. </summary>
    public static ulong ItemIn(JObject? state, string slot)
        => state?["Equipment"]?[slot]?["ItemId"] is JValue { Type: JTokenType.Integer } v ? Convert.ToUInt64(v.Value) : 0;

    /// <summary>
    /// The character's colours as Glamourer's parameters (the colour of the skin, hair, eyes… as its colour picker shows
    /// them; the looks' key → Glamourer's parameter). The same table as <c>Looks.LookDesign.ParameterOf</c>.
    /// </summary>
    public static readonly (string Key, string Parameter)[] ColourParameters =
    {
        ("skin", "SkinDiffuse"), ("hair", "HairDiffuse"), ("highlights", "HairHighlight"), ("eyeLeft", "LeftEye"), ("eyeRight", "RightEye"),
        ("lips", "LipDiffuse"), ("facePaint", "DecalColor"), ("tattoo", "FeatureColor"),
    };

    /// <summary> What can be shown or hidden: the looks' key → Glamourer's equipment entry and its value's name. </summary>
    public static readonly (string Key, string Entry, string Value)[] Showings =
    {
        ("weapon", "Weapon", "Show"), ("hat", "Hat", "Show"), ("visor", "Visor", "IsToggled"),
    };

    /// <summary> The character's colours (0xRRGGBB) and showings in a Glamourer state; what the state does not have is left out. </summary>
    public static (Dictionary<string, uint> Colours, Dictionary<string, bool> Showing) CharacterOf(JObject? state)
    {
        var colours = new Dictionary<string, uint>(StringComparer.Ordinal);
        var showing = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (state?["Parameters"] is JObject parameters)
            foreach (var (key, parameter) in ColourParameters)
                if (parameters[parameter] is JObject p && Number(p["Red"]) is { } r && Number(p["Green"]) is { } g && Number(p["Blue"]) is { } b)
                    colours[key] = Rgb(r, g, b);
        if (state?["Equipment"] is JObject equipment)
            foreach (var (key, entry, value) in Showings)
                if (equipment[entry]?[value] is JValue { Type: JTokenType.Boolean } on)
                    showing[key] = (bool)on.Value!;
        return (colours, showing);
    }

    /// <summary>
    /// A Glamourer state that sets only these colours and showings (every other entry is not applied; race, clan, gender
    /// and body type are copied unapplied so Glamourer reads the parameters for the right body). A lip or face paint colour
    /// keeps the strength it has now.
    /// </summary>
    public static JObject CharacterState(IReadOnlyDictionary<string, uint> colours, IReadOnlyDictionary<string, bool> showing, JObject? current)
    {
        var customize = new JObject();
        var baseline  = current?["Customize"] as JObject;
        if (baseline?["ModelId"] is { Type: JTokenType.Integer } model)
            customize["ModelId"] = model.Value<uint>();
        foreach (var key in Identity)
            if (baseline?[key]?["Value"] is { Type: JTokenType.Integer } v)
                customize[key] = new JObject { ["Value"] = v.Value<int>(), ["Apply"] = false };

        var parameters = new JObject();
        foreach (var (key, parameter) in ColourParameters)
            if (colours.TryGetValue(key, out var rgb))
            {
                var entry = new JObject
                {
                    ["Red"]   = ((rgb >> 16) & 0xFF) / 255f,
                    ["Green"] = ((rgb >> 8) & 0xFF) / 255f,
                    ["Blue"]  = (rgb & 0xFF) / 255f,
                    ["Apply"] = true,
                };
                if (current?["Parameters"]?[parameter]?["Alpha"] is { } alpha)
                    entry["Alpha"] = alpha.DeepClone();
                else if (parameter is "LipDiffuse" or "DecalColor")
                    entry["Alpha"] = 1f;
                parameters[parameter] = entry;
            }

        var equipment = new JObject();
        foreach (var (key, entry, value) in Showings)
            if (showing.TryGetValue(key, out var on))
                equipment[entry] = new JObject { [value] = on, ["Apply"] = true };

        var state = Wrap(equipment, customize);
        state["Parameters"] = parameters;
        return state;
    }

    private static float? Number(JToken? token)
        => token is JValue { Type: JTokenType.Float or JTokenType.Integer } v ? Convert.ToSingle(v.Value) : null;

    private static uint Rgb(float r, float g, float b)
    {
        static uint C(float v)
            => (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return C(r) << 16 | C(g) << 8 | C(b);
    }

    private static JObject Wrap(JObject equipment, JObject customize)
        => new()
        {
            ["FileVersion"] = 1,
            ["Equipment"]   = equipment,
            ["Bonus"]       = new JObject(),
            ["Customize"]   = customize,
            ["Parameters"]  = new JObject(),
            ["Materials"]   = new JObject(),
        };
}
