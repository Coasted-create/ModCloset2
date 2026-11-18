using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ModCloset2.Colours;

// Pure code (no game, no Dalamud): linked into tests/ColourTests.

/// <summary> One mesh of a model's highest detail, for colouring: positions, texture coordinates and the triangles shown. </summary>
public sealed class UvMesh
{
    public float[] Positions = Array.Empty<float>(); // x, y, z per vertex
    public float[] Uvs = Array.Empty<float>();       // u, v per vertex
    public int[] Indices = Array.Empty<int>();       // three per triangle
    public int MaterialIndex;

    public int VertexCount
        => Positions.Length / 3;

    public bool HasUvs { get; set; }
}

/// <summary>
/// Read-only .mdl reader (v5 and v6): the material names and the meshes of LOD 0 with positions and texture
/// coordinates, only the parts the game shows. Ported from the old plugin's Edit/MdlFile.cs (header layout, after
/// Penumbra.GameData's MdlFile) and Web/Model3d/ModelDecode.cs (DecodeLod0: vertex decoding, attributes, shape keys).
/// Never throws: <see cref="Read"/> gives null with a reason on a damaged file.
/// </summary>
public sealed class ModelFile
{
    public const uint V5 = 0x01000005;
    public const uint V6 = 0x01000006;
    private const int HeaderSize = 0x44;
    private const int ElementsPerDeclaration = 17;

    // vertex element types and usages of the .mdl format
    private const byte Single1 = 0, Single2 = 1, Single3 = 2, Single4 = 3, UByte4 = 5, UByte4N = 8, Half2 = 13, Half4 = 14, UShort2 = 16, UShort4 = 17;
    private const byte UsagePosition = 0, UsageUv = 4;

    public List<string> Materials { get; } = new();
    public List<string> Attributes { get; } = new();
    public List<UvMesh> Meshes { get; } = new();

    private readonly record struct Element(byte Stream, byte Offset, byte Type, byte Usage);

    private sealed class Mesh
    {
        public int VertexCount;
        public uint IndexCount;
        public int MaterialIndex;
        public int SubMeshIndex;
        public int SubMeshCount;
        public uint StartIndex;
        public uint[] Offsets = new uint[3];
        public byte[] Strides = new byte[3];
    }

    private readonly record struct SubMesh(uint IndexOffset, uint IndexCount, uint AttributeMask);

    private readonly record struct Shape(ushort MeshStart, ushort MeshCount);

    private readonly record struct ShapeMesh(uint MeshIndexOffset, uint ValueCount, uint ValueOffset);

    private readonly record struct ShapeValue(ushort BaseIndicesIndex, ushort ReplacingVertexIndex);

    /// <summary>
    /// Reads a model. Only the parts the game would show: a part with attributes (atr_…) is kept when all of them are in
    /// <paramref name="enabledAttributes"/>, and the shape keys in <paramref name="enabledShapes"/> are applied (bit i =
    /// the model's attribute / shape i, as the game counts them). The defaults keep everything and no shapes.
    /// </summary>
    public static ModelFile? Read(byte[] bytes, out string error, uint enabledAttributes = uint.MaxValue, uint enabledShapes = 0)
    {
        error = string.Empty;
        try
        {
            return ReadUnsafe(bytes, enabledAttributes, enabledShapes);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            error = ex is InvalidDataException ? ex.Message : "the model file is damaged";
            return null;
        }
    }

    private static ModelFile ReadUnsafe(byte[] b, uint enabledAttributes, uint enabledShapes)
    {
        var m = new ModelFile();
        var r = new Reader(b);
        var version          = r.U32();
        var stack            = r.U32();
        var runtime          = r.U32();
        var declarationCount = r.U16();
        var materialCount    = r.U16();
        var vertexOffset = new uint[3];
        var indexOffset  = new uint[3];
        for (var i = 0; i < 3; ++i) vertexOffset[i] = r.U32();
        for (var i = 0; i < 3; ++i) indexOffset[i]  = r.U32();
        r.Skip(24); // vertex and index buffer sizes
        var lodCount = r.U8();
        r.Skip(3);

        if (version is not (V5 or V6))
            throw new InvalidDataException($"unsupported model version 0x{version:X}");
        if (lodCount is 0 or > 3)
            throw new InvalidDataException("bad LOD count");

        var dataOffset = (long)HeaderSize + runtime + stack;
        if (dataOffset > b.Length)
            throw new InvalidDataException("the model file is cut off");

        var declarations = new List<List<Element>>();
        for (var d = 0; d < declarationCount; ++d)
        {
            var els = new List<Element>();
            for (var k = 0; k < ElementsPerDeclaration; ++k)
            {
                var e = new Element(r.U8(), r.U8(), r.U8(), r.U8());
                r.Skip(4); // usage index and padding
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
            throw new InvalidDataException("unexpected vertex declaration size");

        var stringCount = r.U16();
        r.U16();
        var stringSize = (int)r.U32();
        var strings    = r.Bytes(stringSize);
        var offsets    = new Dictionary<uint, string>();
        var start      = 0;
        for (var i = 0; i < stringCount && start < strings.Length; ++i)
        {
            var end = Array.IndexOf(strings, (byte)0, start);
            if (end < 0)
                end = strings.Length;
            offsets[(uint)start] = Encoding.UTF8.GetString(strings, start, end - start);
            start = end + 1;
        }

        string S(uint o) => offsets.TryGetValue(o, out var s) ? s : string.Empty;

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
        r.U8();  // lod count (repeated)
        r.U8();  // flags 1
        var elementIdCount         = r.U16();
        var terrainShadowMeshCount = r.U8();
        var flags2                 = r.U8();
        r.Skip(10); // clip distances, culling grid count
        var terrainShadowSubMeshCount = r.U16();
        r.Skip(4);  // flags 3, material indices, neck morphs
        var boneTableArrayCountTotal = r.U16();
        r.Skip(4 + 6);

        if (mhMaterialCount != materialCount)
            throw new InvalidDataException("material counts in the headers disagree");

        r.Skip(elementIdCount * 32);

        var lodMeshIndex = 0;
        var lodMeshCount = 0;
        for (var i = 0; i < 3; ++i)
        {
            var meshIndex = r.U16();
            var count     = r.U16();
            r.Skip(60 - 4);
            if (i == 0)
            {
                lodMeshIndex = meshIndex;
                lodMeshCount = count;
            }
        }

        if ((flags2 & 0x10) != 0)
            r.Skip(3 * 40); // extra LODs

        var meshes = new List<Mesh>();
        for (var i = 0; i < meshCount; ++i)
        {
            var me = new Mesh { VertexCount = r.U16() };
            r.U16();
            me.IndexCount    = r.U32();
            me.MaterialIndex = r.U16();
            me.SubMeshIndex  = r.U16();
            me.SubMeshCount  = r.U16();
            r.U16(); // bone table
            me.StartIndex = r.U32();
            for (var k = 0; k < 3; ++k) me.Offsets[k] = r.U32();
            for (var k = 0; k < 3; ++k) me.Strides[k] = r.U8();
            r.U8();
            meshes.Add(me);
        }

        for (var i = 0; i < attributeCount; ++i)
            m.Attributes.Add(S(r.U32()));
        r.Skip(terrainShadowMeshCount * 20);
        var subMeshes = new List<SubMesh>();
        for (var i = 0; i < subMeshCount; ++i)
        {
            subMeshes.Add(new SubMesh(r.U32(), r.U32(), r.U32()));
            r.Skip(4);
        }

        r.Skip(terrainShadowSubMeshCount * 12);
        for (var i = 0; i < materialCount; ++i)
            m.Materials.Add(S(r.U32()));
        r.Skip(boneCount * 4);

        if (version == V5)
        {
            r.Skip(boneTableCount * (64 * 2 + 4));
        }
        else
        {
            r.Skip(boneTableCount * 4);
            r.Skip(boneTableArrayCountTotal * 2);
        }

        var shapes = new List<Shape>();
        for (var i = 0; i < shapeCount; ++i)
        {
            r.U32(); // name
            var meshStart = r.U16();
            r.Skip(4);
            var count = r.U16();
            r.Skip(4);
            shapes.Add(new Shape(meshStart, count));
        }

        var shapeMeshes = new List<ShapeMesh>();
        for (var i = 0; i < shapeMeshCount; ++i)
            shapeMeshes.Add(new ShapeMesh(r.U32(), r.U32(), r.U32()));
        var shapeValues = new List<ShapeValue>();
        for (var i = 0; i < shapeValueCount; ++i)
            shapeValues.Add(new ShapeValue(r.U16(), r.U16()));

        var data = b.AsSpan((int)dataOffset);
        var vertexStart = (long)vertexOffset[0] - dataOffset;
        var indexStart0 = (long)indexOffset[0] - dataOffset;

        for (var mi = lodMeshIndex; mi < lodMeshIndex + lodMeshCount && mi < meshes.Count; ++mi)
        {
            var mesh = meshes[mi];
            if (mesh.VertexCount == 0 || mesh.IndexCount < 3 || mi >= declarations.Count)
                continue;

            var decl = declarations[mi];
            if (decl.All(e => e.Usage != UsagePosition))
                continue;
            var pos   = decl.First(e => e.Usage == UsagePosition);
            var hasUv = decl.Any(e => e.Usage == UsageUv);
            var uv    = hasUv ? decl.First(e => e.Usage == UsageUv) : default;

            var n = mesh.VertexCount;
            var d = new UvMesh
            {
                Positions     = new float[n * 3],
                Uvs           = new float[n * 2],
                MaterialIndex = mesh.MaterialIndex,
                HasUvs        = hasUv,
            };

            for (var v = 0; v < n; ++v)
            {
                var p = Read(data, Start(vertexStart, mesh, pos, v), pos.Type);
                d.Positions[v * 3]     = float.IsFinite(p.X) ? p.X : 0;
                d.Positions[v * 3 + 1] = float.IsFinite(p.Y) ? p.Y : 0;
                d.Positions[v * 3 + 2] = float.IsFinite(p.Z) ? p.Z : 0;
                if (hasUv)
                {
                    var t = Read(data, Start(vertexStart, mesh, uv, v), uv.Type);
                    d.Uvs[v * 2]     = float.IsFinite(t.X) ? t.X : 0;
                    d.Uvs[v * 2 + 1] = float.IsFinite(t.Y) ? t.Y : 0;
                }
            }

            var indexStart = indexStart0 + mesh.StartIndex * 2L;
            var count      = (int)mesh.IndexCount;
            if (indexStart < 0 || indexStart + count * 2L > data.Length)
                continue;
            var all = new int[count];
            for (var i = 0; i < count; ++i)
            {
                var index = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)(indexStart + i * 2)..]);
                all[i] = index < n ? index : 0;
            }

            ApplyShapes(shapes, shapeMeshes, shapeValues, mesh, all, n, enabledShapes);
            d.Indices = VisibleTriangles(subMeshes, mesh, all, enabledAttributes);
            if (d.Indices.Length > 0)
                m.Meshes.Add(d);
        }

        return m;
    }

    /// <summary> A shape key swaps some corners of the mesh's triangles for other vertices (stored after the normal ones). </summary>
    private static void ApplyShapes(List<Shape> shapes, List<ShapeMesh> shapeMeshes, List<ShapeValue> values, Mesh mesh, int[] indices, int vertexCount, uint enabled)
    {
        if (enabled == 0)
            return;
        for (var s = 0; s < shapes.Count && s < 32; ++s)
        {
            if ((enabled >> s & 1) == 0)
                continue;
            for (var k = 0; k < shapes[s].MeshCount; ++k)
            {
                var at = shapes[s].MeshStart + k;
                if (at >= shapeMeshes.Count)
                    break;
                var sm = shapeMeshes[at];
                if (sm.MeshIndexOffset != mesh.StartIndex)
                    continue;
                for (var v = 0; v < sm.ValueCount; ++v)
                {
                    var vi = (int)(sm.ValueOffset + v);
                    if (vi >= values.Count)
                        break;
                    var value = values[vi];
                    if (value.BaseIndicesIndex < indices.Length && value.ReplacingVertexIndex < vertexCount)
                        indices[value.BaseIndicesIndex] = value.ReplacingVertexIndex;
                }
            }
        }
    }

    /// <summary> The triangles of the mesh's parts that are shown (a mesh without parts is shown whole). </summary>
    private static int[] VisibleTriangles(List<SubMesh> subMeshes, Mesh mesh, int[] indices, uint enabled)
    {
        if (mesh.SubMeshCount == 0 || enabled == uint.MaxValue)
            return indices.Length % 3 == 0 ? indices : indices[..(indices.Length / 3 * 3)];

        var kept = new List<int>(indices.Length);
        for (var k = 0; k < mesh.SubMeshCount; ++k)
        {
            var at = mesh.SubMeshIndex + k;
            if (at >= subMeshes.Count)
                break;
            var sub = subMeshes[at];
            if ((sub.AttributeMask & ~enabled) != 0)
                continue;
            var begin = (long)sub.IndexOffset - mesh.StartIndex;
            var end   = Math.Min(begin + sub.IndexCount, indices.Length);
            if (begin < 0)
                continue;
            for (var i = begin; i + 2 < end; i += 3)
            {
                kept.Add(indices[i]);
                kept.Add(indices[i + 1]);
                kept.Add(indices[i + 2]);
            }
        }

        return kept.ToArray();
    }

    private static long Start(long vertexStart, Mesh mesh, Element e, int vertex)
        => vertexStart + mesh.Offsets[e.Stream % 3] + (long)vertex * mesh.Strides[e.Stream % 3] + e.Offset;

    private static Vector4 Read(ReadOnlySpan<byte> b, long at, byte type)
    {
        if (at < 0 || at + Size(type) > b.Length)
            return Vector4.Zero;
        var s = b[(int)at..];
        return type switch
        {
            Single1 => new Vector4(F(s, 0), 0, 0, 0),
            Single2 => new Vector4(F(s, 0), F(s, 4), 0, 0),
            Single3 => new Vector4(F(s, 0), F(s, 4), F(s, 8), 0),
            Single4 => new Vector4(F(s, 0), F(s, 4), F(s, 8), F(s, 12)),
            Half2   => new Vector4(H(s, 0), H(s, 2), 0, 0),
            Half4   => new Vector4(H(s, 0), H(s, 2), H(s, 4), H(s, 6)),
            UByte4  => new Vector4(s[0], s[1], s[2], s[3]),
            UByte4N => new Vector4(s[0] / 255f, s[1] / 255f, s[2] / 255f, s[3] / 255f),
            UShort2 => new Vector4(BinaryPrimitives.ReadUInt16LittleEndian(s), BinaryPrimitives.ReadUInt16LittleEndian(s[2..]), 0, 0),
            UShort4 => new Vector4(BinaryPrimitives.ReadUInt16LittleEndian(s), BinaryPrimitives.ReadUInt16LittleEndian(s[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(s[4..]), BinaryPrimitives.ReadUInt16LittleEndian(s[6..])),
            _ => Vector4.Zero,
        };

        static float F(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadSingleLittleEndian(s[o..]);
        static float H(ReadOnlySpan<byte> s, int o) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(s[o..]));
    }

    private static int Size(byte type)
        => type switch
        {
            Single1 => 4, Single2 => 8, Single3 => 12, Single4 => 16, Half2 => 4, Half4 => 8, UShort2 => 4, UShort4 => 8, _ => 4,
        };

    private sealed class Reader(byte[] data)
    {
        public int Position;

        private void Need(int n)
        {
            if (Position < 0 || Position + n > data.Length)
                throw new InvalidDataException("the model file is cut off");
        }

        public byte U8() { Need(1); return data[Position++]; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Position)); Position += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Position)); Position += 4; return v; }
        public float F32() { Need(4); var v = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(Position)); Position += 4; return v; }
        public byte[] Bytes(int n) { Need(n); var v = data.AsSpan(Position, n).ToArray(); Position += n; return v; }
        public void Skip(int n) { Need(n); Position += n; }
    }
}
