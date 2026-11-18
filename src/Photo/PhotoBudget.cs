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
