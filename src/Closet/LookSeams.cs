using ModCloset2.Core;
using ModCloset2.Library;
using ModCloset2.Looks;

namespace ModCloset2.Closet;

// The foundation's side of the looks engine's seams (src/Looks/Seams.cs) besides the outfit, which the session itself is
// (ClosetSession : ILooksOutfit): the library as the looks see it, the store (one file, written in the background), and
// undo (one step in the session's history). No Dalamud types.

/// <summary> The library, as the looks see it: the parts with their shelves after the user's moves. </summary>
public sealed class LooksLibrary : ILooksLibrary
{
    private readonly ClosetSession _session;
    private readonly ILibrary _library;

    private readonly Dictionary<PartKey, (LibPart Part, string Shelf, LookLibraryPart Record)> _records = new();
    private (int Library, int Sort, int Photos) _seen = (int.MinValue, 0, 0);
    private int _version;
    private int _libraryVersion = int.MinValue;
    /// <summary> Mod → the sockets of its parts (a part gone inside a mod can make a look's piece go missing too). </summary>
    private Dictionary<string, string>? _mods;
    private int _modsVersion;
    private (int Library, int Sort, int Photos) _allFor = (int.MinValue, 0, 0);
    private IReadOnlyList<LookLibraryPart> _all = Array.Empty<LookLibraryPart>();

    public LooksLibrary(ClosetSession session, ILibrary library)
    {
        _session = session;
        _library = library;
    }

    public int Version
    {
        get
        {
            var seen = (_library.Version, _session.SortStamp, _session.PhotoStamp);
            if (seen != _seen)
            {
                _seen = seen;
                _version++;
            }

            return _version;
        }
    }

    /// <summary>
    /// Moves when a mod came, went or was renamed, or when the parts of a mod are others than before (detector 3 made a
    /// body mod's gear parts one body part): whatever can make a look's piece go missing or come back. Worked out from the
    /// library's change list, a full look only when it cannot say.
    /// </summary>
    public int ModsVersion
    {
        get
        {
            if (_library.Version == _libraryVersion)
                return _modsVersion;
            var changed = _mods is null ? null : _library.ChangedSince(_libraryVersion);
            _libraryVersion = _library.Version;
            if (changed is null)
            {
                var mods = _library.Parts.GroupBy(p => p.Mod.Directory, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => SocketsOf(g), StringComparer.OrdinalIgnoreCase);
                if (_mods is null || mods.Count != _mods.Count || mods.Any(kv => !_mods.TryGetValue(kv.Key, out var was) || was != kv.Value))
                    _modsVersion++;
                _mods = mods;
                return _modsVersion;
            }

            var known = _mods!;
            foreach (var mod in changed)
            {
                var parts = _library.PartsOf(mod);
                var now   = parts.Count > 0 ? SocketsOf(parts) : null;
                var was   = known.GetValueOrDefault(mod);
                if (now == was)
                    continue;
                if (now is null)
                    known.Remove(mod);
                else
                    known[mod] = now;
                _modsVersion++;
            }

            return _modsVersion;
        }
    }

    private static string SocketsOf(IEnumerable<LibPart> parts)
        => string.Join(",", parts.Select(p => (int)p.Key.Socket).Distinct().Order());

    public bool Ready
        => _library.Progress.Ready;

    public LookLibraryPart? Part(PartKey key)
        => _library.PartsOf(key.Mod).FirstOrDefault(p => p.Key == key && p.FitsMe) is { } part ? Record(part) : null;

    public IReadOnlyList<LookLibraryPart> PartsOf(string mod)
        => _library.PartsOf(mod).Where(p => p.FitsMe).Select(Record).ToList();

    public IReadOnlyList<LookLibraryPart> PartsOn(string shelf)
        => _session.PartsOnShelf(shelf).Select(Record).ToList();

    public IReadOnlyList<LookLibraryPart> Parts
    {
        get
        {
            var key = (_library.Version, _session.SortStamp, _session.PhotoStamp);
            if (key != _allFor)
            {
                _allFor = key;
                _all    = _library.Parts.Where(p => p.FitsMe).Select(Record).ToList();
            }

            return _all;
        }
    }

    /// <summary>
    /// The names of the files of the part's socket, as the library kept them when it read the mod's definition on its own
    /// thread (a face part also has its face textures). No file is read here.
    /// </summary>
    public IReadOnlyList<string> FilesOf(PartKey key)
    {
        if (_library.Mod(key.Mod)?.Facts?.Files is not { } files)
            return Array.Empty<string>();
        var own = files.GetValueOrDefault(key.Socket) ?? Array.Empty<string>();
        if (key.Socket == Socket.Face && files.TryGetValue(Socket.FaceTextures, out var textures))
            return own.Concat(textures).Distinct().Order(StringComparer.Ordinal).Take(ModFolder.MaxFilesPerSocket).ToList();
        return own;
    }

    public int VersionOf(PartKey key, ulong item)
        => _library.PartsOf(key.Mod).FirstOrDefault(p => p.Key == key) is { } part ? part.VersionOf(item) : -1;

    public string ShelfName(string shelf)
        => _session.NameOfShelf(shelf);

    public string ShelfIcon(string shelf)
        => _session.IconOfShelf(shelf);

    /// <summary> One record per part, made again only when the part or its shelf changed. </summary>
    private LookLibraryPart Record(LibPart part)
    {
        var shelf   = _session.ShelfOfPart(part);
        var picture = _session.PictureOf(part);
        if (_records.TryGetValue(part.Key, out var known) && ReferenceEquals(known.Part, part) && known.Shelf == shelf && known.Record.Picture == picture)
            return known.Record;

        var bodyOrRace = (part.Mod.Facts?.Groups ?? Array.Empty<GroupInfo>()).Where(g => g.IsBody || g.IsRace).Select(g => g.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var record = new LookLibraryPart(part.Key, part.Name, part.Mod.Author, picture, shelf, part.Info,
            part.Info.Groups.Where(g => !bodyOrRace.Contains(g)).ToList());
        _records[part.Key] = (part, shelf, record);
        return record;
    }
}

/// <summary>
/// The looks' file: <c>looks.json</c> in the plugin's own config folder, written in the background by one writer (the
/// newest text wins). The old plugin's settings are only read.
/// </summary>
public sealed class LooksFileStore : ILooksStore, IDisposable
{
    private readonly BackgroundFile _file;
    private readonly string _damaged;
    private readonly string _oldSettings;
    private readonly Action<string>? _log;
    private string? _last;

    /// <param name="folder"> The plugin's config folder (pluginConfigs/ModCloset2). </param>
    /// <param name="oldSettings"> The old Mod Closet's settings file (pluginConfigs/ModCloset.json), read only. </param>
    public LooksFileStore(string folder, string oldSettings, Action<string>? log = null)
    {
        _file        = new BackgroundFile(Path.Combine(folder, "looks.json"), TimeSpan.Zero, log);
        _damaged     = Path.Combine(folder, "looks.damaged.json");
        _oldSettings = oldSettings;
        _log         = log;
    }

    /// <summary> "" only when there is no file; null when it is there but cannot be read now (the looks then ask again later and write nothing). </summary>
    public string? Read()
    {
        if (_last is { } last)
            return last;
        try
        {
            return File.Exists(_file.Path) ? ModFolder.ReadShared(_file.Path) : string.Empty;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"looks.json could not be read now: {ex.Message}");
            return null;
        }
    }

    public void Write(string text)
    {
        _last = text;
        _file.Write(() => text);
    }

    public void SetAside(string text)
    {
        try
        {
            File.WriteAllText(_damaged, text);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"looks.damaged.json could not be written: {ex.Message}");
        }
    }

    /// <summary> "" when the old plugin left no settings file, null when it is there but cannot be read now. </summary>
    public string? OldSettings()
    {
        try
        {
            return File.Exists(_oldSettings) ? ModFolder.ReadShared(_oldSettings) : string.Empty;
        }
        catch
        {
            return null;
        }
    }

    /// <summary> Waits until the last text is on disk (unload). </summary>
    public void Flush()
        => _file.Flush();

    public void Dispose()
        => Flush();
}

/// <summary> A looks command as one step of the session's history; taking it back hands "before" to the engine. </summary>
public sealed class LooksUndo(ClosetSession session, Func<LooksEngine?> engine) : ILooksUndo
{
    public void Record(string description, string before, string after)
        => session.RecordLooksStep(description, () => engine()?.Restore(before));
}
