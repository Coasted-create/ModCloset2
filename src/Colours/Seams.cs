using ModCloset2.Core;
using Newtonsoft.Json.Linq;

namespace ModCloset2.Colours;

// What the colour engine needs from the rest of the plugin. The foundation implements these when wiring the colours in;
// the tests implement them with fakes. Everything here is called on the framework thread (from OutfitColours.Tick and
// the IOutfitColours commands), except IFileSource and ILiveModels as noted on them. No Dalamud types, so the engine
// stays testable.

/// <summary> What shows in one gear slot of the player right now. While trying on, the tried part counts. </summary>
/// <param name="Slot"> Gear slot as Glamourer counts them: 0 head, 1 body, 2 hands, 3 legs, 4 feet, 5 ears, 6 neck, 7 wrists, 8 right ring, 9 left ring. </param>
/// <param name="Part"> The part whose files show there; null for a game item no mod changes. </param>
/// <param name="Piece"> The shelf the part is on (after the user's moves), e.g. "top"; for a game item the shelf of its slot. </param>
/// <param name="PieceName"> That shelf's name, translated ("Tops"). </param>
/// <param name="Icon"> The shelf's icon key. </param>
/// <param name="Name"> The mod's name, or the game item's when no mod shows there. </param>
/// <param name="Picture"> The part's picture file, or null. </param>
/// <param name="Trying"> It shows because it is being tried on (not kept). </param>
public sealed record SlotShowing(int Slot, PartKey? Part, string Piece, string PieceName, string Icon, string Name, string? Picture, bool Trying);

/// <summary> The player and what they wear, for the colours. Implemented by the foundation (from the wardrobe and the game state). </summary>
public interface IColourWorld
{
    /// <summary> Bumped whenever <see cref="Showing"/> may have changed (a try, a put back, a keep, an undo, another item). </summary>
    int Version { get; }

    /// <summary> One entry per gear slot that shows something (slots showing nothing are left out). </summary>
    IReadOnlyList<SlotShowing> Showing { get; }

    /// <summary> The player's object index for Glamourer (0). </summary>
    int PlayerIndex { get; }

    /// <summary> The player's game object (to read the drawn models), 0 when there is none. </summary>
    nint PlayerAddress { get; }

    /// <summary>
    /// Who the player is ("name@world", as GameState.PlayerKey), "" when nobody is logged in or it is not known. The rows
    /// the colours wrote belong to this character: they are only ever put back on it. (Default "": then they are put
    /// back on whoever is logged in, but still only where a row holds what was written.)
    /// </summary>
    string PlayerKey
        => string.Empty;

    /// <summary> Why the character cannot be coloured now, one translated sentence ("Log in with a character first."), "" when it can. </summary>
    string WhyNot { get; }

    /// <summary> The game is redrawing the character (or the wardrobe waits for that): models are not read and nothing is applied meanwhile. </summary>
    bool Busy { get; }

    /// <summary>
    /// The colours may call Glamourer on their own in this frame (putting rows on after following what is worn, checking
    /// that they are still there). False while the game loads the character and the seconds after, or when another call
    /// of the closet already ran in this frame: they wait for a later frame. (Default: always.)
    /// </summary>
    bool MayCall
        => true;

    /// <summary> The colours called Glamourer in this frame (it takes the frame's turn). </summary>
    void Called()
    {
    }
}

/// <summary> Glamourer's state, as the foundation's Ipc has it (GetStateJson / ApplyState). </summary>
public interface IGlamourerStates
{
    /// <summary> Glamourer is installed and answers. </summary>
    bool Available { get; }

    /// <summary> The actor's state as JSON (design-base layout, Glamourer.GetState) and Glamourer's code (0 success). </summary>
    JObject? GetState(int objectIndex, out int code);

    /// <summary> Glamourer.ApplyState with key 0. Returns Glamourer's code (0 success, 6 locked by another plugin, 255 the call failed). </summary>
    int ApplyState(JObject state, int objectIndex, ulong flags);
}

/// <summary> A small store for the colours' memory: strings by key (the session's Remember / Remembered fit). </summary>
public interface IColourStore
{
    /// <summary> The stored value, "" when unknown. </summary>
    string Get(string key);
    void Set(string key, string value);
}

/// <summary>
/// The closet's undo. The colours hand over one step with how things were before and after (opaque strings); when the
/// user takes that step back, the journal calls <see cref="OutfitColours.Restore"/> with the "before" string (and with
/// "after" if it ever redoes).
/// </summary>
public interface IColourUndo
{
    void Record(string description, string before, string after);
}

/// <summary> The models the game draws for the player. Implemented in Game/LiveModels.cs. Framework thread only. </summary>
public interface ILiveModels
{
    /// <summary> The drawn models of the gear slots (0 … 9), or null when the character is not drawn. Never throws. </summary>
    IReadOnlyList<LiveModel>? Read(nint player);
}
