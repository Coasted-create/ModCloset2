using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary>
/// A small PNG writer and reader with only the base library (zlib from System.IO.Compression). Ported from the old
/// plugin's Visual/PngCodec.cs; the reader understands 8-bit, non-interlaced RGB and RGBA, which is what the writer makes
/// (enough for tests that read their own pictures back).
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly uint[] CrcTable = MakeCrcTable();

    /// <summary> Encodes RGBA pixels (4 bytes per pixel, rows of width * 4 bytes) as a PNG, with or without the alpha. </summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, bool keepAlpha = false)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("Pixel data does not match the size.");

        var channels = keepAlpha ? 4 : 3;
        var stride   = width * channels;
        var raw      = new byte[(stride + 1) * height];
        var prev     = new byte[stride];
        var line     = new byte[stride];
        for (var y = 0; y < height; ++y)
        {
            for (var x = 0; x < width; ++x)
                for (var c = 0; c < channels; ++c)
                    line[x * channels + c] = rgba[(y * width + x) * 4 + c];

            // Filter "Up" for every row but the first: cheap and compresses pictures noticeably better than none.
            var o = y * (stride + 1);
            raw[o] = (byte)(y == 0 ? 0 : 2);
            for (var i = 0; i < stride; ++i)
                raw[o + 1 + i] = (byte)(y == 0 ? line[i] : line[i] - prev[i]);
            (prev, line) = (line, prev);
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, true))
            z.Write(raw);

        using var png = new MemoryStream();
        png.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;                          // bit depth
        ihdr[9] = (byte)(keepAlpha ? 6 : 2);  // RGBA / RGB
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    /// <summary> Decodes an 8-bit RGB or RGBA PNG (as <see cref="Encode"/> writes them) to RGBA. Null for anything else. </summary>
    public static (int Width, int Height, byte[] Rgba)? Decode(ReadOnlySpan<byte> file)
    {
        try
        {
            if (file.Length < 8 || !file[..8].SequenceEqual(Signature))
                return null;
            int width = 0, height = 0, depth = 0, type = 0, interlace = 0;
            using var idat = new MemoryStream();
            var pos = 8;
            while (pos + 8 <= file.Length)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(file[pos..]);
                var name   = Encoding.ASCII.GetString(file.Slice(pos + 4, 4));
                var data   = file.Slice(pos + 8, length);
                pos += 12 + length;
                switch (name)
                {
                    case "IHDR":
                        width     = BinaryPrimitives.ReadInt32BigEndian(data);
                        height    = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                        depth     = data[8];
                        type      = data[9];
                        interlace = data[12];
                        break;
                    case "IDAT": idat.Write(data); break;
                    case "IEND": pos = file.Length; break;
                }
            }

            if (depth != 8 || interlace != 0 || type is not (2 or 6))
                return null;
            var channels = type == 6 ? 4 : 3;
            var stride   = width * channels;
            var raw      = new byte[(stride + 1) * height];
            idat.Position = 0;
            using (var z = new ZLibStream(idat, CompressionMode.Decompress))
                z.ReadExactly(raw);

            var prev = new byte[stride];
            var cur  = new byte[stride];
            var rgba = new byte[width * height * 4];
            for (var y = 0; y < height; ++y)
            {
                var filter = raw[y * (stride + 1)];
                Array.Copy(raw, y * (stride + 1) + 1, cur, 0, stride);
                for (var i = 0; i < stride; ++i)
                {
                    int a = i >= channels ? cur[i - channels] : 0;
                    int b = prev[i];
                    int c = i >= channels ? prev[i - channels] : 0;
                    cur[i] = filter switch
                    {
                        0 => cur[i],
                        1 => (byte)(cur[i] + a),
                        2 => (byte)(cur[i] + b),
                        3 => (byte)(cur[i] + (a + b) / 2),
                        4 => (byte)(cur[i] + Paeth(a, b, c)),
                        _ => throw new InvalidDataException($"Unknown PNG filter {filter}."),
                    };
                }

                for (var x = 0; x < width; ++x)
                {
                    var o = (y * width + x) * 4;
                    var s = x * channels;
                    rgba[o]     = cur[s];
                    rgba[o + 1] = cur[s + 1];
                    rgba[o + 2] = cur[s + 2];
                    rgba[o + 3] = channels == 4 ? cur[s + 3] : (byte)255;
                }

                (prev, cur) = (cur, prev);
            }

            return (width, height, rgba);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or EndOfStreamException)
        {
            return null;
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p  = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream s, string name, byte[] data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        s.Write(buf);
        var typeBytes = Encoding.ASCII.GetBytes(name);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; ++n)
        {
            var c = n;
            for (var k = 0; k < 8; ++k)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
