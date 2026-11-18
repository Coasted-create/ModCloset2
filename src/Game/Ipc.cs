using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Newtonsoft.Json.Linq;

namespace ModCloset2.Game;

/// <summary>
/// Thin wrappers around the IPC gates of Penumbra and Glamourer (port of the old Ipc.cs, without Customize+ and without
/// what the new closet does not use). Labels and generic signatures are taken 1:1 from Penumbra.Api and Glamourer.Api
/// (IpcSubscribers). Every call is wrapped; <see cref="CallFailed"/> (255) means the call itself did not go through
/// (plugin missing or unloading). Framework thread only.
/// </summary>
public sealed class Ipc : IDisposable
{
    // Glamourer.Api.Enums.ApplyFlag
    public const ulong FlagOnce          = 0x01;
    public const ulong FlagEquipment     = 0x02;
    public const ulong FlagCustomization = 0x04;

    public const int GlamourerSuccess     = 0;
    public const int GlamourerNothingDone = 1;
    public const int GlamourerInvalidKey  = 6;

    public const int PenumbraSuccess        = 0;
    public const int PenumbraNothingChanged = 1;

    public const int CallFailed = 255;

    /// <summary>
    /// Identification-only (negative) key for temporary Penumbra settings: tags them without locking them. Different from
    /// the old Mod Closet's key ("MCLS"), so "remove all of ours" never takes away what the old plugin is trying on.
    /// </summary>
    public const int PenumbraKey = -0x4D433232; // "MC22"

    public const string TemporarySource = "Mod Closet 2";

    /// <summary> Penumbra's TabType.Mods. </summary>
    private const int PenumbraModsTab = 1;

    private static readonly List<byte> NoStains = new() { 0, 0 };

    private readonly ICallGateSubscriber<(int, int)> _penumbraApiVersion;
    private readonly ICallGateSubscriber<string> _penumbraModDirectory;
    private readonly ICallGateSubscriber<Dictionary<string, string>> _penumbraModList;
    private readonly ICallGateSubscriber<string, string, (int, string, bool, bool)> _penumbraModPath;
    private readonly ICallGateSubscriber<string, int> _penumbraAddMod;
    private readonly ICallGateSubscriber<string, string, string, int> _penumbraSetModPath;
    private readonly ICallGateSubscriber<string, string, Dictionary<string, object?>> _penumbraChangedItems;
    private readonly ICallGateSubscriber<Guid, bool, bool, int, (int, Dictionary<string, (bool, int, Dictionary<string, List<string>>, bool, bool)>?)> _penumbraAllSettings;
    private readonly ICallGateSubscriber<Guid, string, string, bool, int> _penumbraTrySetMod;
    private readonly ICallGateSubscriber<Guid, string, string, bool, int> _penumbraTryInherit;
    private readonly ICallGateSubscriber<Guid, string, string, int, int> _penumbraTrySetPriority;
    private readonly ICallGateSubscriber<Guid, string, string, string, IReadOnlyList<string>, int> _penumbraTrySetSettings;
    private readonly ICallGateSubscriber<int, (bool, bool, (Guid, string))> _penumbraCollectionForObject;
    private readonly ICallGateSubscriber<int, string, string, (bool, bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>), string, int, int> _penumbraSetTemp;
    private readonly ICallGateSubscriber<Guid, string, string, int, int> _penumbraRemoveTemp;
    private readonly ICallGateSubscriber<Guid, int, int> _penumbraRemoveAllTemp;
    private readonly ICallGateSubscriber<int, int, object> _penumbraRedraw;
    private readonly ICallGateSubscriber<nint, int, object> _penumbraRedrawn;
    private readonly ICallGateSubscriber<string, object> _penumbraModAdded;
    private readonly ICallGateSubscriber<string, object> _penumbraModDeleted;
    private readonly ICallGateSubscriber<string, string, object> _penumbraModMoved;
    private readonly ICallGateSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]> _penumbraResourcePaths;
    private readonly ICallGateSubscriber<int, string, string, int> _penumbraOpenMainWindow;

    private readonly ICallGateSubscriber<(int, int)> _glamourerApiVersion;
    private readonly ICallGateSubscriber<int, uint, (int, string?)> _glamourerGetState;
    private readonly ICallGateSubscriber<int, uint, (int, JObject?)> _glamourerGetStateJson;
    private readonly ICallGateSubscriber<object, int, uint, ulong, int> _glamourerApplyState;
    private readonly ICallGateSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int> _glamourerSetItem;

    private DateTime _lastRefresh = DateTime.MinValue;

    /// <summary> Raised when Penumbra finished redrawing a game object (object index). </summary>
    public event Action<int>? Redrawn;

    /// <summary> Raised when mods were added, deleted or renamed in Penumbra. </summary>
    public event Action? ModsChanged;

    /// <summary> Raised when a mod directory was renamed (old, new). </summary>
    public event Action<string, string>? ModMoved;

    public bool PenumbraAvailable { get; private set; }
    public bool GlamourerAvailable { get; private set; }

    public Ipc(IDalamudPluginInterface pi)
    {
        _penumbraApiVersion          = pi.GetIpcSubscriber<(int, int)>("Penumbra.ApiVersion.V5");
        _penumbraModDirectory        = pi.GetIpcSubscriber<string>("Penumbra.GetModDirectory");
        _penumbraModList             = pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
        _penumbraModPath             = pi.GetIpcSubscriber<string, string, (int, string, bool, bool)>("Penumbra.GetModPath.V5");
        _penumbraAddMod              = pi.GetIpcSubscriber<string, int>("Penumbra.AddMod.V5");
        _penumbraSetModPath          = pi.GetIpcSubscriber<string, string, string, int>("Penumbra.SetModPath.V5");
        _penumbraChangedItems        = pi.GetIpcSubscriber<string, string, Dictionary<string, object?>>("Penumbra.GetChangedItems.V5");
        _penumbraAllSettings         = pi.GetIpcSubscriber<Guid, bool, bool, int, (int, Dictionary<string, (bool, int, Dictionary<string, List<string>>, bool, bool)>?)>("Penumbra.GetAllModSettings");
        _penumbraTrySetMod           = pi.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5");
        _penumbraTryInherit          = pi.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TryInheritMod.V5");
        _penumbraTrySetPriority      = pi.GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.TrySetModPriority.V5");
        _penumbraTrySetSettings      = pi.GetIpcSubscriber<Guid, string, string, string, IReadOnlyList<string>, int>("Penumbra.TrySetModSettings.V5");
        _penumbraCollectionForObject = pi.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
        _penumbraSetTemp             = pi.GetIpcSubscriber<int, string, string, (bool, bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>), string, int, int>("Penumbra.SetTemporaryModSettingsPlayer.V5");
        _penumbraRemoveTemp          = pi.GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.RemoveTemporaryModSettings.V5");
        _penumbraRemoveAllTemp       = pi.GetIpcSubscriber<Guid, int, int>("Penumbra.RemoveAllTemporaryModSettings.V5");
        _penumbraRedraw              = pi.GetIpcSubscriber<int, int, object>("Penumbra.RedrawObject.V5");
        _penumbraRedrawn             = pi.GetIpcSubscriber<nint, int, object>("Penumbra.GameObjectRedrawn");
        _penumbraModAdded            = pi.GetIpcSubscriber<string, object>("Penumbra.ModAdded");
        _penumbraModDeleted          = pi.GetIpcSubscriber<string, object>("Penumbra.ModDeleted");
        _penumbraModMoved            = pi.GetIpcSubscriber<string, string, object>("Penumbra.ModMoved");
        _penumbraResourcePaths       = pi.GetIpcSubscriber<ushort[], Dictionary<string, HashSet<string>>?[]>("Penumbra.GetGameObjectResourcePaths.V5");
        _penumbraOpenMainWindow      = pi.GetIpcSubscriber<int, string, string, int>("Penumbra.OpenMainWindow.V5");
        _penumbraRedrawn.Subscribe(OnRedrawn);
        _penumbraModAdded.Subscribe(OnModAddedOrDeleted);
        _penumbraModDeleted.Subscribe(OnModAddedOrDeleted);
        _penumbraModMoved.Subscribe(OnModMoved);

        _glamourerApiVersion   = pi.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersion.V2");
        _glamourerGetState     = pi.GetIpcSubscriber<int, uint, (int, string?)>("Glamourer.GetStateBase64");
        _glamourerGetStateJson = pi.GetIpcSubscriber<int, uint, (int, JObject?)>("Glamourer.GetState");
        _glamourerApplyState   = pi.GetIpcSubscriber<object, int, uint, ulong, int>("Glamourer.ApplyState");
        _glamourerSetItem      = pi.GetIpcSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int>("Glamourer.SetItem.V3");

        Refresh(force: true);
    }

    private void OnRedrawn(nint address, int index)
        => Redrawn?.Invoke(index);

    private void OnModAddedOrDeleted(string _)
        => ModsChanged?.Invoke();

    private void OnModMoved(string oldDirectory, string newDirectory)
    {
        ModMoved?.Invoke(oldDirectory, newDirectory);
        ModsChanged?.Invoke();
    }

    /// <summary> Re-checks which plugins answer. Cheap calls only; throttled to once per second unless forced. </summary>
    public void Refresh(bool force = false)
    {
        if (!force && (DateTime.Now - _lastRefresh).TotalSeconds < 1)
            return;

        _lastRefresh       = DateTime.Now;
        PenumbraAvailable  = Try("Penumbra.ApiVersion", () => _penumbraApiVersion.InvokeFunc());
        GlamourerAvailable = Try("Glamourer.ApiVersion", () => _glamourerApiVersion.InvokeFunc());
    }

    // Every call into another plugin goes through one stopwatch (FrameReport): a slow frame's log line then says which
    // call took the time. Two timestamps per call, nothing allocated for it.

    private static bool Try(string name, Func<(int, int)> f)
    {
        var started = FrameReport.CallStarted();
        try
        {
            f();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            FrameReport.CallEnded(name, started);
        }
    }

    private static T Call<T>(string name, Func<T> f, T fallback)
    {
        var started = FrameReport.CallStarted();
        try
        {
            return f();
        }
        catch
        {
            return fallback;
        }
        finally
        {
            FrameReport.CallEnded(name, started);
        }
    }

    // ---- Penumbra: mods ----

    public string ModDirectory()
        => Call("Penumbra.GetModDirectory", () => _penumbraModDirectory.InvokeFunc() ?? string.Empty, string.Empty);

    /// <summary> Mod directory → display name. </summary>
    public Dictionary<string, string> ModList()
        => Call("Penumbra.GetModList", () => _penumbraModList.InvokeFunc() ?? new Dictionary<string, string>(), new Dictionary<string, string>());

    /// <summary> The mod's place in Penumbra's folder tree ("Clothes/Female/Top/Mod name"); null when Penumbra can't say. </summary>
    public string? ModPath(string modDirectory)
        => Call("Penumbra.GetModPath", () =>
        {
            var (ec, path, _, _) = _penumbraModPath.InvokeFunc(modDirectory, string.Empty);
            return ec == 0 ? path : null;
        }, null);

    /// <summary>
    /// Has Penumbra take in a new mod folder inside its mod folder (the folder's name, not its full path). Penumbra's code:
    /// <see cref="PenumbraSuccess"/>, or why not (the folder is missing, it lies outside the mod folder);
    /// <see cref="CallFailed"/> when the call did not go through. Success means Penumbra took the call, not that it loaded.
    /// </summary>
    public int AddMod(string modDirectory)
        => Call("Penumbra.AddMod", () => _penumbraAddMod.InvokeFunc(modDirectory), CallFailed);

    /// <summary> Puts a mod at a place in Penumbra's folder tree ("Clothes/Tops/New name": folders, then the mod's own name). </summary>
    public int SetModPath(string modDirectory, string newPath)
        => Call("Penumbra.SetModPath", () => _penumbraSetModPath.InvokeFunc(modDirectory, string.Empty, newPath), CallFailed);

    /// <summary> Changed item name → game object (or null) for one mod. Null when the call failed (don't cache that). </summary>
    public Dictionary<string, object?>? ChangedItems(string modDirectory)
        => Call("Penumbra.GetChangedItems", () => _penumbraChangedItems.InvokeFunc(modDirectory, string.Empty) ?? new Dictionary<string, object?>(), null);

    /// <summary> Opens Penumbra's window at a mod. </summary>
    public int OpenMod(string modDirectory, string modName)
        => Call("Penumbra.OpenMainWindow", () => _penumbraOpenMainWindow.InvokeFunc(PenumbraModsTab, modDirectory, modName), CallFailed);

    /// <summary> Game path → the file actually used for it (a mod file on disk, or the game path itself), for one object. Null when Penumbra could not tell. </summary>
    public Dictionary<string, string>? ResourceMap(ushort objectIndex)
    {
        var result = Call<Dictionary<string, HashSet<string>>?[]?>("Penumbra.GetGameObjectResourcePaths", () => _penumbraResourcePaths.InvokeFunc(new[] { objectIndex }), null);
        if (result is not { Length: > 0 } || result[0] is not { } paths)
            return null;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (actual, games) in paths)
        {
            var file = actual;
            var bar  = file.LastIndexOf('|');
            if (bar >= 0)
                file = file[(bar + 1)..];
            foreach (var game in games)
                map[game.Replace('\\', '/')] = file;
        }

        return map;
    }

    /// <summary> Mod directory → (enabled, priority, options, inherited, temporary) in a collection, or null. </summary>
    public Dictionary<string, (bool, int, Dictionary<string, List<string>>, bool, bool)>? AllSettings(Guid collection, bool ignoreTemporary)
        => Call("Penumbra.GetAllModSettings", () =>
        {
            var (code, settings) = _penumbraAllSettings.InvokeFunc(collection, false, ignoreTemporary, 0);
            return code == PenumbraSuccess ? settings : null;
        }, null);

    public int TrySetMod(Guid collection, string modDirectory, bool enabled)
        => Call("Penumbra.TrySetMod", () => _penumbraTrySetMod.InvokeFunc(collection, modDirectory, string.Empty, enabled), CallFailed);

    /// <summary> True: the mod drops its own settings in this collection and inherits again (or has none). </summary>
    public int TryInherit(Guid collection, string modDirectory, bool inherit)
        => Call("Penumbra.TryInheritMod", () => _penumbraTryInherit.InvokeFunc(collection, modDirectory, string.Empty, inherit), CallFailed);

    public int TrySetPriority(Guid collection, string modDirectory, int priority)
        => Call("Penumbra.TrySetModPriority", () => _penumbraTrySetPriority.InvokeFunc(collection, modDirectory, string.Empty, priority), CallFailed);

    public int TrySetSettings(Guid collection, string modDirectory, string group, IReadOnlyList<string> options)
        => Call("Penumbra.TrySetModSettings", () => _penumbraTrySetSettings.InvokeFunc(collection, modDirectory, string.Empty, group, options), CallFailed);

    /// <summary> The collection that currently applies to a game object, or null. </summary>
    public Guid? CollectionForObject(int objectIndex)
        => Call<Guid?>("Penumbra.GetCollectionForObject", () =>
        {
            var (valid, _, (id, _)) = _penumbraCollectionForObject.InvokeFunc(objectIndex);
            return valid && id != Guid.Empty ? id : null;
        }, null);

    // ---- Penumbra: temporary settings and redraw ----

    public int SetTemporaryMod(int objectIndex, string modDirectory, int priority, IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
        => Call("Penumbra.SetTemporaryModSettingsPlayer", () => _penumbraSetTemp.InvokeFunc(objectIndex, modDirectory, string.Empty, (false, true, priority, settings), TemporarySource, PenumbraKey), CallFailed);

    /// <summary> Temporarily switches a mod off for the player (removed again with <see cref="RemoveTemporaryMod"/>). </summary>
    public int SetTemporaryModOff(int objectIndex, string modDirectory, int priority)
        => Call("Penumbra.SetTemporaryModSettingsPlayer", () => _penumbraSetTemp.InvokeFunc(objectIndex, modDirectory, string.Empty,
            (false, false, priority, (IReadOnlyDictionary<string, IReadOnlyList<string>>)new Dictionary<string, IReadOnlyList<string>>()), TemporarySource, PenumbraKey), CallFailed);

    /// <summary> Removes our temporary setting of one mod from a collection (works without the character being present). </summary>
    public int RemoveTemporaryMod(Guid collection, string modDirectory)
        => Call("Penumbra.RemoveTemporaryModSettings", () => _penumbraRemoveTemp.InvokeFunc(collection, modDirectory, string.Empty, PenumbraKey), CallFailed);

    /// <summary> Removes every temporary setting with our key from a collection (safety net). </summary>
    public int RemoveAllTemporaryMods(Guid collection)
        => Call("Penumbra.RemoveAllTemporaryModSettings", () => _penumbraRemoveAllTemp.InvokeFunc(collection, PenumbraKey), CallFailed);

    public void Redraw(int objectIndex)
    {
        var started = FrameReport.CallStarted();
        try
        {
            _penumbraRedraw.InvokeAction(objectIndex, 0);
        }
        catch
        {
            // Penumbra gone: nothing to redraw with.
        }
        finally
        {
            FrameReport.CallEnded("Penumbra.RedrawObject", started);
        }
    }

    // ---- Glamourer ----

    public int GetState(int objectIndex, out string? base64)
    {
        var started = FrameReport.CallStarted();
        try
        {
            var (code, state) = _glamourerGetState.InvokeFunc(objectIndex, 0);
            base64 = state;
            return code;
        }
        catch
        {
            base64 = null;
            return CallFailed;
        }
        finally
        {
            FrameReport.CallEnded("Glamourer.GetStateBase64", started);
        }
    }

    /// <summary> The actor's current state as JSON (design-base layout), or null. </summary>
    public JObject? GetStateJson(int objectIndex)
        => Call("Glamourer.GetState", () =>
        {
            var (code, state) = _glamourerGetStateJson.InvokeFunc(objectIndex, 0);
            return code == GlamourerSuccess ? state : null;
        }, null);

    /// <summary> The actor's current state as JSON with Glamourer's code (0 success; <see cref="CallFailed"/> when the call failed). </summary>
    public JObject? GetStateJson(int objectIndex, out int code)
    {
        var started = FrameReport.CallStarted();
        try
        {
            var (c, state) = _glamourerGetStateJson.InvokeFunc(objectIndex, 0);
            code = c;
            return c == GlamourerSuccess ? state : null;
        }
        catch
        {
            code = CallFailed;
            return null;
        }
        finally
        {
            FrameReport.CallEnded("Glamourer.GetState", started);
        }
    }

    /// <summary> Applies a state: a base64 string from <see cref="GetState"/> or a Newtonsoft JObject. </summary>
    public int ApplyState(object state, int objectIndex, ulong flags)
        => Call("Glamourer.ApplyState", () => _glamourerApplyState.InvokeFunc(state, objectIndex, 0, flags), CallFailed);

    public int SetItem(int objectIndex, byte slot, ulong itemId)
        => Call("Glamourer.SetItem", () => _glamourerSetItem.InvokeFunc(objectIndex, slot, itemId, NoStains, 0, FlagOnce), CallFailed);

    public void Dispose()
    {
        try
        {
            _penumbraRedrawn.Unsubscribe(OnRedrawn);
            _penumbraModAdded.Unsubscribe(OnModAddedOrDeleted);
            _penumbraModDeleted.Unsubscribe(OnModAddedOrDeleted);
            _penumbraModMoved.Unsubscribe(OnModMoved);
        }
        catch
        {
            // ignored
        }
    }
}
