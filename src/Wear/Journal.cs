using System.Text.Json;
using Dalamud.Plugin.Services;
using ModCloset2.Game;

namespace ModCloset2.Wear;

/// <summary>
/// Undo history of everything the closet changed in Penumbra and Glamourer, kept on disk so it survives restarts and
/// crashes (port of the old Journal, without redo and without collection switching, which the new closet does not do).
/// Undo puts back exactly how things were before that step: mod settings, the look, Keep records. Framework thread only.
/// </summary>
public sealed class Journal
{
    private const int MaxEntries  = 150;
    private const int PlayerIndex = 0;

    private readonly Ipc _ipc;
    private readonly Dictionary<string, WornRecord> _worn;
    private readonly Action _saveConfig;
    private readonly Func<string> _playerKey;
    private readonly Action _redraw;
    private readonly IPluginLog _log;
    private readonly string _path;

    public List<JournalEntry> Entries { get; private set; } = new();

    public Journal(Ipc ipc, Dictionary<string, WornRecord> worn, Action saveConfig, Func<string> playerKey, Action redraw, IPluginLog log, string configDirectory)
    {
        _ipc        = ipc;
        _worn       = worn;
        _saveConfig = saveConfig;
        _playerKey  = playerKey;
        _redraw     = redraw;
        _log        = log;
        _path       = Path.Combine(configDirectory, "history.json");
        Load();

        // A step that was started but never finished (the game closed mid-step): the old plugin asked; the new closet keeps
        // things as they are (what is on the character is what the user saw last) and only notes it in the log.
        foreach (var entry in Entries.Where(e => !e.Committed && !e.Undone))
        {
            entry.Committed = true;
            _log.Information($"Mod Closet 2: the step \"{entry.Description}\" was interrupted last time; kept as it is.");
        }
    }

    // ---- recording ----

    /// <summary> Reads how the given mods stand right now in a collection. </summary>
    public static List<ModSettingState> Snapshot(
        IReadOnlyDictionary<string, (bool Enabled, int Priority, Dictionary<string, List<string>> Options, bool Inherited)> settings,
        IEnumerable<string> modDirectories)
        => modDirectories.Distinct(StringComparer.OrdinalIgnoreCase).Select(dir =>
            settings.TryGetValue(dir, out var s) && !s.Inherited
                ? new ModSettingState { ModDirectory = dir, HasOwn = true, Enabled = s.Enabled, Priority = s.Priority, Options = s.Options.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()) }
                : new ModSettingState { ModDirectory = dir, HasOwn = false }).ToList();

    /// <summary> Starts a step: saved to disk before anything is changed. </summary>
    public JournalEntry Begin(Guid id, string description, Guid collection, string owner, List<ModSettingState> before, string? lookBefore, IEnumerable<string> wornDirectories)
    {
        var entry = new JournalEntry
        {
            Id          = id,
            Description = description,
            Collection  = collection,
            Owner       = owner,
            Before      = before,
            LookBefore  = lookBefore,
            WornBefore  = wornDirectories.Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(d => d, d => _worn.TryGetValue(d, out var r) ? Clone(r) : null),
        };

        // A new step drops what was undone (no redo in the new closet).
        Entries.RemoveAll(e => e.Undone);
        Entries.Add(entry);
        if (Entries.Count > MaxEntries)
            Entries.RemoveRange(0, Entries.Count - MaxEntries);
        Save();
        return entry;
    }

    /// <summary> Finishes a step with the state it ended in. </summary>
    public void Commit(JournalEntry entry, List<ModSettingState> after, string? lookAfter)
    {
        entry.After     = after;
        entry.LookAfter = lookAfter;
        entry.WornAfter = entry.WornBefore.Keys.ToDictionary(d => d, d => _worn.TryGetValue(d, out var r) ? Clone(r) : null);
        entry.Committed = true;
        Save();
    }

    // ---- undo ----

    public JournalEntry? NextUndo
        => Entries.LastOrDefault(e => e.Committed && !e.Undone);

    public bool Has(Guid id)
        => Entries.Any(e => e.Id == id && e.Committed);

    /// <summary> Puts back how things were before the step. Returns how many things could not be put back (0: all fine). </summary>
    public int Undo(JournalEntry entry)
    {
        var problems = Apply(entry, entry.Before, entry.LookBefore, entry.WornBefore);
        entry.Undone    = true;
        entry.Committed = true;
        Save();
        return problems;
    }

    private int Apply(JournalEntry entry, List<ModSettingState> states, string? look, Dictionary<string, WornRecord?> worn)
    {
        var failed = states.Count(s => !ApplyState(entry.Collection, s));

        foreach (var (dir, record) in worn)
        {
            if (record is null)
                _worn.Remove(dir);
            else
                _worn[dir] = Clone(record)!;
        }

        if (look is { Length: > 0 })
        {
            // Only ever put a look back on the character it came from.
            if (_playerKey() == entry.Owner && entry.Owner.Length > 0)
            {
                var r = _ipc.ApplyState(look, PlayerIndex, Ipc.FlagEquipment | Ipc.FlagCustomization | Ipc.FlagOnce);
                if (r is not (Ipc.GlamourerSuccess or Ipc.GlamourerNothingDone))
                    failed++;
            }
            else
            {
                failed++;
            }
        }

        _redraw();
        _saveConfig();
        return failed;
    }

    /// <summary> Puts one mod's collection setting into the given state. </summary>
    public bool ApplyState(Guid collection, ModSettingState s)
    {
        if (!s.HasOwn)
            return _ipc.TryInherit(collection, s.ModDirectory, true) is Ipc.PenumbraSuccess or Ipc.PenumbraNothingChanged;

        var ok = _ipc.TrySetMod(collection, s.ModDirectory, s.Enabled) is Ipc.PenumbraSuccess or Ipc.PenumbraNothingChanged;
        ok &= _ipc.TrySetPriority(collection, s.ModDirectory, s.Priority) is Ipc.PenumbraSuccess or Ipc.PenumbraNothingChanged;
        foreach (var (group, options) in s.Options)
            _ipc.TrySetSettings(collection, s.ModDirectory, group, options);
        return ok;
    }

    /// <summary> Moves remembered records when Penumbra renames a mod directory. </summary>
    public void OnModMoved(string oldDirectory, string newDirectory)
    {
        var changed = false;
        foreach (var entry in Entries)
            foreach (var list in new[] { entry.Before, entry.After })
                foreach (var s in list.Where(s => s.ModDirectory.Equals(oldDirectory, StringComparison.OrdinalIgnoreCase)))
                {
                    s.ModDirectory = newDirectory;
                    changed        = true;
                }

        if (changed)
            Save();
    }

    // ---- storage ----

    private static WornRecord? Clone(WornRecord? r)
        => r is null ? null : JsonSerializer.Deserialize<WornRecord>(JsonSerializer.Serialize(r));

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                Entries = JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex)
        {
            _log.Warning($"Mod Closet 2: history file unreadable, starting a new one: {ex.Message}");
            Entries = new();
        }
    }

    private readonly object _writeLock = new();
    private Task _lastWrite = Task.CompletedTask;

    /// <summary> Serialises now (so later changes can't leak in) and writes the file in the background, in order. </summary>
    private void Save()
    {
        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(Entries);
        }
        catch (Exception ex)
        {
            _log.Warning($"Mod Closet 2: could not save the history: {ex.Message}");
            return;
        }

        lock (_writeLock)
            _lastWrite = _lastWrite.ContinueWith(_ =>
            {
                try
                {
                    var tmp = _path + ".tmp";
                    File.WriteAllBytes(tmp, bytes);
                    File.Move(tmp, _path, true);
                }
                catch (Exception ex)
                {
                    _log.Warning($"Mod Closet 2: could not save the history: {ex.Message}");
                }
            }, TaskScheduler.Default);
    }

    /// <summary> Waits for pending history writes (on unload). </summary>
    public void Flush()
    {
        Task last;
        lock (_writeLock)
            last = _lastWrite;
        last.Wait(TimeSpan.FromSeconds(3));
    }
}
