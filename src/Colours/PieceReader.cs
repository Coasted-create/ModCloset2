namespace ModCloset2.Colours;

// Pure code (no game, no Dalamud): linked into tests/ColourTests.

/// <summary> A material the game uses for a drawn model: its file and the files of its textures (in the material's order). </summary>
public sealed record LiveMaterial(string Path, string[] Textures);

/// <summary>
/// A model the game draws in one gear slot of the player right now: the file it was loaded from, which of its parts are
/// switched on (attributes), which shape keys are on, and the materials it really uses (ported from the old LiveModels).
/// </summary>
/// <param name="Slot"> Gear slot as Glamourer counts them: 0 head, 1 body, 2 hands, 3 legs, 4 feet, 5 ears, 6 neck, 7 wrists, 8 right ring, 9 left ring. </param>
public sealed record LiveModel(int Slot, string Path, uint Attributes, uint Shapes, LiveMaterial?[] Materials)
{
    /// <summary> Everything the reading depends on, as one string: equal signatures read the same. </summary>
    public string Signature
        => $"{Slot}:{Path}:{Attributes:X}:{Shapes:X}:" + string.Join(',', Materials.Select(m => m is null ? "-" : m.Path + ";" + string.Join(';', m.Textures)));
}

/// <summary> Reads files by the path the game loaded them from: a file on disk (a mod's file) or a game path. Thread-safe. </summary>
public interface IFileSource
{
    /// <summary> The bytes, or null when the file is missing or unreadable. Never throws. </summary>
    byte[]? Read(string path);
}

/// <summary> One colour table row of a worn piece, with how much of the piece it covers. </summary>
public sealed class RowInfo
{
    /// <summary> "material index:material file:row" (no slot, so a pair of rings shares its rows). Stable across sessions. </summary>
    public string Id { get; init; } = string.Empty;
    public int Material { get; init; }
    public int Row { get; init; }

    /// <summary> "material index:material file", the row's material. </summary>
    public string MaterialKey
        => Id[..Math.Max(0, Id.LastIndexOf(':'))];

    /// <summary> Gear slots this row shows in (two for a pair of rings with the same material). </summary>
    public List<int> Slots { get; init; } = new();

    /// <summary> The row's 32 table values (colours squared, as the game keeps them). </summary>
    public float[] Values { get; init; } = new float[MaterialFile.HalvesPerRow];

    /// <summary>
    /// Surface the row covers, in model units squared (all slots together). Where the index map blends the two rows of a
    /// pair (green between 0 and 255), each row counts for its part of the blend, as the shader mixes them.
    /// </summary>
    public double Weight { get; set; }

    /// <summary>
    /// Surface where this row and the other row of its pair are mixed by the index map (a gradient between the two). Rows
    /// mixed over much of their surface look like one part of the piece and are grouped into one area.
    /// </summary>
    public double Blended { get; set; }
    public int DyeChannel { get; init; } = -1;

    /// <summary> The diffuse colour on screen. </summary>
    public Rgb Colour
        => ColourMath.FromTable(Values[RowField.DiffuseR], Values[RowField.DiffuseG], Values[RowField.DiffuseB]);

    /// <summary> 0 cloth … 1 metal. Mods write values outside that (1.5, 5, -2.2 in the sample packs); the shader clamps them, so do we. </summary>
    public float Metalness
        => Math.Clamp(Values[RowField.Metalness], 0, 1);

    /// <summary>
    /// The row's finish says metal. Dawntrail rows carry a metalness (0 cloth, 1 metal); a row half-way that is also
    /// glossy (low roughness) is counted as metal too.
    /// </summary>
    public bool IsMetal
        => Metalness >= 0.5f || Metalness >= 0.3f && Values[RowField.Roughness] <= 0.35f;

    /// <summary> The row glows on its own (bright emissive). </summary>
    public bool Glows
        => Values[RowField.EmissiveR] + Values[RowField.EmissiveG] + Values[RowField.EmissiveB] > 0.3f;
}

/// <summary> What was read of one worn piece (one or more slots of the same part). </summary>
public sealed class PieceReading
{
    public string Signature { get; init; } = string.Empty;
    public List<RowInfo> Rows { get; init; } = new();

    /// <summary> Surface with no colours to change (materials without a colour table, unreadable index maps…). </summary>
    public double PaintedWeight { get; set; }

    /// <summary>
    /// Surface of materials the piece's shown meshes use but that could not be read (the game did not load the file, or
    /// it is missing or damaged): no colours to change there either, but not a painted look.
    /// </summary>
    public double UnreadableWeight { get; set; }

    /// <summary> About the colour of the painted surface (for drawing only). </summary>
    public Rgb PaintedColour { get; set; } = new(0.48f, 0.45f, 0.5f);

    public double TotalWeight
        => Rows.Sum(r => r.Weight) + PaintedWeight + UnreadableWeight;

    /// <summary> What was left out and why (for the log). </summary>
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Reads a worn piece's materials, their colour table rows and how much of the piece each row covers: the model's
/// triangles (LOD 0, only the parts shown) are rasterised into the index map at its full size, and every texel inside
/// counts for the row it picks, weighted by the triangle's surface. Ported from the old plugin's
/// CharacterExport.Colours.cs (ReadMaterial, Coverage, Fill, WholePiece); the surface weighting and full-size reading
/// are new. Background thread; never throws.
/// </summary>
public static class PieceReader
{
    /// <summary> Glamourer addresses at most ten materials per model. </summary>
    public const int MaxMaterials = 10;

    public static PieceReading Read(IReadOnlyList<LiveModel> models, IFileSource files, CancellationToken cancel = default)
    {
        var reading = new PieceReading { Signature = string.Join('|', models.Select(m => m.Signature)) };
        var rows    = new Dictionary<string, RowInfo>(StringComparer.Ordinal);
        var images  = new Dictionary<string, TexImage?>(StringComparer.OrdinalIgnoreCase);
        double paintedR = 0, paintedG = 0, paintedB = 0, paintedW = 0;

        TexImage? Image(string? path, int minSide = 0)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            var key = path + "@" + minSide;
            if (images.TryGetValue(key, out var cached))
                return cached;
            TexImage? img = null;
            if (files.Read(path) is { } bytes && TexDecoder.ReadHeader(bytes) is { } header)
            {
                // the index map at full size, decoded block by block where the piece's triangles land; averages from a small level
                img = TexDecoder.Decode(bytes, out var error, TexDecoder.MipFor(header, minSide), lazy: minSide == 0);
                if (img is null)
                    reading.Notes.Add($"{Name(path)}: {error}");
            }

            return images[key] = img;
        }

        foreach (var model in models)
        {
            cancel.ThrowIfCancellationRequested();
            ModelFile? mdl = null;
            if (files.Read(model.Path) is { } mdlBytes)
                mdl = ModelFile.Read(mdlBytes, out _, model.Attributes, model.Shapes);
            if (mdl is null)
                reading.Notes.Add($"{SlotWord(model.Slot)} ({Name(model.Path)}): model not readable, measuring whole textures");

            for (var j = 0; j < model.Materials.Length; ++j)
            {
                cancel.ThrowIfCancellationRequested();
                // a material none of the shown parts use is not on the piece (hidden variant, other race, …)
                var meshes = mdl?.Meshes.Where(x => x.MaterialIndex == j).ToList();
                if (meshes is { Count: 0 })
                    continue;
                MaterialFile? mtrl = null;
                var why = model.Materials[j] is not { } loaded ? "not loaded by the game"
                    : files.Read(loaded.Path) is not { } mtrlBytes ? "file not readable"
                    : (mtrl = MaterialFile.Read(mtrlBytes, out var damage)) is null ? "file damaged: " + damage
                    : string.Empty;
                if (mtrl is null || model.Materials[j] is not { } live)
                {
                    var missing = model.Materials[j] is { } l ? Name(l.Path) : mdl is not null && j < mdl.Materials.Count ? Name(mdl.Materials[j]) : "?";
                    if (IsBodyMaterial(missing))
                        continue; // the character's skin under the piece: not part of it, readable or not
                    reading.Notes.Add($"{SlotWord(model.Slot)}, material {j} ({missing}): {why}, cannot be coloured");
                    if (meshes is not null)
                        reading.UnreadableWeight += meshes.Sum(Surface); // its real share, shown as a part that cannot be coloured
                    continue;
                }

                if (mtrl.IsBody)
                    continue; // skin, hair or eyes showing through: part of you, not of the piece

                var surface = meshes is null ? 1.0 : meshes.Sum(Surface);
                if (mtrl.Table != TableKind.Dawntrail || j >= MaxMaterials)
                {
                    reading.Notes.Add($"{SlotWord(model.Slot)}, material {j} ({Name(live.Path)}): {(j >= MaxMaterials ? "beyond Glamourer's ten" : mtrl.Table == TableKind.Legacy ? "an older material" : "no colour table")}, painted");
                    AddPaint(mtrl, live, meshes, surface);
                    continue;
                }

                // the row comes from the index map; only a material without one picks it by the normal map's alpha (the
                // old characterlegacy way)
                var legacy = !mtrl.Samplers.ContainsKey(MaterialFile.SamplerIndex);
                var path   = mtrl.TextureOf(legacy ? MaterialFile.SamplerNormal : MaterialFile.SamplerIndex, live.Textures);
                var image  = Image(path);
                if (image is null)
                {
                    reading.Notes.Add($"{SlotWord(model.Slot)}, material {j} ({Name(live.Path)}): no index map, painted");
                    AddPaint(mtrl, live, meshes, surface);
                    continue;
                }

                var weights = new double[MaterialFile.Rows];
                var blended = new double[MaterialFile.Rows / 2];
                if (meshes is null)
                    Histogram(image, legacy, weights, blended);
                else
                    foreach (var mesh in meshes)
                        Measure(mesh, image, legacy, weights, blended);

                var file = Name(live.Path);
                for (var r = 0; r < MaterialFile.Rows; ++r)
                {
                    if (weights[r] <= 0)
                        continue;
                    var id = $"{j}:{file}:{r}";
                    if (!rows.TryGetValue(id, out var info))
                        rows[id] = info = new RowInfo { Id = id, Material = j, Row = r, Values = mtrl.RowValues[r], DyeChannel = mtrl.DyeChannels[r] };
                    if (!info.Slots.Contains(model.Slot))
                        info.Slots.Add(model.Slot);
                    info.Weight  += weights[r];
                    info.Blended += blended[r / 2];
                }
            }
        }

        reading.Rows.AddRange(rows.Values.OrderBy(r => r.Material).ThenBy(r => r.Row));
        if (paintedW > 0)
            reading.PaintedColour = new Rgb((float)(paintedR / paintedW), (float)(paintedG / paintedW), (float)(paintedB / paintedW));
        return reading;

        void AddPaint(MaterialFile mtrl, LiveMaterial live, List<UvMesh>? meshes, double surface)
        {
            reading.PaintedWeight += surface;
            if (Image(mtrl.TextureOf(MaterialFile.SamplerDiffuse, live.Textures), 64) is not { } diffuse)
                return;
            var (r, g, b, n) = AveragePaint(diffuse, meshes);
            if (n <= 0)
                return;
            paintedR += r * surface;
            paintedG += g * surface;
            paintedB += b * surface;
            paintedW += surface;
        }
    }

    /// <summary> The row a texel of the index map (or, for <paramref name="legacy"/>, the normal map) picks. </summary>
    public static int RowAt(TexImage image, int x, int y, bool legacy)
    {
        var p = image.At(x, y);
        if (legacy)
            return Math.Clamp((int)MathF.Round(image.Rgba[p + 3] / 17f), 0, 15);

        // red picks the row pair; green is the weight of the pair's first row (A): full green = row A (checked against
        // Glamourer's rows in the old plugin; it was one row off before)
        var pair = Math.Clamp((int)MathF.Round(image.Rgba[p] / 17f), 0, 15);
        return pair * 2 + (image.Rgba[p + 1] >= 128 ? 0 : 1);
    }

    /// <summary> Green within this far of 0 or 255 is a pure row (block compression leaves a little noise). </summary>
    public const int BlendNoise = 8;

    /// <summary>
    /// The rows a texel shows and how much of each: the pair's row A with weight <c>a</c>, row B with <c>1 - a</c>
    /// (green is the weight of row A, as the shader mixes them). Legacy materials pick one row by the normal map's alpha
    /// (<c>a</c> = 1).
    /// </summary>
    public static (int A, float WeightA) Mix(TexImage image, int x, int y, bool legacy)
    {
        var p = image.At(x, y);
        if (legacy)
            return (Math.Clamp((int)MathF.Round(image.Rgba[p + 3] / 17f), 0, 15), 1f);
        var pair  = Math.Clamp((int)MathF.Round(image.Rgba[p] / 17f), 0, 15);
        int green = image.Rgba[p + 1];
        var a     = green >= 255 - BlendNoise ? 1f : green <= BlendNoise ? 0f : green / 255f;
        return (pair * 2, a);
    }

    /// <summary> Adds one texel to the rows it shows (and, when it mixes the pair, to the pair's blended surface). </summary>
    private static void Add(TexImage image, int x, int y, bool legacy, double weight, double[] rows, double[]? blended, List<int>? touched = null)
    {
        var (a, wa) = Mix(image, x, y, legacy);
        if (legacy || wa >= 1f)
        {
            Touch(a, weight);
            return;
        }

        if (wa <= 0f)
        {
            Touch(a + 1, weight);
            return;
        }

        Touch(a, weight * wa);
        Touch(a + 1, weight * (1 - wa));
        if (blended is not null)
            blended[a / 2] += weight * 2 * Math.Min(wa, 1 - wa);

        void Touch(int row, double w)
        {
            if (rows[row] == 0)
                touched?.Add(row);
            rows[row] += w;
        }
    }

    /// <summary> Surface of a mesh's shown triangles. </summary>
    public static double Surface(UvMesh mesh)
    {
        double total = 0;
        for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
            total += TriangleSurface(mesh, mesh.Indices[t], mesh.Indices[t + 1], mesh.Indices[t + 2]);
        return total;
    }

    private static double TriangleSurface(UvMesh m, int a, int b, int c)
    {
        var p = m.Positions;
        double ax = p[b * 3] - p[a * 3], ay = p[b * 3 + 1] - p[a * 3 + 1], az = p[b * 3 + 2] - p[a * 3 + 2];
        double bx = p[c * 3] - p[a * 3], by = p[c * 3 + 1] - p[a * 3 + 1], bz = p[c * 3 + 2] - p[a * 3 + 2];
        double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
        return 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
    }

    /// <summary> Every texel counts once (no model to say which ones the piece uses). </summary>
    private static void Histogram(TexImage image, bool legacy, double[] weights, double[]? blended = null)
    {
        var counts = new double[MaterialFile.Rows];
        var mixed  = new double[MaterialFile.Rows / 2];
        for (var y = 0; y < image.Height; ++y)
        for (var x = 0; x < image.Width; ++x)
            Add(image, x, y, legacy, 1, counts, mixed);
        var total = Math.Max(1, (double)image.Width * image.Height);
        for (var r = 0; r < counts.Length; ++r)
            weights[r] += counts[r] / total;
        if (blended is not null)
            for (var k = 0; k < mixed.Length; ++k)
                blended[k] += mixed[k] / total;
    }

    /// <summary> Maps larger than this per side are sampled every second (or fourth) texel: exact texel values, a quarter of the work. </summary>
    public const int MaxSamples = 2048;

    private struct RowCounter : ITexelVisitor
    {
        public TexImage Image;
        public bool Legacy;
        public int Step;
        public double[] Counts;
        public double[] Blended;
        public List<int> Touched;

        public void Visit(int x, int y)
            => Add(Image, Math.Min(Image.Width - 1, x * Step + Step / 2), Math.Min(Image.Height - 1, y * Step + Step / 2), Legacy, 1, Counts, Blended, Touched);
    }

    /// <summary>
    /// Adds each triangle's surface to the rows of the texels inside its texture-coordinate triangle, shared by how much
    /// each row shows there (a texel that mixes a pair counts for both rows by the mix). A triangle too small to hold a
    /// texel centre counts for the texel under its middle. <paramref name="blended"/> (16 entries, one per row pair)
    /// gets the surface where a pair is mixed.
    /// </summary>
    public static void Measure(UvMesh mesh, TexImage image, bool legacy, double[] weights, double[]? blended = null)
    {
        var step = 1;
        while (Math.Max(image.Width, image.Height) / step > MaxSamples)
            step *= 2;
        int gw = Math.Max(1, image.Width / step), gh = Math.Max(1, image.Height / step);
        var counter = new RowCounter
        {
            Image = image, Legacy = legacy, Step = step, Counts = new double[MaterialFile.Rows], Blended = new double[MaterialFile.Rows / 2],
            Touched = new List<int>(4),
        };
        for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            int a = mesh.Indices[t], b = mesh.Indices[t + 1], c = mesh.Indices[t + 2];
            var surface = TriangleSurface(mesh, a, b, c);
            if (surface <= 0)
                continue;
            if (!mesh.HasUvs)
            {
                // no texture coordinates: the texel at 0, 0 (rare; such a mesh has one look)
                Add(image, 0, 0, legacy, surface, weights, blended);
                continue;
            }

            var uv = mesh.Uvs;
            float u0 = uv[a * 2], v0 = uv[a * 2 + 1], u1 = uv[b * 2], v1 = uv[b * 2 + 1], u2 = uv[c * 2], v2 = uv[c * 2 + 1];
            counter.Touched.Clear();
            var inside = Rasterise(gw, gh, u0, v0, u1, v1, u2, v2, ref counter);
            if (inside == 0)
            {
                var (x, y) = Texel(image.Width, image.Height, (u0 + u1 + u2) / 3, (v0 + v1 + v2) / 3);
                Add(image, x, y, legacy, surface, weights, blended);
                continue;
            }

            foreach (var row in counter.Touched)
            {
                weights[row]       += surface * counter.Counts[row] / inside;
                counter.Counts[row] = 0;
            }

            for (var k = 0; k < counter.Blended.Length; ++k)
                if (counter.Blended[k] > 0)
                {
                    if (blended is not null)
                        blended[k] += surface * counter.Blended[k] / inside;
                    counter.Blended[k] = 0;
                }
        }
    }

    /// <summary> The texel under a texture coordinate (coordinates wrap around, as the game's samplers do). </summary>
    public static (int X, int Y) Texel(int w, int h, float u, float v)
    {
        var x = (int)((u - MathF.Floor(u)) * w);
        var y = (int)((v - MathF.Floor(v)) * h);
        return (Math.Clamp(x, 0, w - 1), Math.Clamp(y, 0, h - 1));
    }

    public interface ITexelVisitor
    {
        void Visit(int x, int y);
    }

    private struct ActionVisitor(Action<int, int> visit) : ITexelVisitor
    {
        public void Visit(int x, int y)
            => visit(x, y);
    }

    /// <summary> <see cref="Rasterise{T}"/> with a callback. </summary>
    public static int Rasterise(int w, int h, float u0, float v0, float u1, float v1, float u2, float v2, Action<int, int> visit)
    {
        var visitor = new ActionVisitor(visit);
        return Rasterise(w, h, u0, v0, u1, v1, u2, v2, ref visitor);
    }

    /// <summary>
    /// Visits every texel whose centre lies inside the triangle (with a small tolerance on the barycentric weights, as the
    /// old plugin's Fill) and returns how many there were. The triangle is moved into the 0..1 tile it starts in (pieces
    /// sometimes use tiled coordinates). Each row's span is solved from the three edge functions, which are linear along
    /// the row, instead of testing every texel of the bounding box.
    /// </summary>
    public static int Rasterise<T>(int w, int h, float u0, float v0, float u1, float v1, float u2, float v2, ref T visitor) where T : struct, ITexelVisitor
    {
        var ou = MathF.Floor(Math.Min(u0, Math.Min(u1, u2)));
        var ov = MathF.Floor(Math.Min(v0, Math.Min(v1, v2)));
        double x0 = (u0 - ou) * w, y0 = (v0 - ov) * h, x1 = (u1 - ou) * w, y1 = (v1 - ov) * h, x2 = (u2 - ou) * w, y2 = (v2 - ov) * h;
        var minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2))));
        var maxX = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
        var minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2))));
        var maxY = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
        var area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (Math.Abs(area) < 1e-6 || minX > maxX || minY > maxY)
            return 0;

        const double tolerance = -0.02;
        var n = 0;
        for (var y = minY; y <= maxY; ++y)
        {
            var py = y + 0.5;
            // the barycentric weights along this row: w = (c + k · px) / area
            var c0 = x1 * (y2 - py) - x2 * (y1 - py);
            var k0 = y1 - y2;
            var c1 = x2 * (y0 - py) - x0 * (y2 - py);
            var k1 = y2 - y0;
            double lo = minX + 0.5, hi = maxX + 0.5;
            if (!Limit(c0 / area, k0 / area, tolerance, ref lo, ref hi)
             || !Limit(c1 / area, k1 / area, tolerance, ref lo, ref hi)
             || !Limit(1 - (c0 + c1) / area, -(k0 + k1) / area, tolerance, ref lo, ref hi))
                continue;
            var from = Math.Max(minX, (int)Math.Ceiling(lo - 0.5 - 1e-9));
            var to   = Math.Min(maxX, (int)Math.Floor(hi - 0.5 + 1e-9));
            for (var x = from; x <= to; ++x)
                visitor.Visit(x, y);
            if (to >= from)
                n += to - from + 1;
        }

        return n;
    }

    /// <summary> Narrows [lo, hi] to where a + b · px ≥ t. False when nothing is left. </summary>
    private static bool Limit(double a, double b, double t, ref double lo, ref double hi)
    {
        if (Math.Abs(b) < 1e-12)
            return a >= t;
        var at = (t - a) / b;
        if (b > 0)
            lo = Math.Max(lo, at);
        else
            hi = Math.Min(hi, at);
        return lo <= hi;
    }

    /// <summary> The average colour of a (small) diffuse picture where the meshes use it. </summary>
    private static (double R, double G, double B, long N) AveragePaint(TexImage image, List<UvMesh>? meshes)
    {
        double r = 0, g = 0, b = 0;
        long n = 0;
        void Add(int x, int y)
        {
            var p = (y * image.Width + x) * 4;
            r += image.Rgba[p] / 255.0;
            g += image.Rgba[p + 1] / 255.0;
            b += image.Rgba[p + 2] / 255.0;
            ++n;
        }

        if (meshes is null || meshes.All(m => !m.HasUvs))
        {
            for (var y = 0; y < image.Height; ++y)
            for (var x = 0; x < image.Width; ++x)
                Add(x, y);
        }
        else
        {
            foreach (var m in meshes.Where(m => m.HasUvs))
                for (var t = 0; t + 2 < m.Indices.Length; t += 3)
                {
                    int a = m.Indices[t], bb = m.Indices[t + 1], c = m.Indices[t + 2];
                    Rasterise(image.Width, image.Height, m.Uvs[a * 2], m.Uvs[a * 2 + 1], m.Uvs[bb * 2], m.Uvs[bb * 2 + 1], m.Uvs[c * 2], m.Uvs[c * 2 + 1], Add);
                }
        }

        return n == 0 ? (0, 0, 0, 0) : (r / n, g / n, b / n, n);
    }

    /// <summary> The gear slot in words, for the log ("hands slot"). </summary>
    public static string SlotWord(int slot)
        => slot switch
        {
            0 => "head slot", 1 => "body slot", 2 => "hands slot", 3 => "legs slot", 4 => "feet slot", 5 => "ears slot",
            6 => "neck slot", 7 => "wrists slot", 8 => "right ring slot", 9 => "left ring slot", _ => $"slot {slot}",
        };

    /// <summary> A body material by its name ("mt_c0201b0001_a.mtrl": the skin a top shows), so it is left out even when it cannot be read. </summary>
    public static bool IsBodyMaterial(string file)
        => System.Text.RegularExpressions.Regex.IsMatch(file, @"^mt_c\d{4}b\d{4}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string Name(string path)
    {
        var cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return (cut >= 0 ? path[(cut + 1)..] : path).ToLowerInvariant();
    }
}
