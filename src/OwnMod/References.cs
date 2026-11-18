using System.Buffers.Binary;
using System.Text;
using ModCloset2.Detect;

namespace ModCloset2.OwnMod;

/// <summary> What a model names: its materials, and the attributes and shape keys it carries. </summary>
public sealed record ModelNames(IReadOnlyList<string> Materials, IReadOnlyList<string> Attributes, IReadOnlyList<string> Shapes);

/// <summary>
/// The names inside the files a copy must follow: a model's materials (and its attributes and shape keys), a material's
/// textures. Pure and read only; files are opened with sharing, so a running game or Penumbra is never in the way.
/// A model is read only as far as its name table (a few kilobytes of a file that may be tens of megabytes).
/// </summary>
public static class References
{
    private const int HeaderSize = 0x44;
    private const int DeclarationSize = 17 * 8;
    private const long MaxMaterialBytes = 16L << 20;
    private const long MaxScanBytes = 64L << 20;

    /// <summary> The names in a model, or null when the file cannot be opened. A model in an unknown form is scanned for names. </summary>
    public static ModelNames? Model(string fullPath)
    {
        try
        {
            using var s = Open(fullPath);
            return FromTable(s) ?? FromScan(s);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary> The texture game paths a material names (normalised), or null when the file cannot be opened. </summary>
    public static IReadOnlyList<string>? MaterialTextures(string fullPath)
    {
        try
        {
            using var s = Open(fullPath);
            if (s.Length > MaxMaterialBytes)
                return Scan(s, ".tex").Select(ModDefinition.NormalizeGamePath).ToList();
            var bytes = new byte[s.Length];
            s.ReadExactly(bytes);

            // The detection's own reader, so the copy follows the same names the detection sees.
            if (MtrlData.TryRead(bytes, out _) is { } m)
                return m.Textures.Where(t => t.Length > 0).Select(ModDefinition.NormalizeGamePath).ToList();
            s.Position = 0;
            return Scan(s, ".tex").Select(ModDefinition.NormalizeGamePath).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileStream Open(string fullPath)
        => new(fullPath, FileMode.Open, FileAccess.Read, ModSource.Sharing, 4096, FileOptions.SequentialScan);

    /// <summary> The model's name table (after the header and the vertex declarations), as the game reads it. </summary>
    private static ModelNames? FromTable(FileStream s)
    {
        var header = new byte[HeaderSize];
        if (s.Length < HeaderSize + 8 || s.Read(header, 0, HeaderSize) != HeaderSize)
            return null;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (version is not (MdlReader.V5 or MdlReader.V6))
            return null;
        var stack        = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var declarations = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12));
        if (stack != (long)declarations * DeclarationSize || HeaderSize + stack + 8 > s.Length)
            return null;

        s.Position = HeaderSize + stack;
        var counts = new byte[8];
        if (s.Read(counts, 0, 8) != 8)
            return null;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(counts.AsSpan(4));
        if (size > s.Length - s.Position)
            return null;
        var table = new byte[size];
        s.ReadExactly(table);

        var materials  = new List<string>();
        var attributes = new List<string>();
        var shapes     = new List<string>();
        foreach (var name in Encoding.UTF8.GetString(table).Split('\0'))
        {
            if (name.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
                materials.Add(name);
            else if (name.StartsWith("atr", StringComparison.Ordinal))
                attributes.Add(name);
            else if (name.StartsWith("shp", StringComparison.Ordinal))
                shapes.Add(name);
        }

        return new ModelNames(materials, attributes, shapes);
    }

    /// <summary> A model the table reader does not know: every name in it that ends in ".mtrl". </summary>
    private static ModelNames FromScan(FileStream s)
    {
        s.Position = 0;
        return new ModelNames(Scan(s, ".mtrl"), Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary> Runs of printable characters in a file that end in the given extension. </summary>
    private static List<string> Scan(FileStream s, string extension)
    {
        var found = new List<string>();
        var bytes = new byte[Math.Min(s.Length, MaxScanBytes)];
        s.ReadExactly(bytes);
        var start = -1;
        for (var i = 0; i <= bytes.Length; ++i)
        {
            var printable = i < bytes.Length && bytes[i] >= 0x20 && bytes[i] < 0x7F;
            if (printable)
            {
                if (start < 0)
                    start = i;
                continue;
            }

            if (start >= 0)
            {
                var text = Encoding.ASCII.GetString(bytes, start, i - start);
                if (text.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    found.Add(text);
                start = -1;
            }
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
