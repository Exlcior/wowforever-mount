using System.Text.Json.Nodes;

namespace MiniBox.WowForever;

/// <summary>Ground cover from the terrain layers' GroundEffect entries, with the same seeded draw as the
/// offline enrich_terrain.cjs (medium density, holes and no-effect bits respected).</summary>
internal static class Foliage
{
    public static IEnumerable<JsonObject> Generate(int mapId, string mapKey, string tileId, AdtRoot adt, AdtTex tex)
    {
        var parts = tileId.Split('_');
        uint seed = (uint)((int)Imul(mapId + 1, unchecked((int)2654435761)) ^ Imul(int.Parse(parts[0]) + 1, 73856093) ^ Imul(int.Parse(parts[1]) + 1, 19349663));
        double Random() { seed = (uint)(Imul((int)seed, 1664525) + 1013904223); return seed / 4294967296.0; }
        for (int ci = 0; ci < 256; ci++)
        {
            var c = adt.Chunks[ci]; var chunk = tex.Chunks[ci];
            if (!chunk.Layers.Any(l => Tables.GroundEffects.ContainsKey(l.Effect))) continue;
            int maxDensity = chunk.Layers.Max(l => Tables.GroundEffects.TryGetValue(l.Effect, out var e) ? e.Density : 0);
            int attempts = Math.Min(32, (int)Math.Ceiling(maxDensity / 4.0));
            for (int n = 0; n < attempts; n++)
            {
                double x = Random() * 8, y = Random() * 8; int col = (int)Math.Floor(x), row = (int)Math.Floor(y), bit = row * 8 + col;
                if ((c.NoEffectDoodad >> bit & 1) != 0) continue;
                bool hole = (c.Flags & 65536) != 0 ? (c.HolesHigh[row] >> col & 1) != 0 : (c.HolesLow >> (col / 2 + row / 2 * 4) & 1) != 0;
                if (hole) continue;
                int a = Math.Min(63, (int)Math.Floor(y * 8)) * 64 + Math.Min(63, (int)Math.Floor(x * 8));
                var weights = chunk.Layers.Select((l, i) => i == 0 ? 1.0 : (i < chunk.Alpha.Length && chunk.Alpha[i] is { } alpha ? alpha[a] / 255.0 : 0)).ToArray();
                weights[0] = Math.Max(0, 1 - weights.Skip(1).Sum());
                double pick = Random() * weights.Sum(); int index = 0;
                for (; index < weights.Length - 1; index++) { pick -= weights[index]; if (pick <= 0) break; }
                if (!Tables.GroundEffects.TryGetValue(chunk.Layers[index].Effect, out var effect) || Random() > effect.Density / (double)Math.Max(1, maxDensity)) continue;
                double target = Random() * effect.Weights.Sum(); int selected = 0;
                for (; selected < 3; selected++) { target -= effect.Weights[selected]; if (target <= 0) break; }
                if (!Tables.GroundDoodads.TryGetValue(effect.Doodads[selected], out var d) || d.Model == 0) continue;
                double angle = (d.RotationMin + Random() * (d.RotationMax - d.RotationMin)) * Math.PI / 360;
                var pos = new[] { c.Position.X - y * 100 / 24, c.Position.Y - x * 100 / 24, Elevation(c, x, y) };
                double scale = d.SizeMin + Random() * (d.SizeMax - d.SizeMin);
                yield return new JsonObject
                {
                    ["Id"] = $"foliage:{mapKey}:{tileId}:{ci}:{n}", ["Model"] = d.Model.ToString(),
                    ["Position"] = new JsonArray(pos.Select(v => (JsonNode)Math.Round(v, 4)).ToArray()),
                    ["Rotation"] = new JsonArray(0, 0, Math.Sin(angle), Math.Cos(angle)),
                    ["Scale"] = scale, ["Type"] = "foliage",
                };
            }
        }
    }

    private static int Imul(int a, int b) => unchecked(a * b);

    private static double Elevation(AdtRoot.Chunk c, double x, double y)
    {
        int col = Math.Min(7, (int)Math.Floor(x)), row = Math.Min(7, (int)Math.Floor(y)); double u = x - col, v = y - row; int n = row * 17 + col;
        double tl = c.Heights[n], tr = c.Heights[n + 1], bl = c.Heights[n + 17], br = c.Heights[n + 18], center = c.Heights[n + 9], z = c.Position.Z;
        if (v <= u && v <= 1 - u) return z + tl * (1 - u - v) + tr * (u - v) + center * 2 * v;
        if (u >= v && u >= 1 - v) return z + tr * (u - v) + br * (u + v - 1) + center * 2 * (1 - u);
        if (v >= u && v >= 1 - u) return z + bl * (v - u) + br * (u + v - 1) + center * 2 * (1 - v);
        return z + tl * (1 - u - v) + bl * (v - u) + center * 2 * u;
    }
}
