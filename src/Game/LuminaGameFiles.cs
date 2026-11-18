using Dalamud.Plugin.Services;
using Lumina.Data;
using Lumina.Data.Attributes;
using Lumina.Data.Structs;

namespace ModCloset2.Game;

/// <summary>
/// The game's own files through Dalamud's data manager, i.e. Lumina reading the game's sqpack archives straight from the
/// disk (Dalamud 14 with Lumina 7.7.0; what was read is in docs/reports/foundation.md, "Photos of everything"):
/// <list type="bullet">
/// <item>Unmodded: Lumina opens the sqpack files itself, so Penumbra's redirections (made in the game's own file loading)
/// never reach it. Dalamud's only substitution is in its texture provider, which this does not use.</item>
/// <item>Any thread: Dalamud does the same itself (<c>GetFileAsync</c> is <c>Task.Run</c> around the same call). The index
/// tables are made once at start and only read after; every read rents its own stream from a thread-safe pool.</item>
/// <item>The layout a mod ships: a model comes out with the 0x44-byte .mdl header rebuilt by Lumina in front of the data,
/// a texture with its 80-byte .tex header and the levels after it, anything else as it is: what Lumina's own MdlFile and
/// TexFile parse from offset 0, and what the readers in src/Photo read. Nothing is converted here.</item>
/// <item>Uncached: <see cref="PhotoGameFile"/> tells Lumina not to keep the file in its shared cache, so the array is new,
/// the caller's alone, and nothing of a long photo pass stays in Lumina's memory.</item>
/// </list>
/// May throw (a broken archive, a disk error); <see cref="GameFiles"/> catches and counts.
/// </summary>
public sealed class LuminaGameFiles(IDataManager data) : IGameFileSource
{
    public long? SizeOf(string gamePath)
        => data.GameData.GetFileMetadata(gamePath) is { } info && info.Type != FileType.Empty ? info.RawFileSize : null;

    public byte[]? Read(string gamePath)
    {
        try
        {
            return data.GetFile<PhotoGameFile>(gamePath)?.Data;
        }
        catch (FileNotFoundException)
        {
            // Lumina's word for an entry the index names but the archive marks empty
            return null;
        }
    }

    public bool Has(string gamePath)
        => data.FileExists(gamePath);

    /// <summary> A plain file Lumina reads fresh every time, without its shared cache, and does not parse. </summary>
    [FileOptions(FileOptionsAttribute.FileCacheBehaviour.Never)]
    public sealed class PhotoGameFile : FileResource
    {
    }
}
