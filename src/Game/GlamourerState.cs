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
