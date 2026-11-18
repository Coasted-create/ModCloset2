using System.Buffers.Binary;
using System.Numerics;
using ModCloset2.Colours;
using ModCloset2.Detect;

namespace ModCloset2.Photo;

// Pure code (no game, no Dalamud): linked into tests/PhotoTests and tools/photo.

/// <summary>
/// Reads each surface's textures at the smallest mip level that still gives the picture about one texel per pixel. A
/// card is a few hundred pixels; a top's 2048 texture spreads over its front, back and sleeves, so the level of 256 or
/// 512 is usually enough. How many texels are needed is measured: the surface's triangles inside the picture, their
/// area there against their area in the texture.
/// <para>
/// In a mod folder (how Penumbra keeps its mods) only the header and that one level are read. The level goes into a
/// reused buffer and is decoded lazily, 4 × 4 block by block, only where the picture samples it, into reused pixels.
/// Everything a texture's header says is checked: a known format, a level inside the file, a size within the budget.
/// </para>
/// </summary>
public static class TextureLoader
{
    /// <summary> Never smaller than this (very small levels blur rows and stitching together). </summary>
    public const int MinSide = 64;

    /// <summary>
    /// Index maps are read one level finer than colours: a smaller level averages neighbouring row numbers at the
    /// borders of two areas, which draws a seam of a third colour.
    /// </summary>
    public const int IndexExtraLevels = 1;

    /// <summary> The coarsest a texture is made to fit the budget (per side), before the picture is refused. </summary>
    public const int FloorSide = 32;

    /// <summary> One texture a picture wants, at the level its surfaces need (one per file and need, as 0.5 read them). </summary>
    private sealed class Want
    {
        public FileRef File;
        public int Need;
        public TexDecoder.Header Header;
        public long Length;
        public byte[]? Whole;

        /// <summary>
        /// A game texture: its header and its levels from the finest this picture wants on (the file itself is not held;
        /// the budget only ever makes levels coarser, which lie further on).
        /// </summary>
        public GameCache.TextureTail? Tail;
        public int Mip;

        /// <summary>
        /// Halvings beyond the file's last level, for a file without levels small enough (made without mipmaps): every
        /// 2^n-th block (or texel) of its last level, read straight from the file. Only when the budget needs it.
        /// </summary>
        public int Skip;
        public double Priority;
        public TexImage? Image;
        public string Why = string.Empty;
        public bool Kept = true;

        public int Width
            => Math.Max(1, Math.Max(1, Header.Width >> Mip) >> Skip);

        public int Height
            => Math.Max(1, Math.Max(1, Header.Height >> Mip) >> Skip);

        public long Texels
            => (long)Width * Height;

        /// <summary> At the file's last level, and still large enough to halve by skipping. </summary>
        public bool CanSkip
            => Mip + 1 >= Header.Mips && Math.Max(Width, Height) > FloorSide;
    }

    /// <summary>
    /// Reads the textures of every surface. First what each wants (the level its surfaces need on screen), then the
    /// budget: more textures than <see cref="PhotoBudget.MaxTextures"/> keep those of the largest surfaces (the rest
    /// are drawn without); more texels than <see cref="PhotoBudget.MaxTexels"/> make the largest texture one level
    /// coarser, again and again, down to <see cref="FloorSide"/>. Only a picture that does not fit even then is
    /// refused. Then the levels are read, each into a reused slot.
    /// </summary>
    public static void Load(PhotoScene scene, PhotoFiles files, PhotoCamera camera, int width, int height, PhotoPools pools, PhotoBudget budget,
        CancellationToken stop)
    {
        Measure(scene, camera, width, height);
        var wants = new List<Want>();
        var byKey = new Dictionary<(string, string?, int, int), Want>();
        var asks  = new List<(PhotoSurface Surface, int Channel, Want Want)>();
        Want? Ask(FileRef? file, double density, double priority)
        {
            if (file is not { } f)
                return null;
            var need = (int)Math.Clamp(density, MinSide, budget.MaxTextureSide);
            if (!byKey.TryGetValue((f.GamePath, f.Local, f.Layer, need), out var w))
            {
                byKey[(f.GamePath, f.Local, f.Layer, need)] = w = new Want { File = f, Need = need };
                wants.Add(w);
            }

            w.Priority = Math.Max(w.Priority, priority);
            return w;
        }

        foreach (var item in scene.Items)
        {
            var surface = item.Surface;
            if (surface.Kind is not (SurfaceKind.Table or SurfaceKind.Painted or SurfaceKind.Hair or SurfaceKind.Iris or SurfaceKind.SkinTextured) || surface.Loaded)
                continue;
            surface.Loaded = true;

            // texels per unit of texture coordinates the picture can show
            var density = surface.UvArea > 1e-9 ? Math.Sqrt(surface.ScreenArea / surface.UvArea) : 256;
            var p = surface.ScreenArea;
            if (Ask(surface.IndexFile, density * (1 << IndexExtraLevels), p) is { } a0) asks.Add((surface, 0, a0));
            if (Ask(surface.NormalFile, density, p) is { } a1) asks.Add((surface, 1, a1));
            if (Ask(surface.MaskFile, density, p) is { } a2) asks.Add((surface, 2, a2));
            if (Ask(surface.DiffuseFile, density, p) is { } a3) asks.Add((surface, 3, a3));
            if (Ask(surface.SpecularFile, density, p) is { } a4) asks.Add((surface, 4, a4));
        }

        // a face paint lies over the whole face: read at a fixed, moderate level
        var decal = Ask(scene.Decal, 512, double.MaxValue);

        // ---- the count first, before any file is opened: the largest surfaces keep their textures
        var byPriority = wants.OrderByDescending(w => w.Priority).ToList();
        if (byPriority.Count > budget.MaxTextures)
        {
            foreach (var w in byPriority.Skip(budget.MaxTextures))
            {
                w.Kept = false;
                w.Why  = "more textures than a picture takes: left out";
            }

            scene.Note($"more than {budget.MaxTextures} textures: the smallest surfaces are drawn without some");
        }

        // ---- what each file is, largest surfaces first (a game file wanted at two levels is read once, at the finer)
        var tails  = new Dictionary<string, GameCache.TextureTail>(StringComparer.Ordinal);
        var finest = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in byPriority.Where(w => w.Kept && w.File.FromGame))
            finest[w.File.GamePath] = Math.Max(finest.GetValueOrDefault(w.File.GamePath), w.Need);
        foreach (var w in byPriority.Where(w => w.Kept))
        {
            stop.ThrowIfCancellationRequested();
            if (Header(files, w, budget, tails, finest) is { } header)
            {
                w.Header = header;
                w.Mip    = TexDecoder.MipFor(header, w.Need);
                while (w.Mip + 1 < header.Mips && Math.Max(header.Width >> w.Mip, header.Height >> w.Mip) > budget.MaxTextureSide)
                    ++w.Mip; // no picture needs more than the largest level allowed
            }
            else
            {
                w.Kept = false;
            }
        }

        if (byPriority.Any(w => w.Why == GameBytesSpent))
            scene.Note("the game's files this picture may read are read: the smallest surfaces are drawn without some");

        // ---- the texel budget: coarser levels before any refusal
        var kept = byPriority.Where(w => w.Kept).ToList();
        var total = kept.Sum(w => w.Texels);
        var coarsened = 0;
        var skipped = 0;
        var dropped = 0;
        while (total > budget.MaxTexels)
        {
            // the file's own coarser levels first (the pictures of 0.6.0 that fitted are made exactly as then)
            var largest = kept.Where(w => w.Mip + 1 < w.Header.Mips && Math.Max(w.Header.Width >> w.Mip, w.Header.Height >> w.Mip) > FloorSide)
                .OrderByDescending(w => w.Texels).FirstOrDefault();
            if (largest is not null)
            {
                total -= largest.Texels;
                ++largest.Mip;
                total += largest.Texels;
                ++coarsened;
                continue;
            }

            // then files without levels small enough (made without mipmaps: a 4K hairstyle): skipped down to the floor
            largest = kept.Where(w => w.CanSkip).OrderByDescending(w => w.Texels).FirstOrDefault();
            if (largest is not null)
            {
                total -= largest.Texels;
                ++largest.Skip;
                total += largest.Texels;
                ++skipped;
                continue;
            }

            // nothing left to make coarser: the smallest surface goes without its texture, never the whole picture
            var least = kept[^1];
            least.Kept = false;
            least.Why  = "left out to fit the budget";
            kept.RemoveAt(kept.Count - 1);
            total -= least.Texels;
            ++dropped;
        }

        if (skipped > 0)
        {
            // a picture that needed skipping reads no file at a level larger than allowed either
            foreach (var w in kept.Where(w => w.CanSkip || w.Skip > 0))
                while (Math.Max(w.Width, w.Height) > budget.MaxTextureSide && w.CanSkip)
                    ++w.Skip;
        }

        if (coarsened > 0)
            scene.Note($"textures made coarser to fit the budget ({coarsened} steps)");
        if (skipped > 0)
            scene.Note($"textures without small levels read sparsely to fit the budget ({skipped} steps)");
        if (dropped > 0)
            scene.Note($"{dropped} textures left out to fit the budget");

        // ---- read the levels
        foreach (var w in kept)
        {
            stop.ThrowIfCancellationRequested();
            var name = SceneReader.Name(w.File.Local ?? w.File.GamePath);
            w.Image = ReadLevel(files, w, pools, budget, out var why);
            if (w.Image is { } img)
            {
                scene.TextureCount++;
                scene.Texels += (long)img.Width * img.Height;
                scene.Textures.Add($"{name} {img.Width}x{img.Height} (of {w.Header.Width}x{w.Header.Height}){(w.File.FromGame ? " (the game's)" : "")}");
            }
            else
            {
                w.Why = why;
            }
        }

        foreach (var w in wants.Where(w => w.Image is null))
            scene.Note($"texture not drawn: {SceneReader.Name(w.File.Local ?? w.File.GamePath)} ({(w.Why.Length > 0 ? w.Why : "unreadable")})");

        foreach (var (surface, channel, w) in asks)
            switch (channel)
            {
                case 0: surface.Index = w.Image; break;
                case 1: surface.Normal = w.Image; break;
                case 2: surface.Mask = w.Image; break;
                case 3: surface.Diffuse = w.Image; break;
                default: surface.Specular = w.Image; break;
            }

        scene.DecalImage = decal?.Image;
    }

    /// <summary> A texture's header (and for a pack or the game, the whole file), checked; null with the reason kept. </summary>
    private const string GameBytesSpent = "the game's files for one picture are read: left out";

    private static TexDecoder.Header? Header(PhotoFiles files, Want w, PhotoBudget budget, Dictionary<string, GameCache.TextureTail>? tails = null,
        Dictionary<string, int>? finest = null)
    {
        try
        {
            byte[]? head;
            if (w.File.FromGame)
            {
                if (GameTexture(files, w.File.GamePath, finest?.GetValueOrDefault(w.File.GamePath) ?? w.Need, budget, tails, out var why) is not { } tail)
                {
                    w.Why = why;
                    return null;
                }

                w.Tail   = tail;
                head     = tail.Head;
                w.Length = tail.Length;
            }
            else if (w.File.Local is { } local && files.SourceOf(w.File) is FolderSource folder)
            {
                if (folder.FullPath(local) is not { } path)
                {
                    w.Why = "missing";
                    return null;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, ModSource.Sharing, 1, FileOptions.RandomAccess);
                head = new byte[TexDecoder.HeaderSize];
                stream.ReadExactly(head);
                w.Length = stream.Length;
            }
            else
            {
                w.Whole = w.File.Local is { } l ? files.SourceOf(w.File).Read(l, budget.MaxTextureFileBytes) : null;
                if (w.Whole is null || w.Whole.Length < TexDecoder.HeaderSize || w.Whole.Length > budget.MaxTextureFileBytes)
                {
                    w.Why = "missing or too big";
                    return null;
                }

                head     = w.Whole;
                w.Length = w.Whole.Length;
            }

            if (TexDecoder.ReadHeader(head) is not { } header)
            {
                w.Why = "not a texture, or larger than any texture can be";
                return null;
            }

            if (TexDecoder.MipBytes(header.Format, 1, 1) is null)
            {
                w.Why = $"texture format 0x{header.Format:X4} not supported";
                return null;
            }

            return header;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or EndOfStreamException or OverflowException)
        {
            w.Why = "unreadable";
            return null;
        }
    }

    /// <summary>
    /// A game texture as far as a picture needs it: its header and its levels from the one for <paramref name="need"/>
    /// on. From this picture's earlier want of it, else the game's cache when it kept that much, else the file read
    /// again (within the picture's game bytes), trimmed, and offered to the cache. Only ".tex" paths are asked.
    /// </summary>
    private static GameCache.TextureTail? GameTexture(PhotoFiles files, string gamePath, int need, PhotoBudget budget,
        Dictionary<string, GameCache.TextureTail>? tails, out string why)
    {
        why = string.Empty;
        if (files.Game is not { } game || !PhotoFiles.IsGameKind(gamePath, ".tex"))
        {
            why = "missing";
            return null;
        }

        long? Wanted(byte[] head)
        {
            if (TexDecoder.ReadHeader(head) is not { } h || TexDecoder.MipBytes(h.Format, 1, 1) is null)
                return null;
            var mip = TexDecoder.MipFor(h, need);
            while (mip + 1 < h.Mips && Math.Max(h.Width >> mip, h.Height >> mip) > budget.MaxTextureSide)
                ++mip;
            return LevelOffset(h, mip);
        }

        if (tails?.GetValueOrDefault(gamePath) is { } mine)
            return mine;
        if (game.Tail(gamePath) is { } kept && Wanted(kept.Head) is { } from && from >= kept.Start)
            return tails is null ? kept : tails[gamePath] = kept;
        if (!files.MayReadGame)
        {
            why = GameBytesSpent;
            return null;
        }

        var whole = files.GameReadOnce(gamePath);
        if (whole is null || whole.Length < TexDecoder.HeaderSize || whole.Length > budget.MaxTextureFileBytes)
        {
            why = "missing or too big";
            return null;
        }

        var head = whole[..TexDecoder.HeaderSize];
        if (Wanted(head) is not { } start)
        {
            why = "not a texture, or one of a format not supported";
            return null;
        }

        if (start < TexDecoder.HeaderSize || start >= whole.Length)
        {
            why = "cut off";
            return null;
        }

        var tail = new GameCache.TextureTail(head, whole.Length, start, whole[(int)start..]);
        game.KeepTail(gamePath, tail);
        if (tails is not null)
            tails[gamePath] = tail;
        return tail;
    }

    /// <summary> The chosen level of a wanted texture into a reused slot; null with a reason. </summary>
    private static TexImage? ReadLevel(PhotoFiles files, Want want, PhotoPools pools, PhotoBudget budget, out string why)
    {
        why = string.Empty;
        var header = want.Header;
        var mip = want.Mip;
        int w = want.Width, h = want.Height;
        if (w > budget.MaxTextureSide || h > budget.MaxTextureSide)
        {
            why = $"no level small enough ({w}x{h})";
            return null;
        }

        if (want.Skip > 0)
            return ReadSparse(files, want, pools, out why);

        if (TexDecoder.MipBytes(header.Format, w, h) is not { } size || LevelOffset(header, mip) is not { } offset
            || offset < TexDecoder.HeaderSize || offset + size > want.Length)
        {
            why = "cut off";
            return null;
        }

        try
        {
            var slot = pools.NextSlot(w * h * 4);
            PhotoPools.Ensure(ref slot.Level, (int)size);
            if (want.Whole is { } whole)
            {
                Array.Copy(whole, offset, slot.Level, 0, size);
            }
            else if (want.Tail is { } kept)
            {
                if (offset < kept.Start || offset - kept.Start + size > kept.Bytes.Length)
                {
                    why = "missing";
                    return null;
                }

                Array.Copy(kept.Bytes, offset - kept.Start, slot.Level, 0, size);
            }
            else
            {
                if (want.File.Local is not { } local || files.SourceOf(want.File) is not FolderSource folder || folder.FullPath(local) is not { } path)
                {
                    why = "missing";
                    return null;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, ModSource.Sharing, 1, FileOptions.RandomAccess);
                if (offset + size > stream.Length)
                {
                    why = "cut off";
                    return null;
                }

                stream.Position = offset;
                stream.ReadExactly(slot.Level, 0, (int)size);
                pools.BytesRead += TexDecoder.HeaderSize + size;
            }

            return Image(slot, header.Format, w, h);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or EndOfStreamException or OverflowException)
        {
            why = "unreadable";
            return null;
        }
    }

    /// <summary>
    /// A file's last level read sparsely (<see cref="Want.Skip"/>): every 2^n-th block of every 2^n-th row of blocks
    /// (texels, for the plain formats), so a level 2^n times smaller is made without reading or decoding the rest. Rows
    /// are read one at a time; nothing the size of the whole level is allocated.
    /// </summary>
    private static TexImage? ReadSparse(PhotoFiles files, Want want, PhotoPools pools, out string why)
    {
        why = string.Empty;
        var header = want.Header;
        var format = header.Format;
        int sw = Math.Max(1, header.Width >> want.Mip), sh = Math.Max(1, header.Height >> want.Mip);
        int w = want.Width, h = want.Height, step = 1 << want.Skip;
        var block = format is TexDecoder.Bc1 or TexDecoder.Bc2 or TexDecoder.Bc3 or TexDecoder.Bc4 or TexDecoder.Bc5 or TexDecoder.Bc7;
        if (TexDecoder.MipBytes(format, sw, sh) is not { } full || LevelOffset(header, want.Mip) is not { } offset
            || offset < TexDecoder.HeaderSize || offset + full > want.Length || TexDecoder.MipBytes(format, w, h) is not { } size
            || TexDecoder.MipBytes(format, block ? 4 : 1, block ? 4 : 1) is not { } unit)
        {
            why = "cut off";
            return null;
        }

        // in blocks of 4 x 4 (one texel for the plain formats)
        int cell = block ? 4 : 1, sCols = (sw + cell - 1) / cell, sRows = (sh + cell - 1) / cell, oCols = (w + cell - 1) / cell, oRows = (h + cell - 1) / cell;
        var rowBytes = (int)(sCols * unit);
        FileStream? stream = null;
        try
        {
            if (want.Whole is null && want.Tail is null)
            {
                if (want.File.Local is not { } local || files.SourceOf(want.File) is not FolderSource folder || folder.FullPath(local) is not { } path)
                {
                    why = "missing";
                    return null;
                }

                stream = new FileStream(path, FileMode.Open, FileAccess.Read, ModSource.Sharing, 1, FileOptions.RandomAccess);
                if (offset + full > stream.Length)
                {
                    why = "cut off";
                    return null;
                }
            }

            var slot = pools.NextSlot(w * h * 4);
            PhotoPools.Ensure(ref slot.Level, (int)size);
            var row = System.Buffers.ArrayPool<byte>.Shared.Rent(rowBytes);
            try
            {
                for (var oy = 0; oy < oRows; ++oy)
                {
                    var at = offset + (long)Math.Min(oy * step, sRows - 1) * rowBytes;
                    if (want.Whole is { } whole)
                    {
                        Array.Copy(whole, at, row, 0, rowBytes);
                    }
                    else if (want.Tail is { } tail)
                    {
                        if (at < tail.Start || at - tail.Start + rowBytes > tail.Bytes.Length)
                        {
                            why = "missing";
                            return null;
                        }

                        Array.Copy(tail.Bytes, at - tail.Start, row, 0, rowBytes);
                    }
                    else
                    {
                        stream!.Position = at;
                        stream.ReadExactly(row, 0, rowBytes);
                        pools.BytesRead += rowBytes;
                    }

                    for (var ox = 0; ox < oCols; ++ox)
                        Array.Copy(row, Math.Min(ox * step, sCols - 1) * unit, slot.Level, (oy * oCols + ox) * unit, unit);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(row);
            }

            return Image(slot, format, w, h);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or EndOfStreamException or OverflowException)
        {
            why = "unreadable";
            return null;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>
    /// One level of a texture, for a caller outside a picture (the flat picture, the tests): the level for
    /// <paramref name="need"/>, coarser when it is larger than the budget allows.
    /// </summary>
    public static TexImage? Read(PhotoFiles files, FileRef file, int need, PhotoPools pools, PhotoBudget budget, out TexDecoder.Header full, out string why)
    {
        var want = new Want { File = file, Need = need };
        full = default;
        if (Header(files, want, budget) is not { } header)
        {
            why = want.Why;
            return null;
        }

        full = header;
        want.Header = header;
        want.Mip = TexDecoder.MipFor(header, need);
        while (want.Mip + 1 < header.Mips && Math.Max(header.Width >> want.Mip, header.Height >> want.Mip) > budget.MaxTextureSide)
            ++want.Mip;
        while (Math.Max(want.Width, want.Height) > budget.MaxTextureSide && want.CanSkip)
            ++want.Skip; // a file without small levels: read sparsely rather than not at all
        return ReadLevel(files, want, pools, budget, out why);
    }

    /// <summary>
    /// The level in the slot as an image. Block-compressed levels decode lazily (the decoder's own blocks, decoded when a
    /// texel of them is first sampled); the few plain formats are converted at once.
    /// </summary>
    private static TexImage? Image(PhotoPools.TextureSlot slot, uint format, int w, int h)
    {
        PhotoPools.Ensure(ref slot.Rgba, w * h * 4);
        if (format is TexDecoder.Bc1 or TexDecoder.Bc2 or TexDecoder.Bc3 or TexDecoder.Bc4 or TexDecoder.Bc5 or TexDecoder.Bc7)
        {
            var blocks = (w + 3) / 4 * ((h + 3) / 4);
            PhotoPools.Ensure(ref slot.Decoded, blocks);
            Array.Clear(slot.Decoded, 0, blocks);
            return new TexImage { Width = w, Height = h, Rgba = slot.Rgba, Source = slot.Level, Format = format, Decoded = slot.Decoded };
        }

        var src = slot.Level;
        var rgba = slot.Rgba;
        for (var i = 0; i < w * h; ++i)
        {
            var o = i * 4;
            switch (format)
            {
                case TexDecoder.B8G8R8A8:
                case TexDecoder.B8G8R8X8:
                    rgba[o]     = src[o + 2];
                    rgba[o + 1] = src[o + 1];
                    rgba[o + 2] = src[o];
                    rgba[o + 3] = format == TexDecoder.B8G8R8X8 ? (byte)255 : src[o + 3];
                    break;
                case TexDecoder.B4G4R4A4:
                {
                    var v = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(i * 2));
                    rgba[o]     = (byte)((v >> 8 & 0xF) * 17);
                    rgba[o + 1] = (byte)((v >> 4 & 0xF) * 17);
                    rgba[o + 2] = (byte)((v & 0xF) * 17);
                    rgba[o + 3] = (byte)((v >> 12 & 0xF) * 17);
                    break;
                }
                case TexDecoder.B5G5R5A1:
                {
                    var v = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(i * 2));
                    rgba[o]     = (byte)((v >> 10 & 0x1F) * 255 / 31);
                    rgba[o + 1] = (byte)((v >> 5 & 0x1F) * 255 / 31);
                    rgba[o + 2] = (byte)((v & 0x1F) * 255 / 31);
                    rgba[o + 3] = (v & 0x8000) != 0 ? (byte)255 : (byte)0;
                    break;
                }
                case TexDecoder.L8:
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = src[i];
                    rgba[o + 3] = 255;
                    break;
                case TexDecoder.A8:
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = 0;
                    rgba[o + 3] = src[i];
                    break;
                default:
                    return null;
            }
        }

        return new TexImage { Width = w, Height = h, Rgba = rgba };
    }

    /// <summary> Where a mip level starts, as <see cref="TexDecoder.Decode"/> finds it (the offset table, else levels in a row). </summary>
    private static long? LevelOffset(TexDecoder.Header header, int mip)
    {
        long offset = header.Surfaces[mip];
        if (offset != 0 && !(mip > 0 && offset <= header.Surfaces[0]))
            return offset;
        offset = header.Surfaces[0] == 0 ? TexDecoder.HeaderSize : header.Surfaces[0];
        for (var m = 0; m < mip; ++m)
        {
            if (TexDecoder.MipBytes(header.Format, Math.Max(1, header.Width >> m), Math.Max(1, header.Height >> m)) is not { } bytes)
                return null;
            offset += bytes;
        }

        return offset;
    }

    /// <summary> Each surface's area in the picture (output pixels) and in its texture, over the triangles inside the picture. </summary>
    public static void Measure(PhotoScene scene, PhotoCamera camera, int width, int height)
    {
        foreach (var item in scene.Items)
        {
            var mesh = item.Mesh;
            if (!mesh.HasUvs)
                continue;
            var p   = mesh.Positions;
            var uv  = mesh.Uvs;
            var idx = mesh.Indices;
            for (var t = mesh.IndexStart; t + 2 < mesh.IndexEnd; t += 3)
            {
                Vector3 a = camera.ToView(p[idx[t]]), b = camera.ToView(p[idx[t + 1]]), c = camera.ToView(p[idx[t + 2]]);
                if (a.Z <= camera.Near || b.Z <= camera.Near || c.Z <= camera.Near)
                    continue;
                Vector2 sa = camera.ToPicture(a), sb = camera.ToPicture(b), sc = camera.ToPicture(c);
                if (Math.Max(sa.X, Math.Max(sb.X, sc.X)) < 0 || Math.Min(sa.X, Math.Min(sb.X, sc.X)) > 1
                 || Math.Max(sa.Y, Math.Max(sb.Y, sc.Y)) < 0 || Math.Min(sa.Y, Math.Min(sb.Y, sc.Y)) > 1)
                    continue;
                var screen = Math.Abs(Cross(sb - sa, sc - sa)) / 2 * width * height;
                var tex    = Math.Abs(Cross(uv[idx[t + 1]] - uv[idx[t]], uv[idx[t + 2]] - uv[idx[t]])) / 2;
                if (!double.IsFinite(screen) || !double.IsFinite(tex))
                    continue;
                item.Surface.ScreenArea += screen;
                item.Surface.UvArea     += tex;
            }
        }
    }

    private static double Cross(Vector2 a, Vector2 b)
        => a.X * (double)b.Y - a.Y * (double)b.X;
}
