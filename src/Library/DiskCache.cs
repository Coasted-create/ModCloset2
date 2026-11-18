using System.Text.Json;
using ModCloset2.Core;

namespace ModCloset2.Library;

/// <summary> One mod's detection as kept on disk, with what it was made from. </summary>
public sealed class DetectionEntry
{
    public string Stamp { get; set; } = string.Empty;
    public int Version { get; set; }
    public ModDetection Detection { get; set; } = new();
}

/// <summary>
/// A cache file of mod directory → entry, in the plugin's own config folder (never in a mod). Read once; written in the
/// background from a snapshot taken on the framework thread, one write at a time, through a temporary file so a crash
/// mid-write leaves the old file. Values must not be changed after they are put in. No Dalamud types.
/// </summary>
public sealed class DiskCache<T> where T : class
{
    private readonly string _path;
    private readonly Action<string>? _log;
    private Dictionary<string, T> _entries;
    private readonly object _writeLock = new();
    private Task _lastWrite = Task.CompletedTask;

    public DiskCache(string path, Action<string>? log = null)
    {
        _path    = path;
        _log     = log;
        _entries = Load(path, log);
    }

    public bool Dirty { get; private set; }

    public int Count
        => _entries.Count;

    public T? Get(string mod)
        => _entries.TryGetValue(mod, out var entry) ? entry : null;

    public void Set(string mod, T entry)
    {
        _entries[mod] = entry;
        Dirty         = true;
    }

    public void Remove(string mod)
        => Dirty |= _entries.Remove(mod);

    public void Clear()
    {
        _entries = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        Dirty    = true;
    }

    /// <summary> Moves an entry when Penumbra renames a mod directory. </summary>
    public void Rename(string oldMod, string newMod)
    {
        if (_entries.Remove(oldMod, out var entry))
        {
            _entries[newMod] = entry;
            Dirty            = true;
        }
    }

    /// <summary> Writes the file in the background when something changed. </summary>
    public void Save()
    {
        if (!Dirty)
            return;
        Dirty = false;
        var snapshot = new Dictionary<string, T>(_entries, StringComparer.OrdinalIgnoreCase);
        lock (_writeLock)
            _lastWrite = _lastWrite.ContinueWith(_ =>
            {
                try
                {
                    var dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    var tmp = _path + ".tmp";
                    using (var stream = File.Create(tmp))
                        JsonSerializer.Serialize(stream, snapshot);
                    File.Move(tmp, _path, true);
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"could not save {Path.GetFileName(_path)}: {ex.Message}");
                }
            }, TaskScheduler.Default);
    }

    /// <summary> Waits for pending writes (on unload). </summary>
    public void Flush()
    {
        Task last;
        lock (_writeLock)
            last = _lastWrite;
        last.Wait(TimeSpan.FromSeconds(5));
    }

    private static Dictionary<string, T> Load(string path, Action<string>? log)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                if (JsonSerializer.Deserialize<Dictionary<string, T>>(stream) is { } loaded)
                    return new Dictionary<string, T>(loaded.Where(kv => kv.Value is not null), StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"{Path.GetFileName(path)} unreadable, starting a new one: {ex.Message}");
        }

        return new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary> When the closet first saw a mod (for "New" when folder dates are not to be trusted). </summary>
public sealed class SeenEntry
{
    public DateTime At { get; set; }
}

/// <summary> When a cached detection can be used again. </summary>
public static class DetectionKey
{
    /// <summary> Same mod definition (stamp) and same detector version; an empty stamp never matches (unknown state). </summary>
    public static bool Valid(DetectionEntry? entry, string stamp, int detectorVersion)
        => entry is not null && stamp.Length > 0 && entry.Stamp == stamp && entry.Version == detectorVersion;
}

/// <summary> Stands in for the smart detection until it is plugged in: knows nothing, so step 1 alone carries the closet. </summary>
public sealed class NoDetector : IDetector
{
    public int Version
        => 0;

    public ModDetection Detect(string modFolder, ModHints hints, CancellationToken cancel = default)
        => new() { DetectorVersion = 0 };

    public IReadOnlyList<PartInfo> LookAlikes(PartInfo moved, IEnumerable<PartInfo> candidates)
        => Array.Empty<PartInfo>();

    public int WriteCensus(string file, IEnumerable<(string Folder, ModHints Hints)> mods, Action<int>? progress = null, CancellationToken cancel = default)
        => 0;
}
