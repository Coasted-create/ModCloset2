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

    public static void Load(PhotoScene scene, ModSource source, PhotoCamera camera, int width, int height, PhotoPools pools, PhotoBudget budget,
        CancellationToken stop)
    {
        Measure(scene, camera, width, height);
        var cache = new Dictionary<(string, int), TexImage?>();
        foreach (var item in scene.Items)
        {
            stop.ThrowIfCancellationRequested();
            var surface = item.Surface;
            if (surface.Kind is not (SurfaceKind.Table or SurfaceKind.Painted) || surface.Loaded)
                continue;
            surface.Loaded = true;

            // texels per unit of texture coordinates the picture can show
            var density = surface.UvArea > 1e-9 ? Math.Sqrt(surface.ScreenArea / surface.UvArea) : 256;
            surface.Index    = Image(surface.IndexFile, density * (1 << IndexExtraLevels));
            surface.Normal   = Image(surface.NormalFile, density);
            surface.Mask     = Image(surface.MaskFile, density);
            surface.Diffuse  = Image(surface.DiffuseFile, density);
            surface.Specular = Image(surface.SpecularFile, density);
            if (scene.Refused.Length > 0)
                return; // over the budget: the picture is refused, nothing more is read
        }

        TexImage? Image(string? local, double density)
        {
            if (local is null)
                return null;
            var need = (int)Math.Clamp(density, MinSide, budget.MaxTextureSide);
            if (cache.TryGetValue((local, need), out var hit))
                return hit;
            var name = SceneReader.Name(local);
            TexImage? image = null;
            if (scene.Refused.Length > 0)
                return null;
            if (scene.TextureCount >= budget.MaxTextures)
                scene.Refuse(PhotoProblems.TooManyTextures, $"more than {budget.MaxTextures}");
            else if (Read(source, local, need, pools, budget, scene, out var full, out var why) is { } img)
            {
                image = img;
                scene.TextureCount++;
                scene.Texels += (long)img.Width * img.Height;
                scene.Textures.Add($"{name} {img.Width}x{img.Height} (of {full.Width}x{full.Height})");
            }
            else
            {
                scene.Note($"texture not drawn: {name} ({why})");
            }

            return cache[(local, need)] = image;
        }
    }

    /// <summary>
    /// One level of a texture as an image backed by a reused slot, or null with a reason. A folder's file is read only
    /// as far as its header and that level; a pack's file comes whole (a zip entry is inflated whole anyway).
    /// </summary>
    public static TexImage? Read(ModSource source, string local, int need, PhotoPools pools, PhotoBudget budget, PhotoScene? scene,
        out TexDecoder.Header full, out string why)
    {
        full = default;
        why  = string.Empty;
        try
        {
            byte[] head;
            Stream? stream = null;
            byte[]? whole  = null;
            try
            {
                if (source is FolderSource folder)
                {
                    if (folder.FullPath(local) is not { } path)
                    {
                        why = "missing";
                        return null;
                    }

                    stream = new FileStream(path, FileMode.Open, FileAccess.Read, ModSource.Sharing, 1, FileOptions.RandomAccess);
                    head   = new byte[TexDecoder.HeaderSize];
                    stream.ReadExactly(head);
                }
                else
                {
                    whole = source.Read(local, budget.MaxTextureFileBytes);
                    if (whole is null || whole.Length < TexDecoder.HeaderSize)
                    {
                        why = "missing or too big";
                        return null;
                    }

                    head = whole;
                }

                var fileLength = stream?.Length ?? whole!.Length;
                if (TexDecoder.ReadHeader(head) is not { } header)
                {
                    why = "not a texture, or larger than any texture can be";
                    return null;
                }

                full = header;
                var mip = TexDecoder.MipFor(header, need);
                int w = Math.Max(1, header.Width >> mip), h = Math.Max(1, header.Height >> mip);
                if (w > budget.MaxTextureSide || h > budget.MaxTextureSide)
                {
                    why = $"no level small enough ({w}x{h})";
                    return null;
                }

                if (scene is not null && scene.Texels + (long)w * h > budget.MaxTexels)
                {
                    why = "over the picture's texture budget";
                    scene.Refuse(PhotoProblems.TooManyTexels, $"{local}: {w}x{h} after {scene.Texels:N0} texels");
                    return null;
                }

                if (TexDecoder.MipBytes(header.Format, w, h) is not { } size)
                {
                    why = $"texture format 0x{header.Format:X4} not supported";
                    return null;
                }

                if (LevelOffset(header, mip) is not { } offset || offset < TexDecoder.HeaderSize || offset + size > fileLength)
                {
                    why = "cut off";
                    return null;
                }

                var slot = pools.NextSlot(w * h * 4);
                PhotoPools.Ensure(ref slot.Level, (int)size);
                if (stream is not null)
                {
                    stream.Position = offset;
                    stream.ReadExactly(slot.Level, 0, (int)size);
                    pools.BytesRead += TexDecoder.HeaderSize + size;
                }
                else
                {
                    Array.Copy(whole!, offset, slot.Level, 0, size);
                }

                return Image(slot, header.Format, w, h);
            }
            finally
            {
                stream?.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or EndOfStreamException or OverflowException)
        {
            why = "unreadable";
            return null;
        }
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
