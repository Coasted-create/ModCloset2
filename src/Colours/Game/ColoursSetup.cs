using Dalamud.Plugin.Services;

namespace ModCloset2.Colours.Game;

/// <summary> Makes the colour engine with its game parts (for the plugin: one line when wiring the colours in). </summary>
public static class ColoursSetup
{
    public static OutfitColours Create(IColourWorld world, IGlamourerStates glamourer, IColourStore store, IColourUndo undo, IDataManager data, IPluginLog log)
        => new(world, glamourer, store, undo, new GameModels(), new GameFiles(data), new ColourOptions { Log = line => log.Information(line) });
}
