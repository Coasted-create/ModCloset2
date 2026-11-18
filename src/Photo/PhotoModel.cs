using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary>
/// The vertices and triangles of one picture, all meshes in one set of arrays that the maker keeps from picture to
/// picture (they only grow, up to the budget), so drawing thousands of pictures does not make thousands of arrays.
/// Triangle corners are indices into these arrays, not into their mesh.
/// </summary>
public sealed class MeshArena
{
    public Vector3[] Positions = Array.Empty<Vector3>();
    public Vector3[] Normals = Array.Empty<Vector3>();
    public Vector2[] Uvs = Array.Empty<Vector2>();

    /// <summary> The second texture coordinates (the second pair of a four-value element): where a face's paint sits. </summary>
    public Vector2[] Uvs2 = Array.Empty<Vector2>();
    public int[] Indices = Array.Empty<int>();
    public int VertexCount;
    public int IndexCount;

    public void Reset()
    {
        VertexCount = 0;
        IndexCount  = 0;
    }

    /// <summary> Room for <paramref name="vertices"/> and <paramref name="indices"/> more. Only called within the budget. </summary>
    public void Reserve(int vertices, int indices)
    {
        Grow(ref Positions, VertexCount + vertices);
        Grow(ref Normals, VertexCount + vertices);
        Grow(ref Uvs, VertexCount + vertices);
        Grow(ref Uvs2, VertexCount + vertices);
        Grow(ref Indices, IndexCount + indices);
    }

    private static void Grow<T>(ref T[] array, int needed)
    {
        if (array.Length >= needed)
            return;
        var size = Math.Max(needed, Math.Min(array.Length * 2, needed + (1 << 20)));
        var next = new T[size];
        Array.Copy(array, next, array.Length);
        array = next;
    }
}

/// <summary> One mesh of a model's highest detail: a range of the arena's vertices and of its triangle corners. </summary>
public sealed class PhotoMesh
{
    public required MeshArena Arena;
    public int VertexStart, VertexCount;
    public int IndexStart, IndexCount;
    public int MaterialIndex;
    public bool HasUvs;
    public bool HasNormals;

    public Vector3[] Positions => Arena.Positions;
    public Vector3[] Normals => Arena.Normals;
    public Vector2[] Uvs => Arena.Uvs;
    public Vector2[] Uvs2 => Arena.Uvs2;
    public int[] Indices => Arena.Indices;

    public int IndexEnd
        => IndexStart + IndexCount;

    public int TriangleCount
        => IndexCount / 3;
}

/// <summary> A model read for drawing: its material names, attribute names and the meshes of LOD 0 that are shown. </summary>
public sealed class PhotoModel
{
    public List<string> Materials { get; } = new();
    public List<string> Attributes { get; } = new();
    public List<PhotoMesh> Meshes { get; } = new();

    /// <summary>
    /// For each attribute, the materials of the parts it marks in LOD 0 (bit j = material j; materials past 31 count as
    /// 31). Known when the reader asks which attributes are shown, so the answer can depend on what a part is made of.
    /// </summary>
    public uint[] AttributeMaterials { get; internal set; } = Array.Empty<uint>();
}

/// <summary>
/// Read-only .mdl reader (v5 and v6) for the photo: like <c>Colours/ModelFile</c> (positions, texture coordinates, the
/// parts that are shown) plus the vertex normals, into a <see cref="MeshArena"/>. Header layout after Penumbra.GameData's
/// MdlFile, as in the old plugin's Edit/MdlFile.cs and Web/Model3d/ModelDecode.cs.
/// <para>
/// A mod's files come from anywhere, so nothing in them is believed: every count and offset is checked against the
/// file before it is used, every mesh's vertices and triangles must lie inside the file, and the whole model must fit
/// the <see cref="PhotoBudget"/> before anything is decoded. Never throws: <see cref="Read"/> gives null with a reason.
/// </para>
/// </summary>
public static class PhotoModelReader
{
    public const uint V5 = 0x01000005;
    public const uint V6 = 0x01000006;
    private const int HeaderSize = 0x44;
    private const int ElementsPerDeclaration = 17;

    // vertex element types and usages of the .mdl format
    private const byte Single1 = 0, Single2 = 1, Single3 = 2, Single4 = 3, UByte4 = 5, UByte4N = 8, Half2 = 13, Half4 = 14, UShort2 = 16, UShort4 = 17;
    private const byte UsagePosition = 0, UsageNormal = 3, UsageUv = 4;

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

    /// <summary>
    /// Reads a model from the first <paramref name="length"/> bytes of <paramref name="data"/> into the arena (after what
    /// is there). <paramref name="shown"/> gets the model's attribute names and answers which are on (bit i = attribute
    /// i); a part is kept when all its attributes are on. Null when the file is damaged or over budget: then
    /// <paramref name="error"/> is one of <see cref="PhotoProblems.ModelReason"/> (a fixed reason, safe to log),
    /// <paramref name="detail"/> the numbers behind it, and <paramref name="overBudget"/> says the budget refused it.
    /// </summary>
    public static PhotoModel? Read(byte[] data, int length, MeshArena arena, PhotoBudget budget, Func<PhotoModel, uint> shown,
        out string error, out string detail, out bool overBudget)
    {
        error      = string.Empty;
        detail     = string.Empty;
        overBudget = false;
        var vertices = arena.VertexCount;
        var indices  = arena.IndexCount;
        try
        {
            if (length < 0 || length > data.Length)
                throw new ModelTrouble(PhotoProblems.ModelReason.CutOff);
            return ReadUnsafe(data, length, arena, budget, shown);
        }
        catch (Exception ex) when (ex is ModelTrouble or InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            (error, detail, overBudget) = ex is ModelTrouble t ? (t.Reason, t.Detail, t.OverBudget) : (PhotoProblems.ModelReason.Damaged, ex.Message, false);
            arena.VertexCount = vertices;
            arena.IndexCount  = indices;
            return null;
        }
    }

    /// <summary> For tests and small files: the whole array is the file. </summary>
    public static PhotoModel? Read(byte[] bytes, out string error, uint enabledAttributes = uint.MaxValue)
        => Read(bytes, bytes.Length, new MeshArena(), PhotoBudget.Default, _ => enabledAttributes, out error, out _, out _);

    private static PhotoModel ReadUnsafe(byte[] b, int length, MeshArena arena, PhotoBudget budget, Func<PhotoModel, uint> shown)
    {
        var m = new PhotoModel();
        var r = new Reader(b, length);
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
            throw new ModelTrouble(PhotoProblems.ModelReason.Version, $"version 0x{version:X}");
        if (lodCount is 0 or > 3)
            throw new ModelTrouble(PhotoProblems.ModelReason.Lods, $"{lodCount} levels of detail");

        var dataOffset = (long)HeaderSize + runtime + stack;
        if (dataOffset > length)
            throw new ModelTrouble(PhotoProblems.ModelReason.CutOff, $"data at {dataOffset}, file {length} bytes");
        if ((long)declarationCount * ElementsPerDeclaration * 8 != stack)
            throw new ModelTrouble(PhotoProblems.ModelReason.Declarations, $"{declarationCount} declarations in {stack} bytes");

        var declarations = new List<Element[]>(declarationCount);
        for (var d = 0; d < declarationCount; ++d)
        {
            var els = new List<Element>(4);
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

            declarations.Add(els.ToArray());
        }

        var stringCount = r.U16();
        r.U16();
        var stringSize = (int)Math.Min(r.U32(), int.MaxValue);
        r.Need(stringSize);
        var stringStart = r.Position;
        r.Skip(stringSize);

        // names are read when asked for, by offset into the table: a table cannot make more strings than it has bytes
        string S(uint o)
        {
            if (o >= stringSize)
                return string.Empty;
            var span = b.AsSpan(stringStart + (int)o, stringSize - (int)o);
            var end  = span.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
        }

        _ = stringCount;
        r.F32(); // radius
        var meshCount       = r.U16();
        var attributeCount  = r.U16();
        var subMeshCount    = r.U16();
        var mhMaterialCount = r.U16();
        r.U16(); // bones
        r.U16(); // bone tables
        r.U16(); // shapes
        r.U16(); // shape meshes
        r.U16(); // shape values
        r.U8();  // lod count (repeated)
        r.U8();  // flags 1
        var elementIdCount         = r.U16();
        var terrainShadowMeshCount = r.U8();
        var flags2                 = r.U8();
        r.Skip(10); // clip distances, culling grid count
        var terrainShadowSubMeshCount = r.U16();
        r.Skip(4);  // flags 3, material indices, neck morphs
        r.U16();    // bone table array count
        r.Skip(4 + 6);

        if (mhMaterialCount != materialCount)
            throw new ModelTrouble(PhotoProblems.ModelReason.MaterialCounts, $"{materialCount} and {mhMaterialCount}");

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

        // every table below is read from the file, so its count is bounded by the file's length (Need throws first)
        r.Need(meshCount * 36);
        var meshes = new List<Mesh>(meshCount);
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

        r.Need(attributeCount * 4);
        for (var i = 0; i < attributeCount; ++i)
            m.Attributes.Add(S(r.U32()));
        r.Skip(terrainShadowMeshCount * 20);
        r.Need(subMeshCount * 16);
        var subMeshes = new SubMesh[subMeshCount];
        for (var i = 0; i < subMeshCount; ++i)
        {
            subMeshes[i] = new SubMesh(r.U32(), r.U32(), r.U32());
            r.Skip(4);
        }

        r.Skip(terrainShadowSubMeshCount * 12);
        r.Need(materialCount * 4);
        for (var i = 0; i < materialCount; ++i)
            m.Materials.Add(S(r.U32()));

        // Bones, bone tables and shape keys are not needed: the picture is the rest pose without shapes. The vertex and
        // index data are found by the header's offsets, so nothing after the materials is read.

        var attributeMaterials = new uint[Math.Min(m.Attributes.Count, 32)];
        for (var mi = lodMeshIndex; mi < lodMeshIndex + lodMeshCount && mi < meshes.Count; ++mi)
            for (var k = 0; k < meshes[mi].SubMeshCount && meshes[mi].SubMeshIndex + k < subMeshes.Length; ++k)
            {
                var mask = subMeshes[meshes[mi].SubMeshIndex + k].AttributeMask;
                for (var a = 0; a < attributeMaterials.Length; ++a)
                    if ((mask >> a & 1) != 0)
                        attributeMaterials[a] |= 1u << Math.Min((int)meshes[mi].MaterialIndex, 31);
            }

        m.AttributeMaterials = attributeMaterials;
        var enabled     = shown(m);
        var vertexStart = (long)vertexOffset[0];
        var indexStart0 = (long)indexOffset[0];

        // ---- what LOD 0 holds, checked against the file and the budget before anything is decoded
        var plan = new List<(Mesh Mesh, Element[] Decl, Element Pos, Element? Uv, Element? Normal)>();
        long totalVertices = 0, totalIndices = 0;
        for (var mi = lodMeshIndex; mi < lodMeshIndex + lodMeshCount && mi < meshes.Count; ++mi)
        {
            var mesh = meshes[mi];
            if (mesh.VertexCount == 0 || mesh.IndexCount < 3 || mi >= declarations.Count)
                continue;
            var decl = declarations[mi];
            var pi = Array.FindIndex(decl, e => e.Usage == UsagePosition);
            if (pi < 0)
                continue;
            var pos    = decl[pi];
            var uvAt   = Array.FindIndex(decl, e => e.Usage == UsageUv);
            var nrmAt  = Array.FindIndex(decl, e => e.Usage == UsageNormal);
            Element? uv = uvAt >= 0 ? decl[uvAt] : null, normal = nrmAt >= 0 ? decl[nrmAt] : null;

            // every vertex element of every vertex must be inside the file
            if (!Fits(vertexStart, mesh, pos, length) || uv is { } u && !Fits(vertexStart, mesh, u, length) || normal is { } n && !Fits(vertexStart, mesh, n, length))
                throw new ModelTrouble(PhotoProblems.ModelReason.Vertices, $"mesh {mi}");
            if (indexStart0 + mesh.StartIndex * 2L + mesh.IndexCount * 2L > length)
                throw new ModelTrouble(PhotoProblems.ModelReason.Triangles, $"mesh {mi}");

            totalVertices += mesh.VertexCount;
            totalIndices  += mesh.IndexCount / 3 * 3;
            plan.Add((mesh, decl, pos, uv, normal));
        }

        if (arena.VertexCount + totalVertices > budget.MaxVertices || (arena.IndexCount + totalIndices) / 3 > budget.MaxTriangles)
            throw new ModelTrouble("too large", $"{totalIndices / 3:N0} triangles, {totalVertices:N0} vertices") { OverBudget = true };

        arena.Reserve((int)totalVertices, (int)totalIndices);
        foreach (var (mesh, _, pos, uv, normal) in plan)
        {
            var n    = mesh.VertexCount;
            var base0 = arena.VertexCount;
            var d = new PhotoMesh
            {
                Arena = arena, VertexStart = base0, VertexCount = n, MaterialIndex = mesh.MaterialIndex,
                HasUvs = uv is not null, HasNormals = normal is not null,
            };

            var span = b.AsSpan(0, length);
            for (var v = 0; v < n; ++v)
            {
                var p = Read(span, Start(vertexStart, mesh, pos, v), pos.Type);
                arena.Positions[base0 + v] = new Vector3(Finite(p.X), Finite(p.Y), Finite(p.Z));
                arena.Uvs[base0 + v] = Vector2.Zero;
                arena.Uvs2[base0 + v] = Vector2.Zero;
                arena.Normals[base0 + v] = Vector3.UnitY;
                if (uv is { } ue)
                {
                    var t = Read(span, Start(vertexStart, mesh, ue, v), ue.Type);
                    arena.Uvs[base0 + v] = new Vector2(Finite(t.X), Finite(t.Y));
                    arena.Uvs2[base0 + v] = new Vector2(Finite(t.Z), Finite(t.W));
                }

                if (normal is { } ne)
                {
                    var q   = Read(span, Start(vertexStart, mesh, ne, v), ne.Type);
                    var vec = new Vector3(Finite(q.X), Finite(q.Y), Finite(q.Z));
                    // UByte4N normals are stored as 0..1: move them back to -1..1
                    if (ne.Type is UByte4N)
                        vec = vec * 2 - Vector3.One;
                    var len = vec.Length();
                    arena.Normals[base0 + v] = len > 1e-6f ? vec / len : Vector3.UnitY;
                }
            }

            arena.VertexCount += n;
            d.IndexStart = arena.IndexCount;
            d.IndexCount = Visible(span, indexStart0 + mesh.StartIndex * 2L, subMeshes, mesh, base0, enabled, arena);
            arena.IndexCount += d.IndexCount;
            if (!d.HasNormals)
                FaceNormals(d);
            if (d.IndexCount > 0)
                m.Meshes.Add(d);
        }

        return m;
    }

    private static bool Fits(long vertexStart, Mesh mesh, Element e, int length)
    {
        var stream = e.Stream % 3;
        var first  = vertexStart + mesh.Offsets[stream] + e.Offset;
        var last   = first + (long)(mesh.VertexCount - 1) * mesh.Strides[stream] + Size(e.Type);
        return first >= 0 && last <= length;
    }

    /// <summary> Positions further out than this (metres) are a damaged file, not a piece: they are drawn at the origin. </summary>
    public const float MaxCoordinate = 50f;

    private static float Finite(float v)
        => float.IsFinite(v) && MathF.Abs(v) <= MaxCoordinate ? v : 0;

    /// <summary> Smooth normals from the triangles, for the rare mesh that has none of its own. </summary>
    private static void FaceNormals(PhotoMesh d)
    {
        var p = d.Positions;
        var n = d.Normals;
        for (var v = d.VertexStart; v < d.VertexStart + d.VertexCount; ++v)
            n[v] = Vector3.Zero;
        var idx = d.Indices;
        for (var t = d.IndexStart; t + 2 < d.IndexEnd; t += 3)
        {
            var f = Vector3.Cross(p[idx[t + 1]] - p[idx[t]], p[idx[t + 2]] - p[idx[t]]);
            n[idx[t]] += f;
            n[idx[t + 1]] += f;
            n[idx[t + 2]] += f;
        }

        for (var v = d.VertexStart; v < d.VertexStart + d.VertexCount; ++v)
            n[v] = n[v].LengthSquared() > 1e-20f ? Vector3.Normalize(n[v]) : Vector3.UnitY;
    }

    /// <summary>
    /// Copies the triangles of the mesh's parts that are shown (a mesh without parts is shown whole) into the arena as
    /// absolute corners, and returns how many corners. A part outside the mesh is skipped; parts that overlap are not
    /// copied twice past the mesh's own size, so a file cannot make this grow beyond what was reserved.
    /// </summary>
    private static int Visible(ReadOnlySpan<byte> b, long at, SubMesh[] subMeshes, Mesh mesh, int vertexBase, uint enabled, MeshArena arena)
    {
        var count = (int)(mesh.IndexCount / 3 * 3);
        var into  = arena.IndexCount;
        var data  = b.Slice((int)at, count * 2);
        int Corner(ReadOnlySpan<byte> d, long i)
        {
            int index = BinaryPrimitives.ReadUInt16LittleEndian(d[(int)(i * 2)..]);
            return vertexBase + (index < mesh.VertexCount ? index : 0);
        }

        if (mesh.SubMeshCount == 0 || enabled == uint.MaxValue)
        {
            for (var i = 0; i < count; ++i)
                arena.Indices[into + i] = Corner(data, i);
            return count;
        }

        var n = 0;
        for (var k = 0; k < mesh.SubMeshCount; ++k)
        {
            var sub = mesh.SubMeshIndex + k;
            if (sub >= subMeshes.Length)
                break;
            if ((subMeshes[sub].AttributeMask & ~enabled) != 0)
                continue;
            var begin = (long)subMeshes[sub].IndexOffset - mesh.StartIndex;
            var end   = Math.Min(begin + subMeshes[sub].IndexCount, count);
            if (begin < 0)
                continue;
            for (var i = begin; i + 2 < end && n + 3 <= count; i += 3)
            {
                arena.Indices[into + n++] = Corner(data, i);
                arena.Indices[into + n++] = Corner(data, i + 1);
                arena.Indices[into + n++] = Corner(data, i + 2);
            }
        }

        return n;
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

    /// <summary> Reads the header field by field; every read is checked against the file's length first. </summary>
    private sealed class Reader(byte[] data, int length)
    {
        public int Position;

        public void Need(long n)
        {
            if (n < 0 || Position < 0 || Position + n > length)
                throw new ModelTrouble(PhotoProblems.ModelReason.CutOff, $"{n} bytes wanted at {Position}, file {length} bytes");
        }

        public byte U8() { Need(1); return data[Position++]; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Position)); Position += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Position)); Position += 4; return v; }
        public float F32() { Need(4); var v = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(Position)); Position += 4; return v; }
        public void Skip(long n) { Need(n); Position += (int)n; }
    }
}
