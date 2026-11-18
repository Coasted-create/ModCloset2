using System.Buffers.Binary;
using System.Text;

namespace ModCloset2.Colours;

// Pure code (no game, no Dalamud): linked into tests/ColourTests.

/// <summary> What kind of colour table a material has. </summary>
public enum TableKind
{
    /// <summary> No colour table: the look comes from textures alone ("painted"). </summary>
    None,

    /// <summary> The 16-row table of materials from before Dawntrail (left as they are, like the old plugin did). </summary>
    Legacy,

    /// <summary> The 32-row Dawntrail table (32 halves per row). </summary>
    Dawntrail,
}

/// <summary>
/// A material (.mtrl), read only, with what colouring needs: shader, which texture each sampler uses, the colour table and
/// the dye table. Ported from the old plugin's Edit/MtrlFile.cs (layout), Model3d/ModelDecode.cs (MaterialInfo: samplers)
/// and CharacterExport.Colours.cs (rows and dye channels).
/// </summary>
public sealed class MaterialFile
{
    public const uint SamplerDiffuse = 0x115306BE;
    public const uint SamplerNormal  = 0x0C5EC1F1;
    public const uint SamplerIndex   = 0x565F8FD8;
    public const uint SamplerMask    = 0x8A4E82B6;

    public const int Rows = 32;
    public const int HalvesPerRow = 32;
    public const int ColourTableSize = Rows * HalvesPerRow * 2;
    public const int DyeTableSize = Rows * 4;

    public string Shader { get; private init; } = string.Empty;

    /// <summary> The texture paths as written in the material. </summary>
    public List<string> Textures { get; } = new();

    /// <summary> Sampler id → texture index. </summary>
    public Dictionary<uint, int> Samplers { get; } = new();

    public TableKind Table { get; private init; }

    /// <summary> Dawntrail: 32 rows of 32 values (table values; colours are squared). Empty otherwise. </summary>
    public float[][] RowValues { get; private init; } = Array.Empty<float[]>();

    /// <summary> Dye channel of each row (0 or 1), -1 when the row takes no dye. </summary>
    public int[] DyeChannels { get; private init; } = Array.Empty<int>();

    public bool IsSkin
        => Shader.StartsWith("skin", StringComparison.OrdinalIgnoreCase);

    public bool IsHair
        => Shader.StartsWith("hair", StringComparison.OrdinalIgnoreCase);

    public bool IsIris
        => Shader.StartsWith("iris", StringComparison.OrdinalIgnoreCase);

    /// <summary> Part of the character's body, not of the clothes (skin shown through a top, hair, eyes). </summary>
    public bool IsBody
        => IsSkin || IsHair || IsIris;

    /// <summary> The texture a sampler uses: the file the game really loaded (<paramref name="live"/>, same order) or the material's own path. </summary>
    public string? TextureOf(uint sampler, IReadOnlyList<string>? live)
    {
        if (!Samplers.TryGetValue(sampler, out var index))
            return null;
        if (live is not null && index < live.Count && live[index].Length > 0)
            return live[index];
        return index < Textures.Count ? Textures[index] : null;
    }

    /// <summary> Reads a material; null with a reason when it is damaged. Never throws. </summary>
    public static MaterialFile? Read(byte[] b, out string error)
    {
        error = string.Empty;
        try
        {
            return ReadUnsafe(b, out error);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            error = "the material file is damaged";
            return null;
        }
    }

    private static MaterialFile? ReadUnsafe(byte[] b, out string error)
    {
        error = string.Empty;
        if (b.Length < 16)
        {
            error = "the material file is cut off";
            return null;
        }

        var dataSetSize    = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6));
        var stringSize     = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8));
        var shaderOffset   = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(10));
        int textureCount   = b[12];
        int uvSetCount     = b[13];
        int colorSetCount  = b[14];
        int additionalSize = b[15];

        var pos     = 16;
        var count   = textureCount + uvSetCount + colorSetCount;
        var offsets = new ushort[count];
        for (var i = 0; i < count; ++i, pos += 4)
            offsets[i] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(pos));

        if (pos + stringSize + additionalSize + dataSetSize > b.Length)
        {
            error = "the material file is cut off";
            return null;
        }

        var strings    = b.AsSpan(pos, stringSize).ToArray();
        pos += stringSize;
        var additional = b.AsSpan(pos, additionalSize).ToArray();
        pos += additionalSize;
        var data = b.AsSpan(pos, dataSetSize).ToArray();
        pos += dataSetSize;
        var rest = b.AsSpan(pos).ToArray();

        // Table flags (as Penumbra.GameData's TableFlags): bit 2 has a table. The old plugin decided by size alone (a data
        // set of 2048 bytes or more is the 32-row table, the dye table follows it), which works in the game; kept so.
        uint flags = 0;
        for (var i = 0; i < Math.Min(4, additional.Length); ++i)
            flags |= (uint)additional[i] << (8 * i);
        var kind = data.Length >= ColourTableSize ? TableKind.Dawntrail
            : (flags & 0x4) != 0 && data.Length >= 512 ? TableKind.Legacy
            : TableKind.None;

        var m = new MaterialFile
        {
            Shader      = StringAt(strings, shaderOffset),
            Table       = kind,
            RowValues   = kind == TableKind.Dawntrail ? ReadRows(data) : Array.Empty<float[]>(),
            DyeChannels = kind == TableKind.Dawntrail ? ReadDyes(data) : Array.Empty<int>(),
        };
        for (var i = 0; i < textureCount; ++i)
            m.Textures.Add(StringAt(strings, offsets[i]));

        // samplers: after the 12-byte header come shader keys (8 bytes each), constants (8) and samplers (12)
        if (rest.Length >= 12)
        {
            int keys     = BinaryPrimitives.ReadUInt16LittleEndian(rest.AsSpan(2));
            int consts   = BinaryPrimitives.ReadUInt16LittleEndian(rest.AsSpan(4));
            int samplers = BinaryPrimitives.ReadUInt16LittleEndian(rest.AsSpan(6));
            var at       = 12 + keys * 8 + consts * 8;
            for (var i = 0; i < samplers && at + 12 <= rest.Length; ++i, at += 12)
            {
                var id    = BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(at));
                var index = rest[at + 8];
                if (index < textureCount)
                    m.Samplers[id] = index;
            }
        }

        return m;
    }

    private static float[][] ReadRows(byte[] data)
    {
        var rows = new float[Rows][];
        for (var r = 0; r < Rows; ++r)
        {
            rows[r] = new float[HalvesPerRow];
            for (var k = 0; k < HalvesPerRow; ++k)
            {
                var v = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((r * HalvesPerRow + k) * 2)));
                rows[r][k] = float.IsFinite(v) ? v : 0;
            }
        }

        return rows;
    }

    private static int[] ReadDyes(byte[] data)
    {
        var channels = new int[Rows];
        for (var r = 0; r < Rows; ++r)
        {
            channels[r] = -1;
            if (data.Length < ColourTableSize + DyeTableSize)
                continue;
            var d = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(ColourTableSize + r * 4));
            if ((d & 1) != 0)
                channels[r] = (int)(d >> 27 & 3);
        }

        return channels;
    }

    private static string StringAt(byte[] strings, int o)
    {
        if (o >= strings.Length)
            return string.Empty;
        var end = Array.IndexOf(strings, (byte)0, o);
        return Encoding.UTF8.GetString(strings, o, (end < 0 ? strings.Length : end) - o);
    }
}

/// <summary> Where the values of a Dawntrail colour table row are (as Glamourer reads them). </summary>
public static class RowField
{
    public const int DiffuseR = 0, DiffuseG = 1, DiffuseB = 2;
    public const int SpecularR = 4, SpecularG = 5, SpecularB = 6;
    public const int EmissiveR = 8, EmissiveG = 9, EmissiveB = 10;
    public const int Exposure = 11, Sheen = 12, SheenTint = 13, SheenAperture = 14;
    public const int Roughness = 16, Metalness = 18, Anisotropy = 19, SphereMapMask = 21, SphereMapIndex = 27;
}
