using Dalamud.Plugin.Services;

namespace ModCloset2.Colours.Game;

/// <summary>
/// Reads the files the game loaded for the player: a file on disk (a mod's file, as Penumbra hands it to the game) or a
/// game path from the game's own data. Read only, thread-safe, never throws (old plugin: CharacterExport.ReadFile).
/// </summary>
public sealed class GameFiles(IDataManager data) : IFileSource
{
    /// <summary> Larger files are not read (no gear file comes near this). </summary>
    private const long MaxBytes = 256L << 20;

    public byte[]? Read(string path)
    {
        try
        {
            if (Path.IsPathRooted(path))
                return DiskFiles.Read(path, MaxBytes); // shared for writing and deleting: Penumbra may change the mod meanwhile
            return data.GetFile(path)?.Data;
        }
        catch
        {
            return null;
        }
    }
}
