using System.Text.RegularExpressions;
using ModCloset2.Core;
using ModCloset2.Detect;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary>
/// The most one picture may take. A mod's files are not trusted: anything above these is refused with a problem rather
/// than attempted, so a damaged or hostile file cannot make the game allocate or work without bound. The numbers are
/// several times the largest pieces of the sample packs (the boots: 198 000 triangles; 16 textures for a pair of
/// hands; levels of at most 1024 read; 2.2 million texels for the jersey, the most). At these ceilings one picture
/// holds at most about 64 MB of model file, 26 MB of vertices, 40 MB of texture levels and pixels.
/// </summary>
public sealed record PhotoBudget
{
    public static readonly PhotoBudget Default = new();

    public long MaxModelBytes { get; init; } = 64L << 20;
    public long MaxMaterialBytes { get; init; } = 1L << 20;
    public int MaxVertices { get; init; } = 600_000;
    public int MaxTriangles { get; init; } = 600_000;

    /// <summary> Materials read per picture (meshes beyond it are drawn plain). </summary>
    public int MaxMaterials { get; init; } = 40;

    /// <summary> Textures read per picture (beyond it a surface goes without). </summary>
    public int MaxTextures { get; init; } = 64;

    /// <summary> The largest level ever decoded (per side), and all levels of a picture together. </summary>
    public int MaxTextureSide { get; init; } = 2048;
    public long MaxTexels { get; init; } = 8L << 20;

    /// <summary> Rasterising work allowed, as a multiple of the picture's pixels (the boots of the samples: about 3). </summary>
    public long MaxOverdraw { get; init; } = 80;

    /// <summary> A texture file read whole (from a pack) may be at most this large. </summary>
    public long MaxTextureFileBytes { get; init; } = 96L << 20;

    /// <summary>
    /// The game's files one picture may read (brief 13, review 7): the game gives whole files, so this is what a picture
    /// can allocate from the game. Past it, what is left is drawn without (a texture) or left out (a dummy's file). Real
    /// pieces read 3 to 46 MB here (a recolour's textures, makeup on the game's face, cold); a 4K texture is 21 MB.
    /// </summary>
    public long MaxGameBytes { get; init; } = 128L << 20;

    /// <summary>
    /// Questions to the game ("is this file there?") one picture may ask: the recolour's and hair's searches, the head,
    /// the materials' places. The sheet's pictures ask at most 28; a piece of 40 materials none of which the game has could ask about 250. Past it, nothing more is asked.
    /// </summary>
    public int MaxGameAsks { get; init; } = 400;
}

/// <summary>
/// Which of a model's attributes are off with the mod's default settings, by Penumbra's rules. A port of the
/// detection's <c>FactReader.HiddenByDefault</c> and <c>AtrOff</c> (private there), so the maker can draw what the
/// library measured without a second run of the detection:
/// <list type="bullet">
/// <item> gear variant attributes (atr_gv_c…) follow the item's IMC attribute mask (bit 0 = a), from the mod's IMC
/// option group or an IMC edit of an option that is on; without either they are shown (the game's own entry is not
/// known). Accessories never hide "a". </item>
/// <item> custom attributes (atrx_…) are shown unless an ATR edit of an option that is on switches them off; the most
/// specific edit wins. </item>
/// </list>
/// </summary>
public static partial class Visibility
{
    [GeneratedRegex(@"^atr_([a-z])v_([a-j])$")]
    private static partial Regex VariantAttribute();

    /// <summary> Bit i set: the model's attribute i is off by default. </summary>
    public static uint Hidden(IReadOnlyList<string> attributes, Socket socket, string modelGamePath, ModDefinition def)
    {
        if (attributes.Count == 0)
            return 0;
        var info    = GamePaths.Classify(modelGamePath);
        var applied = def.AppliedByDefault();

        int? imc = null;
        foreach (var gi in Enumerable.Range(0, def.Groups.Count).Reverse().OrderByDescending(i => def.Groups[i].Priority))
        {
            var g = def.Groups[gi];
            if (g.Imc is { } id && GamePaths.ImcSocket(id) == socket && (id.Set == 0 || id.Set == info.Set))
            {
                imc = ModDefinition.ImcMask(g);
                break;
            }
        }

        if (imc is null)
            foreach (var (_, _, option) in applied)
                if (option.Metas.FirstOrDefault(m => m.Type == "imc" && m.Mask >= 0 && GamePaths.ImcSocket(m) == socket && m.Set == info.Set) is { } edit)
                {
                    imc = edit.Mask;
                    break;
                }

        var letter = socket switch
        {
            Socket.Head => 'm', Socket.Body => 't', Socket.Hands => 'g', Socket.Legs => 'd', Socket.Feet => 's',
            Socket.Ears => 'e', Socket.Neck => 'n', Socket.Wrists => 'w', Socket.Rings => 'r', _ => '\0',
        };

        uint hidden = 0;
        for (var i = 0; i < attributes.Count && i < 32; ++i)
        {
            var name    = attributes[i];
            var variant = VariantAttribute().Match(name);
            var off     = false;
            if (variant.Success && variant.Groups[1].Value[0] == letter)
            {
                var bit = variant.Groups[2].Value[0] - 'a';
                off = !(info.SetType == 'a' && bit == 0) && imc is { } mask && (mask >> bit & 1) == 0;
            }
            else if (name.StartsWith("atrx_", StringComparison.Ordinal))
            {
                off = AtrOff(name, socket, info.Set, applied);
            }

            if (off)
                hidden |= 1u << i;
        }

        return hidden;
    }

    /// <summary>
    /// A face's optional features (review of 0.6.0): the scars, tattoos and marks a character may choose are parts of
    /// the face model marked atr_fv_a to atr_fv_h ("separate part of a face hidden by character customizations"), which
    /// the game shows only for the features chosen; nobody has chosen any for a picture. Bit i set: attribute i is such a
    /// part and stays off. Kept on: a part on none of the materials features are painted with (the face's skin, its
    /// "etc" materials other than the lashes): a sculpt's own additions (horns, ears) that its author put on a feature's
    /// switch; a part an ATR edit of an option that is on switches on; everything when <paramref name="featuresMod"/>
    /// (the mod is about the features themselves).
    /// </summary>
    public static uint FaceFeaturesOff(PhotoModel model, ModDefinition def, bool featuresMod)
    {
        if (featuresMod)
            return 0;
        var attributes = model.Attributes;
        uint carriers = 0;
        for (var j = 0; j < model.Materials.Count && j < 32; ++j)
        {
            var name = model.Materials[j];
            var file = name[(name.LastIndexOf('/') + 1)..];
            if (file.Contains("_fac_", StringComparison.Ordinal) && !file.Contains("teeth", StringComparison.Ordinal)
                || file.Contains("_etc_", StringComparison.Ordinal) && !file.Contains("lash", StringComparison.Ordinal))
                carriers |= 1u << j;
        }

        List<(int Group, int Option, OptionDef Def)>? applied = null;
        uint off = 0;
        for (var i = 0; i < attributes.Count && i < 32; ++i)
        {
            if (!attributes[i].StartsWith("atr_fv_", StringComparison.Ordinal))
                continue;
            var materials = i < model.AttributeMaterials.Length ? model.AttributeMaterials[i] : 0;
            if (materials != 0 && (materials & carriers) == 0)
                continue; // the sculpt's own part, not a feature painted on the face
            applied ??= def.AppliedByDefault();
            if (!applied.Any(a => a.Def.Metas.Any(m => m.Type == "atr" && m.Attribute == attributes[i] && m.Entry == true)))
                off |= 1u << i;
        }

        return off;
    }

    private static bool AtrOff(string attribute, Socket socket, int set, List<(int Group, int Option, OptionDef Def)> applied)
    {
        (int Specific, bool Entry)? best = null;
        foreach (var (_, _, option) in applied)
            foreach (var m in option.Metas)
            {
                if (m.Type != "atr" || m.Attribute != attribute || m.Entry is not { } entry)
                    continue;
                var slot = GamePaths.AttributeSocket(m.Slot);
                if (slot is null || slot != Socket.None && slot != socket || m.Set != 0 && m.Set != set)
                    continue;
                var specific = slot == Socket.None ? 0 : m.Set != 0 ? 2 : 1;
                if (best is null || specific > best.Value.Specific)
                    best = (specific, entry);
            }

        return best is { Entry: false };
    }
}
