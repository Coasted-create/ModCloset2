using ModCloset2.Core;
using ModCloset2.Game;

namespace ModCloset2.Library;

/// <summary> A wearable item from the game's item list: row id, name, slot name ("Body", "RFinger"…) and model (ModelMain). </summary>
public sealed record ItemRef(uint Id, string Name, string Slot, ulong Model);

/// <summary> One game item a mod changes. <see cref="Model"/> is the item's ModelMain (0 when unknown). </summary>
public sealed record ItemChoice(ulong Id, string Name, ulong Model = 0)
{
    /// <summary> The model set the item shows (low 16 bits of ModelMain): items of one set are one version. </summary>
    public int SetId
        => (int)(Model & 0xFFFF);
}

/// <summary> All items of one gear slot a mod changes, best first. </summary>
public sealed class SlotPieces
{
    public string Slot { get; set; } = string.Empty;
    public byte SlotId { get; set; }
    public List<ItemChoice> Items { get; set; } = new();
}

/// <summary>
/// What Penumbra says a mod changes, as far as dressing up is concerned (step 1). Cached on disk by the mod's stamp.
/// </summary>
public sealed class ModItems
{
    public string Stamp { get; set; } = string.Empty;
    public List<SlotPieces> Slots { get; set; } = new();
    public List<CustomizationPart> Customizations { get; set; } = new();

    /// <summary> Bodies it changes as "Race|Gender" ("Miqote|1"), from "Customization: … Body" entries. </summary>
    public List<string> Bodies { get; set; } = new();

    /// <summary> What the rest is when nothing is worn ("animation", "effect", "sound", "interface", "mount or minion", ""). </summary>
    public string OtherKind { get; set; } = string.Empty;
    public int ChangedCount { get; set; }
}

/// <summary> One option of an option group, with the sockets its files and edits change and the race codes of its files. </summary>
public sealed class OptionInfo
{
    public string Name { get; init; } = string.Empty;
    public HashSet<Socket> Sockets { get; init; } = new();

    /// <summary> Race codes of its character files, models first (like the old ModFolder.OptionCodes). </summary>
    public HashSet<int> RaceCodes { get; init; } = new();
}

/// <summary> One option group of a mod. </summary>
public sealed class GroupInfo
{
    public string Name { get; init; } = string.Empty;

    /// <summary> Penumbra's group type: "Single", "Multi", "Imc", "Combining" ("" in very old mods: single). </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary> Single: the index of the default option. Every other type: a bit mask of the options on by default. </summary>
    public long Default { get; init; }
    public List<OptionInfo> Options { get; init; } = new();

    /// <summary> Every socket an option of this group changes. </summary>
    public HashSet<Socket> Sockets { get; init; } = new();

    public bool Multi
        => Type.Length > 0 && Type != "Single";

    /// <summary> A choice between bodies or body sizes ("Rue M", "YAB - small"): picked for the user, never shown. </summary>
    public bool IsBody { get; set; }

    /// <summary> A choice between races or genders: picked for the user, never shown. </summary>
    public bool IsRace { get; set; }

    /// <summary> The options on by default. </summary>
    public List<string> DefaultChoice()
    {
        if (Options.Count == 0)
            return new List<string>();
        if (Multi)
        {
            var chosen = new List<string>();
            for (var i = 0; i < Options.Count && i < 63; ++i)
                if ((Default >> i & 1) == 1)
                    chosen.Add(Options[i].Name);
            return chosen;
        }

        var index = Default >= 0 && Default < Options.Count ? (int)Default : 0;
        return new List<string> { Options[index].Name };
    }
}

/// <summary> Race codes of one socket's files over all options: models and the rest (materials, textures). </summary>
public sealed class SocketCodes
{
    public HashSet<int> Models { get; init; } = new();
    public HashSet<int> Other { get; init; } = new();
}

/// <summary> What the mod folder says about a mod (read in the background, never on the draw thread). </summary>
public sealed class ModFacts
{
    public string Folder { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string? Picture { get; init; }
    public DateTime Added { get; init; }

    /// <summary> Changes when the mod's definition changes (meta.json, default_mod.json, group_*.json). </summary>
    public string Stamp { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<GroupInfo> Groups { get; init; } = Array.Empty<GroupInfo>();

    /// <summary> Race codes per socket over every file the mod can change. </summary>
    public IReadOnlyDictionary<Socket, SocketCodes> Codes { get; init; } = new Dictionary<Socket, SocketCodes>();

    /// <summary> Gear slot names the mod has files for ("LFinger" tells a ring mod changes the left hand too). </summary>
    public IReadOnlySet<string> GearSlots { get; init; } = new HashSet<string>();

    /// <summary> Sockets of the files that are always on (default data). </summary>
    public IReadOnlySet<Socket> DefaultSockets { get; init; } = new HashSet<Socket>();

    /// <summary> Something went wrong reading the definition (the mod is still listed, with what could be read). </summary>
    public bool Broken { get; init; }

    /// <summary>
    /// Per socket, the names of the files the mod has for it (default data, every option and container), lower case,
    /// sorted, at most <see cref="ModFolder.MaxFilesPerSocket"/>. Made when the definition is read (the library's own
    /// thread), so the looks never read mod files themselves.
    /// </summary>
    public IReadOnlyDictionary<Socket, IReadOnlyList<string>> Files { get; init; } = new Dictionary<Socket, IReadOnlyList<string>>();
}

/// <summary> One mod of the library. Replaced, never changed, when something about it changes. </summary>
public sealed class LibMod
{
    public string Directory { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary> The user's Penumbra folder ("Clothes/Female/Top"), "" when none or not read yet. </summary>
    public string SortFolder { get; init; } = string.Empty;
    public ModFacts? Facts { get; init; }
    public ModItems? Items { get; init; }
    public ModDetection? Detection { get; init; }

    /// <summary> When the closet first saw the mod (null: not known). </summary>
    public DateTime? FirstSeen { get; init; }

    /// <summary>
    /// When the mod came into the library: its folder's creation time, or when the closet first saw it if that was earlier
    /// (a library copied to a new drive gets new folder times) or the folder time is unknown.
    /// </summary>
    public DateTime Since
        => FirstSeen is { } seen && (Added == DateTime.MinValue || seen < Added) ? seen : Added;

    public string Author
        => Facts?.Author ?? string.Empty;

    public string? Picture
        => Facts?.Picture;

    public DateTime Added
        => Facts?.Added ?? DateTime.MinValue;

    public IReadOnlyList<GroupInfo> Groups
        => Facts?.Groups ?? Array.Empty<GroupInfo>();

    /// <summary> A copy with some things replaced (a mod is never changed in place: other threads may be reading it). </summary>
    public LibMod With(string? sortFolder = null, ModFacts? facts = null, ModItems? items = null, ModDetection? detection = null, bool clearDetection = false)
        => new()
        {
            Directory  = Directory,
            Name       = Name,
            SortFolder = sortFolder ?? SortFolder,
            Facts      = facts ?? Facts,
            Items      = items ?? Items,
            Detection  = clearDetection ? null : detection ?? Detection,
            FirstSeen  = FirstSeen,
        };
}

/// <summary> One part of a mod as the closet sees it. </summary>
public sealed class LibPart
{
    public PartKey Key { get; init; }
    public LibMod Mod { get; init; } = new();

    /// <summary> What is known about the part: the detector's answer, or step 1 (by socket alone). </summary>
    public PartInfo Info { get; init; } = new();
    public bool FromDetector { get; init; }

    /// <summary> Gear: the item to put on for each version, best first (empty for other sockets). </summary>
    public IReadOnlyList<ItemChoice> Versions { get; init; } = Array.Empty<ItemChoice>();

    /// <summary> Gear: every item the mod changes in this socket (several items can show one version). </summary>
    public IReadOnlyList<ItemChoice> Items { get; init; } = Array.Empty<ItemChoice>();

    /// <summary> Gear: Glamourer slot ids the item goes into (a ring mod with files for both hands: both). </summary>
    public IReadOnlyList<byte> SlotIds { get; init; } = Array.Empty<byte>();

    /// <summary> Hair, face, tail, ears: the customize values to set for the player (key → value). </summary>
    public IReadOnlyDictionary<string, int> Customize { get; init; } = new Dictionary<string, int>();

    /// <summary> Every number the mod has for the player per customize key (the first is in <see cref="Customize"/>). </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> CustomizeChoices { get; init; } = new Dictionary<string, IReadOnlyList<int>>();

    /// <summary> It can show on the player (race and gender). Parts that do not fit are left out of the closet. </summary>
    public bool FitsMe { get; init; } = true;

    public string Name
        => Mod.Name;

    private string? _keyText;

    /// <summary> <see cref="Key"/> as text (how the user's moves and use are kept), made once. </summary>
    public string KeyText
        => _keyText ??= Key.ToString();

    private string? _nameLower;

    /// <summary> The name in lower case, made once (search). </summary>
    public string NameLower
        => _nameLower ??= Name.ToLowerInvariant();

    public int VersionCount
        => Math.Max(1, Versions.Count);

    /// <summary> The version an item belongs to (by its model set), -1 when it is none of this part's items. </summary>
    public int VersionOf(ulong itemId)
    {
        if (Items.FirstOrDefault(i => i.Id == itemId) is not { } item)
            return -1;
        for (var v = 0; v < Versions.Count; ++v)
            if (Versions[v].Id == item.Id || item.Model != 0 && Versions[v].SetId == item.SetId)
                return v;
        return -1;
    }
}
