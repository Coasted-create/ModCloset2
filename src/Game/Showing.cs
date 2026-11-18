using ModCloset2.Core;

namespace ModCloset2.Game;

/// <summary>
/// The ground truth of what is on (port and widening of the old SlotTruth): Penumbra says which file the game loaded for
/// each game path on the character, and the file's folder says which mod it came from. Priorities and settings are
/// guesses; this is what is on screen. No Dalamud types.
/// </summary>
public static class Showing
{
    private static readonly IReadOnlyList<string> None = Array.Empty<string>();

    /// <summary>
    /// Socket → the mods whose files show there, the one that supplies the model first, then by how many files they supply.
    /// The old plugin looked at gear models only; Mod Closet 2 also counts materials and textures (a recolour of the game's
    /// own top is on too) and the hair, face, tail, ear and body files.
    /// </summary>
    /// <param name="map"> Game path → loaded file, from <c>Ipc.ResourceMap</c>. </param>
    /// <param name="rootFull"> The Penumbra mod root (<see cref="GamePaths.RootFull"/>). </param>
    public static Dictionary<Socket, IReadOnlyList<string>> Read(IReadOnlyDictionary<string, string> map, string rootFull)
    {
        var tally = new Dictionary<Socket, Dictionary<string, (bool Model, int Files)>>();
        foreach (var (game, actual) in map)
        {
            var socket = GamePaths.SocketOf(game);
            if (socket is Socket.Other or Socket.None)
                continue;
            if (GamePaths.ModDirectoryOf(rootFull, actual) is not { } dir)
                continue;

            if (!tally.TryGetValue(socket, out var mods))
                tally[socket] = mods = new Dictionary<string, (bool, int)>(StringComparer.OrdinalIgnoreCase);
            var (model, files) = mods.GetValueOrDefault(dir);
            mods[dir] = (model || GamePaths.IsModel(game), files + 1);
        }

        var result = new Dictionary<Socket, IReadOnlyList<string>>();
        foreach (var (socket, mods) in tally)
            result[socket] = mods.OrderByDescending(kv => kv.Value.Model).ThenByDescending(kv => kv.Value.Files)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key).ToList();
        return result;
    }

    /// <summary>
    /// Gear slot name → the mod whose model file shows there (null: the game's own model), for every gear model on the
    /// character. This is the old SlotTruth.Read, which the check after Keep is built on.
    /// </summary>
    public static Dictionary<string, string?> ModelsBySlot(IReadOnlyDictionary<string, string> map, string rootFull)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (game, actual) in map)
        {
            if (!GamePaths.IsModel(game) || !GamePaths.TryParseGearModel(game, out var slot, out _, out _))
                continue;
            result[slot.Name] = GamePaths.ModDirectoryOf(rootFull, actual);
        }

        return result;
    }

    public static IReadOnlyList<string> In(IReadOnlyDictionary<Socket, IReadOnlyList<string>> showing, Socket socket)
        => showing.TryGetValue(socket, out var list) ? list : None;

    /// <summary> True when two showing maps say the same. </summary>
    public static bool Same(IReadOnlyDictionary<Socket, IReadOnlyList<string>> a, IReadOnlyDictionary<Socket, IReadOnlyList<string>> b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (var (socket, list) in a)
            if (!b.TryGetValue(socket, out var other) || !list.SequenceEqual(other, StringComparer.OrdinalIgnoreCase))
                return false;
        return true;
    }
}
