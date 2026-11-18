using System.IO.Compression;

namespace ModCloset2.Detect;

/// <summary>
/// Read-only access to the files of one mod: a Penumbra mod folder, or a .pmp pack (a zip) read in place without
/// unpacking. Paths are relative to the mod's root, with either slash; lookups ignore case (Penumbra runs on Windows and
/// mod definitions often spell a file differently from the disk or the zip). Not thread-safe: one source per detection.
/// </summary>
public abstract class ModSource : IDisposable
{
    /// <summary> The mod's directory name (for a pack: the file name without ".pmp"). </summary>
    public abstract string Name { get; }

    /// <summary> Every file of the mod, relative, with forward slashes, as stored. </summary>
    public abstract IReadOnlyList<string> Files { get; }

    /// <summary> Size of a file in bytes, or -1 when it does not exist. </summary>
    public abstract long Length(string relative);

    /// <summary>
    /// The bytes of a file, or null when it is missing, larger than <paramref name="maxBytes"/> or cannot be read.
    /// Never throws for a missing or damaged file.
    /// </summary>
    public abstract byte[]? Read(string relative, long maxBytes);

    /// <summary> Bytes handed out by <see cref="Read"/> so far (the per-mod reading budget is checked against it). </summary>
    public long BytesRead { get; protected set; }

    public virtual void Dispose() { }

    /// <summary>
    /// How every file of someone else is opened: Penumbra (or the user) may write, replace or delete the mod while we read
    /// it, and must never get a sharing violation because of us. A file that changes or disappears under us reads as
    /// missing, which the reader lists as a problem.
    /// </summary>
    public const FileShare Sharing = FileShare.ReadWrite | FileShare.Delete;

    /// <summary> Opens a mod folder, or a .pmp file. Null when neither exists. </summary>
    public static ModSource? Open(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return new FolderSource(path);
            if (File.Exists(path))
                return new ZipSource(path);
        }
        catch (Exception)
        {
            // A pack that is not a zip, a folder we may not list: the caller reports it as a problem.
        }

        return null;
    }

    /// <summary> Forward slashes, no leading slash, no "./". The key used for case-insensitive lookups. </summary>
    public static string Normalize(string relative)
    {
        var p = relative.Replace('\\', '/').Trim();
        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        return p.TrimStart('/');
    }

    /// <summary> True for a relative path that stays inside the mod (no "..", not rooted). </summary>
    public static bool IsSafe(string relative)
    {
        var p = Normalize(relative);
        if (p.Length == 0 || Path.IsPathRooted(p) || p.Contains(':'))
            return false;
        foreach (var part in p.Split('/'))
            if (part == "..")
                return false;
        return true;
    }
}

/// <summary> A mod folder on disk. </summary>
public sealed class FolderSource : ModSource
{
    private readonly string _root;
    private List<string>? _files;
    private Dictionary<string, string>? _byKey;

    public FolderSource(string root)
    {
        _root = Path.GetFullPath(root);
        Name  = Path.GetFileName(_root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    public override string Name { get; }

    public override IReadOnlyList<string> Files
    {
        get
        {
            if (_files is null)
                Index();
            return _files!;
        }
    }

    private void Index()
    {
        _files = new List<string>();
        _byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var full in Directory.EnumerateFiles(_root, "*", options))
            {
                var rel = Normalize(Path.GetRelativePath(_root, full));
                _files.Add(rel);
                _byKey.TryAdd(rel, full);
            }
        }
        catch (Exception)
        {
            // A folder that cannot be listed reads as empty; missing files are reported by the reader.
        }
    }

    private string? Find(string relative)
    {
        if (!IsSafe(relative))
            return null;

        var rel = Normalize(relative);
        var direct = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct))
            return direct;

        // Case differs from the disk (common: definitions written on Windows).
        if (_byKey is null)
            Index();
        return _byKey!.GetValueOrDefault(rel);
    }

    /// <summary> The full path of a file of the mod as it is on disk (its case may differ from the spelling asked), or null. </summary>
    public string? FullPath(string relative)
        => Find(relative);

    /// <summary> The mod's folder, full path. </summary>
    public string Root
        => _root;

    public override long Length(string relative)
    {
        try
        {
            return Find(relative) is { } f ? new FileInfo(f).Length : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    public override byte[]? Read(string relative, long maxBytes)
    {
        try
        {
            if (Find(relative) is not { } f)
                return null;

            // Not File.ReadAllBytes: it shares only Read, so Penumbra could not save or delete the mod meanwhile.
            using var stream = new FileStream(f, FileMode.Open, FileAccess.Read, Sharing, 1, FileOptions.SequentialScan);
            var length = stream.Length;
            if (length > maxBytes)
                return null;

            var bytes = new byte[length];
            var read  = 0;
            while (read < bytes.Length)
            {
                var n = stream.Read(bytes, read, bytes.Length - read);
                if (n == 0)
                    return null; // cut short while we read (being rewritten): missing for now
                read += n;
            }

            BytesRead += bytes.Length;
            return bytes;
        }
        catch (Exception)
        {
            // gone, locked, replaced: the caller lists it as a problem
            return null;
        }
    }
}

/// <summary> A .pmp pack (zip), read in place. </summary>
public sealed class ZipSource : ModSource
{
    private readonly FileStream _stream;
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _files = new();

    public ZipSource(string file)
    {
        Name    = Path.GetFileNameWithoutExtension(file);
        _stream = new FileStream(file, FileMode.Open, FileAccess.Read, Sharing);
        try
        {
            _zip = new ZipArchive(_stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch
        {
            _stream.Dispose();
            throw;
        }

        foreach (var e in _zip.Entries)
        {
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'))
                continue;
            var rel = Normalize(e.FullName);
            _files.Add(rel);
            _byKey.TryAdd(rel, e);
        }
    }

    public override string Name { get; }

    public override IReadOnlyList<string> Files
        => _files;

    public override long Length(string relative)
        => IsSafe(relative) && _byKey.TryGetValue(Normalize(relative), out var e) ? e.Length : -1;

    public override byte[]? Read(string relative, long maxBytes)
    {
        try
        {
            if (!IsSafe(relative) || !_byKey.TryGetValue(Normalize(relative), out var e) || e.Length > maxBytes)
                return null;

            using var s = e.Open();
            var bytes = new byte[e.Length];
            s.ReadExactly(bytes);
            BytesRead += bytes.Length;
            return bytes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public override void Dispose()
    {
        _zip.Dispose();
        _stream.Dispose();
    }
}
