using ModCloset2.Core;

namespace ModCloset2.Closet;

/// <summary> A shelf the user made ("Wings"). Its id starts with <see cref="Pieces.UserPrefix"/>. </summary>
[Serializable]
public sealed class UserShelf
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The kind it belongs to ("piercing"), "" for none. Every part of that kind the user has not placed themselves sits
    /// on this shelf. Set when the shelf is made or renamed to a kind's name; kept when it is renamed to something else.
    /// </summary>
    public string Kind { get; set; } = string.Empty;
}

/// <summary>
/// A mod the closet made out of one thing of another mod ("Make it its own mod"), kept under the new mod's folder name.
/// While the copy is in the library, the thing has no card of its own in the mod it came from (unless it is on or tried on
/// from there).
/// </summary>
[Serializable]
public sealed class OwnCopy
{
    /// <summary> The folder of the mod it was copied out of, and that mod's name when it was made. </summary>
    public string From { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;

    /// <summary> The new mod's name as made. </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary> The thing's gear slots (sockets as numbers): the same in both mods. </summary>
    public List<int> Slots { get; set; } = new();

    /// <summary> The shelf the card was on: the new mod's parts go there (once, when they are first placed). </summary>
    public string Shelf { get; set; } = string.Empty;

    /// <summary> Slots whose part was given <see cref="Shelf"/> already (sockets as numbers). </summary>
    public List<int> Shelved { get; set; } = new();

    /// <summary> The library has had the new mod at least once (before that, its absence means nothing yet). </summary>
    public bool Seen { get; set; }

    /// <summary>
    /// A ring made for the other hand (<see cref="Hands.Left"/> or <see cref="Hands.Right"/>): the thing it came from keeps
    /// its card, the copy is a ring of that hand. <see cref="Hands.Both"/>: an ordinary copy, which stands in for the thing.
    /// </summary>
    public Hands Hand { get; set; }

    public DateTime MadeUtc { get; set; }
}

/// <summary> How a part was used: for the order of cards and the "New" mark. </summary>
[Serializable]
public sealed class PartUse
{
    public int Kept { get; set; }
    public DateTime LastKeptUtc { get; set; }

    /// <summary> Keeps, each weighing half as much after <see cref="Usage.HalfLifeDays"/> days, as of <see cref="LastKeptUtc"/>. </summary>
    public double Score { get; set; }

    /// <summary> When it was last tried on (a tried part is no longer "New"). </summary>
    public DateTime TriedUtc { get; set; }
}

/// <summary>
/// What the closet remembers about the user's sorting and use. Kept in the plugin's settings (Dalamud saves them as JSON).
/// Parts are keyed by <see cref="PartKey.ToString"/>. No Dalamud types.
/// </summary>
/// <remarks>
/// Collections start empty on purpose: Dalamud's settings loader adds saved entries into a pre-filled collection, which
/// would bring defaults back on every load (a bug the old plugin had).
/// </remarks>
[Serializable]
public sealed class ClosetData
{
    /// <summary> The user's moves: part → shelf. A move wins over any detection until "Put back where the closet had it". </summary>
    public Dictionary<string, string> Moves { get; set; } = new();
    public HashSet<string> Hidden { get; set; } = new();
    public List<UserShelf> Shelves { get; set; } = new();
    public Dictionary<string, PartUse> Use { get; set; } = new();

    /// <summary> The version of a part the user picked last (index into its versions). </summary>
    public Dictionary<string, int> Versions { get; set; } = new();

    /// <summary>
    /// Body and race choices the user made by hand (part → group → choice): they win over the closet's own pick for the
    /// player's body and race whenever the mod is put on with nothing saved for it, until the user picks "automatic" again.
    /// </summary>
    public Dictionary<string, Dictionary<string, List<string>>> FitChoices { get; set; } = new();

    /// <summary>
    /// The slot the user picked last for a thing its mod offers through several slots (key: the thing's first part, by the
    /// game's slot order; value: the socket as a number).
    /// </summary>
    public Dictionary<string, int> Slots { get; set; } = new();

    /// <summary> Small things a face wants remembered (window sizes, which face). </summary>
    public Dictionary<string, string> Remembered { get; set; } = new();

    /// <summary> "Move them too" offers already made ("part&gt;shelf"), so each is offered once. </summary>
    public HashSet<string> Offered { get; set; } = new();

    /// <summary> The shelf picked last. </summary>
    public string Selected { get; set; } = string.Empty;

    /// <summary> "" (follow the game), "en" or "de". </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary> The user turned photos of pieces off (on unless they did). </summary>
    public bool PhotosOff { get; set; }

    /// <summary>
    /// The user unlocked dragging cards onto shelves (locked unless they did: a settings file without it, a new user's or
    /// one from before the lock, reads locked).
    /// </summary>
    public bool MovesUnlocked { get; set; }

    /// <summary> Mods the closet made of one thing of another mod: new mod's folder → where it came from. </summary>
    public Dictionary<string, OwnCopy> Copies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> Moves what is remembered about a mod when Penumbra renames its directory. </summary>
    public bool RenameMod(string oldDirectory, string newDirectory)
    {
        var changed = false;
        foreach (var socket in Enum.GetValues<Socket>())
        {
            var from = new PartKey(oldDirectory, socket).ToString();
            var to   = new PartKey(newDirectory, socket).ToString();
            if (Moves.Remove(from, out var shelf))
            {
                Moves[to] = shelf;
                changed   = true;
            }

            if (Hidden.Remove(from))
            {
                Hidden.Add(to);
                changed = true;
            }

            if (Use.Remove(from, out var use))
            {
                Use[to] = use;
                changed = true;
            }

            if (Versions.Remove(from, out var version))
            {
                Versions[to] = version;
                changed      = true;
            }

            if (FitChoices.Remove(from, out var fit))
            {
                FitChoices[to] = fit;
                changed        = true;
            }

            if (Slots.Remove(from, out var slot))
            {
                Slots[to] = slot;
                changed   = true;
            }
        }

        // a copy follows its own folder and the folder it came from
        if (Copies.Remove(oldDirectory, out var copy))
        {
            Copies[newDirectory] = copy;
            changed              = true;
        }

        foreach (var c in Copies.Values.Where(c => c.From.Equals(oldDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            c.From  = newDirectory;
            changed = true;
        }

        return changed;
    }
}

/// <summary> The weight of past keeps in the card order. Pure. </summary>
public static class Usage
{
    /// <summary> A keep weighs half as much after this many days. </summary>
    public const double HalfLifeDays = 30;

    /// <summary> Days a part counts as "New" after it was added (when it was never tried). </summary>
    public const double NewDays = 14;

    /// <summary> How much the keeps of a part weigh now. </summary>
    public static double Weight(PartUse? use, DateTime now)
    {
        if (use is null || use.Score <= 0)
            return 0;
        var days = Math.Max(0, (now - use.LastKeptUtc).TotalDays);
        return use.Score * Math.Pow(0.5, days / HalfLifeDays);
    }

    /// <summary> Counts one more keep now. </summary>
    public static void Kept(PartUse use, DateTime now)
    {
        use.Score       = Weight(use, now) + 1;
        use.Kept       += 1;
        use.LastKeptUtc = now;
    }

    /// <summary> Added lately and never tried or kept. </summary>
    public static bool IsNew(DateTime added, PartUse? use, DateTime now)
        => added > DateTime.MinValue && (now - added).TotalDays <= NewDays && (use is null || use.Kept == 0 && use.TriedUtc == default);
}
