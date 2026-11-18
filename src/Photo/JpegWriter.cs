namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary>
/// A small baseline JPEG writer (JFIF, 8-bit, YCbCr with colour at half resolution in both directions, the standard
/// quantisation and Huffman tables of the JPEG specification, Annex K). Photos are about a quarter of their PNG size at
/// a quality where the eye finds no difference on a card: the smooth backdrop costs almost nothing, and the piece keeps
/// its edges. One writer is kept by the maker and its working buffer reused; only the finished file is a new array.
/// </summary>
public sealed class JpegWriter
{
    private static readonly int[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    };

    private static readonly byte[] LumaQuant =
    {
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    };

    private static readonly byte[] ChromaQuant =
    {
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    };

    internal static readonly byte[] DcLumaBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    internal static readonly byte[] DcChromaBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    internal static readonly byte[] DcValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    internal static readonly byte[] AcLumaBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };

    internal static readonly byte[] AcLumaValues =
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    };

    internal static readonly byte[] AcChromaBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };

    internal static readonly byte[] AcChromaValues =
    {
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
        0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    };

    /// <summary> cos((2x + 1) u π / 16) × c(u) / 2, for the separable 8 × 8 DCT. </summary>
    private static readonly float[] Cosines = MakeCosines();

    private static readonly (ushort Code, byte Length)[] DcLuma = Codes(DcLumaBits, DcValues);
    private static readonly (ushort Code, byte Length)[] DcChroma = Codes(DcChromaBits, DcValues);
    private static readonly (ushort Code, byte Length)[] AcLuma = Codes(AcLumaBits, AcLumaValues);
    private static readonly (ushort Code, byte Length)[] AcChroma = Codes(AcChromaBits, AcChromaValues);

    private byte[] _out = new byte[64 << 10];
    private int _length;
    private uint _bits;
    private int _bitCount;
    private readonly float[] _block = new float[64];
    private readonly float[] _temp = new float[64];
    private readonly float[] _lumaDivisor = new float[64];
    private readonly float[] _chromaDivisor = new float[64];
    private readonly byte[] _lumaTable = new byte[64];
    private readonly byte[] _chromaTable = new byte[64];
    private int _quality = -1;

    /// <summary>
    /// The quality the closet's photos use (1..100). On the sample packs: 16 KB a picture on average (82: 13 KB, 92: 19 KB);
    /// at 82 the chains of a necklace ring faintly at their edges when zoomed, at 88 they do not.
    /// </summary>
    public const int PhotoQuality = 88;

    /// <summary> Encodes RGBA pixels (rows top to bottom) as a JPEG file. </summary>
    public byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, int quality = PhotoQuality)
    {
        if (width <= 0 || height <= 0 || width > 65535 || height > 65535 || rgba.Length < width * height * 4)
            throw new ArgumentException("Pixel data does not match the size.");
        Tables(Math.Clamp(quality, 1, 100));
        _length   = 0;
        _bits     = 0;
        _bitCount = 0;

        Header(width, height);

        // one MCU: four luminance blocks (16 × 16 pixels) and one block each of Cb and Cr, averaged over 2 × 2 pixels
        int dcY = 0, dcCb = 0, dcCr = 0;
        Span<float> y  = stackalloc float[256];
        Span<float> cb = stackalloc float[64];
        Span<float> cr = stackalloc float[64];
        for (var my = 0; my < height; my += 16)
        for (var mx = 0; mx < width; mx += 16)
        {
            cb.Clear();
            cr.Clear();
            for (var py = 0; py < 16; ++py)
            for (var px = 0; px < 16; ++px)
            {
                // pixels beyond the edge repeat the last row and column
                var sx = Math.Min(mx + px, width - 1);
                var sy = Math.Min(my + py, height - 1);
                var o  = (sy * width + sx) * 4;
                float r = rgba[o], g = rgba[o + 1], b = rgba[o + 2];
                y[py * 16 + px] = 0.299f * r + 0.587f * g + 0.114f * b - 128;
                var c = (py >> 1) * 8 + (px >> 1);
                cb[c] += (-0.168736f * r - 0.331264f * g + 0.5f * b) * 0.25f;
                cr[c] += (0.5f * r - 0.418688f * g - 0.081312f * b) * 0.25f;
            }

            for (var k = 0; k < 4; ++k)
            {
                int ox = (k & 1) * 8, oy = (k >> 1) * 8;
                for (var r = 0; r < 8; ++r)
                    for (var c = 0; c < 8; ++c)
                        _block[r * 8 + c] = y[(oy + r) * 16 + ox + c];
                dcY = Block(dcY, _lumaDivisor, DcLuma, AcLuma);
            }

            cb.CopyTo(_block);
            dcCb = Block(dcCb, _chromaDivisor, DcChroma, AcChroma);
            cr.CopyTo(_block);
            dcCr = Block(dcCr, _chromaDivisor, DcChroma, AcChroma);
        }

        // fill the last byte with ones, as the specification asks
        if (_bitCount > 0)
            Bits((1u << (8 - _bitCount)) - 1, 8 - _bitCount);
        Marker(0xD9);
        return _out.AsSpan(0, _length).ToArray();
    }

    private void Tables(int quality)
    {
        if (quality == _quality)
            return;
        _quality = quality;
        var scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        for (var i = 0; i < 64; ++i)
        {
            _lumaTable[i]   = (byte)Math.Clamp((LumaQuant[i] * scale + 50) / 100, 1, 255);
            _chromaTable[i] = (byte)Math.Clamp((ChromaQuant[i] * scale + 50) / 100, 1, 255);
            _lumaDivisor[i]   = 1f / _lumaTable[i];
            _chromaDivisor[i] = 1f / _chromaTable[i];
        }
    }

    private void Header(int width, int height)
    {
        Marker(0xD8);
        // JFIF APP0: version 1.1, no density, no thumbnail
        Marker(0xE0);
        Word(16);
        Byte((byte)'J'); Byte((byte)'F'); Byte((byte)'I'); Byte((byte)'F'); Byte(0);
        Byte(1); Byte(1); Byte(0); Word(1); Word(1); Byte(0); Byte(0);

        // the two quantisation tables, in zigzag order
        Marker(0xDB);
        Word(2 + 2 * 65);
        Byte(0);
        for (var i = 0; i < 64; ++i)
            Byte(_lumaTable[ZigZag[i]]);
        Byte(1);
        for (var i = 0; i < 64; ++i)
            Byte(_chromaTable[ZigZag[i]]);

        // frame: 8 bits, three components, Y sampled 2 × 2, Cb and Cr 1 × 1
        Marker(0xC0);
        Word(17);
        Byte(8);
        Word(height);
        Word(width);
        Byte(3);
        Byte(1); Byte(0x22); Byte(0);
        Byte(2); Byte(0x11); Byte(1);
        Byte(3); Byte(0x11); Byte(1);

        Marker(0xC4);
        Word(2 + 4 * 17 + DcValues.Length * 2 + AcLumaValues.Length + AcChromaValues.Length);
        Table(0x00, DcLumaBits, DcValues);
        Table(0x10, AcLumaBits, AcLumaValues);
        Table(0x01, DcChromaBits, DcValues);
        Table(0x11, AcChromaBits, AcChromaValues);

        Marker(0xDA);
        Word(12);
        Byte(3);
        Byte(1); Byte(0x00);
        Byte(2); Byte(0x11);
        Byte(3); Byte(0x11);
        Byte(0); Byte(63); Byte(0);
    }

    private void Table(byte id, byte[] bits, byte[] values)
    {
        Byte(id);
        foreach (var b in bits)
            Byte(b);
        foreach (var v in values)
            Byte(v);
    }

    /// <summary> DCT, quantisation and Huffman coding of the block in <see cref="_block"/>; returns its DC value. </summary>
    private int Block(int previousDc, float[] divisor, (ushort Code, byte Length)[] dc, (ushort Code, byte Length)[] ac)
    {
        // separable DCT: rows, then columns
        for (var r = 0; r < 8; ++r)
            for (var u = 0; u < 8; ++u)
            {
                float s = 0;
                for (var x = 0; x < 8; ++x)
                    s += _block[r * 8 + x] * Cosines[u * 8 + x];
                _temp[r * 8 + u] = s;
            }

        Span<int> q = stackalloc int[64];
        for (var u = 0; u < 8; ++u)
            for (var v = 0; v < 8; ++v)
            {
                float s = 0;
                for (var x = 0; x < 8; ++x)
                    s += _temp[x * 8 + u] * Cosines[v * 8 + x];
                q[v * 8 + u] = (int)MathF.Round(s * divisor[v * 8 + u]);
            }

        var value = q[0];
        Coded(value - previousDc, dc, 0);
        var zeros = 0;
        for (var i = 1; i < 64; ++i)
        {
            var c = q[ZigZag[i]];
            if (c == 0)
            {
                ++zeros;
                continue;
            }

            while (zeros >= 16)
            {
                Bits(ac[0xF0].Code, ac[0xF0].Length);
                zeros -= 16;
            }

            Coded(c, ac, zeros << 4);
            zeros = 0;
        }

        if (zeros > 0)
            Bits(ac[0x00].Code, ac[0x00].Length); // end of block
        return value;
    }

    /// <summary> A value as its size category's Huffman code (with the run of zeros before it) and then its bits. </summary>
    private void Coded(int value, (ushort Code, byte Length)[] table, int run)
    {
        var magnitude = Math.Abs(value);
        var size = 0;
        while (magnitude >> size != 0)
            ++size;
        var code = table[run | size];
        Bits(code.Code, code.Length);
        if (size > 0)
            Bits((uint)(value < 0 ? value - 1 : value) & ((1u << size) - 1), size);
    }

    private void Bits(uint value, int count)
    {
        _bits = _bits << count | value & ((1u << count) - 1);
        _bitCount += count;
        while (_bitCount >= 8)
        {
            var b = (byte)(_bits >> (_bitCount - 8));
            Byte(b);
            if (b == 0xFF)
                Byte(0); // a 0xFF in the data is followed by 0, so it is not read as a marker
            _bitCount -= 8;
        }

        _bits &= (1u << _bitCount) - 1;
    }

    private void Marker(byte m)
    {
        Byte(0xFF);
        Byte(m);
    }

    private void Word(int v)
    {
        Byte((byte)(v >> 8));
        Byte((byte)v);
    }

    private void Byte(byte b)
    {
        if (_length == _out.Length)
            Array.Resize(ref _out, _out.Length * 2);
        _out[_length++] = b;
    }

    /// <summary> The canonical Huffman codes of a table (by symbol), as the specification builds them from the counts. </summary>
    private static (ushort, byte)[] Codes(byte[] bits, byte[] values)
    {
        var codes = new (ushort, byte)[256];
        var code  = 0;
        var k     = 0;
        for (var length = 1; length <= 16; ++length)
        {
            for (var i = 0; i < bits[length - 1]; ++i)
                codes[values[k++]] = ((ushort)code++, (byte)length);
            code <<= 1;
        }

        return codes;
    }

    private static float[] MakeCosines()
    {
        var c = new float[64];
        for (var u = 0; u < 8; ++u)
            for (var x = 0; x < 8; ++x)
                c[u * 8 + x] = (float)((u == 0 ? Math.Sqrt(0.125) : 0.5) * Math.Cos((2 * x + 1) * u * Math.PI / 16));
        return c;
    }
}
