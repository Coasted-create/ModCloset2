namespace ModCloset2.Colours;

// Pure code (no game, no Dalamud): linked into tests/ColourTests.

/// <summary> Reads a mod's file from disk without getting in Penumbra's way. </summary>
public static class DiskFiles
{
    /// <summary>
    /// Others may read, write and delete the file while it is read (as the detection's ModSource.Sharing): Penumbra must
    /// be able to change or remove a mod whose file the colours are reading.
    /// </summary>
    public const FileShare Sharing = FileShare.ReadWrite | FileShare.Delete;

    public static FileStream Open(string path)
        => new(path, FileMode.Open, FileAccess.Read, Sharing, 1, FileOptions.SequentialScan);

    /// <summary> The bytes, or null when the file is missing, larger than <paramref name="maxBytes"/> or cannot be read. Never throws. </summary>
    public static byte[]? Read(string path, long maxBytes)
    {
        try
        {
            using var stream = Open(path);
            var length = stream.Length;
            if (length > maxBytes)
                return null;
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch
        {
            // missing, changed under us or no access: not read this time
            return null;
        }
    }
}
