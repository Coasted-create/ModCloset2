using System.Text;
using System.Text.Json;
using ModCloset2.Core;

namespace ModCloset2.Closet;

/// <summary>
/// The closet's photos on disk (docs/briefs/12-photos-in-the-closet.md): <c>photos/</c> beside the settings, one file
/// per part, and a small index (<c>photos/index.json</c>: part → stamp and file, or "nothing to show").
///
/// Only the photo worker uses it (one thread): files are written and deleted there, never on the frame.
///
/// File names: <c>&lt;two hex&gt;/&lt;16 hex of the part&gt;-&lt;8 hex of the stamp&gt;.photo.jpg</c>. The stamp comes from what
/// decides the picture (the mod's fingerprint, the maker's version, the width), so a new take has a new name and a face
/// that caches pictures by path never shows an old one under a new name.
///
/// Deleting is strict. A file is deleted only when all of these hold: its full path lies inside <c>photos/</c> (after
/// resolving <c>..</c>); its name carries <see cref="Pictures.PhotoMark"/>; neither it nor any folder between it and
/// <c>photos/</c> (nor <c>photos/</c> itself) is a link; and it is a file, not a folder. No folder is ever deleted.
/// Files are deleted (1) when a new photo of the same part was written (the part's older file), (2) when a part
/// is gone from the library once the library has read everything (its file), (3) then also files with the mark that no
/// index entry names, and leftover temporary files from an unload in the middle of a write.
/// </summary>
public sealed class PhotoStore
{
    /// <param name="Problem">
    /// A take that failed: the plugin version that tried (it is tried once more under another version). "" otherwise.
    /// </param>
    /// <param name="Why"> A take that failed: the maker's sentence (fixed words, no names), or "other". </param>
    public sealed record Entry(string Stamp, string File, string Problem = "", string Why = "")
    {
        /// <summary> The maker had nothing to show for it (an effect, a few pixels): not tried again until the stamp changes. </summary>
        public bool Nothing
            => File.Length == 0 && Problem.Length == 0;
    }

    private const string IndexName = "index.json";

    /// <summary> A stamp no part has: the entry's photo is shown, and taken again. </summary>
    private const string Stale = "-";
    private const string TempEnd = ".tmp";

    private readonly string _folder;
    private readonly string _folderFull;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, Entry> _index = new(StringComparer.Ordinal);
    private bool _dirty;

    private readonly string _version;

    /// <param name="version"> The plugin's version: failed takes of another version are tried again. </param>
    public PhotoStore(string folder, string version, Action<string>? log = null)
    {
        _version    = version;
        _folder     = folder;
        _folderFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        _log        = log;
    }

    public string Folder
        => _folder;

    /// <summary> For the tests: a fault to raise where a photo is written, or where the folder is looked through. </summary>
    internal Func<Exception?>? WriteFault { get; set; }
    internal Func<Exception?>? PruneFault { get; set; }

    // each of these is said once a session, never once per photo or per rest
    private bool _saveWarned;
    private bool _pruneWarned;
    private bool _deleteWarned;

    private void Warn(ref bool said, string text)
    {
        if (said)
            return;
        said = true;
        _log?.Invoke(text);
    }

    /// <summary> How many files were deleted (the tests read it). </summary>
    public int Deleted { get; private set; }

    public IReadOnlyDictionary<string, Entry> Index
        => _index;

    /// <summary> The full path of an entry's file. </summary>
    public string PathOf(Entry entry)
        => Path.Combine(_folder, entry.File);

    // ---- the index ----

    /// <summary> Reads the index (a missing or damaged one is an empty one: the photos are taken again). </summary>
    public void Load()
    {
        _index.Clear();
        try
        {
            var file = Path.Combine(_folder, IndexName);
            if (!File.Exists(file))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Object)
                return;
            foreach (var p in parts.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object)
                    continue;
                var stamp   = p.Value.TryGetProperty("stamp", out var s) ? s.GetString() ?? string.Empty : string.Empty;
                var name    = p.Value.TryGetProperty("file", out var f) ? f.GetString() ?? string.Empty : string.Empty;
                var problem = p.Value.TryGetProperty("problem", out var q) ? q.GetString() ?? string.Empty : string.Empty;
                var why     = p.Value.TryGetProperty("why", out var y) ? y.GetString() ?? string.Empty : string.Empty;
                // a file the index names must be one of ours, inside the folder, and still there (a photo deleted by hand
                // is taken again); a failed take of another plugin version is tried once more
                if (stamp.Length == 0 || name.Length > 0 && (!Ours(Path.Combine(_folder, name)) || !File.Exists(Path.Combine(_folder, name))))
                {
                    _dirty = true;
                    continue;
                }

                if (problem.Length > 0 && problem != _version)
                {
                    // tried once more: a photo from before stays shown meanwhile (under a stamp that matches nothing)
                    _dirty = true;
                    if (name.Length > 0)
                        _index[p.Name] = new Entry(Stale, name);
                    continue;
                }

                _index[p.Name] = new Entry(stamp, name, problem, problem.Length > 0 ? SafeWhy(why) : string.Empty);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"photos: the index could not be read, the photos are taken again ({ex.GetType().Name})");
            _index.Clear();
        }
    }

    /// <summary> Writes the index when it changed: to a temporary file, then over the old one. </summary>
    public void Save()
    {
        if (!_dirty)
            return;
        try
        {
            Directory.CreateDirectory(_folder);
            var json = new StringBuilder();
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("version", 1);
                    writer.WriteStartObject("parts");
                    foreach (var (key, entry) in _index.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        writer.WriteStartObject(key);
                        writer.WriteString("stamp", entry.Stamp);
                        writer.WriteString("file", entry.File);
                        if (entry.Problem.Length > 0)
                        {
                            writer.WriteString("problem", entry.Problem);
                            writer.WriteString("why", entry.Why);
                        }
                        writer.WriteEndObject();
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                json.Append(Encoding.UTF8.GetString(stream.ToArray()));
            }

            var file = Path.Combine(_folder, IndexName);
            var temp = file + TempEnd;
            File.WriteAllText(temp, json.ToString());
            File.Move(temp, file, overwrite: true);
            _dirty = false;
        }
        catch (Exception ex)
        {
            Warn(ref _saveWarned, $"photos: the index could not be written ({ex.GetType().Name})");
        }
    }

    // ---- photos ----

    /// <summary>
    /// Keeps what came of a take: the picture (written to a temporary name in the same folder, then renamed), or "nothing
    /// to show". The part's older file goes once the new one is there. Returns the entry.
    /// </summary>
    /// <summary>
    /// The waits before a write or a move is tried again: a virus scanner often holds a freshly written file for a moment,
    /// and that must not count as a folder that cannot be written.
    /// </summary>
    private static readonly int[] Waits = { 50, 200, 500 };

    /// <summary> For the tests: a fault to raise where a written photo is moved into place. </summary>
    internal Func<Exception?>? MoveFault { get; set; }

    /// <summary> Does it; an IO or access failure is tried again after each of <see cref="Waits"/>, the stop honoured in between. </summary>
    private static void Patiently(Action act, CancellationToken stop)
    {
        for (var attempt = 0;; ++attempt)
        {
            try
            {
                act();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < Waits.Length)
            {
                if (stop.WaitHandle.WaitOne(Waits[attempt]))
                    throw new OperationCanceledException(stop);
            }
        }
    }

    public Entry Put(string part, string stamp, byte[]? picture, string extension, CancellationToken stop = default)
    {
        var name = string.Empty;
        if (picture is { Length: > 0 })
        {
            if (WriteFault?.Invoke() is { } fault)
                throw fault;
            var ext = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            var hash = Hash(part);
            name = Path.Combine(hash[..2], $"{hash}-{stamp}{Pictures.PhotoMark.TrimEnd('.')}{ext}");
            var full = Path.Combine(_folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var temp = full + TempEnd;
            if (File.Exists(temp))
                Delete(temp);
            try
            {
                WriteNew(temp, picture);
            }
            catch (Exception)
            {
                // a stale temporary file a scanner will not let go: once more under another temporary name (it carries
                // the mark, so a later prune can sweep it), patiently, then give up (the caller counts it)
                temp = $"{full}.{Environment.TickCount64 & 0xFFFFFF:x6}{TempEnd}";
                var second = temp;
                Patiently(() => WriteNew(second, picture), stop);
            }

            try
            {
                var written = temp;
                Patiently(() =>
                {
                    if (MoveFault?.Invoke() is { } fault)
                        throw fault;
                    File.Move(written, full, overwrite: true);
                }, stop);
            }
            catch
            {
                // not moved into place after the waits (or stopped): the temporary file goes, the caller counts it
                Delete(temp);
                throw;
            }
        }

        var entry = new Entry(stamp, name);
        if (_index.TryGetValue(part, out var old) && old.File.Length > 0 && !old.File.Equals(name, StringComparison.OrdinalIgnoreCase))
            Delete(Path.Combine(_folder, old.File));
        _index[part] = entry;
        _dirty       = true;
        return entry;
    }

    /// <summary>
    /// A take that failed: remembered under this plugin version with the maker's sentence (the part's older file, if any,
    /// stays as it is).
    /// </summary>
    public Entry Failed(string part, string stamp, string why)
    {
        // a photo from before stays shown (its file is kept), with the try noted so it is not tried again under this version
        var entry = new Entry(stamp, _index.TryGetValue(part, out var old) ? old.File : string.Empty, _version, SafeWhy(why));
        _index[part] = entry;
        _dirty       = true;
        return entry;
    }

    private static void WriteNew(string path, byte[] picture)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(picture);
    }

    /// <summary>
    /// The library has read everything: the parts not in <paramref name="live"/> go (their files too), and so do files
    /// with the photo mark that no entry names, and leftover temporary files.
    /// </summary>
    public void Prune(IReadOnlySet<string> live)
    {
        foreach (var (key, entry) in _index.ToList())
        {
            if (live.Contains(key))
                continue;
            if (entry.File.Length > 0)
                Delete(Path.Combine(_folder, entry.File));
            _index.Remove(key);
            _dirty = true;
        }

        // looking through the folder can fail (a folder that may not be read, one that vanishes meanwhile): the leftovers
        // then simply wait for the next time
        try
        {
            if (PruneFault?.Invoke() is { } fault)
                throw fault;
            if (!Directory.Exists(_folder) || IsLink(new DirectoryInfo(_folder)))
                return;
            var named = new HashSet<string>(_index.Values.Where(e => e.File.Length > 0).Select(e => Path.GetFullPath(Path.Combine(_folder, e.File))),
                StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true, MaxRecursionDepth = 2 };
            foreach (var file in Directory.EnumerateFiles(_folder, "*", options).ToList())
                if (Pictures.IsPhoto(file) && !named.Contains(Path.GetFullPath(file)))
                    Delete(file);
        }
        catch (Exception ex)
        {
            Warn(ref _pruneWarned, $"photos: the photo folder could not be looked through ({ex.GetType().Name})");
        }
    }

    /// <summary> Deletes one of our files, by the rules above; anything else is left alone. </summary>
    private void Delete(string path)
    {
        try
        {
            if (!Ours(path))
                return;
            var info = new FileInfo(Path.GetFullPath(path));
            if (!info.Exists || IsLink(info))
                return;
            for (var dir = info.Directory; dir is not null; dir = dir.Parent)
            {
                if (IsLink(dir))
                    return;
                if (Path.TrimEndingDirectorySeparator(dir.FullName) + Path.DirectorySeparatorChar == _folderFull)
                    break;
            }

            info.Delete();
            Deleted++;
        }
        catch (Exception ex)
        {
            Warn(ref _deleteWarned, $"photos: an old photo could not be removed ({ex.GetType().Name})");
        }
    }

    /// <summary> Inside the photos folder (after resolving "..") and named as a photo. </summary>
    private bool Ours(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(_folderFull, StringComparison.OrdinalIgnoreCase) && Pictures.IsPhoto(Path.GetFileName(full));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLink(FileSystemInfo info)
        => info.Exists && ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null);

    /// <summary> What a problem's sentence may be when kept or logged: longer than this, or with a path separator, it is "other". </summary>
    public const int WhyLength = 120;

    /// <summary>
    /// A problem's sentence as it may be kept or logged: the maker's fixed words; "other" when it is empty, longer than
    /// <see cref="WhyLength"/> or holds a path separator (should the maker ever slip a name or a path into it).
    /// </summary>
    public static string SafeWhy(string? why)
    {
        var text = why?.Trim() ?? string.Empty;
        return text.Length == 0 || text.Length > WhyLength || text.IndexOfAny(new[] { '/', '\\' }) >= 0 ? "other" : text;
    }

    /// <summary> 16 hex of FNV-1a over the text: the part's file name. </summary>
    public static string Hash(string text)
    {
        var h = 14695981039346656037UL;
        foreach (var c in text)
        {
            h ^= c;
            h *= 1099511628211UL;
        }

        return h.ToString("x16");
    }
}
