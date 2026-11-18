using System.Buffers.Binary;

namespace ModCloset2.Colours;

// Pure code (no game, no Dalamud): linked into tests/ColourTests.

/// <summary>
/// A decoded texture: RGBA bytes, rows top to bottom. A block-compressed texture read with <c>lazy</c> decodes its 4 × 4
/// blocks only when a texel of them is asked for through <see cref="At"/> (a piece rarely uses all of its index map).
/// </summary>
public sealed class TexImage
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Rgba { get; init; } = Array.Empty<byte>();

    internal byte[]? Source;
    internal uint Format;
    internal bool[]? Decoded;

    /// <summary> Where the texel's four bytes are in <see cref="Rgba"/> (its block is decoded first when it was not yet). </summary>
    public int At(int x, int y)
    {
        if (Decoded is not null)
        {
            var bw    = (Width + 3) / 4;
            var block = y / 4 * bw + x / 4;
            if (!Decoded[block])
            {
                TexDecoder.DecodeBlock(this, x / 4, y / 4);
                Decoded[block] = true;
            }
        }

        return (y * Width + x) * 4;
    }

    /// <summary> Blocks decoded so far (all of them for a texture that is not lazy). </summary>
    public int BlocksDecoded
        => Decoded?.Count(d => d) ?? (Width + 3) / 4 * ((Height + 3) / 4);
}

/// <summary>
/// Reads the game's .tex files on the CPU, at full size and without any filtering (new: the old plugin let the GPU
/// convert and scale the index map to 256 × 256, which blends neighbouring row numbers at area edges). Handles the
/// formats gear textures use: plain 8-bit (B8G8R8A8, B8G8R8X8, L8, A8), 16-bit (B4G4R4A4, B5G5R5A1) and block
/// compressed BC1, BC2, BC3, BC4, BC5 and BC7. Never throws; unknown formats give null with a reason.
/// </summary>
public static class TexDecoder
{
    public const int HeaderSize = 80;

    public const uint L8 = 0x1130, A8 = 0x1131, B4G4R4A4 = 0x1440, B5G5R5A1 = 0x1441, B8G8R8A8 = 0x1450, B8G8R8X8 = 0x1451;
    public const uint Bc1 = 0x3420, Bc2 = 0x3430, Bc3 = 0x3431, Bc4 = 0x6120, Bc5 = 0x6230, Bc7 = 0x6432;

    public readonly record struct Header(uint Format, int Width, int Height, int Mips, uint[] Surfaces);

    public static Header? ReadHeader(byte[] file)
    {
        if (file.Length < HeaderSize)
            return null;
        var format = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        int w      = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8));
        int h      = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(10));
        var mips   = Math.Max(1, file[14] & 0x7F);
        var surfaces = new uint[13];
        for (var i = 0; i < 13; ++i)
            surfaces[i] = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(28 + i * 4));
        if (w <= 0 || h <= 0 || w > 16384 || h > 16384)
            return null;
        return new Header(format, w, h, Math.Min(mips, 13), surfaces);
    }

    /// <summary> Bytes of one mip level of a format (null for formats this reader does not know). </summary>
    public static long? MipBytes(uint format, int w, int h)
    {
        long bw = (w + 3) / 4, bh = (h + 3) / 4;
        return format switch
        {
            Bc1 or Bc4              => bw * bh * 8,
            Bc2 or Bc3 or Bc5 or Bc7 => bw * bh * 16,
            B8G8R8A8 or B8G8R8X8    => (long)w * h * 4,
            B4G4R4A4 or B5G5R5A1    => (long)w * h * 2,
            L8 or A8                => (long)w * h,
            _                       => null,
        };
    }

    /// <summary>
    /// The mip level to read: 0 for full size, or the smallest level whose larger side is still at least
    /// <paramref name="minSide"/> (for averages, where a small picture is enough).
    /// </summary>
    public static int MipFor(Header header, int minSide)
    {
        if (minSide <= 0)
            return 0;
        var mip = 0;
        while (mip + 1 < header.Mips && Math.Max(header.Width >> (mip + 1), header.Height >> (mip + 1)) >= minSide)
            ++mip;
        return mip;
    }

    /// <param name="lazy"> Block-compressed textures decode their blocks only when <see cref="TexImage.At"/> asks for them. </param>
    public static TexImage? Decode(byte[] file, out string error, int mip = 0, bool lazy = false)
    {
        error = string.Empty;
        try
        {
            if (ReadHeader(file) is not { } header)
            {
                error = "not a texture";
                return null;
            }

            mip = Math.Clamp(mip, 0, header.Mips - 1);
            int w = Math.Max(1, header.Width >> mip), h = Math.Max(1, header.Height >> mip);
            if (MipBytes(header.Format, w, h) is not { } size)
            {
                error = $"texture format 0x{header.Format:X4} not supported";
                return null;
            }

            long offset = header.Surfaces[mip];
            if (offset == 0 || mip > 0 && offset <= header.Surfaces[0])
            {
                // no offset table entry: the levels follow one another after the header
                offset = header.Surfaces[0] == 0 ? HeaderSize : header.Surfaces[0];
                for (var m = 0; m < mip; ++m)
                    offset += MipBytes(header.Format, Math.Max(1, header.Width >> m), Math.Max(1, header.Height >> m)) ?? 0;
            }

            if (offset < HeaderSize || offset + size > file.Length)
            {
                error = "texture is cut off";
                return null;
            }

            var data = file.AsSpan((int)offset, (int)size);
            var rgba = new byte[w * h * 4];
            if (lazy && header.Format is Bc1 or Bc2 or Bc3 or Bc4 or Bc5 or Bc7)
                return new TexImage
                {
                    Width = w, Height = h, Rgba = rgba, Source = data.ToArray(), Format = header.Format,
                    Decoded = new bool[(w + 3) / 4 * ((h + 3) / 4)],
                };

            switch (header.Format)
            {
                case B8G8R8A8:
                case B8G8R8X8:
                    for (var i = 0; i < w * h; ++i)
                    {
                        rgba[i * 4]     = data[i * 4 + 2];
                        rgba[i * 4 + 1] = data[i * 4 + 1];
                        rgba[i * 4 + 2] = data[i * 4];
                        rgba[i * 4 + 3] = header.Format == B8G8R8X8 ? (byte)255 : data[i * 4 + 3];
                    }

                    break;
                case B4G4R4A4:
                    for (var i = 0; i < w * h; ++i)
                    {
                        var v = BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);
                        rgba[i * 4]     = (byte)((v >> 8 & 0xF) * 17);
                        rgba[i * 4 + 1] = (byte)((v >> 4 & 0xF) * 17);
                        rgba[i * 4 + 2] = (byte)((v & 0xF) * 17);
                        rgba[i * 4 + 3] = (byte)((v >> 12 & 0xF) * 17);
                    }

                    break;
                case B5G5R5A1:
                    for (var i = 0; i < w * h; ++i)
                    {
                        var v = BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);
                        rgba[i * 4]     = Expand5(v >> 10 & 0x1F);
                        rgba[i * 4 + 1] = Expand5(v >> 5 & 0x1F);
                        rgba[i * 4 + 2] = Expand5(v & 0x1F);
                        rgba[i * 4 + 3] = (v & 0x8000) != 0 ? (byte)255 : (byte)0;
                    }

                    break;
                case L8:
                    for (var i = 0; i < w * h; ++i)
                    {
                        rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = data[i];
                        rgba[i * 4 + 3] = 255;
                    }

                    break;
                case A8:
                    for (var i = 0; i < w * h; ++i)
                        rgba[i * 4 + 3] = data[i];
                    break;
                default:
                    DecodeBlocks(header.Format, data, w, h, rgba);
                    break;
            }

            return new TexImage { Width = w, Height = h, Rgba = rgba };
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            error = "texture is damaged";
            return null;
        }
    }

    private static void DecodeBlocks(uint format, ReadOnlySpan<byte> data, int w, int h, byte[] rgba)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        for (var by = 0; by < bh; ++by)
        for (var bx = 0; bx < bw; ++bx)
            DecodeBlock(format, data, w, h, rgba, bx, by);
    }

    internal static void DecodeBlock(TexImage image, int bx, int by)
        => DecodeBlock(image.Format, image.Source, image.Width, image.Height, image.Rgba, bx, by);

    private static void DecodeBlock(uint format, ReadOnlySpan<byte> data, int w, int h, byte[] rgba, int bx, int by)
    {
        var blockBytes = format is Bc1 or Bc4 ? 8 : 16;
        var bw = (w + 3) / 4;
        Span<byte> block = stackalloc byte[64];
        var src = data.Slice((by * bw + bx) * blockBytes, blockBytes);
        switch (format)
        {
            case Bc1:
                Bc1Block(src, block, true);
                break;
            case Bc2:
                Bc1Block(src[8..], block, false);
                for (var i = 0; i < 16; ++i)
                    block[i * 4 + 3] = (byte)((src[i / 2] >> (i % 2 * 4) & 0xF) * 17);
                break;
            case Bc3:
                Bc1Block(src[8..], block, false);
                Bc4Block(src, block, 3);
                break;
            case Bc4:
                Bc4Block(src, block, 0);
                for (var i = 0; i < 16; ++i)
                {
                    block[i * 4 + 1] = block[i * 4 + 2] = block[i * 4];
                    block[i * 4 + 3] = 255;
                }

                break;
            case Bc5:
                Bc4Block(src, block, 0);
                Bc4Block(src[8..], block, 1);
                for (var i = 0; i < 16; ++i)
                {
                    block[i * 4 + 2] = 0;
                    block[i * 4 + 3] = 255;
                }

                break;
            case Bc7:
                Bc7Block(src, block);
                break;
        }

        for (var y = 0; y < 4; ++y)
        {
            var py = by * 4 + y;
            if (py >= h)
                break;
            for (var x = 0; x < 4; ++x)
            {
                var px = bx * 4 + x;
                if (px >= w)
                    break;
                var o = (py * w + px) * 4;
                var b = (y * 4 + x) * 4;
                rgba[o]     = block[b];
                rgba[o + 1] = block[b + 1];
                rgba[o + 2] = block[b + 2];
                rgba[o + 3] = block[b + 3];
            }
        }
    }

    private static byte Expand5(int v)
        => (byte)(v << 3 | v >> 2);

    private static byte Expand6(int v)
        => (byte)(v << 2 | v >> 4);

    // ---- BC1 to BC5 (D3D10 rules) ----

    private static void Bc1Block(ReadOnlySpan<byte> s, Span<byte> o, bool punchThrough)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(s);
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
        var bits = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
        Span<int> p = stackalloc int[16];
        p[0] = Expand5(c0 >> 11); p[1] = Expand6(c0 >> 5 & 0x3F); p[2] = Expand5(c0 & 0x1F); p[3] = 255;
        p[4] = Expand5(c1 >> 11); p[5] = Expand6(c1 >> 5 & 0x3F); p[6] = Expand5(c1 & 0x1F); p[7] = 255;
        if (c0 > c1 || !punchThrough)
        {
            for (var k = 0; k < 3; ++k)
            {
                p[8 + k]  = (2 * p[k] + p[4 + k] + 1) / 3;
                p[12 + k] = (p[k] + 2 * p[4 + k] + 1) / 3;
            }

            p[11] = p[15] = 255;
        }
        else
        {
            for (var k = 0; k < 3; ++k)
            {
                p[8 + k]  = (p[k] + p[4 + k]) / 2;
                p[12 + k] = 0;
            }

            p[11] = 255;
            p[15] = 0;
        }

        for (var i = 0; i < 16; ++i)
        {
            var idx = (int)(bits >> (i * 2) & 3) * 4;
            o[i * 4]     = (byte)p[idx];
            o[i * 4 + 1] = (byte)p[idx + 1];
            o[i * 4 + 2] = (byte)p[idx + 2];
            o[i * 4 + 3] = (byte)p[idx + 3];
        }
    }

    /// <summary> An 8-byte BC4 block into one channel of the 16 pixels. </summary>
    private static void Bc4Block(ReadOnlySpan<byte> s, Span<byte> o, int channel)
    {
        int a0 = s[0], a1 = s[1];
        Span<int> v = stackalloc int[8];
        v[0] = a0;
        v[1] = a1;
        if (a0 > a1)
        {
            for (var i = 1; i < 7; ++i)
                v[i + 1] = ((7 - i) * a0 + i * a1 + 3) / 7;
        }
        else
        {
            for (var i = 1; i < 5; ++i)
                v[i + 1] = ((5 - i) * a0 + i * a1 + 2) / 5;
            v[6] = 0;
            v[7] = 255;
        }

        ulong bits = 0;
        for (var i = 0; i < 6; ++i)
            bits |= (ulong)s[2 + i] << (8 * i);
        for (var i = 0; i < 16; ++i)
            o[i * 4 + channel] = (byte)v[(int)(bits >> (3 * i) & 7)];
    }

    // ---- BC7 (D3D11 rules) ----

    private readonly record struct Bc7Mode(int Subsets, int PartitionBits, int RotationBits, int IndexSelectionBits, int ColourBits, int AlphaBits,
        int EndpointPBits, int SharedPBits, int IndexBits, int Index2Bits);

    private static readonly Bc7Mode[] Modes =
    {
        new(3, 4, 0, 0, 4, 0, 1, 0, 3, 0),
        new(2, 6, 0, 0, 6, 0, 0, 1, 3, 0),
        new(3, 6, 0, 0, 5, 0, 0, 0, 2, 0),
        new(2, 6, 0, 0, 7, 0, 1, 0, 2, 0),
        new(1, 0, 2, 1, 5, 6, 0, 0, 2, 3),
        new(1, 0, 2, 0, 7, 8, 0, 0, 2, 2),
        new(1, 0, 0, 0, 7, 7, 1, 0, 4, 0),
        new(2, 6, 0, 0, 5, 5, 1, 0, 2, 0),
    };

    private static readonly byte[] Weights2 = { 0, 21, 43, 64 };
    private static readonly byte[] Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
    private static readonly byte[] Weights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

    private ref struct Bits
    {
        private readonly ulong _lo;
        private readonly ulong _hi;
        private int _at;

        public Bits(ReadOnlySpan<byte> s)
        {
            _lo = BinaryPrimitives.ReadUInt64LittleEndian(s);
            _hi = BinaryPrimitives.ReadUInt64LittleEndian(s[8..]);
            _at = 0;
        }

        public int Position
            => _at;

        public int Read(int count)
        {
            if (count == 0)
                return 0;
            ulong v;
            if (_at >= 64)
                v = _hi >> (_at - 64);
            else if (_at + count <= 64)
                v = _lo >> _at;
            else
                v = _lo >> _at | _hi << (64 - _at);
            _at += count;
            return (int)(v & ((1UL << count) - 1));
        }
    }

    private static void Bc7Block(ReadOnlySpan<byte> s, Span<byte> o)
    {
        var mode = 0;
        while (mode < 8 && (s[0] >> mode & 1) == 0)
            ++mode;
        if (mode == 8)
        {
            // reserved: transparent black, as the specification says
            o[..64].Clear();
            return;
        }

        var m    = Modes[mode];
        var bits = new Bits(s);
        bits.Read(mode + 1);
        var partition = bits.Read(m.PartitionBits);
        var rotation  = bits.Read(m.RotationBits);
        var selection = bits.Read(m.IndexSelectionBits);

        var endpoints = m.Subsets * 2;
        Span<int> e = stackalloc int[6 * 4]; // endpoint × (r, g, b, a)
        for (var c = 0; c < 3; ++c)
        for (var k = 0; k < endpoints; ++k)
            e[k * 4 + c] = bits.Read(m.ColourBits);
        for (var k = 0; k < endpoints; ++k)
            e[k * 4 + 3] = m.AlphaBits > 0 ? bits.Read(m.AlphaBits) : 255;

        Span<int> p = stackalloc int[6];
        if (m.EndpointPBits > 0)
            for (var k = 0; k < endpoints; ++k)
                p[k] = bits.Read(1);
        else if (m.SharedPBits > 0)
            for (var sub = 0; sub < m.Subsets; ++sub)
                p[sub * 2] = p[sub * 2 + 1] = bits.Read(1);

        var hasP = m.EndpointPBits > 0 || m.SharedPBits > 0;
        for (var k = 0; k < endpoints; ++k)
        {
            for (var c = 0; c < 4; ++c)
            {
                var prec = c < 3 ? m.ColourBits : m.AlphaBits;
                if (prec == 0)
                {
                    e[k * 4 + c] = 255;
                    continue;
                }

                var v = e[k * 4 + c];
                if (hasP)
                {
                    v = v << 1 | p[k];
                    ++prec;
                }

                // expand to 8 bits by repeating the top bits
                v <<= 8 - prec;
                e[k * 4 + c] = v | v >> prec;
            }
        }

        Span<int> subsetOf = stackalloc int[16];
        for (var i = 0; i < 16; ++i)
            subsetOf[i] = m.Subsets switch
            {
                2 => Partition2[partition * 16 + i],
                3 => Partition3[partition * 16 + i],
                _ => 0,
            };

        Span<int> idx  = stackalloc int[16];
        Span<int> idx2 = stackalloc int[16];
        for (var i = 0; i < 16; ++i)
        {
            var anchor = IsAnchor(m.Subsets, partition, i);
            idx[i] = bits.Read(anchor ? m.IndexBits - 1 : m.IndexBits);
        }

        if (m.Index2Bits > 0)
            for (var i = 0; i < 16; ++i)
                idx2[i] = bits.Read(i == 0 ? m.Index2Bits - 1 : m.Index2Bits);

        for (var i = 0; i < 16; ++i)
        {
            var sub = subsetOf[i];
            var e0  = sub * 2 * 4;
            var e1  = e0 + 4;
            int cw, aw;
            if (m.Index2Bits > 0)
            {
                // mode 4 and 5: colour and alpha have their own indices; mode 4 can swap them
                var w1 = Weight(m.IndexBits, idx[i]);
                var w2 = Weight(m.Index2Bits, idx2[i]);
                (cw, aw) = selection == 1 ? (w2, w1) : (w1, w2);
            }
            else
            {
                cw = aw = Weight(m.IndexBits, idx[i]);
            }

            int r = Lerp(e[e0], e[e1], cw), g = Lerp(e[e0 + 1], e[e1 + 1], cw), b = Lerp(e[e0 + 2], e[e1 + 2], cw);
            var a = m.AlphaBits > 0 ? Lerp(e[e0 + 3], e[e1 + 3], aw) : 255;
            switch (rotation)
            {
                case 1: (a, r) = (r, a); break;
                case 2: (a, g) = (g, a); break;
                case 3: (a, b) = (b, a); break;
            }

            o[i * 4]     = (byte)r;
            o[i * 4 + 1] = (byte)g;
            o[i * 4 + 2] = (byte)b;
            o[i * 4 + 3] = (byte)a;
        }
    }

    private static int Weight(int bits, int index)
        => bits switch
        {
            2 => Weights2[index],
            3 => Weights3[index],
            _ => Weights4[index],
        };

    private static int Lerp(int e0, int e1, int w)
        => ((64 - w) * e0 + w * e1 + 32) >> 6;

    private static bool IsAnchor(int subsets, int partition, int i)
        => i == 0
         || subsets == 2 && i == Anchor2[partition]
         || subsets == 3 && (i == Anchor3A[partition] || i == Anchor3B[partition]);

    // the partition and anchor tables of the BC7 specification
    private static readonly byte[] Partition2 =
    {
        0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1, 0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1, 0,1,1,1,0,1,1,1,0,1,1,1,0,1,1,1, 0,0,0,1,0,0,1,1,0,0,1,1,0,1,1,1,
        0,0,0,0,0,0,0,1,0,0,0,1,0,0,1,1, 0,0,1,1,0,1,1,1,0,1,1,1,1,1,1,1, 0,0,0,1,0,0,1,1,0,1,1,1,1,1,1,1, 0,0,0,0,0,0,0,1,0,0,1,1,0,1,1,1,
        0,0,0,0,0,0,0,0,0,0,0,1,0,0,1,1, 0,0,1,1,0,1,1,1,1,1,1,1,1,1,1,1, 0,0,0,0,0,0,0,1,0,1,1,1,1,1,1,1, 0,0,0,0,0,0,0,0,0,0,0,1,0,1,1,1,
        0,0,0,1,0,1,1,1,1,1,1,1,1,1,1,1, 0,0,0,0,0,0,0,0,1,1,1,1,1,1,1,1, 0,0,0,0,1,1,1,1,1,1,1,1,1,1,1,1, 0,0,0,0,0,0,0,0,0,0,0,0,1,1,1,1,
        0,0,0,0,1,0,0,0,1,1,1,0,1,1,1,1, 0,1,1,1,0,0,0,1,0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,1,0,0,0,1,1,1,0, 0,1,1,1,0,0,1,1,0,0,0,1,0,0,0,0,
        0,0,1,1,0,0,0,1,0,0,0,0,0,0,0,0, 0,0,0,0,1,0,0,0,1,1,0,0,1,1,1,0, 0,0,0,0,0,0,0,0,1,0,0,0,1,1,0,0, 0,1,1,1,0,0,1,1,0,0,1,1,0,0,0,1,
        0,0,1,1,0,0,0,1,0,0,0,1,0,0,0,0, 0,0,0,0,1,0,0,0,1,0,0,0,1,1,0,0, 0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,0, 0,0,1,1,0,1,1,0,0,1,1,0,1,1,0,0,
        0,0,0,1,0,1,1,1,1,1,1,0,1,0,0,0, 0,0,0,0,1,1,1,1,1,1,1,1,0,0,0,0, 0,1,1,1,0,0,0,1,1,0,0,0,1,1,1,0, 0,0,1,1,1,0,0,1,1,0,0,1,1,1,0,0,
        0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1, 0,0,0,0,1,1,1,1,0,0,0,0,1,1,1,1, 0,1,0,1,1,0,1,0,0,1,0,1,1,0,1,0, 0,0,1,1,0,0,1,1,1,1,0,0,1,1,0,0,
        0,0,1,1,1,1,0,0,0,0,1,1,1,1,0,0, 0,1,0,1,0,1,0,1,1,0,1,0,1,0,1,0, 0,1,1,0,1,0,0,1,0,1,1,0,1,0,0,1, 0,1,0,1,1,0,1,0,1,0,1,0,0,1,0,1,
        0,1,1,1,0,0,1,1,1,1,0,0,1,1,1,0, 0,0,0,1,0,0,1,1,1,1,0,0,1,0,0,0, 0,0,1,1,0,0,1,0,0,1,0,0,1,1,0,0, 0,0,1,1,1,0,1,1,1,1,0,1,1,1,0,0,
        0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0, 0,0,1,1,1,1,0,0,1,1,0,0,0,0,1,1, 0,1,1,0,0,1,1,0,1,0,0,1,1,0,0,1, 0,0,0,0,0,1,1,0,0,1,1,0,0,0,0,0,
        0,1,0,0,1,1,1,0,0,1,0,0,0,0,0,0, 0,0,1,0,0,1,1,1,0,0,1,0,0,0,0,0, 0,0,0,0,0,0,1,0,0,1,1,1,0,0,1,0, 0,0,0,0,0,1,0,0,1,1,1,0,0,1,0,0,
        0,1,1,0,1,1,0,0,1,0,0,1,0,0,1,1, 0,0,1,1,0,1,1,0,1,1,0,0,1,0,0,1, 0,1,1,0,0,0,1,1,1,0,0,1,1,1,0,0, 0,0,1,1,1,0,0,1,1,1,0,0,0,1,1,0,
        0,1,1,0,1,1,0,0,1,1,0,0,1,0,0,1, 0,1,1,0,0,0,1,1,0,0,1,1,1,0,0,1, 0,1,1,1,1,1,1,0,1,0,0,0,0,0,0,1, 0,0,0,1,1,0,0,0,1,1,1,0,0,1,1,1,
        0,0,0,0,1,1,1,1,0,0,1,1,0,0,1,1, 0,0,1,1,0,0,1,1,1,1,1,1,0,0,0,0, 0,0,1,0,0,0,1,0,1,1,1,0,1,1,1,0, 0,1,0,0,0,1,0,0,0,1,1,1,0,1,1,1,
    };

    private static readonly byte[] Partition3 =
    {
        0,0,1,1,0,0,1,1,0,2,2,1,2,2,2,2, 0,0,0,1,0,0,1,1,2,2,1,1,2,2,2,1, 0,0,0,0,2,0,0,1,2,2,1,1,2,2,1,1, 0,2,2,2,0,0,2,2,0,0,1,1,0,1,1,1,
        0,0,0,0,0,0,0,0,1,1,2,2,1,1,2,2, 0,0,1,1,0,0,1,1,0,0,2,2,0,0,2,2, 0,0,2,2,0,0,2,2,1,1,1,1,1,1,1,1, 0,0,1,1,0,0,1,1,2,2,1,1,2,2,1,1,
        0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2, 0,0,0,0,1,1,1,1,1,1,1,1,2,2,2,2, 0,0,0,0,1,1,1,1,2,2,2,2,2,2,2,2, 0,0,1,2,0,0,1,2,0,0,1,2,0,0,1,2,
        0,1,1,2,0,1,1,2,0,1,1,2,0,1,1,2, 0,1,2,2,0,1,2,2,0,1,2,2,0,1,2,2, 0,0,1,1,0,1,1,2,1,1,2,2,1,2,2,2, 0,0,1,1,2,0,0,1,2,2,0,0,2,2,2,0,
        0,0,0,1,0,0,1,1,0,1,1,2,1,1,2,2, 0,1,1,1,0,0,1,1,2,0,0,1,2,2,0,0, 0,0,0,0,1,1,2,2,1,1,2,2,1,1,2,2, 0,0,2,2,0,0,2,2,0,0,2,2,1,1,1,1,
        0,1,1,1,0,1,1,1,0,2,2,2,0,2,2,2, 0,0,0,1,0,0,0,1,2,2,2,1,2,2,2,1, 0,0,0,0,0,0,1,1,0,1,2,2,0,1,2,2, 0,0,0,0,1,1,0,0,2,2,1,0,2,2,1,0,
        0,1,2,2,0,1,2,2,0,0,1,1,0,0,0,0, 0,0,1,2,0,0,1,2,1,1,2,2,2,2,2,2, 0,1,1,0,1,2,2,1,1,2,2,1,0,1,1,0, 0,0,0,0,0,1,1,0,1,2,2,1,1,2,2,1,
        0,0,2,2,1,1,0,2,1,1,0,2,0,0,2,2, 0,1,1,0,0,1,1,0,2,0,0,2,2,2,2,2, 0,0,1,1,0,1,2,2,0,1,2,2,0,0,1,1, 0,0,0,0,2,0,0,0,2,2,1,1,2,2,2,1,
        0,0,0,0,0,0,0,2,1,1,2,2,1,2,2,2, 0,2,2,2,0,0,2,2,0,0,1,2,0,0,1,1, 0,0,1,1,0,0,1,2,0,0,2,2,0,2,2,2, 0,1,2,0,0,1,2,0,0,1,2,0,0,1,2,0,
        0,0,0,0,1,1,1,1,2,2,2,2,0,0,0,0, 0,1,2,0,1,2,0,1,2,0,1,2,0,1,2,0, 0,1,2,0,2,0,1,2,1,2,0,1,0,1,2,0, 0,0,1,1,2,2,0,0,1,1,2,2,0,0,1,1,
        0,0,1,1,1,1,2,2,2,2,0,0,0,0,1,1, 0,1,0,1,0,1,0,1,2,2,2,2,2,2,2,2, 0,0,0,0,0,0,0,0,2,1,2,1,2,1,2,1, 0,0,2,2,1,1,2,2,0,0,2,2,1,1,2,2,
        0,0,2,2,0,0,1,1,0,0,2,2,0,0,1,1, 0,2,2,0,1,2,2,1,0,2,2,0,1,2,2,1, 0,1,0,1,2,2,2,2,2,2,2,2,0,1,0,1, 0,0,0,0,2,1,2,1,2,1,2,1,2,1,2,1,
        0,1,0,1,0,1,0,1,0,1,0,1,2,2,2,2, 0,2,2,2,0,1,1,1,0,2,2,2,0,1,1,1, 0,0,0,2,1,1,1,2,0,0,0,2,1,1,1,2, 0,0,0,0,2,1,1,2,2,1,1,2,2,1,1,2,
        0,2,2,2,0,1,1,1,0,1,1,1,0,2,2,2, 0,0,0,2,1,1,1,2,1,1,1,2,0,0,0,2, 0,1,1,0,0,1,1,0,0,1,1,0,2,2,2,2, 0,0,0,0,0,0,0,0,2,1,1,2,2,1,1,2,
        0,1,1,0,0,1,1,0,2,2,2,2,2,2,2,2, 0,0,2,2,0,0,1,1,0,0,1,1,0,0,2,2, 0,0,2,2,1,1,2,2,1,1,2,2,0,0,2,2, 0,0,0,0,0,0,0,0,0,0,0,0,2,1,1,2,
        0,0,0,2,0,0,0,1,0,0,0,2,0,0,0,1, 0,2,2,2,1,2,2,2,0,2,2,2,1,2,2,2, 0,1,0,1,2,2,2,2,2,2,2,2,2,2,2,2, 0,1,1,1,2,0,1,1,2,2,0,1,2,2,2,0,
    };

    private static readonly byte[] Anchor2 =
    {
        15,15,15,15,15,15,15,15, 15,15,15,15,15,15,15,15, 15, 2, 8, 2, 2, 8, 8,15,  2, 8, 2, 2, 8, 8, 2, 2,
        15,15, 6, 8, 2, 8,15,15,  2, 8, 2, 2, 2,15,15, 6,  6, 2, 6, 8,15,15, 2, 2, 15,15,15,15,15, 2, 2,15,
    };

    private static readonly byte[] Anchor3A =
    {
         3, 3,15,15, 8, 3,15,15,  8, 8, 6, 6, 6, 5, 3, 3,  3, 3, 8,15, 3, 3, 6,10,  5, 8, 8, 6, 8, 5,15,15,
         8,15, 3, 5, 6,10, 8,15, 15, 3,15, 5,15,15,15,15,  3,15, 5, 5, 5, 8, 5,10,  5,10, 8,13,15,12, 3, 3,
    };

    private static readonly byte[] Anchor3B =
    {
        15, 8, 8, 3,15,15, 3, 8, 15,15,15,15,15,15,15, 8, 15, 8,15, 3,15, 8,15, 8,  3,15, 6,10,15,15,10, 8,
        15, 3,15,10,10, 8, 9,10,  6,15, 8,15, 3, 6, 6, 8, 15, 3,15,15,15,15,15,15, 15,15,15,15, 3,15,15, 8,
    };
}
