using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using ModCloset2.Library;

namespace ModCloset2.Game;

/// <summary>
/// The game's wearable items by name (Penumbra's changed items name them). Built once in the background from the Item
/// sheet (port of the old Catalog.BuildItemIndex), with the model each item shows so versions can be told apart.
/// </summary>
public sealed class ItemIndex
{
    private readonly IPluginLog _log;
    private volatile Dictionary<string, ItemRef>? _byName;
    private volatile Dictionary<ulong, string>? _names;
    private volatile Dictionary<ulong, (uint Id, string Name)>? _weaponCategories;

    public ItemIndex(IDataManager data, IPluginLog log)
    {
        _log = log;
        Task.Run(() => Build(data));
    }

    /// <summary> True once the list is built (step-1 reading of mods waits for it). </summary>
    public bool Ready
        => _byName is not null;

    /// <summary> The item of that name, or null. Thread-safe once <see cref="Ready"/>. </summary>
    public ItemRef? Find(string name)
        => _byName is { } index && index.TryGetValue(name.Trim(), out var item) ? item : null;

    /// <summary>
    /// The game's category of a weapon or off-hand item by its id ("Gladiator's Arm", "Shield"): a job holds only weapons
    /// of its own category (Glamourer keeps to that too). Null for other items and unknown ids. Thread-safe.
    /// </summary>
    public (uint Id, string Name)? WeaponCategory(ulong id)
        => _weaponCategories is { } c && c.TryGetValue(id, out var category) ? category : null;

    /// <summary> The name of a wearable item by its id (as Glamourer gives it), or null. Thread-safe. </summary>
    public string? NameOf(ulong id)
        => _names is { } names && names.TryGetValue(id, out var name) ? name : null;

    private void Build(IDataManager data)
    {
        var index = new Dictionary<string, ItemRef>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<ulong, string>();
        var weapons = new Dictionary<ulong, (uint, string)>();
        try
        {
            foreach (var row in data.GetExcelSheet<Item>())
            {
                var name = row.Name.ExtractText();
                if (name.Length == 0 || SlotOf(row) is not { } slot)
                    continue;
                names[row.RowId] = name;
                var category = row.ItemUICategory.ValueNullable is { } c ? (c.RowId, c.Name.ExtractText()) : (0u, string.Empty);
                if (slot is "MainHand" or "OffHand" && category.Item1 != 0)
                    weapons[row.RowId] = category;

                // Same name twice: keep the newest row (the current version of the item).
                if (!index.TryGetValue(name, out var existing) || existing.Id < row.RowId)
                    index[name] = new ItemRef(row.RowId, name, slot, row.ModelMain, slot is "MainHand" or "OffHand" ? category.Item2 : string.Empty);
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Mod Closet 2: could not read the item list: {ex.Message}");
        }

        _names            = names;
        _weaponCategories = weapons;
        _byName           = index;
    }

    private static string? SlotOf(in Item item)
    {
        if (item.EquipSlotCategory.ValueNullable is not { } c)
            return null;

        // 1 = the slot the item goes into, -1 = a slot it only blocks (a hooded robe blocks Head but goes into Body).
        if (c.MainHand == 1) return "MainHand";
        if (c.OffHand == 1)  return "OffHand";
        if (c.Head == 1)     return "Head";
        if (c.Body == 1)     return "Body";
        if (c.Gloves == 1)   return "Hands";
        if (c.Legs == 1)     return "Legs";
        if (c.Feet == 1)     return "Feet";
        if (c.Ears == 1)     return "Ears";
        if (c.Neck == 1)     return "Neck";
        if (c.Wrists == 1)   return "Wrists";
        if (c.FingerR == 1)  return "RFinger";
        if (c.FingerL == 1)  return "LFinger";
        return null;
    }
}
