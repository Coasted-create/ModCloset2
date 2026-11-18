using System.Numerics;
using ModCloset2.Detect;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary>
/// The working memory of the maker, kept from one picture to the next and let go in <see cref="PhotoMaker.Rest"/>.
/// Inside the game the .NET runtime is shared with every other plugin, and tens of megabytes of garbage per picture
/// show as stutter; so everything that scales with the picture or the model lives here and is reused: the file buffer,
/// the vertices, the textures' levels and decoded pixels, the drawing buffers, the framing's scratch arrays. Arrays only
/// grow (within the budget); none is made per picture once the largest piece has been drawn.
/// </summary>
public sealed class PhotoPools
{
    public readonly MeshArena Arena = new();

    /// <summary> Bytes read by the pools' own partial reads (whole files are counted by the mod source). </summary>
    public long BytesRead;

    private byte[] _file = Array.Empty<byte>();

    // ---- textures: one slot per texture of the picture
    public sealed class TextureSlot
    {
        public byte[] Level = Array.Empty<byte>();
        public byte[] Rgba = Array.Empty<byte>();
        public bool[] Decoded = Array.Empty<bool>();
        public bool InUse;
    }

    private readonly List<TextureSlot> _slots = new();

    // ---- per vertex while drawing
    public Vector3[] View = Array.Empty<Vector3>();
    public Vector2[] Screen = Array.Empty<Vector2>();
    public Vector3[] Tangents = Array.Empty<Vector3>();
    public Vector3[] Bitangents = Array.Empty<Vector3>();

    // ---- framing
    public float[] X = Array.Empty<float>();
    public float[] Area = Array.Empty<float>();
    public float[] SortX = Array.Empty<float>();
    public float[] SortArea = Array.Empty<float>();
    public Vector3[] Points = Array.Empty<Vector3>();
    public int[] Mark = Array.Empty<int>();
    public int Stamp;

    // ---- drawing at the supersampled size, and the picture
    public float[] Depth = Array.Empty<float>();
    public int[] Item = Array.Empty<int>();
    public int[] Triangle = Array.Empty<int>();
    public float[] W0 = Array.Empty<float>();
    public float[] W1 = Array.Empty<float>();
    public Vector3[] Colour = Array.Empty<Vector3>();
    public float[] Coverage = Array.Empty<float>();
    public Vector3[] Lit = Array.Empty<Vector3>();
    public float[] LitCoverage = Array.Empty<float>();
    public byte[] Rgba = Array.Empty<byte>();
    public readonly List<(float Depth, int Item, int Triangle)> Blended = new();

    /// <summary> What the pools hold, in bytes, by part (for the report and the log). </summary>
    public IReadOnlyList<(string Part, long Bytes)> Held()
        => new (string, long)[]
        {
            ("file buffer", _file.Length),
            ("vertices", Arena.Positions.Length * 12L + Arena.Normals.Length * 12L + Arena.Uvs.Length * 8L + Arena.Indices.Length * 4L),
            ("per vertex while drawing", View.Length * 12L + Screen.Length * 8L + Tangents.Length * 12L + Bitangents.Length * 12L),
            ("framing", X.Length * 4L + Area.Length * 4L + SortX.Length * 4L + SortArea.Length * 4L + Points.Length * 12L + Mark.Length * 4L),
            ("drawing buffers", Depth.Length * 4L + Item.Length * 4L + Triangle.Length * 4L + W0.Length * 4L + W1.Length * 4L + Colour.Length * 12L + Coverage.Length * 4L),
            ("picture", Lit.Length * 12L + LitCoverage.Length * 4L + Rgba.Length),
            ("textures", _slots.Sum(s => (long)s.Level.Length + s.Rgba.Length + s.Decoded.Length)),
        };

    public long HeldBytes
        => Held().Sum(h => h.Bytes);

    /// <summary> Starts a picture: the arrays stay, their contents do not matter any more. </summary>
    public void Begin()
    {
        Arena.Reset();
        foreach (var slot in _slots)
            slot.InUse = false;
        Blended.Clear();
    }

    /// <summary>
    /// A texture slot for this picture with room for <paramref name="pixels"/> RGBA bytes: the smallest free one that is
    /// large enough, else the largest free one (it grows), else a new one. So what the slots keep is about the largest
    /// set of textures one picture needs, not every slot's largest texture ever.
    /// </summary>
    public TextureSlot NextSlot(int pixels)
    {
        TextureSlot? fit = null, largest = null;
        foreach (var slot in _slots)
        {
            if (slot.InUse)
                continue;
            if (slot.Rgba.Length >= pixels && (fit is null || slot.Rgba.Length < fit.Rgba.Length))
                fit = slot;
            if (largest is null || slot.Rgba.Length > largest.Rgba.Length)
                largest = slot;
        }

        var pick = fit ?? largest;
        if (pick is null)
            _slots.Add(pick = new TextureSlot());
        pick.InUse = true;
        return pick;
    }

    /// <summary>
    /// A file of the mod: from a mod folder into the reused buffer (the length says how much of it is the file); from a
    /// pack, the source's own array. Null when missing, unreadable or larger than <paramref name="maxBytes"/>.
    /// </summary>
    public (byte[] Bytes, int Length)? ReadFile(ModSource source, string local, long maxBytes)
    {
        if (source is not FolderSource folder)
            return source.Read(local, maxBytes) is { } whole ? (whole, whole.Length) : null;
        try
        {
            if (folder.FullPath(local) is not { } path)
                return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, ModSource.Sharing, 1, FileOptions.SequentialScan);
            if (stream.Length > maxBytes)
                return null;
            var length = (int)stream.Length;
            Ensure(ref _file, length);
            stream.ReadExactly(_file, 0, length);
            BytesRead += length;
            return (_file, length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary> Makes <paramref name="array"/> at least <paramref name="size"/> long (contents not kept). </summary>
    public static void Ensure<T>(ref T[] array, int size)
    {
        if (array.Length < size)
            array = new T[size];
    }
}
