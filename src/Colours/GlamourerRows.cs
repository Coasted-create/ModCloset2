using Newtonsoft.Json.Linq;

namespace ModCloset2.Colours;

// Pure code (Newtonsoft only, no game): linked into tests/ColourTests.

/// <summary> One colour table row to put on the character as a Glamourer advanced dye. </summary>
/// <param name="Slot"> Gear slot, 0 head … 9 left ring. </param>
/// <param name="Values"> The row's 32 table values (colours squared, as the game keeps them). </param>
public sealed record RowWrite(int Slot, int Material, int Row, float[] Values)
{
    public string Key
        => ColourMath.MaterialKey(Slot, Material, Row);
}

/// <summary>
/// Glamourer's advanced dyes as JSON: one entry per row under "Materials", keyed by
/// <c>(1 &lt;&lt; 24 | slot &lt;&lt; 16 | material &lt;&lt; 8 | row)</c> as 16 hex digits. Ported from the old plugin's
/// Colourist (RowJson, RevertRow, Canon, SendMaterials, IsEmptyRow).
/// </summary>
public static class GlamourerRows
{
    /// <summary>
    /// Glamourer keeps the colours of a row as square roots of the table values ("Values are not squared", its ColorRow;
    /// it squares them when writing the table, and reads the game's rows back with a root). The old plugin wrote the
    /// table values themselves, so every colour it set came out darker than picked (squared twice) and every specular it
    /// copied was dimmed. The new engine writes roots; <see cref="Entry"/> with <c>roots: false</c> builds exactly what the
    /// old code built (the tests compare the two). If colours look too light in the game, this is the switch.
    /// </summary>
    public const bool RootValues = true;

    /// <summary> The entry for a row with its (new) table values. Field order and fields as the old RowJson. </summary>
    public static JObject Entry(float[] h, bool roots = RootValues)
    {
        float C(int i) => roots ? Root(h[i]) : h[i];
        return new JObject
        {
            ["Revert"]         = false,
            ["Mode"]           = "Dawntrail",
            ["DiffuseR"]       = C(RowField.DiffuseR),
            ["DiffuseG"]       = C(RowField.DiffuseG),
            ["DiffuseB"]       = C(RowField.DiffuseB),
            ["SpecularR"]      = C(RowField.SpecularR),
            ["SpecularG"]      = C(RowField.SpecularG),
            ["SpecularB"]      = C(RowField.SpecularB),
            ["EmissiveR"]      = C(RowField.EmissiveR),
            ["EmissiveG"]      = C(RowField.EmissiveG),
            ["EmissiveB"]      = C(RowField.EmissiveB),
            ["Roughness"]      = h[RowField.Roughness],
            ["Metalness"]      = h[RowField.Metalness],
            ["Sheen"]          = h[RowField.Sheen],
            ["SheenTint"]      = h[RowField.SheenTint],
            ["SheenAperture"]  = h[RowField.SheenAperture],
            ["Exposure"]       = h[RowField.Exposure],
            ["Anisotropy"]     = h[RowField.Anisotropy],
            ["SphereMapMask"]  = h[RowField.SphereMapMask],
            ["SphereMapIndex"] = (int)Math.Clamp(h[RowField.SphereMapIndex], 0, 65534),
            ["Enabled"]        = true,
        };
    }

    /// <summary> Glamourer's root of a table value (negative values keep their sign, as its ColorRow.Root). </summary>
    public static float Root(float v)
        => v < 0 ? -MathF.Sqrt(-v) : MathF.Sqrt(v);

    /// <summary> An entry that puts a row back to the game's colour (Glamourer skips entries that are not enabled). </summary>
    public static JObject Revert()
        => new() { ["Revert"] = true, ["Enabled"] = true, ["Mode"] = "Dawntrail" };

    public static bool IsRevert(JToken? entry)
        => entry is JObject o && (o["Revert"]?.Value<bool>() ?? false);

    /// <summary>
    /// The advanced dyes with every key written one way (16 hex digits). Glamourer writes its keys its own way; the same
    /// row written twice is refused as a duplicate ("Duplicate material value key"), so the last entry for a row wins.
    /// </summary>
    public static JObject Canon(JToken? materials)
    {
        var result = new JObject();
        if (materials is not JObject m)
            return result;
        foreach (var p in m.Properties())
            if (ulong.TryParse(p.Name, System.Globalization.NumberStyles.HexNumber, null, out var key))
                result[key.ToString("X16")] = p.Value.DeepClone();
        return result;
    }

    /// <summary>
    /// What to give Glamourer: the state with new advanced dyes, and nothing else of it. Sent as lasting (no "once" flag):
    /// Glamourer throws "once" dyes away as soon as the game reloads the material (a dye, a mod or a redraw), but puts
    /// lasting ones on again each time. Equipment and customize are left out by the flags (0); parameters, mods, bonus
    /// items and the reset flag are taken out here (old Colourist.SendMaterials).
    /// </summary>
    public static JObject MaterialsOnly(JObject state, JObject materials)
    {
        var only = (JObject)state.DeepClone();
        only.Remove("Parameters");
        only.Remove("Mods");
        only.Remove("Bonus");
        only.Remove("ResetAdvancedDyes");
        only["FileVersion"] ??= 1;
        only["Materials"] = Canon(materials);
        return only;
    }

    /// <summary> The two entries say the same (numbers within a small tolerance). </summary>
    public static bool Same(JToken? a, JToken? b, double tolerance = 1e-3)
    {
        if (a is not JObject x || b is not JObject y)
            return a is null && b is null;
        if (IsRevert(x) != IsRevert(y) || (x["Enabled"]?.Value<bool>() ?? false) != (y["Enabled"]?.Value<bool>() ?? false))
            return false;
        if (IsRevert(x))
            return true;
        foreach (var p in y.Properties())
        {
            if (p.Value.Type is JTokenType.Float or JTokenType.Integer)
            {
                if (x[p.Name] is not JValue v || v.Type is not (JTokenType.Float or JTokenType.Integer)
                 || Math.Abs(v.Value<double>() - p.Value.Value<double>()) > tolerance)
                    return false;
            }
        }

        return true;
    }

    /// <summary> How near a row must still be to what was written to count as ours (Glamourer keeps floats; a little slack). </summary>
    public const double OursTolerance = 5e-3;

    /// <summary> A row to take off the character: the entries the closet wrote there (newest first), and whether the user asked for it now. </summary>
    /// <param name="Force"> The user just asked for this row's area to show its own colour: put back whoever changed it since. </param>
    public sealed record Release(string Key, IReadOnlyList<JObject> Ours, bool Force = false);

    /// <summary> True when an entry is one the closet wrote (any of <paramref name="ours"/>). </summary>
    public static bool IsOurs(JToken? entry, IReadOnlyList<JObject> ours)
        => ours.Any(o => Same(entry, o, OursTolerance));

    /// <summary>
    /// Patches a state's advanced dyes: <paramref name="wanted"/> rows are set; rows of <paramref name="release"/> are put
    /// back to the game's colour when they still hold an entry the closet wrote (the latest, or an earlier one that
    /// something brought back, such as a look saved before and put on again), or when the user asked for it (forced). A
    /// row someone else changed meanwhile (a Glamourer design, automation, the user in Glamourer) is left alone. Returns
    /// the new materials, whether anything changed, and the released keys that were not ours any more (left alone).
    /// </summary>
    public static (JObject Materials, bool Changed, List<string> NotOurs) Patch(JToken? current, IEnumerable<RowWrite> wanted,
        IEnumerable<Release> release)
    {
        var materials = Canon(current);
        var changed   = false;
        var notOurs   = new List<string>();
        var set       = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in wanted)
        {
            var entry = Entry(row.Values);
            set.Add(row.Key);
            if (Same(materials[row.Key], entry))
                continue;
            materials[row.Key] = entry;
            changed = true;
        }

        foreach (var r in release)
        {
            if (set.Contains(r.Key) || materials[r.Key] is null || IsRevert(materials[r.Key]))
                continue;
            if (!r.Force && !IsOurs(materials[r.Key], r.Ours))
            {
                notOurs.Add(r.Key);
                continue;
            }

            materials[r.Key] = Revert();
            changed = true;
        }

        return (materials, changed, notOurs);
    }
}
