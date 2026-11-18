namespace ModCloset2.Core;

// "Make it its own mod" (docs/briefs/10-own-mod.md): a card's thing, copied out of its mod into a new Penumbra mod that
// holds nothing else. The mod it comes from stays exactly as it is.
//
// Two contracts live here. What a face shows (OwnModPlan, and IClosetSession.OwnModOf / MakeOwnMod in Session.cs), and
// the file work behind it (IOwnModMaker), which knows neither the game nor Penumbra and is given to the session.

/// <summary> Where "Make it its own mod" stands for one card. </summary>
public enum OwnModState : byte
{
    /// <summary> Still reading the mod's files to work out what the copy would hold. </summary>
    Working = 0,

    /// <summary> Worked out: the numbers are filled in and <see cref="IClosetSession.MakeOwnMod"/> can be called. </summary>
    Ready = 1,

    /// <summary> It cannot be done for this card; <see cref="OwnModPlan.Why"/> says why, in one sentence for the user. </summary>
    No = 2,

    /// <summary> The new mod is being written; <see cref="OwnModPlan.Done"/> of <see cref="OwnModPlan.Files"/> files are copied. </summary>
    Making = 3,
}

/// <summary> What making a card's thing a mod of its own would do, for the small sheet that asks. </summary>
public sealed class OwnModPlan
{
    public OwnModState State { get; init; }

    /// <summary> <see cref="OwnModState.No"/>: the reason ("This mod is only this piece."). </summary>
    public string Why { get; init; } = string.Empty;

    /// <summary> The name the closet proposes for the new mod ("Summer Set · Bracelets"). The user may change it. </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary> Name of the mod it comes out of. </summary>
    public string From { get; init; } = string.Empty;

    /// <summary> How many files are copied, and their size in bytes. </summary>
    public int Files { get; init; }
    public long Bytes { get; init; }

    /// <summary> <see cref="OwnModState.Making"/>: files copied so far. </summary>
    public int Done { get; init; }

    /// <summary> Names of the mod's option groups that change this thing and so come along. </summary>
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

    /// <summary> How many of the copied files other pieces of the mod use as well (they are copied, not taken). </summary>
    public int Shared { get; init; }

    /// <summary> Names of what else the mod holds and keeps ("Top", "Skirt"), for the line that says the mod stays as it is. </summary>
    public IReadOnlyList<string> Stays { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Quiet lines for the sheet, already in the user's language: a sentence of the author's about edits or sharing when
    /// the mod has one, anything the copy leaves behind on purpose.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary> What the file work is asked to copy out. </summary>
public sealed class OwnModAsk
{
    /// <summary> Full path of the mod's folder. </summary>
    public string ModFolder { get; init; } = string.Empty;

    /// <summary> The gear slots of the thing (one, or several when the mod offers the same thing through several slots). </summary>
    public IReadOnlyList<Socket> Sockets { get; init; } = Array.Empty<Socket>();

    /// <summary>
    /// The options that are on for this thing now, by group name. The copy starts with these as its defaults, so it looks
    /// like the card it was made from. Groups not named keep the mod's own defaults.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Chosen { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>
/// What the copy would hold, as far as the session needs to know. The maker returns its own subclass with the details
/// and takes it back in <see cref="IOwnModMaker.Write"/>.
/// </summary>
public abstract class OwnModContent
{
    /// <summary> False when the thing cannot be copied out; <see cref="Problem"/> then says why in one sentence for the user. </summary>
    public bool Possible { get; init; }
    public string Problem { get; init; } = string.Empty;

    /// <summary> Name and author of the mod it comes out of, as written in the mod. </summary>
    public string ModName { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public int Files { get; init; }
    public long Bytes { get; init; }

    /// <summary> Names of the option groups that come along. </summary>
    public IReadOnlyList<string> Groups { get; init; } = Array.Empty<string>();

    /// <summary> Files in the copy that the rest of the mod uses too. </summary>
    public int Shared { get; init; }

    /// <summary> True when the mod holds more than this thing (otherwise it already is a mod of its own). </summary>
    public bool ModHasMore { get; init; }

    /// <summary> A sentence of the author's about edits, ports or sharing, as written in the mod ("" when there is none). </summary>
    public string AuthorsLine { get; init; } = string.Empty;

    /// <summary> Meta changes of the mod that belong to no particular piece and stay behind (0 when none). </summary>
    public int LeftBehind { get; init; }
}

/// <summary> How the new mod is written. </summary>
public sealed class OwnModWrite
{
    /// <summary> Full path of Penumbra's mod folder; the new mod becomes one new folder in it. </summary>
    public string ModRoot { get; init; } = string.Empty;

    /// <summary> Name of the new mod. </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary> What the thing is, for the new mod's description ("Bracelets"). </summary>
    public string Piece { get; init; } = string.Empty;

    /// <summary> "Mod Closet 2 0.4.0", for the note inside the new mod. </summary>
    public string MadeBy { get; init; } = string.Empty;
}

/// <summary>
/// The file work behind "Make it its own mod". Reads one mod folder, writes one new folder. It never changes, moves or
/// deletes anything that was there before: not in the mod it reads, not anywhere else. No game, no Penumbra.
/// Both calls can take a while (they read models and materials, and copy files): never call them on the frame.
/// </summary>
public interface IOwnModMaker
{
    /// <summary> Works out what the copy holds. Reads only. Never throws: a problem makes the content not <see cref="OwnModContent.Possible"/>. </summary>
    OwnModContent Plan(OwnModAsk ask);

    /// <summary>
    /// Writes the new mod and returns its folder name (relative to <see cref="OwnModWrite.ModRoot"/>). The folder appears
    /// complete or not at all: on an error or a stop nothing of it is left. Throws with a sentence for the user when it
    /// cannot (disk full, the mod changed since the plan).
    /// </summary>
    /// <param name="progress"> Called with (files done, files in all); may be called from another thread. </param>
    string Write(OwnModContent content, OwnModWrite how, Action<int, int>? progress, CancellationToken stop);
}
