using ModCloset2.Core;
using ModCloset2.Detect;
using ModCloset2.Game;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary>
/// Which model a picture of a slot shows, chosen from the mod's definition alone: no model, material or texture is
/// opened to choose (the closet's asks name only the slot and the player's race, and reading every model of a mod for
/// each of its pieces would be a second run of the detection per photo).
/// <list type="number">
/// <item> The candidates: the model files the definition redirects for this slot (game paths of the slot's gear, a
/// file of the mod behind them, swaps to such a file included) with the mod's default options on; only when none is
/// on, those of options that are off. </item>
/// <item> The race: the player's own when the mod has a model of it; else the nearest along the player's fallback chain
/// (<see cref="Races.Chain"/>, what the library uses to decide what fits); else the race the mod has most models of
/// (<see cref="PhotoMaker.RacesByCount"/>); else any. Race 0 (nobody logged in): the mod's most common. </item>
/// <item> Several models left (two item sets in one slot, a right and a left ring): the first in the definition's
/// order (the default data, then the groups and their options as written, files as written). The same every time. </item>
/// </list>
/// </summary>
public static class ModelChoice
{
    public readonly record struct Choice(string GamePath, int Race);

    public static Choice? Choose(ModFiles files, Socket socket, int race)
    {
        var candidates = files.Models.Where(m => m.Socket == socket && m.On).ToList();
        if (candidates.Count == 0)
            candidates = files.Models.Where(m => m.Socket == socket).ToList();
        if (candidates.Count == 0)
            return null;

        var races = candidates.Select(c => c.Race).ToHashSet();
        var pick  = Races.Chain(race).FirstOrDefault(races.Contains);
        if (pick == 0)
            pick = files.RacesByCount.FirstOrDefault(races.Contains);
        var first = pick != 0 ? candidates.First(c => c.Race == pick) : candidates[0];
        return new Choice(first.GamePath, first.Race);
    }
}
