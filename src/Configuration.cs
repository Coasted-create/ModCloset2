using Dalamud.Configuration;
using ModCloset2.Closet;
using ModCloset2.Wear;

namespace ModCloset2;

/// <summary>
/// Mod Closet 2's settings (pluginConfigs/ModCloset2.json, saved through Dalamud). Only what the new closet needs; the old
/// plugin's settings file is never written. Caches live in pluginConfigs/ModCloset2/.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration, IWearSettings
{
    public int Version { get; set; } = 1;

    /// <summary> The user's body ("Rue"); empty: the one picked in the old Mod Closet, else the body mod that is on. </summary>
    public string MyBody { get; set; } = string.Empty;

    /// <summary> Lowest priority of the temporary settings used for trying on (above any normal priority). </summary>
    public int TemporaryPriority { get; set; } = 10000;

    /// <summary> Mods put on with Keep (mod directory → what to put back on take-off). </summary>
    public Dictionary<string, WornRecord> Worn { get; set; } = new();

    /// <summary> The user's sorting, use, language and what the faces remember. </summary>
    public ClosetData Closet { get; set; } = new();
}
