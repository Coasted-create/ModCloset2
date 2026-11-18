using ModCloset2.Closet;

namespace ModCloset2.Game;

/// <summary> Penumbra's side of "Make it its own mod", through <see cref="Ipc"/> (framework thread). </summary>
public sealed class PenumbraMods(Ipc ipc) : IPenumbraMods
{
    public string ModRoot()
        => ipc.ModDirectory();

    public bool AddMod(string directory)
        => ipc.AddMod(directory) == Ipc.PenumbraSuccess;

    public bool SetModPath(string directory, string path)
        => ipc.SetModPath(directory, path) == Ipc.PenumbraSuccess;
}
