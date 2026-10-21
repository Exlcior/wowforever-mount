using System.Text.Json;
using System.Text.Json.Nodes;
namespace MiniBox.WowForever;

using NVector3 = System.Numerics.Vector3;

/// <summary>Maps, tiles and the models they reference, discovered from the installed WDT and object files.
/// The result is cached per client version outside the game folders.</summary>
internal sealed class WorldIndex
{
    public sealed record Tile(string Map, string Id);
    public readonly List<Tile> Tiles = new();
    public string Catalog;
    public int Missing;

    public static WorldIndex Build(WowForeverMount mount)
    {
        var index = new WorldIndex();
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sbox-wowforever");
        var cacheFile = Path.Combine(cacheDir, $"index-{mount.Storage.Version}-{mount.Storage.Locale}.json");
        Dictionary<string, string> cachedTypes = null;
        try
        {
            if (File.Exists(cacheFile))
            {
                var cached = JsonNode.Parse(File.ReadAllText(cacheFile));
                if (cached?["Missing"]?.GetValue<int>() == 0) cachedTypes = cached["Models"].Deserialize<Dictionary<string, string>>();
            }
        }
        catch { cachedTypes = null; }

        var continents = new JsonArray();
        foreach (var map in Tables.Maps)
        {
            var wdt = mount.GetWdt(map.Key);
            if (wdt is null) { index.Missing++; continue; }
            var tiles = new JsonArray();
            foreach (var t in wdt.Tiles.Values.OrderBy(t => t.Id, StringComparer.Ordinal))
            {
                index.Tiles.Add(new Tile(map.Key, t.Id));
                var parts = t.Id.Split('_'); int a = int.Parse(parts[0]), b = int.Parse(parts[1]);
                const float half = 51200f / 3;
                string folder = $"mount://wowforever/maps/{map.Key}/{t.Id}";
                tiles.Add(new JsonObject
                {
                    ["Id"] = t.Id,
                    ["Center"] = new JsonArray(half - (b + 0.5f) * TerrainTile.TileSize, half - (a + 0.5f) * TerrainTile.TileSize, 0),
                    ["SpawnHeight"] = 120,
                    ["Terrain"] = folder + "/terrain.vmdl",
                    ["LodModels"] = new JsonArray(folder + "/terrain_lod1.vmdl", folder + "/terrain_lod2.vmdl"),
                    ["Placements"] = folder + "/placements.prefab",
                });
                if (cachedTypes is null)
                    foreach (var file in new[] { t.Obj0, t.Obj1 })
                    {
                        var bytes = file == 0 ? null : mount.Storage.TryRead(file);
                        if (bytes is null) { if (file != 0) index.Missing++; continue; }
                        foreach (var p in AdtObj.Read(bytes).Placements) mount.ModelTypes[p.Model] = p.Wmo ? "wmo" : "m2";
                    }
            }
            if (wdt.GlobalWmo != 0)
            {
                index.Tiles.Add(new Tile(map.Key, "global"));
                mount.ModelTypes[wdt.GlobalWmo] = "wmo";
                tiles.Add(new JsonObject { ["Id"] = "global", ["Center"] = new JsonArray(0, 0, 0), ["SpawnHeight"] = 120,
                    ["Placements"] = $"mount://wowforever/maps/{map.Key}/global/placements.prefab" });
            }
            if (tiles.Count == 0) continue;
            var continent = new JsonObject { ["Id"] = map.Key, ["Title"] = GameStrings.Map(mount, map), ["Directory"] = map.Directory, ["Build"] = mount.Storage.Version, ["Tiles"] = tiles };
            if (map.Spawn is not null) continent["Spawn"] = new JsonArray(map.Spawn[0], map.Spawn[1], map.Spawn[2]);
            continents.Add(continent);
        }
        // Ground cover models come from GroundEffectDoodad, not from the object files.
        foreach (var doodad in Tables.GroundDoodads.Values) if (doodad.Model != 0) mount.ModelTypes.TryAdd(doodad.Model, "m2");
        if (cachedTypes is not null)
            foreach (var (id, type) in cachedTypes) mount.ModelTypes[uint.Parse(id)] = type;
        else
        {
            // Building decor references more M2 files than the terrain does.
            foreach (var wmo in mount.ModelTypes.Where(x => x.Value == "wmo").Select(x => x.Key).ToArray())
            {
                var bytes = mount.Storage.TryRead(wmo);
                if (bytes is null) { index.Missing++; continue; }
                foreach (var doodad in WmoDoodads(bytes)) mount.ModelTypes.TryAdd(doodad, "m2");
            }
            try
            {
                Directory.CreateDirectory(cacheDir);
                var json = new JsonObject { ["Version"] = mount.Storage.Version, ["Missing"] = index.Missing,
                    ["Models"] = JsonSerializer.SerializeToNode(mount.ModelTypes.ToDictionary(x => x.Key.ToString(), x => x.Value)) };
                File.WriteAllText(cacheFile + ".tmp", json.ToJsonString()); File.Move(cacheFile + ".tmp", cacheFile, true);
            }
            catch { }
        }
        index.Catalog = new JsonObject
        {
            ["SchemaVersion"] = 2, ["Title"] = "World of Warcraft Forever", ["Mount"] = "wowforever", ["Build"] = mount.Storage.Version,
            ["ModelsDirectory"] = "mount://wowforever/models", ["Continents"] = continents,
        }.ToJsonString();
        return index;
    }

    private static IEnumerable<uint> WmoDoodads(byte[] bytes)
    {
        int offset = 0;
        while (offset + 8 <= bytes.Length)
        {
            uint id = BitConverter.ToUInt32(bytes, offset); int size = (int)BitConverter.ToUInt32(bytes, offset + 4);
            if (id == ChunkId.Of("MODI")) { for (int i = 0; i < size / 4; i++) { uint f = BitConverter.ToUInt32(bytes, offset + 8 + i * 4); if (f != 0) yield return f; } yield break; }
            offset += 8 + size;
        }
    }

    /// <summary>Same conversion as the offline catalogue (convert_world.cjs): client placement to world position and rotation.</summary>
    public static JsonObject Placement(AdtObj.Placement p)
    {
        const float half = 51200f / 3;
        var q = Rotation(p.Rotation);
        return new JsonObject
        {
            ["Id"] = (p.Wmo ? "wmo:" : "m2:") + p.UniqueId,
            ["Model"] = p.Model.ToString(),
            ["Position"] = new JsonArray(half - p.Position.Z, half - p.Position.X, p.Position.Y),
            ["Rotation"] = new JsonArray(q.X, q.Y, q.Z, q.W),
            ["Scale"] = p.Scale,
            ["Type"] = p.Wmo ? "wmo" : "m2",
            ["DoodadSets"] = new JsonArray(p.DoodadSets.Select(s => (JsonNode)s).ToArray()),
        };
    }

    private static System.Numerics.Quaternion Rotation(NVector3 degrees)
    {
        float rx = degrees.X * MathF.PI / 360, ry = degrees.Y * MathF.PI / 360, rz = degrees.Z * MathF.PI / 360;
        var qx = new System.Numerics.Quaternion(MathF.Sin(rz), 0, 0, MathF.Cos(rz));
        var qy = new System.Numerics.Quaternion(0, MathF.Sin(rx), 0, MathF.Cos(rx));
        var qz = new System.Numerics.Quaternion(0, 0, MathF.Sin(MathF.PI / 4 + ry), MathF.Cos(MathF.PI / 4 + ry));
        var z = new System.Numerics.Quaternion(0, 0, MathF.Sqrt(0.5f), MathF.Sqrt(0.5f));
        var zi = new System.Numerics.Quaternion(0, 0, -MathF.Sqrt(0.5f), MathF.Sqrt(0.5f));
        return Mul(Mul(z, Mul(Mul(qz, qy), qx)), zi);
    }

    private static System.Numerics.Quaternion Mul(System.Numerics.Quaternion a, System.Numerics.Quaternion b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
}
