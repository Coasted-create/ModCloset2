using ModCloset2.Core;

namespace ModCloset2.Detect;

/// <summary>
/// Everything the detector read from one mod: the raw facts its witnesses look at. The decision
/// (<see cref="Decision.Decide"/>) is a pure function of these facts, and they are what the census stores, so the
/// decision can be run again from the census alone while the rules are tuned. Plain data (System.Text.Json).
/// </summary>
public sealed class ModFacts
{
    /// <summary> Bumped when the reader starts collecting different facts (old census lines are then not comparable). </summary>
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;

    /// <summary> The mod's directory name (PartInfo.Mod). </summary>
    public string Mod { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string SortFolder { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public List<int> PreferRaceCodes { get; set; } = new();
    public string PreferBody { get; set; } = string.Empty;

    /// <summary> Option groups of the mod (name, type, option names), for "Why is it here?" and the census. </summary>
    public List<GroupFacts> Groups { get; set; } = new();
    public List<PartFacts> Parts { get; set; } = new();

    /// <summary> Custom-path files no part could claim (they are left out unless the mod has nothing else). </summary>
    public List<string> Unclaimed { get; set; } = new();
    public List<string> Problems { get; set; } = new();
    public double Milliseconds { get; set; }
    public long BytesRead { get; set; }
}

public sealed class GroupFacts
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();
}

/// <summary> One piece of text a part is named by, with where it came from. </summary>
public sealed class TextFact
{
    /// <summary>
    /// "group", "offgroup" (none of its options for this part is on by default), "option", "offoption" (off by
    /// default), "toggle" / "offtoggle" (groups and options that only switch parts of the model), "file", "material".
    /// </summary>
    public string Source { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

/// <summary> What was read for one socket of a mod. </summary>
public sealed class PartFacts
{
    public Socket Socket { get; set; }

    /// <summary> Other and weapon parts: what kind of files, by count ("animation" 91, "effect" 95…). </summary>
    public Dictionary<string, int> OtherKinds { get; set; } = new();

    /// <summary> How many game paths the part redirects, and the first ones (for the census). </summary>
    public int PathCount { get; set; }
    public List<string> Paths { get; set; } = new();

    /// <summary> Gear set ids (or hair / face / tail numbers) it changes, and of those, the ones with a model. </summary>
    public List<int> Sets { get; set; } = new();
    public List<int> ModelSets { get; set; } = new();

    /// <summary> Race codes of the part's paths (from models when it has any). </summary>
    public List<int> RaceCodes { get; set; } = new();

    /// <summary> Meta edit types that point at this socket ("eqp", "imc"…). </summary>
    public List<string> Metas { get; set; } = new();

    /// <summary> Option groups whose options change this part. </summary>
    public List<string> Groups { get; set; } = new();

    /// <summary> Names that speak about this part only (its groups, options, local files, materials). </summary>
    public List<TextFact> Texts { get; set; } = new();

    /// <summary> Shader packages of the part's materials that the mod ships ("characterlegacy.shpk", "skin.shpk"…). </summary>
    public List<string> Shaders { get; set; } = new();

    /// <summary> The representative model, when the part has one and it could be read. </summary>
    public ModelFacts? Model { get; set; }

    /// <summary> True when the part has model files (even if the representative one could not be read). </summary>
    public bool HasModel { get; set; }

    /// <summary> Models swapped in from the game (FileSwaps): the socket each game model comes from ("Head"…). </summary>
    public List<Socket> SwappedFrom { get; set; } = new();

    /// <summary> Made from files at made-up paths that no material in the mod uses (skin textures for a body's own materials). </summary>
    public bool FromCustomPaths { get; set; }
}

/// <summary> Where a model's surface is: per-bone areas and positions of the item (bare skin left out). </summary>
public sealed class ModelFacts
{
    public string GamePath { get; set; } = string.Empty;
    public string Option { get; set; } = string.Empty;

    /// <summary> Race code of the model (201 for c0201…): picks the body proportions the positions are compared with. </summary>
    public int Race { get; set; }
    public int Triangles { get; set; }
    public int Meshes { get; set; }

    /// <summary> Square metres of the item surface (skin meshes not counted) and of the skin meshes. </summary>
    public float Area { get; set; }
    public float SkinArea { get; set; }

    /// <summary> Area-weighted centre and bounding box of the item surface (x, y, z; metres, y up, facing +z). </summary>
    public float[] Centroid { get; set; } = new float[3];
    public float[] Min { get; set; } = new float[3];
    public float[] Max { get; set; } = new float[3];
    public List<string> Materials { get; set; } = new();
    public List<string> SkinMaterials { get; set; } = new();
    public List<string> Attributes { get; set; } = new();
    public List<BoneFacts> Bones { get; set; } = new();

    /// <summary>
    /// What these numbers measure: "everything" (nothing is switched off by default), "default" (parts the mod switches
    /// off by default are left out), "default, some toggles unknown", "everything (toggles unknown)" (the item's game
    /// IMC entry decides and we cannot know it) or "everything (nothing shown by default)".
    /// </summary>
    public string Visibility { get; set; } = "everything";

    /// <summary> Attributes switched off by default (their parts are left out above). </summary>
    public List<string> HiddenAttributes { get; set; } = new();

    /// <summary> Variant attributes whose default the mod does not say (measured as shown). </summary>
    public List<string> UnknownAttributes { get; set; } = new();

    /// <summary> The same model with everything measured, when something is left out above (null: the same). </summary>
    public ModelFacts? Everything { get; set; }
}

/// <summary>
/// Surface carried by one bone (triangle area × weight) and where it is. For bones whose surface can be in several
/// places (the head bone carries hats, glasses and earrings alike) a few clusters keep where the area lies.
/// </summary>
public sealed class BoneFacts
{
    public string Name { get; set; } = string.Empty;
    public float Area { get; set; }
    public float[] Centroid { get; set; } = new float[3];

    /// <summary> Up to a few (x, y, z, area) groups, flattened; empty for bones whose place the name tells. </summary>
    public float[] Clusters { get; set; } = Array.Empty<float>();
}
