using Newtonsoft.Json.Linq;

namespace ModCloset2.Library;

/// <summary>
/// The pictures the old Mod Closet took, read once from its settings file (pluginConfigs/ModCloset.json, read-only) and
/// its close-up folder (pluginConfigs/ModCloset/closeups/&lt;mod&gt;/fullbody.*). The new closet never writes there.
/// Also carries the old settings' body name ("Rue"). No Dalamud types.
/// </summary>
public sealed class OldPictures
{
    public static readonly OldPictures None = new(new Dictionary<string, string>(), string.Empty, string.Empty);

    /// <summary> Picture file → "size|last write ticks" when the old plugin wrote it. </summary>
    private readonly IReadOnlyDictionary<string, string> _created;
    private readonly string _closeUps;

    /// <summary> The body the user picked in the old plugin ("" when none). </summary>
    public string MyBody { get; }

    /// <summary> The old plugin's pictures by the folder they are in (it wrote preview.* at the top of a mod folder). </summary>
    private readonly Dictionary<string, List<string>> _byFolder = new(StringComparer.OrdinalIgnoreCase);

    public OldPictures(IReadOnlyDictionary<string, string> created, string closeUpsRoot, string myBody)
    {
        _created  = created;
        _closeUps = closeUpsRoot;
        MyBody    = myBody;
        foreach (var file in created.Keys)
        {
            try
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(file))?.TrimEnd('\\', '/') ?? string.Empty;
                if (!_byFolder.TryGetValue(folder, out var list))
                    _byFolder[folder] = list = new List<string>();
                list.Add(file);
            }
            catch
            {
                // a path that makes no sense here (another computer's): ignore it
            }
        }
    }

    /// <summary> Reads the old settings. Never throws: anything unreadable means no old pictures. </summary>
    public static OldPictures Read(string pluginConfigs)
    {
        var created = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body    = string.Empty;
        try
        {
            var file = Path.Combine(pluginConfigs, "ModCloset.json");
            if (File.Exists(file) && JObject.Parse(ModFolder.ReadShared(file)) is { } json)
            {
                if (json["CreatedFiles"] is JObject files)
                    foreach (var prop in files.Properties())
                        if (prop.Name != "$type" && prop.Value.Type == JTokenType.String)
                            created[prop.Name] = prop.Value.ToString();
                if (json["MyBody"] is { Type: JTokenType.String } b)
                    body = b.ToString();
            }
        }
        catch
        {
            // the old settings are only a source of pictures
        }

        return new OldPictures(created, Path.Combine(pluginConfigs, "ModCloset", "closeups"), body);
    }

    /// <summary> "size|ticks" of a file now, "" when it does not exist (the old Configuration.Stamp). </summary>
    public static string Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}|{info.LastWriteTimeUtc.Ticks}" : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// True when the file is a picture the old plugin wrote and nobody changed since (same size and time). A picture the
    /// user replaced is theirs, so it counts as the mod's own.
    /// </summary>
    public bool IsOldPicture(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return (_created.TryGetValue(path, out var stamp) || _created.TryGetValue(full, out stamp)) && stamp.Length > 0 && stamp == Stamp(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary> The picture the old plugin took of a mod (in its folder, then its full-body close-up), or null. </summary>
    public string? Find(string modFolder, string modDirectory)
    {
        try
        {
            if (modFolder.Length > 0 && _byFolder.TryGetValue(Path.GetFullPath(modFolder).TrimEnd('\\', '/'), out var files))
                foreach (var file in files)
                    if (ModFolder.IsImage(file) && IsOldPicture(file))
                        return file;

            if (_closeUps.Length > 0)
            {
                var dir = Path.Combine(_closeUps, Safe(modDirectory));
                if (Directory.Exists(dir))
                    foreach (var file in Directory.EnumerateFiles(dir))
                        if (Path.GetFileNameWithoutExtension(file).Equals("fullbody", StringComparison.OrdinalIgnoreCase) && ModFolder.IsImage(file))
                            return file;
            }
        }
        catch
        {
            // no old picture then
        }

        return null;
    }

    /// <summary> The picture of a mod: its own, else one the old plugin took, else none (SPEC "Pictures"). </summary>
    public string? PictureFor(string modFolder, string modDirectory)
        => (modFolder.Length > 0 ? ModFolder.FindImage(modFolder, IsOldPicture) : null) ?? Find(modFolder, modDirectory);

    /// <summary> The old CloseUps folder name of a mod directory. </summary>
    private static string Safe(string directory)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s   = new string(directory.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s;
    }
}
