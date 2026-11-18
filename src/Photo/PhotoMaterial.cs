using System.Buffers.Binary;
using System.Numerics;
using ModCloset2.Colours;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary> How a mesh is drawn. </summary>
public enum SurfaceKind
{
    /// <summary> Gear with a colour table (most clothes and jewellery): rows from the index map or the normal map's alpha. </summary>
    Table,

    /// <summary> A material with its own colour picture and no table ("painted"). </summary>
    Painted,

    /// <summary> The body's own skin a piece carries (the "dummy"): one plain mannequin colour. </summary>
    Skin,

    /// <summary> A material the mod does not ship (the game's own) or that cannot be read: a neutral colour. </summary>
    Missing,

    /// <summary> Not drawn: shadow-only meshes, hair and eyes. </summary>
    Hidden,
}

/// <summary> One row of a colour table, as the shader needs it (colours linear, as the game keeps them). </summary>
public readonly record struct TableRow(Vector3 Diffuse, Vector3 Specular, Vector3 Emissive, float Roughness, float Metalness, float SpecularStrength);

/// <summary> The parts of a material the photo needs beyond <see cref="MaterialFile"/>: shader flags and the older 16-row table. </summary>
public sealed class MaterialExtras
{
    /// <summary> The material asks the game to hide the back of its triangles (else both sides are drawn). </summary>
    public bool HideBackfaces { get; init; }

    /// <summary> The material is drawn see-through (blended), not cut out. </summary>
    public bool Transparent { get; init; }

    /// <summary> The 16 rows of a table from before Dawntrail (empty otherwise). </summary>
    public TableRow[] LegacyRows { get; init; } = Array.Empty<TableRow>();

    /// <summary> Reads the flags and the older table; layout as in <see cref="MaterialFile"/>. Never throws. </summary>
    public static MaterialExtras Read(byte[] b)
    {
        try
        {
            if (b.Length < 16)
                return new MaterialExtras();
            var dataSetSize    = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6));
            var stringSize     = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8));
            int textureCount   = b[12];
            int uvSetCount     = b[13];
            int colorSetCount  = b[14];
            int additionalSize = b[15];
            var pos  = 16 + (textureCount + uvSetCount + colorSetCount) * 4 + stringSize;
            var flags = 0u;
            for (var i = 0; i < Math.Min(4, additionalSize) && pos + i < b.Length; ++i)
                flags |= (uint)b[pos + i] << (8 * i);
            pos += additionalSize;
            var data = pos + dataSetSize <= b.Length ? b.AsSpan(pos, dataSetSize).ToArray() : Array.Empty<byte>();
            pos += dataSetSize;

            // the shader package's own flags follow its header: value list size, key, constant, sampler counts, flags
            var shaderFlags = pos + 12 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pos + 8)) : 0u;

            // 16 rows of 16 halves: diffuse (0-2), specular strength (3), specular (4-6), gloss (7), emissive (8-10)
            var legacy = Array.Empty<TableRow>();
            if (data.Length is >= 512 and < MaterialFile.ColourTableSize && (flags & 0x4) != 0)
            {
                legacy = new TableRow[16];
                for (var r = 0; r < 16; ++r)
                {
                    float H(int k)
                    {
                        var v = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((r * 16 + k) * 2)));
                        return float.IsFinite(v) ? Math.Max(0, v) : 0;
                    }

                    var gloss = H(7);
                    // gloss is a specular power (about 1 to 100): a rough cloth has a low one
                    var roughness = Math.Clamp(1 - MathF.Log2(1 + gloss) / 7, 0.15f, 1);
                    legacy[r] = new TableRow(new Vector3(H(0), H(1), H(2)), new Vector3(H(4), H(5), H(6)), new Vector3(H(8), H(9), H(10)),
                        roughness, 0, Math.Min(H(3), 2));
                }
            }

            return new MaterialExtras { HideBackfaces = (shaderFlags & 0x1) != 0, Transparent = (shaderFlags & 0x10) != 0, LegacyRows = legacy };
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            return new MaterialExtras();
        }
    }

    /// <summary> The rows of a Dawntrail table in the shader's terms (<see cref="RowField"/> says where each value is). </summary>
    public static TableRow[] DawntrailRows(MaterialFile m)
    {
        var rows = new TableRow[m.RowValues.Length];
        for (var r = 0; r < rows.Length; ++r)
        {
            var v = m.RowValues[r];
            float At(int k) => Math.Max(0, v[k]);
            rows[r] = new TableRow(
                new Vector3(At(RowField.DiffuseR), At(RowField.DiffuseG), At(RowField.DiffuseB)),
                new Vector3(At(RowField.SpecularR), At(RowField.SpecularG), At(RowField.SpecularB)),
                new Vector3(At(RowField.EmissiveR), At(RowField.EmissiveG), At(RowField.EmissiveB)),
                Math.Clamp(v[RowField.Roughness], 0.05f, 1),
                Math.Clamp(v[RowField.Metalness], 0, 1),
                1);
        }

        return rows;
    }
}

/// <summary> A mesh's material as the renderer uses it: what kind, its rows and its textures (decoded once the zoom is known). </summary>
public sealed class PhotoSurface
{
    public string Name { get; init; } = string.Empty;
    public SurfaceKind Kind { get; set; }
    public string Shader { get; init; } = string.Empty;
    public bool TwoSided { get; init; } = true;

    /// <summary> Blended over what is behind it (glass, see-through layers) rather than cut out. </summary>
    public bool Blend { get; init; }

    /// <summary> Rows of the colour table (32 Dawntrail, 16 older, empty for none). </summary>
    public TableRow[] Rows { get; init; } = Array.Empty<TableRow>();

    /// <summary> The row comes from the normal map's alpha (older materials without an index map). </summary>
    public bool RowFromNormalAlpha { get; init; }

    /// <summary> Local files of the textures the shader uses (null: none, or not shipped by the mod). </summary>
    public string? IndexFile, NormalFile, MaskFile, DiffuseFile, SpecularFile;

    /// <summary> The decoded textures (filled after framing, at the mip level the picture needs). </summary>
    public TexImage? Index, Normal, Mask, Diffuse, Specular;

    /// <summary> The textures were looked for (once per surface, however many meshes share it). </summary>
    public bool Loaded;

    /// <summary> Area of its triangles inside the picture, in output pixels, and the same triangles' area in texture coordinates. </summary>
    public double ScreenArea, UvArea;
}
