using System.Text.RegularExpressions;
using ModCloset2.Core;

namespace ModCloset2.Detect;

/// <summary>
/// Turns a decoded model into <see cref="ModelFacts"/>: how much surface each bone carries (triangle area × weight) and
/// where. Bare skin that gear models carry (the hands under a bracelet, the legs under shorts) is counted apart: it is
/// the body, not the item.
/// </summary>
public static partial class Geometry
{
    // Body skin materials: "/mt_c0201b0001_bibo.mtrl", "/mt_c0101b0001_a.mtrl". Gear models use them for exposed skin.
    [GeneratedRegex(@"mt_c\d{4}b\d{4}_", RegexOptions.IgnoreCase)]
    private static partial Regex SkinMaterial();

    /// <summary> True for a material name of the body's own skin. </summary>
    public static bool IsSkinMaterial(string material)
        => SkinMaterial().IsMatch(material);

    /// <param name="skinShaders"> Material names (as the model lists them) whose shipped file uses the skin shader. </param>
    /// <param name="hidden"> Bits of the model's attributes that are off: sub-meshes that need any of them are left out. </param>
    public static ModelFacts Summarise(ModelData m, string gamePath, string option, int race, bool excludeSkin, IReadOnlySet<string>? skinShaders = null,
        uint hidden = 0)
    {
        var f = new ModelFacts
        {
            GamePath   = gamePath,
            Option     = option,
            Race       = race,
            Meshes     = m.Meshes.Count,
            Materials  = m.Materials.Distinct().ToList(),
            Attributes = m.Attributes.Where(a => a.Length > 0).Distinct().ToList(),
        };

        var bones = new Dictionary<string, Acc>(StringComparer.Ordinal);
        double area = 0, skin = 0, cx = 0, cy = 0, cz = 0;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        Span<int> slotBone = stackalloc int[24];
        Span<float> slotWeight = stackalloc float[24];

        foreach (var mesh in m.Meshes)
        {
            var material = mesh.MaterialIndex < m.Materials.Count ? m.Materials[mesh.MaterialIndex] : string.Empty;
            var isSkin = excludeSkin && (IsSkinMaterial(material) || skinShaders?.Contains(material) == true);
            if (isSkin && !f.SkinMaterials.Contains(material))
                f.SkinMaterials.Add(material);

            // The mesh's bone table as accumulators, looked up once per mesh, not per triangle.
            var table = new Acc?[mesh.BoneNames.Length];
            for (var i = 0; i < table.Length; ++i)
            {
                var name = mesh.BoneNames[i];
                if (name.Length == 0)
                    continue;
                if (!bones.TryGetValue(name, out var acc))
                    bones[name] = acc = new Acc(BodyMap.OfBone(name) == BodyMap.ByPosition);
                table[i] = acc;
            }

            var p = mesh.Positions;
            var idx = mesh.Indices;
            var k = mesh.Influences;

            // Triangles of parts switched off by default (a skirt toggled off, rings left off) are not counted.
            bool[]? off = null;
            if (hidden != 0)
                foreach (var (start, count, attributes) in mesh.SubMeshes)
                    if ((attributes & hidden) != 0)
                    {
                        off ??= new bool[idx.Length / 3];
                        for (var i = start / 3; i < (start + count) / 3 && i < off.Length; ++i)
                            off[i] = true;
                    }

            for (var t = 0; t + 2 < idx.Length; t += 3)
            {
                if (off is not null && off[t / 3])
                    continue;
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                double ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
                double vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                var tri = 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (!double.IsFinite(tri) || tri <= 0)
                    continue;
                if (isSkin)
                {
                    skin += tri;
                    continue;
                }

                var mx = (p[a * 3] + p[b * 3] + p[c * 3]) / 3f;
                var my = (p[a * 3 + 1] + p[b * 3 + 1] + p[c * 3 + 1]) / 3f;
                var mz = (p[a * 3 + 2] + p[b * 3 + 2] + p[c * 3 + 2]) / 3f;
                area += tri;
                f.Triangles++;
                cx += tri * mx;
                cy += tri * my;
                cz += tri * mz;
                minX = Math.Min(minX, Math.Min(p[a * 3], Math.Min(p[b * 3], p[c * 3])));
                maxX = Math.Max(maxX, Math.Max(p[a * 3], Math.Max(p[b * 3], p[c * 3])));
                minY = Math.Min(minY, Math.Min(p[a * 3 + 1], Math.Min(p[b * 3 + 1], p[c * 3 + 1])));
                maxY = Math.Max(maxY, Math.Max(p[a * 3 + 1], Math.Max(p[b * 3 + 1], p[c * 3 + 1])));
                minZ = Math.Min(minZ, Math.Min(p[a * 3 + 2], Math.Min(p[b * 3 + 2], p[c * 3 + 2])));
                maxZ = Math.Max(maxZ, Math.Max(p[a * 3 + 2], Math.Max(p[b * 3 + 2], p[c * 3 + 2])));

                // The triangle's share per bone: the average of its corners' weights.
                var n = 0;
                var total = 0f;
                if (k > 0 && k <= 8)
                {
                    for (var corner = 0; corner < 3; ++corner)
                    {
                        var v = corner == 0 ? a : corner == 1 ? b : c;
                        for (var j = 0; j < k; ++j)
                        {
                            var w = mesh.Weights[v * k + j];
                            if (w <= 0)
                                continue;
                            int bi = mesh.BoneIndices[v * k + j];
                            if (bi >= table.Length || table[bi] is null)
                                continue;
                            var found = -1;
                            for (var s2 = 0; s2 < n; ++s2)
                                if (slotBone[s2] == bi)
                                {
                                    found = s2;
                                    break;
                                }

                            if (found < 0)
                            {
                                found = n++;
                                slotBone[found]   = bi;
                                slotWeight[found] = 0;
                            }

                            slotWeight[found] += w;
                            total += w;
                        }
                    }
                }

                if (total <= 0)
                {
                    // Not weighted to anything: placed by position alone.
                    if (!bones.TryGetValue("(none)", out var none))
                        bones["(none)"] = none = new Acc(true);
                    none.Add(tri, mx, my, mz);
                    continue;
                }

                for (var s2 = 0; s2 < n; ++s2)
                    table[slotBone[s2]]!.Add(tri * slotWeight[s2] / total, mx, my, mz);
            }
        }

        f.Area     = (float)Math.Round(area, 6);
        f.SkinArea = (float)Math.Round(skin, 6);
        if (area > 0)
        {
            f.Centroid = [R(cx / area), R(cy / area), R(cz / area)];
            f.Min      = [R(minX), R(minY), R(minZ)];
            f.Max      = [R(maxX), R(maxY), R(maxZ)];
        }

        foreach (var (name, acc) in bones.OrderByDescending(kv => kv.Value.Area))
        {
            if (acc.Area <= area * 1e-4)
                continue;
            f.Bones.Add(new BoneFacts
            {
                Name     = name,
                Area     = (float)Math.Round(acc.Area, 7),
                Centroid = [R(acc.X / acc.Area), R(acc.Y / acc.Area), R(acc.Z / acc.Area)],
                Clusters = acc.Clusters(),
            });
        }

        return f;
    }

    private static float R(double v)
        => (float)Math.Round(v, 3);

    /// <summary> Area and position sums of one bone; for bones placed by position also a coarse grid of where. </summary>
    private sealed class Acc(bool keepCells)
    {
        public double Area, X, Y, Z;
        private readonly Dictionary<long, double[]>? _cells = keepCells ? new() : null;
        private long _lastKey = long.MinValue;
        private double[]? _last;

        public void Add(double area, float x, float y, float z)
        {
            Area += area;
            X    += area * x;
            Y    += area * y;
            Z    += area * z;
            if (_cells is null)
                return;

            // Neighbouring triangles mostly fall in the same cell: remember the last one.
            var key = Pack((int)MathF.Floor(x / Tuning.ClusterCell), (int)MathF.Floor(y / Tuning.ClusterCell), (int)MathF.Floor(z / Tuning.ClusterCell));
            var c = key == _lastKey ? _last! : null;
            if (c is null && !_cells.TryGetValue(key, out c))
                _cells[key] = c = new double[4];
            _lastKey = key;
            _last    = c;
            c[0] += area * x;
            c[1] += area * y;
            c[2] += area * z;
            c[3] += area;
        }

        private static long Pack(int x, int y, int z)
            => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

        /// <summary> The largest cells, with every other cell merged into the nearest of them. </summary>
        public float[] Clusters()
        {
            if (_cells is null || _cells.Count == 0 || Area <= 0)
                return Array.Empty<float>();

            var all  = _cells.Values.OrderByDescending(c => c[3]).ToList();
            var keep = all.Take(Tuning.ClustersPerBone).Select(c => (double[])c.Clone()).ToList();
            foreach (var c in all.Skip(Tuning.ClustersPerBone))
            {
                double cxp = c[0] / c[3], cyp = c[1] / c[3], czp = c[2] / c[3];
                var best = keep.MinBy(k =>
                {
                    double dx = k[0] / k[3] - cxp, dy = k[1] / k[3] - cyp, dz = k[2] / k[3] - czp;
                    return dx * dx + dy * dy + dz * dz;
                })!;
                for (var i = 0; i < 4; ++i)
                    best[i] += c[i];
            }

            var result = new float[keep.Count * 4];
            for (var i = 0; i < keep.Count; ++i)
            {
                var k = keep[i];
                result[i * 4]     = R(k[0] / k[3]);
                result[i * 4 + 1] = R(k[1] / k[3]);
                result[i * 4 + 2] = R(k[2] / k[3]);
                result[i * 4 + 3] = (float)Math.Round(k[3], 7);
            }

            return result;
        }
    }

    /// <summary>
    /// Shares of the item's surface per region (bones by name, the others by where their clusters are), and the share
    /// that is away from the body. Shares of placed regions sum to 1 − away (or all zero for a model without surface).
    /// </summary>
    public static (Dictionary<BodyRegions, float> Shares, float Away) Regions(ModelFacts m)
    {
        var shares = new Dictionary<BodyRegions, float>();
        var total  = m.Bones.Sum(b => (double)b.Area);
        if (total <= 0)
            return (shares, 0);

        double away = 0;
        foreach (var b in m.Bones)
        {
            var region = BodyMap.OfBone(b.Name);
            if (region != BodyMap.ByPosition)
            {
                shares[region] = shares.GetValueOrDefault(region) + (float)(b.Area / total);
                continue;
            }

            if (b.Clusters.Length >= 4)
            {
                var sum = 0.0;
                for (var i = 0; i + 3 < b.Clusters.Length; i += 4)
                    sum += b.Clusters[i + 3];
                for (var i = 0; i + 3 < b.Clusters.Length; i += 4)
                {
                    var part = b.Area * (sum > 0 ? b.Clusters[i + 3] / sum : 0);
                    Place(BodyMap.OfPosition(b.Name, b.Clusters[i], b.Clusters[i + 1], b.Clusters[i + 2], m.Race), part);
                }
            }
            else
            {
                Place(BodyMap.OfPosition(b.Name, b.Centroid[0], b.Centroid[1], b.Centroid[2], m.Race), b.Area);
            }
        }

        return (shares, (float)(away / total));

        void Place(BodyRegions r, double a)
        {
            if (r == BodyMap.Away)
                away += a;
            else
                shares[r] = shares.GetValueOrDefault(r) + (float)(a / total);
        }
    }
}
