using System.Buffers.Binary;
using System.Text;

namespace ModCloset2.Detect;

/// <summary> One mesh of a model's highest detail: positions, triangles and skinning. </summary>
public sealed class MeshData
{
    public float[] Positions = Array.Empty<float>(); // x, y, z per vertex
    public int[] Indices = Array.Empty<int>();       // three per triangle

    /// <summary> The mesh's bones by name; per vertex <see cref="Influences"/> slots (index into BoneNames) with weights. </summary>
    public string[] BoneNames = Array.Empty<string>();
    public byte[] BoneIndices = Array.Empty<byte>();
    public float[] Weights = Array.Empty<float>();
    public int Influences;
    public int MaterialIndex;

    /// <summary>
    /// Parts of the mesh that attributes switch on and off: (first index into <see cref="Indices"/>, index count, bits of
    /// the model's attributes the part needs). Empty: the mesh is one part that always shows.
    /// </summary>
    public List<(int Start, int Count, uint Attributes)> SubMeshes = new();

    public int VertexCount
        => Positions.Length / 3;
}

/// <summary> What the detector reads from a .mdl: names and the meshes of LOD 0. </summary>
public sealed class ModelData
{
    public uint Version;
    public List<string> Bones = new();
    public List<string> Materials = new();
    public List<string> Attributes = new();
    public List<string> Shapes = new();
    public List<MeshData> Meshes = new();

    /// <summary> Model bounding box from the header (min x y z w, max x y z w). </summary>
    public float[] BoundingBox = new float[8];
}

/// <summary>
/// Read-only .mdl reader (v5 and v6) for the detector. Ported from the old plugin's MdlFile.Read (header layout after
/// Penumbra.GameData's MdlFile) and ModelDecode.DecodeLod0 (vertex decoding). Only LOD 0 is decoded; nothing is ever
/// written. Throws <see cref="InvalidDataException"/> on a damaged file; <see cref="TryRead"/> wraps that.
/// </summary>
public static class MdlReader
{
    public const uint V5 = 0x01000005;
    public const uint V6 = 0x01000006;
    private const int HeaderSize = 0x44;
    private const int ElementsPerDeclaration = 17;

    // vertex element types and usages of the .mdl format
    private const byte Single1 = 0, Single2 = 1, Single3 = 2, Single4 = 3, UByte4 = 5, UByte4N = 8, Half2 = 13, Half4 = 14, UShort2 = 16, UShort4 = 17;
    private const byte UsagePosition = 0, UsageWeights = 1, UsageBones = 2;

    private readonly record struct Element(byte Stream, byte Offset, byte Type, byte Usage);

    private sealed class Mesh
    {
        public int VertexCount;
        public uint IndexCount;
        public int MaterialIndex;
        public int BoneTableIndex;
        public uint StartIndex;
        public int SubMeshIndex;
        public int SubMeshCount;
        public uint[] Offsets = new uint[3];
        public byte[] Strides = new byte[3];
    }

    /// <summary> Reads a model; null with a reason when it is damaged or not a model. </summary>
    public static ModelData? TryRead(byte[] bytes, out string error)
    {
        try
        {
            error = string.Empty;
            return Read(bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            error = ex is InvalidDataException ? ex.Message : "The model file is damaged.";
            return null;
        }
    }

    public static ModelData Read(byte[] b)
    {
        var m = new ModelData();
        var r = new Reader(b);
        m.Version = r.U32();
        var stack            = r.U32();
        var runtime          = r.U32();
        var declarationCount = r.U16();
        var materialCount    = r.U16();
        var vertexOffset     = new uint[3];
        var indexOffset      = new uint[3];
        var vertexSize       = new uint[3];
        var indexSize        = new uint[3];
        for (var i = 0; i < 3; ++i) vertexOffset[i] = r.U32();
        for (var i = 0; i < 3; ++i) indexOffset[i]  = r.U32();
        for (var i = 0; i < 3; ++i) vertexSize[i]   = r.U32();
        for (var i = 0; i < 3; ++i) indexSize[i]    = r.U32();
        var lodCount = r.U8();
        r.Skip(3);

        if (m.Version is not (V5 or V6))
            throw new InvalidDataException($"Unsupported model version 0x{m.Version:X}.");
        if (lodCount > 3)
            throw new InvalidDataException("Bad LOD count.");

        var dataOffset = (long)HeaderSize + runtime + stack;
        if (dataOffset > b.Length)
            throw new InvalidDataException("The model file is cut off.");

        // Every level of detail must be in the file, even those the detector does not read: a file cut short is
        // damaged for the game too (the old MdlSummary checked the same).
        for (var i = 0; i < lodCount; ++i)
            if ((long)vertexOffset[i] + vertexSize[i] > b.Length || (long)indexOffset[i] + indexSize[i] > b.Length)
                throw new InvalidDataException("The model file is cut off.");

        var declarations = new List<List<Element>>();
        for (var d = 0; d < declarationCount; ++d)
        {
            var els = new List<Element>();
            for (var k = 0; k < ElementsPerDeclaration; ++k)
            {
                var e = new Element(r.U8(), r.U8(), r.U8(), r.U8());
                r.Skip(4);
                if (e.Stream == 255)
                {
                    r.Skip((ElementsPerDeclaration - 1 - k) * 8);
                    break;
                }

                els.Add(e);
            }

            declarations.Add(els);
        }

        if (r.Position != HeaderSize + stack)
            throw new InvalidDataException("Unexpected vertex declaration size.");

        var stringCount = r.U16();
        r.U16();
        var stringSize = (int)r.U32();
        if (stringSize < 0 || r.Position + stringSize > b.Length)
            throw new InvalidDataException("The model name table is damaged.");
        var strings = r.Bytes(stringSize);
        var names   = new Dictionary<uint, string>();
        var start   = 0;
        for (var i = 0; i < stringCount && start < strings.Length; ++i)
        {
            var end = Array.IndexOf(strings, (byte)0, start);
            if (end < 0)
                end = strings.Length;
            names[(uint)start] = Encoding.UTF8.GetString(strings, start, end - start);
            start = end + 1;
        }

        string S(uint o) => names.TryGetValue(o, out var s) ? s : string.Empty;

        r.F32(); // radius
        var meshCount       = r.U16();
        var attributeCount  = r.U16();
        var subMeshCount    = r.U16();
        var mhMaterialCount = r.U16();
        var boneCount       = r.U16();
        var boneTableCount  = r.U16();
        var shapeCount      = r.U16();
        var shapeMeshCount  = r.U16();
        var shapeValueCount = r.U16();
        r.U8(); // lod count (repeated)
        r.U8(); // flags 1
        var elementIdCount         = r.U16();
        var terrainShadowMeshCount = r.U8();
        var flags2                 = r.U8();
        r.Skip(8); // clip distances
        r.U16();   // culling grid count
        var terrainShadowSubMeshCount = r.U16();
        r.Skip(4); // flags 3, bg change indices, neck morph count
        var boneTableArrayCountTotal = r.U16();
        r.Skip(4 + 6);

        if (mhMaterialCount != materialCount)
            throw new InvalidDataException("Material counts in the headers disagree.");

        r.Skip(elementIdCount * 32);

        // LOD 0 only: its first mesh and mesh count.
        var lod0MeshIndex = 0;
        var lod0MeshCount = 0;
        for (var i = 0; i < 3; ++i)
        {
            var meshIndex = r.U16();
            var count     = r.U16();
            if (i == 0)
            {
                lod0MeshIndex = meshIndex;
                lod0MeshCount = count;
            }

            r.Skip(56);
        }

        if ((flags2 & 0x10) != 0)
            r.Skip(3 * 40); // extra LODs

        var meshes = new List<Mesh>();
        for (var i = 0; i < meshCount; ++i)
        {
            var me = new Mesh { VertexCount = r.U16() };
            r.U16();
            me.IndexCount     = r.U32();
            me.MaterialIndex  = r.U16();
            me.SubMeshIndex   = r.U16();
            me.SubMeshCount   = r.U16();
            me.BoneTableIndex = r.U16();
            me.StartIndex     = r.U32();
            for (var k = 0; k < 3; ++k) me.Offsets[k] = r.U32();
            for (var k = 0; k < 3; ++k) me.Strides[k] = r.U8();
            r.U8(); // stream count
            meshes.Add(me);
        }

        for (var i = 0; i < attributeCount; ++i)
            m.Attributes.Add(S(r.U32()));
        r.Skip(terrainShadowMeshCount * 20);
        var subMeshes = new List<(uint Offset, uint Count, uint Mask)>();
        for (var i = 0; i < subMeshCount; ++i)
        {
            var offset = r.U32();
            var count  = r.U32();
            var mask   = r.U32();
            r.Skip(4); // bone start, bone count
            subMeshes.Add((offset, count, mask));
        }

        r.Skip(terrainShadowSubMeshCount * 12);
        for (var i = 0; i < materialCount; ++i)
            m.Materials.Add(S(r.U32()));
        for (var i = 0; i < boneCount; ++i)
            m.Bones.Add(S(r.U32()));

        var boneTables = new List<ushort[]>();
        if (m.Version == V5)
        {
            for (var i = 0; i < boneTableCount; ++i)
            {
                var idx = new ushort[64];
                for (var k = 0; k < 64; ++k) idx[k] = r.U16();
                var cnt = (int)r.U32();
                boneTables.Add(idx[..Math.Clamp(cnt, 0, 64)]);
            }
        }
        else
        {
            // v6: a table of (offset in u32 units from this entry, size), then the arrays.
            for (var i = 0; i < boneTableCount; ++i)
            {
                var entry  = r.Position;
                var offset = r.U16();
                var size   = r.U16();
                var table  = new ushort[size];
                var p      = entry + offset * 4;
                if (p + size * 2 > b.Length)
                    throw new InvalidDataException("The bone tables are damaged.");
                for (var k = 0; k < size; ++k)
                    table[k] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 2 * k));
                boneTables.Add(table);
            }

            r.Skip(boneTableArrayCountTotal * 2);
        }

        for (var i = 0; i < shapeCount; ++i)
        {
            m.Shapes.Add(S(r.U32()));
            r.Skip(12);
        }

        r.Skip(shapeMeshCount * 12 + shapeValueCount * 4);
        var boneMapBytes = r.U32();
        r.Skip((int)Math.Min(boneMapBytes, int.MaxValue));
        var pad = r.U8();
        r.Skip(pad);
        for (var i = 0; i < 8; ++i)
            m.BoundingBox[i] = r.F32();

        if (r.Position > dataOffset)
            throw new InvalidDataException("The model header is larger than stated.");

        if (lodCount == 0)
            return m;

        var vBase = (long)vertexOffset[0];
        var iBase = (long)indexOffset[0];
        for (var mi = lod0MeshIndex; mi < lod0MeshIndex + lod0MeshCount && mi < meshes.Count; ++mi)
            if (mi < declarations.Count && Decode(b, m, meshes[mi], declarations[mi], boneTables, vBase, iBase) is { } mesh)
            {
                // Sub-mesh index offsets count from the start of the LOD's index buffer, not the mesh.
                var me = meshes[mi];
                for (var k = 0; k < me.SubMeshCount && me.SubMeshIndex + k < subMeshes.Count; ++k)
                {
                    var sub   = subMeshes[me.SubMeshIndex + k];
                    var first = (long)sub.Offset - me.StartIndex;
                    if (first < 0 || first >= mesh.Indices.Length)
                        continue;
                    var count = (int)Math.Min(sub.Count, mesh.Indices.Length - first) / 3 * 3;
                    mesh.SubMeshes.Add(((int)first, count, sub.Mask));
                }

                m.Meshes.Add(mesh);
            }

        return m;
    }

    private static MeshData? Decode(byte[] data, ModelData m, Mesh mesh, List<Element> decl, List<ushort[]> boneTables, long vBase, long iBase)
    {
        if (mesh.VertexCount == 0 || mesh.IndexCount < 3)
            return null;
        if (decl.All(e => e.Usage != UsagePosition))
            return null;

        var pos = decl.First(e => e.Usage == UsagePosition);
        var n   = mesh.VertexCount;
        var d   = new MeshData { Positions = new float[n * 3], MaterialIndex = mesh.MaterialIndex };

        for (var v = 0; v < n; ++v)
        {
            var at = vBase + mesh.Offsets[pos.Stream] + (long)v * mesh.Strides[pos.Stream] + pos.Offset;
            var (x, y, z) = ReadXyz(data, at, pos.Type);
            d.Positions[v * 3]     = x;
            d.Positions[v * 3 + 1] = y;
            d.Positions[v * 3 + 2] = z;
        }

        var indexStart = iBase + mesh.StartIndex * 2L;
        var count      = (int)(mesh.IndexCount / 3 * 3);
        if (indexStart < 0 || indexStart + count * 2L > data.Length)
            throw new InvalidDataException("The model file is cut off.");
        d.Indices = new int[count];
        for (var i = 0; i < count; ++i)
        {
            int index = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)(indexStart + i * 2)));
            d.Indices[i] = index < n ? index : 0;
        }

        ReadSkinning(data, m, mesh, decl, boneTables, n, d, vBase);
        for (var i = 0; i < d.Positions.Length; ++i)
            if (!float.IsFinite(d.Positions[i]))
                d.Positions[i] = 0;
        return d;
    }

    /// <summary> Bone weights: 4 per vertex, or 8 in newer models (stored as 8 bytes, "UShort4" types). </summary>
    private static void ReadSkinning(byte[] data, ModelData m, Mesh mesh, List<Element> decl, List<ushort[]> boneTables, int n, MeshData d, long vBase)
    {
        if (!decl.Any(e => e.Usage == UsageWeights) || !decl.Any(e => e.Usage == UsageBones) || mesh.BoneTableIndex >= boneTables.Count)
            return;

        var we = decl.First(e => e.Usage == UsageWeights);
        var be = decl.First(e => e.Usage == UsageBones);
        if (we.Type is not (UByte4N or UByte4 or UShort4) || be.Type is not (UByte4 or UByte4N or UShort4))
            return;

        static int Width(byte type) => type is UShort4 ? 8 : 4;
        var k     = Math.Min(Width(we.Type), Width(be.Type));
        var table = boneTables[mesh.BoneTableIndex];
        d.BoneNames   = table.Select(x => x < m.Bones.Count ? m.Bones[x] : string.Empty).ToArray();
        d.BoneIndices = new byte[n * k];
        d.Weights     = new float[n * k];
        d.Influences  = k;
        for (var v = 0; v < n; ++v)
        {
            var wAt = vBase + mesh.Offsets[we.Stream] + (long)v * mesh.Strides[we.Stream] + we.Offset;
            var bAt = vBase + mesh.Offsets[be.Stream] + (long)v * mesh.Strides[be.Stream] + be.Offset;
            if (wAt < 0 || bAt < 0 || wAt + k > data.Length || bAt + k > data.Length)
                continue;
            for (var j = 0; j < k; ++j)
            {
                d.Weights[v * k + j]     = data[wAt + j] / 255f;
                d.BoneIndices[v * k + j] = data[bAt + j];
            }
        }
    }

    private static (float, float, float) ReadXyz(byte[] b, long at, byte type)
    {
        var size = type switch { Single3 => 12, Single4 => 16, Half4 => 8, Half2 => 4, Single2 => 8, Single1 => 4, _ => 4 };
        if (at < 0 || at + size > b.Length)
            throw new InvalidDataException("The model file is cut off.");
        var s = b.AsSpan((int)at);
        return type switch
        {
            Single3 or Single4 => (F(s, 0), F(s, 4), F(s, 8)),
            Half4              => (H(s, 0), H(s, 2), H(s, 4)),
            Single2            => (F(s, 0), F(s, 4), 0),
            Half2              => (H(s, 0), H(s, 2), 0),
            Single1            => (F(s, 0), 0, 0),
            _                  => (0, 0, 0),
        };

        static float F(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadSingleLittleEndian(s[o..]);
        static float H(ReadOnlySpan<byte> s, int o) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(s[o..]));
    }

    private sealed class Reader(byte[] data)
    {
        public int Position;

        private void Need(int n)
        {
            if (n < 0 || Position + (long)n > data.Length)
                throw new InvalidDataException("The model file is cut off.");
        }

        public byte U8() { Need(1); return data[Position++]; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Position)); Position += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Position)); Position += 4; return v; }
        public float F32() { Need(4); var v = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(Position)); Position += 4; return v; }
        public byte[] Bytes(int n) { Need(n); var v = data.AsSpan(Position, n).ToArray(); Position += n; return v; }
        public void Skip(int n) { Need(n); Position += n; }
    }
}

/// <summary>
/// Read-only .mtrl summary: shader package and texture paths. After the old plugin's MtrlSummary / MtrlFile (layout:
/// 16-byte header, texture / uv set / colour set entries, string table). Never throws.
/// </summary>
public sealed class MtrlData
{
    public string Shader = string.Empty;
    public List<string> Textures = new();

    public static MtrlData? TryRead(byte[] b, out string error)
    {
        error = string.Empty;
        try
        {
            if (b.Length < 16)
            {
                error = "The material file is too short.";
                return null;
            }

            var stringSize = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8));
            var shpkOffset = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(10));
            int texCount   = b[12];
            int uvCount    = b[13];
            int csCount    = b[14];
            var strings    = 16 + (texCount + uvCount + csCount) * 4;
            if (strings + stringSize > b.Length)
            {
                error = "The material file is cut off.";
                return null;
            }

            var m = new MtrlData();
            for (var i = 0; i < texCount; ++i)
            {
                var off = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(16 + i * 4));
                if (off < stringSize)
                    m.Textures.Add(Str(b, strings + off, strings + stringSize));
            }

            if (shpkOffset < stringSize)
                m.Shader = Str(b, strings + shpkOffset, strings + stringSize);
            return m;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or ArgumentException or IndexOutOfRangeException)
        {
            error = "The material file is damaged.";
            return null;
        }
    }

    private static string Str(byte[] b, int start, int limit)
    {
        var end = Array.IndexOf(b, (byte)0, start, Math.Max(0, limit - start));
        return Encoding.UTF8.GetString(b, start, (end < 0 ? limit : end) - start);
    }
}
