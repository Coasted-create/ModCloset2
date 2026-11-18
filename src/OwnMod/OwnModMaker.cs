using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModCloset2.Core;
using ModCloset2.Detect;

namespace ModCloset2.OwnMod;

/// <summary> A failure of <see cref="OwnModMaker.Write"/>; the message is one sentence for the user. </summary>
public sealed class OwnModException : Exception
{
    public OwnModException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// "Make it its own mod" (brief 10): copies a card's thing out of its mod into one new mod folder. Pure file work: no
/// game, no Penumbra. It never changes, moves or deletes anything that was there before; files of the old mod are only
/// ever opened for reading, with sharing. The new folder is written under a temporary name and renamed when whole.
/// </summary>
public sealed class OwnModMaker : IOwnModMaker
{
    /// <summary> The tag every copy carries. </summary>
    public const string Tag = "Mod Closet";

    /// <summary> Start of the temporary folder's name while a copy is written. </summary>
    public const string TempPrefix = ".modcloset-";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary> For tests: called after each copied file, before the next; an exception there stops the write like a failing disk. </summary>
    public Action<int>? AfterFile { get; set; }

    /// <summary> For tests: called with the temporary folder when everything but meta.json is written. </summary>
    public Action<string>? BeforeMeta { get; set; }

    public OwnModContent Plan(OwnModAsk ask)
    {
        try
        {
            return new OwnModPlanner(ask).Plan();
        }
        catch (Exception)
        {
            // Plan never throws: whatever went wrong while reading, the user gets one sentence.
            return new OwnModDetails { Possible = false, Problem = Loc.T("The mod's files could not be read.") };
        }
    }

    public string Write(OwnModContent content, OwnModWrite how, Action<int, int>? progress, CancellationToken stop)
    {
        if (content is not OwnModDetails d || !d.Possible)
            throw new OwnModException(Loc.T("There is nothing to copy. Nothing was changed."));
        if (string.IsNullOrWhiteSpace(how.ModRoot) || !Directory.Exists(how.ModRoot))
            throw new OwnModException(Loc.T("Penumbra's mod folder was not found. Nothing was changed."));

        stop.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(how.ModRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Unchanged(d);

        var name   = how.Name.Trim().Length > 0 ? how.Name.Trim() : d.ModName;
        var files  = SettingsFiles(d).Select(f => f.Name).Concat(d.Copy.Select(c => c.Relative)).ToList();
        var room   = Math.Max(d.LongestPath, Names.MaxFullPath) - root.Length - 2 - files.Max(f => f.Length);
        if (room < 1)
            throw new OwnModException(Loc.T("The mod's file paths are too long for a copy. Nothing was changed."));
        var folder = Names.FreeFolder(root, name, room);

        RemoveLeftovers(root);
        var temp = Path.Combine(root, TempPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            var done = 0;
            progress?.Invoke(0, d.Copy.Count);
            foreach (var file in d.Copy)
            {
                stop.ThrowIfCancellationRequested();
                CopyOne(d, file, temp, stop);
                ++done;
                progress?.Invoke(done, d.Copy.Count);
                AfterFile?.Invoke(done);
            }

            stop.ThrowIfCancellationRequested();
            // meta.json last: a temporary folder left by a crash holds no meta.json, so Penumbra never takes it for a mod.
            var meta = NewMeta(d, name, how.Piece);
            foreach (var (fileName, node) in SettingsFiles(d, meta).Where(f => f.Name != "meta.json"))
                Save(Path.Combine(temp, fileName), node);
            Save(Path.Combine(temp, "mod-closet.json"), Note(d, how));
            BeforeMeta?.Invoke(temp);
            Save(Path.Combine(temp, "meta.json"), meta);

            stop.ThrowIfCancellationRequested();
            var target = Path.Combine(root, folder);
            if (Directory.Exists(target) || File.Exists(target))
            {
                folder = Names.FreeFolder(root, name, room);
                target = Path.Combine(root, folder);
            }

            Directory.Move(temp, target);
            return folder;
        }
        catch (OperationCanceledException)
        {
            RemoveTemp(root, temp);
            throw new OperationCanceledException(Loc.T("Stopped. Nothing was changed."), stop);
        }
        catch (OwnModException)
        {
            RemoveTemp(root, temp);
            throw;
        }
        catch (Exception ex)
        {
            RemoveTemp(root, temp);
            throw new OwnModException(Sentence(ex), ex);
        }
    }

    // ------------------------------------------------------------------ files

    /// <summary> Stops when the old mod changed since the plan: a copy of something else would be half true. </summary>
    private static void Unchanged(OwnModDetails d)
    {
        foreach (var f in d.Copy.Concat(d.Settings))
        {
            var full = Path.Combine(d.ModFolder, f.Relative.Replace('/', Path.DirectorySeparatorChar));
            FileInfo info;
            try
            {
                info = new FileInfo(full);
            }
            catch (Exception)
            {
                throw Changed();
            }

            if (!info.Exists || info.Length != f.Length || info.LastWriteTimeUtc != f.WrittenUtc)
                throw Changed();
        }
    }

    private static OwnModException Changed()
        => new(Loc.T("The mod changed since it was looked at. Look again, then make the copy. Nothing was changed."));

    private static void CopyOne(OwnModDetails d, CopiedFile file, string temp, CancellationToken stop)
    {
        var source = Path.Combine(d.ModFolder, file.Relative.Replace('/', Path.DirectorySeparatorChar));
        var target = Path.GetFullPath(Path.Combine(temp, file.Relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!ModSource.IsSafe(file.Relative) || !target.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new OwnModException(Loc.T("The mod names a file outside its own folder. Nothing was changed."));

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // The old file: read only, and shared, so a running game or Penumbra is never stopped by us.
        using var input  = new FileStream(source, FileMode.Open, FileAccess.Read, ModSource.Sharing, 1 << 16, FileOptions.SequentialScan);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        var buffer = new byte[1 << 16];
        long total = 0;
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            stop.ThrowIfCancellationRequested();
            output.Write(buffer, 0, n);
            total += n;
        }

        if (total != file.Length)
            throw Changed();
    }

    /// <summary> How long a temporary folder must have been untouched before a later write removes it as left over. </summary>
    public static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Removes temporary folders an earlier write left behind (a crash, a rename that was refused): only folders directly
    /// in the mod root named exactly ".modcloset-" and 32 hex digits, in which nothing was written for a minute. Never
    /// anything else. Best effort: a folder that cannot be removed now is tried again next time.
    /// </summary>
    public static void RemoveLeftovers(string root)
    {
        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(root, TempPrefix + "*").ToList();
        }
        catch (Exception)
        {
            return;
        }

        var limit = DateTime.UtcNow - LeftoverAge;
        foreach (var folder in folders)
        {
            if (!IsTempName(Path.GetFileName(folder)))
                continue;
            try
            {
                var info = new DirectoryInfo(folder);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                var newest = info.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                    .Select(i => i.LastWriteTimeUtc).Append(info.LastWriteTimeUtc).Max();
                if (newest < limit)
                    info.Delete(true);
            }
            catch (Exception)
            {
                // in use or not ours to touch now: next time
            }
        }
    }

    /// <summary> ".modcloset-" and exactly 32 hex digits (lower case, as written). </summary>
    public static bool IsTempName(string name)
        => name.Length == TempPrefix.Length + 32 && name.StartsWith(TempPrefix, StringComparison.Ordinal)
         && name.AsSpan(TempPrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary> Only our own temporary folder is ever removed. </summary>
    private static void RemoveTemp(string root, string temp)
    {
        if (!Path.GetFileName(temp).StartsWith(TempPrefix, StringComparison.Ordinal) || !string.Equals(Path.GetDirectoryName(temp), root, StringComparison.Ordinal))
            return;
        for (var attempt = 0; attempt < 3; ++attempt)
        {
            try
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, true);
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static string Sentence(Exception ex)
        => ex switch
        {
            UnauthorizedAccessException => Loc.T("Mod Closet may not write in Penumbra's mod folder. Nothing was changed."),
            PathTooLongException        => Loc.T("The mod's file paths are too long for a copy. Nothing was changed."),
            IOException io when DiskFull(io) => Loc.T("The disk is full. Nothing was changed."),
            _                           => Loc.T("The new mod could not be written. Nothing was changed."),
        };

    private static bool DiskFull(IOException io)
        => (io.HResult & 0xFFFF) is 0x70 or 0x27 || io.HResult == 28 || io.Message.Contains("space", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ settings

    private static IEnumerable<(string Name, JsonNode Node)> SettingsFiles(OwnModDetails d, JsonObject? meta = null)
    {
        yield return ("meta.json", meta ?? d.Meta);
        if (d.Layout == ModLayout.Single)
            yield break;
        if (d.Default is not null)
            yield return ("default_mod.json", d.Default);
        for (var i = 0; i < d.GroupNodes.Count; ++i)
            yield return (Names.GroupFile(i, OwnModPlanner.Str(d.GroupNodes[i]["Name"])), d.GroupNodes[i]);
    }

    /// <summary>
    /// The copy's meta.json: the old one with the new name, the one line that says what this is at the top of the
    /// description, the tag "Mod Closet", and a new identity where the mod has one. Author, website, version and every
    /// other field stay as they are.
    /// </summary>
    private static JsonObject NewMeta(OwnModDetails d, string name, string piece)
    {
        var meta = (JsonObject)d.Meta.DeepClone();
        meta["Name"] = name;

        var author = d.Author.Length > 0 ? d.Author : Loc.T("its author");
        var line = Loc.F("{0} from “{1}” by {2}, copied out with Mod Closet for my own use. Everything in it is the author's work.",
            piece.Length > 0 ? piece : name, d.ModName, author);
        var old = OwnModPlanner.Str(meta["Description"]);
        meta["Description"] = old.Length > 0 ? line + "\n\n" + old : line;

        var tags = meta["ModTags"] as JsonArray;
        if (tags is null)
            meta["ModTags"] = tags = new JsonArray();
        if (!tags.Any(t => string.Equals(OwnModPlanner.Str(t), Tag, StringComparison.OrdinalIgnoreCase)))
            tags.Add(Tag);

        // Two mods with one identity would be one mod to Penumbra.
        if (meta["Identifier"] is JsonValue)
            meta["Identifier"] = Guid.NewGuid().ToString();
        return meta;
    }

    private static JsonObject Note(OwnModDetails d, OwnModWrite how)
        => new()
        {
            ["About"]      = "Made by Mod Closet with \"Make it its own mod\": a copy of one piece of another mod, for the user's own use.",
            ["MadeBy"]     = how.MadeBy,
            ["Made"]       = DateTime.UtcNow.ToString("o"),
            ["FromFolder"] = d.FolderName,
            ["FromName"]   = d.ModName,
            ["Author"]     = d.Author,
            ["Slots"]      = new JsonArray(d.Sockets.Select(s => (JsonNode)s.ToString()).ToArray()),
            ["Files"]      = d.Copy.Count,
        };

    private static void Save(string path, JsonNode node)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = Json.Encoder });
        node.WriteTo(writer, Json);
    }
}
