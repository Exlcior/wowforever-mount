
namespace MiniBox.WowForever;

using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

/// <summary>One ADT tile as welded relief, grouped by its texture layers, with MCAL blend masks
/// and distant lattice levels. Same rules as the offline converter (geometry.cjs, repair_terrain.py,
/// terrain_lods.py) so the mount and the earlier exports agree. Units: client yards, tile-local XY.</summary>
public sealed class TerrainTile
{
    public const float TileSize = 1600f / 3f;
    public const float Unit = 100f / 24f;
    public Vector3 Center;
    public float SpawnHeight;
    public Vector3[] Positions; public Vector3[] Normals; public Vector2[] Uv;
    /// <summary>Triangles per layer combination (chunks sharing the same textures and scales).</summary>
    public List<(LayerSet Layers, int[] Indices)> Groups = new();
    /// <summary>1024² RGBA masks: layers 1..3 in RGB of mask 0, 4..6 in mask 1, 7 in mask 2.</summary>
    public List<byte[]> Masks = new();
    public bool HeightBlend;
    public List<(ushort Type, Vector3[] Positions, Vector2[] Uv, int[] Indices)> Liquids = new();
    private readonly Dictionary<(int, int), int> lattice = new();

    public sealed record LayerSet(uint[] Diffuse, uint[] Height, float[] Scale, float[] HeightScale, float[] HeightOffset)
    {
        public string Key => string.Join(";", Diffuse.Select((d, i) => $"{d}/{Height[i]}/{Scale[i]}/{HeightScale[i]}/{HeightOffset[i]}"));
    }

    public static TerrainTile Build(AdtRoot adt, AdtTex tex, uint wdtFlags)
    {
        var t = new TerrainTile();
        float grid = Unit / 2;
        float maxX = MathF.Round(adt.Chunks[0].Position.X / grid) * grid, maxY = MathF.Round(adt.Chunks[0].Position.Y / grid) * grid;
        t.Center = new Vector3(maxX - 800f / 3, maxY - 800f / 3, 0);
        var lookup = new Dictionary<(int, int), int>();
        var pos = new List<Vector3>(); var uv = new List<Vector2>(); var heights = new List<List<float>>();
        var chunkTris = new List<int>[256];
        float maxZ = float.MinValue;
        for (int ci = 0; ci < 256; ci++)
        {
            var c = adt.Chunks[ci]; var remap = new int[145]; int i = 0;
            for (int row = 0; row < 17; row++)
                for (int col = 0; col < (row % 2 == 1 ? 8 : 9); col++)
                {
                    int gx = (int)MathF.Round((c.Position.X - row * Unit / 2) / grid);
                    int gy = (int)MathF.Round((c.Position.Y - (col + (row % 2 == 1 ? 0.5f : 0)) * Unit) / grid);
                    float z = c.Position.Z + c.Heights[i];
                    if (!lookup.TryGetValue((gx, gy), out int index))
                    {
                        index = pos.Count; lookup[(gx, gy)] = index;
                        pos.Add(new Vector3(gx * grid - t.Center.X, gy * grid - t.Center.Y, z));
                        uv.Add(new Vector2((maxY - gy * grid) / TileSize, (maxX - gx * grid) / TileSize));
                        heights.Add(new List<float> { z });
                    }
                    else heights[index].Add(z);
                    maxZ = MathF.Max(maxZ, z); remap[i++] = index;
                }
            var tris = chunkTris[ci] = new List<int>();
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                {
                    bool hole = (c.Flags & 65536) != 0 ? (c.HolesHigh[row] >> col & 1) != 0 : (c.HolesLow >> (col / 2 + row / 2 * 4) & 1) != 0;
                    if (hole) continue;
                    int n = row * 17 + 9 + col;
                    foreach (int v in new[] { n, n - 9, n + 8, n, n - 8, n - 9, n, n + 9, n - 8, n, n + 8, n + 9 }) tris.Add(remap[v]);
                }
        }
        t.Positions = pos.Select((p, k) => new Vector3(p.X, p.Y, heights[k].Average())).ToArray();
        t.Uv = uv.ToArray();
        t.SpawnHeight = maxZ + 80;
        t.Normals = FaceNormals(t.Positions, chunkTris.SelectMany(x => x));
        foreach (var ((gx, gy), index) in lookup)
        {
            // Outer vertices sit on the 129 x 129 lattice used by the distant levels.
            float x = (maxX - gx * grid) / Unit, y = (maxY - gy * grid) / Unit;
            if (MathF.Abs(x - MathF.Round(x)) < 1e-3f && MathF.Abs(y - MathF.Round(y)) < 1e-3f) t.lattice[((int)MathF.Round(x), (int)MathF.Round(y))] = index;
        }
        t.BuildLayers(tex, wdtFlags, chunkTris);
        t.BuildLiquids(adt);
        return t;
    }

    private static Vector3[] FaceNormals(Vector3[] p, IEnumerable<int> indices)
    {
        var n = new Vector3[p.Length]; var list = indices.ToArray();
        for (int i = 0; i + 2 < list.Length; i += 3)
        {
            var f = Vector3.Cross(p[list[i + 1]] - p[list[i]], p[list[i + 2]] - p[list[i]]);
            n[list[i]] += f; n[list[i + 1]] += f; n[list[i + 2]] += f;
        }
        for (int i = 0; i < n.Length; i++) n[i] = n[i].LengthSquared() > 0 ? Vector3.Normalize(n[i]) : Vector3.UnitZ;
        return n;
    }

    private void BuildLayers(AdtTex tex, uint wdtFlags, List<int>[] chunkTris)
    {
        int maxLayers = tex.Chunks.Max(c => c.Layers.Length);
        int maskCount = Math.Max(1, (maxLayers - 1 + 2) / 3);
        for (int k = 0; k < maskCount; k++) Masks.Add(new byte[1024 * 1024 * 4]);
        var groups = new Dictionary<string, (LayerSet Set, List<int> Tris)>();
        HeightBlend = (wdtFlags & 128) != 0 && tex.Height.Any(h => h != 0);
        for (int ci = 0; ci < 256; ci++)
        {
            var c = tex.Chunks[ci]; int n = c.Layers.Length;
            var set = new LayerSet(new uint[n], new uint[n], new float[n], new float[n], new float[n]);
            for (int j = 0; j < n; j++)
            {
                uint idx = c.Layers[j].Texture;
                set.Diffuse[j] = idx < tex.Diffuse.Length ? tex.Diffuse[idx] : 0;
                set.Height[j] = idx < tex.Height.Length ? tex.Height[idx] : 0;
                var p = idx < tex.Params.Length ? tex.Params[idx] : (0u, 1f, 0f);
                set.Scale[j] = MathF.Pow(2, (p.Item1 & 240) >> 4);
                set.HeightScale[j] = set.Height[j] != 0 ? p.Item2 : 1;
                set.HeightOffset[j] = set.Height[j] != 0 ? p.Item3 : 0;
                if (j == 0 || c.Alpha.Length <= j || c.Alpha[j] is null) continue;
                var mask = Masks[(j - 1) / 3]; int channel = (j - 1) % 3, ox = ci % 16 * 64, oy = ci / 16 * 64;
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 64; x++)
                    {
                        int o = ((oy + y) * 1024 + ox + x) * 4;
                        mask[o + channel] = c.Alpha[j][y * 64 + x]; mask[o + 3] = 255;
                    }
            }
            if (!groups.TryGetValue(set.Key, out var g)) groups[set.Key] = g = (set, new List<int>());
            g.Tris.AddRange(chunkTris[ci]);
        }
        foreach (var (set, tris) in groups.Values) if (tris.Count > 0) Groups.Add((set, tris.ToArray()));
    }

    private void BuildLiquids(AdtRoot adt)
    {
        foreach (var byType in adt.Liquids.GroupBy(l => l.Type))
        {
            var p = new List<Vector3>(); var uv = new List<Vector2>(); var idx = new List<int>();
            foreach (var w in byType)
            {
                var o = adt.Chunks[w.Chunk].Position; int b = p.Count;
                for (int y = 0; y <= w.Height; y++)
                    for (int x = 0; x <= w.Width; x++)
                    {
                        p.Add(new Vector3(o.X - (y + w.Y) * Unit - Center.X, o.Y - (x + w.X) * Unit - Center.Y, w.Heights[y * (w.Width + 1) + x]));
                        uv.Add(new Vector2(x / 4f, y / 4f));
                    }
                for (int y = 0; y < w.Height; y++)
                    for (int x = 0; x < w.Width; x++)
                    {
                        int bit = y * w.Width + x;
                        if (w.Bitmap is { Length: > 0 } && (w.Bitmap[bit >> 3] >> (bit & 7) & 1) == 0) continue;
                        int a = b + y * (w.Width + 1) + x, bb = a + 1, c = a + w.Width + 1, d = c + 1;
                        idx.AddRange(new[] { a, c, bb, bb, c, d });
                    }
            }
            if (idx.Count > 0) Liquids.Add((byType.Key, p.ToArray(), uv.ToArray(), idx.ToArray()));
        }
    }

    /// <summary>Distant relief: every stride-th lattice vertex (4 → 33², 16 → 9²) with a skirt against cracks.</summary>
    public (Vector3[] Positions, Vector3[] Normals, Vector2[] Uv, int[] Indices) Lod(int stride, float skirt)
    {
        int n = 128 / stride + 1; var p = new List<Vector3>(); var nr = new List<Vector3>(); var uv = new List<Vector2>();
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                if (!lattice.TryGetValue((a * stride, b * stride), out int v))
                    v = lattice.OrderBy(kv => (kv.Key.Item1 - a * stride) * (kv.Key.Item1 - a * stride) + (kv.Key.Item2 - b * stride) * (kv.Key.Item2 - b * stride)).First().Value;
                var pos = Positions[v];
                p.Add(new Vector3(TileSize / 2 - a * stride * Unit, TileSize / 2 - b * stride * Unit, pos.Z));
                nr.Add(Normals[v]); uv.Add(Uv[v]);
            }
        var idx = new List<int>();
        int I(int a, int b) => a * n + b;
        for (int a = 0; a < n - 1; a++)
            for (int b = 0; b < n - 1; b++)
            {
                int q0 = I(a, b), q1 = I(a + 1, b), q2 = I(a, b + 1), q3 = I(a + 1, b + 1);
                idx.AddRange(new[] { q0, q2, q1, q1, q2, q3 });
            }
        // Wind like the full relief (+Z facing).
        var f = Vector3.Cross(p[idx[1]] - p[idx[0]], p[idx[2]] - p[idx[0]]);
        if (f.Z < 0) for (int i = 0; i < idx.Count; i += 3) (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
        var border = new List<int>();
        for (int b = 0; b < n; b++) border.Add(I(0, b));
        for (int a = 1; a < n; a++) border.Add(I(a, n - 1));
        for (int b = n - 2; b >= 0; b--) border.Add(I(n - 1, b));
        for (int a = n - 2; a > 0; a--) border.Add(I(a, 0));
        int start = p.Count;
        foreach (int v in border) { p.Add(p[v] - new Vector3(0, 0, skirt)); nr.Add(nr[v]); uv.Add(uv[v]); }
        for (int k = 0; k < border.Count; k++)
        {
            int a0 = border[k], a1 = border[(k + 1) % border.Count], b0 = start + k, b1 = start + (k + 1) % border.Count;
            idx.AddRange(new[] { a0, a1, b0, b0, a1, b1, a0, b0, a1, b0, b1, a1 }); // two-sided skirt
        }
        return (p.ToArray(), nr.ToArray(), uv.ToArray(), idx.ToArray());
    }
}
