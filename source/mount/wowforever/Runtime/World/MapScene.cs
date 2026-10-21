using System.Text.Json.Nodes;
using Sandbox;
using Sandbox.Mounting;
namespace MiniBox.WowForever;

using NVector3 = System.Numerics.Vector3;

/// <summary>A playable place of the world, built only from the player's installation: walkable terrain with
/// collisions, buildings, props and ground cover, interiors, the zone's sun, sky and haze, particle effects,
/// distant relief to the horizon and a spawn point. It loads in any game, the default Sandbox mode included.</summary>
internal sealed class MapSceneLoader(MapSceneLoader.Place place) : SceneLoader<WowForeverMount>
{
    /// <summary>Top: highest height (client yards) the spawn may sit at, under city roofs; 0 means anywhere.</summary>
    public sealed record Place(string Key, string Title, string Map, float X, float Y, float Top = 0, uint Area = 0);

    /// <summary>The places offered as maps (client world coordinates, in yards), named by their AreaTable entry.</summary>
    public static readonly Place[] Places =
    {
        new("northshire", "Northshire Valley", "eastern_kingdoms", -8949, -132, Area: 9),
        new("goldshire", "Goldshire", "eastern_kingdoms", -9464, 62, Area: 87),
        new("stormwind", "Stormwind City", "eastern_kingdoms", -8833, 628, 130, 1519),
        new("ironforge", "Ironforge", "eastern_kingdoms", -4918, -940, 540, 1537),
        new("brill", "Brill", "eastern_kingdoms", 2260, 290, Area: 159),
        new("booty_bay", "Booty Bay", "eastern_kingdoms", -14297, 530, Area: 35),
        new("orgrimmar", "Orgrimmar", "kalimdor", 1500, -4415, 80, 1637),
        new("razor_hill", "Razor Hill", "kalimdor", 315, -4743, Area: 362),
        new("crossroads", "The Crossroads", "kalimdor", -452, -2650, Area: 380),
        new("thunder_bluff", "Thunder Bluff", "kalimdor", -1277, 124, Area: 1638),
        new("darnassus", "Darnassus", "kalimdor", 9947, 2482, 1400, 1657),
    };

    // Yards from the spawn point. Everything is placed at load time (no streaming outside the explorer).
    private const float WalkRadius = 650, BuildingRadius = 2600, FoliageRadius = 160, EffectRadius = 250;
    private const float LandscapeRadius = 2200;
    private const float SmallPropRadius = 220, InteriorRadius = 250;
    private const int MaxEffects = 120;
    private const float LodRadius = 150;
    /// <summary>Scale of the whole place for s&box players (1 = the client's yards at 36 units).</summary>
    private const float WorldScale = 0.85f;
    private const float DetailTerrain = 750, MediumTerrain = 2200, HorizonTerrain = 5000;
    private const float Inches = Geometry.Inches;

    /// <summary>What the last build produced (QA).</summary>
    public static readonly Dictionary<string, string> Reports = new();

    private System.Diagnostics.Stopwatch watch;
    private NVector3 center;
    private List<(string Id, string Level, NVector3 Center)> terrain;
    private List<(uint Id, bool Solid, Vector3 Position, Rotation Rotation, float Scale, bool Effects, List<int> Sets)> items;

    private Vector3 Local(NVector3 source) => new Vector3(source.X - center.X, source.Y - center.Y, source.Z - center.Z) * Inches;

    // Planning and decoding run on workers, then every model, texture and sprite the scene needs is loaded through
    // the mount's own (frame-budgeted) loaders; only then is the scene assembled, in one synchronous pass.
    protected override async Task<object> LoadAsync()
    {
        watch = System.Diagnostics.Stopwatch.StartNew();
        // A game that loads the map synchronously blocks the main thread: build it in one go on that thread.
        if (!WorkerLoader<object>.AsynchronousCaller()) { items = null; synchronous = true; return Load(); }
        synchronous = false;
        await Task.Run(Plan);
        var paths = terrain.Select(t => $"maps/{place.Map}/{t.Id}/{t.Level}.vmdl")
            .Concat(items.Select(i => ModelPath(i.Id, i.Solid, i.Position)))
            .Concat(items.Where(i => i.Effects).SelectMany(i => SpritePaths(i.Id)))
            .Append($"materials/sky_{place.Key}.vmat").Distinct().ToList();
        var pending = new List<Task<object>>();
        foreach (var path in paths)
        {
            if (Host.GetByPath($"mount://wowforever/{path}") is { } loader) pending.Add(loader.GetOrCreate());
            if (pending.Count >= 64) { await Task.WhenAny(pending); pending.RemoveAll(t => t.IsCompleted); }
        }
        try { await Task.WhenAll(pending); } catch (Exception) { /* missing pieces are skipped while building */ }
        return Load();
    }

    private void Plan()
    {
        var wdt = Host.GetWdt(place.Map) ?? throw new FileNotFoundException("Map not installed: " + place.Map);
        center = new NVector3(place.X, place.Y, 0);
        center.Z = Ground(wdt, center);

        // Terrain rings: walkable detail near the spawn, then lighter relief to the horizon.
        terrain = new List<(string Id, string Level, NVector3 Center)>();
        foreach (var t in wdt.Tiles.Values)
        {
            var tc = TileCenter(t.Id);
            float dx = MathF.Max(MathF.Abs(tc.X - center.X) - TerrainTile.TileSize / 2, 0), dy = MathF.Max(MathF.Abs(tc.Y - center.Y) - TerrainTile.TileSize / 2, 0);
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            string level = distance < DetailTerrain ? "terrain_collision" : distance < MediumTerrain ? "terrain_lod1" : distance < HorizonTerrain ? "terrain_lod2" : null;
            if (level is not null) terrain.Add((t.Id, level, tc));
        }
        var detailTiles = terrain.Where(t => t.Level == "terrain_collision").Select(t => t.Id).ToList();

        items = new List<(uint Id, bool Solid, Vector3 Position, Rotation Rotation, float Scale, bool Effects, List<int> Sets)>();
        var tilePlacements = new System.Collections.Concurrent.ConcurrentDictionary<string, JsonArray>();
        var placementTiles = terrain.Where(t =>
        {
            float dx = MathF.Max(MathF.Abs(t.Center.X - center.X) - TerrainTile.TileSize / 2, 0);
            float dy = MathF.Max(MathF.Abs(t.Center.Y - center.Y) - TerrainTile.TileSize / 2, 0);
            return dx * dx + dy * dy <= BuildingRadius * BuildingRadius;
        }).Select(t => t.Id).ToList();
        Parallel.ForEach(placementTiles, new ParallelOptions { MaxDegreeOfParallelism = 4 }, tile => tilePlacements[tile] = PlacementLoader.ForTile(Host, place.Map, tile));
        // A building spanning several tiles is listed by each of them: one placement per unique id.
        var seen = new HashSet<string>();
        foreach (var tile in placementTiles)
            foreach (var node in tilePlacements[tile])
            {
                if (!seen.Add((string)node["Id"])) continue;
                var p = node["Position"].AsArray();
                var source = new NVector3(F(p[0]), F(p[1]), F(p[2]));
                string type = (string)node["Type"]; uint id = uint.Parse((string)node["Model"]);
                float distance = new NVector3(source.X - center.X, source.Y - center.Y, 0).Length();
                if (distance > (type == "wmo" ? BuildingRadius : type == "foliage" ? FoliageRadius : LandscapeRadius)) continue;
                if (type == "m2" && distance > WalkRadius)
                {
                    float radius = Radius(id) * F(node["Scale"]);
                    if (radius < 4 || distance > Math.Clamp(300 + radius * 45, 650, LandscapeRadius)) continue;
                }
                var q = node["Rotation"].AsArray();
                // Walkable decor carries collisions; buildings beyond the walking area are scenery and use the client's LODs.
                items.Add((id, type != "foliage" && distance <= WalkRadius + (type == "wmo" ? Radius(id) * F(node["Scale"]) : 0), Local(source), new Rotation(F(q[0]), F(q[1]), F(q[2]), F(q[3])), F(node["Scale"]),
                    type == "m2" && distance < EffectRadius, type == "wmo" ? (node["DoodadSets"]?.AsArray().Select(x => (int)F(x)).ToList() ?? new List<int>()) : (List<int>)null));
            }
        Prepare(items.Select(i => i.Id), detailTiles);
        // Small props only near the spawn: the scene is static, so its density is what every frame draws.
        items.RemoveAll(i => i.Sets is null && i.Solid && i.Position.Length > SmallPropRadius * Inches && Radius(i.Id) * i.Scale < 3);
        // Interior and courtyard decor, filtered by the placement's doodad sets like the explorer.
        // City-sized buildings hold thousands of them: only the decor near the spawn is placed.
        foreach (var building in items.Where(i => i.Sets is not null).ToList())
        {
            ModelData wmo;
            try { wmo = Host.GetModel(building.Id); } catch (Exception) { continue; }
            foreach (var d in wmo.Doodads)
            {
                if (d.Sets.Length > 0 && !d.Sets.Contains(0) && !d.Sets.Any(building.Sets.Contains)) continue;
                var local = new Vector3(d.Position.X, d.Position.Y, d.Position.Z) * Inches * building.Scale;
                var position = building.Position + building.Rotation * local;
                if (position.Length > InteriorRadius * Inches) continue;
                items.Add((d.Model, true, position, building.Rotation * new Rotation(d.Rotation.X, d.Rotation.Y, d.Rotation.Z, d.Rotation.W),
                    building.Scale * d.Scale, (position.Length < EffectRadius * Inches), null));
            }
        }
        Prepare(items.Select(i => i.Id), Array.Empty<string>());
        // Particle effects: the nearest ones only, each runs every frame.
        bool Emits(uint id) { try { return Host.GetModel(id).Emitters.Count > 0; } catch (Exception) { return false; } }
        var effectOrder = items.Select((i, k) => (i, k)).Where(x => x.i.Effects).OrderBy(x => Emits(x.i.Id) ? x.i.Position.Length : float.MaxValue).Skip(MaxEffects).Select(x => x.k).ToList();
        foreach (var k in effectOrder) items[k] = items[k] with { Effects = false };
        // Models that draw nothing (sound or light markers) are not placed.
        items.RemoveAll(i => { try { var m = Host.GetModel(i.Id); return m.Parts.Count == 0 && m.Emitters.Count == 0; } catch (Exception) { return true; } });
    }

    // Placement values are numbers built in code (double, float or int), not parsed text.
    private static float F(JsonNode n) => n.GetValueKind() == System.Text.Json.JsonValueKind.Number ? n.AsValue() switch
    {
        var v when v.TryGetValue<double>(out var d) => (float)d,
        var v when v.TryGetValue<float>(out var f) => f,
        var v when v.TryGetValue<int>(out var i) => i,
        var v when v.TryGetValue<long>(out var l) => l,
        var v when v.TryGetValue<decimal>(out var m) => (float)m,
        _ => float.Parse(n.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture),
    } : 0;

    private float Radius(uint id) { try { return Host.GetModel(id).Radius; } catch (Exception) { return 0; } }

    private string ModelPath(uint id, bool solid, Vector3 position = default)
    {
        if (solid) return $"models/{id}_collision.vmdl";
        float yards = position.Length / Inches;
        return yards > 700 ? $"models/{id}_lod2.vmdl" : yards > 350 ? $"models/{id}_lod1.vmdl" : $"models/{id}.vmdl";
    }

    private IEnumerable<string> SpritePaths(uint id)
    {
        int slots; try { slots = ParticleTextureLoader.Slots(Host.GetModel(id)).Count; } catch (Exception) { yield break; }
        for (int slot = 0; slot < slots; slot++) yield return $"models/{id}/sprite{slot}";
    }

    protected override void BuildScene()
    {
        int objects = 0, colliders = 0, effects = 0, failures = 0; string lastFailure = null;
        watch ??= System.Diagnostics.Stopwatch.StartNew();
        if (items is null) Plan(); // synchronous load: pieces not loaded yet are skipped
        foreach (var (id, level, tc) in terrain)
        {
            var go = new GameObject(true, $"Terrain {id}");
            go.WorldPosition = Local(new NVector3(tc.X, tc.Y, 0));
            var model = Resource<Model>($"maps/{place.Map}/{id}/{level}.vmdl");
            go.Components.Create<ModelRenderer>().Model = model;
            if (level == "terrain_collision") { var c = go.Components.Create<ModelCollider>(); c.Static = true; c.Model = model; colliders++; }
            objects++;
        }
        foreach (var item in items)
        {
            try
            {
                var go = Spawn(item.Id, item.Solid, item.Position, item.Rotation, item.Scale, ref colliders);
                if (go is null) continue;
                objects++;
                if (item.Effects) effects += Effects(go, item.Id);
            }
            catch (Exception error) { failures++; lastFailure = $"{item.Id}: {error.Message}"; }
        }

        // The zone's light at noon: sun, ambient, banded sky and haze, as in the explorer.
        var light = Ambience.At(place.Map, new NVector3(center.X, center.Y, center.Z));
        var sun = new GameObject(true, "Soleil");
        sun.WorldRotation = new Angles(50, 40, 0).ToRotation();
        var directional = sun.Components.Create<DirectionalLight>();
        directional.LightColor = Ambience.Gamma(light.Direct, 0.9f);
        directional.SkyColor = Ambience.Gamma(light.Ambient, 1.5f);
        directional.Shadows = true;
        var sky = new GameObject(true, "Ciel");
        sky.Components.Create<SkyBox2D>().SkyMaterial = Resource<Material>($"materials/sky_{place.Key}.vmat");
        var fog = sky.Components.Create<GradientFog>();
        fog.Color = new Color(light.Fog.X, light.Fog.Y, light.Fog.Z, 1);
        fog.Height = 100000 * Inches; fog.VerticalFalloffExponent = 1; fog.FalloffExponent = 1.2f;
        fog.StartDistance = MediumTerrain * 0.6f * Inches; fog.EndDistance = (HorizonTerrain - TerrainTile.TileSize * 0.25f) * Inches;

        // No camera here: the game's own player brings one (a scene camera would stay fixed and take over the view).
        var spawn = new GameObject(true, "Point d'apparition");
        spawn.WorldPosition = Vector3.Up * (SpawnHeight() + 8);
        spawn.Components.Create<SpawnPoint>();

        // WoW builds its doors, streets and props larger than life for its stocky characters: the world is scaled
        // around the spawn so that a standard s&box player feels the client's proportions.
        var world = new GameObject(true, "Monde");
        foreach (var child in sun.Scene.Children.ToList())
            if (child != world && child != sun && child != sky) child.SetParent(world, true);
        world.WorldScale = new Vector3(WorldScale);

        built.Clear();
        lock (Reports) Reports[place.Key] = $"objects {objects}, colliders {colliders}, effects {effects}, tiles {terrain.Count(t => t.Level == "terrain_collision")}, failures {failures} {lastFailure}, {watch.Elapsed.TotalSeconds:0.0}s";
    }

    // The scene is built on the main thread: the engine's own loading would wait there for this mount's
    // asynchronous loaders, which need that same thread. Resources are taken from their loaders directly.
    private bool synchronous;
    private readonly Dictionary<string, object> built = new();

    private T Resource<T>(string path) where T : class
    {
        var loader = Host.GetByPath($"mount://wowforever/{path}") ?? throw new FileNotFoundException(path);
        object result;
        if (synchronous)
        {
            // The caller blocks the main thread: the engine's cache holds a lock while another load of the same path
            // waits for that thread, so each piece is built here directly through its loader's synchronous path.
            if (!built.TryGetValue(path, out result))
            {
                // Not being loaded elsewhere: the engine's own path, which registers the resource (sprites need that).
                var pending = typeof(ResourceLoader).GetField("_loadTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(loader) as Task;
                if (pending is null || pending.IsCompleted)
                {
                    var direct = loader.GetOrCreate();
                    if (direct.IsCompletedSuccessfully) { built[path] = result = direct.Result; return result as T ?? throw new InvalidDataException($"{path} is not a {typeof(T).Name}"); }
                }
                var load = loader.GetType().GetMethod("Load", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, System.Type.EmptyTypes);
                built[path] = result = load?.Invoke(loader, null);
            }
            return result as T ?? throw new InvalidDataException($"{path} is not a {typeof(T).Name}");
        }
        var task = loader.GetOrCreate();
        if (task.IsCompletedSuccessfully) result = task.Result;
        else
        {
            // Another caller (the explorer's streaming) is loading it asynchronously and the main thread cannot wait
            // here: build a copy synchronously through the loader's own synchronous path.
            var load = loader.GetType().GetMethod("Load", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, System.Type.EmptyTypes);
            result = load?.Invoke(loader, null) ?? throw new FileNotFoundException($"{path} is not loaded");
        }
        return result as T ?? throw new InvalidDataException($"{path} is not a {typeof(T).Name}");
    }

    /// <summary>Reads and decodes models, their textures and the detailed terrain on worker threads.</summary>
    private void Prepare(IEnumerable<uint> models, IEnumerable<string> tiles)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) };
        Parallel.ForEach(models.Distinct().ToList(), options, id =>
        {
            try
            {
                var m = Host.GetModel(id);
                foreach (var part in m.Parts.Where(x => x.Liquid == 0)) { Host.PrepareTexture(part.Texture, false); if (part.Blend is 1 or 2 or 4 or 7) Host.PrepareTexture(part.Texture, true); }
            }
            catch (Exception) { }
        });
        Parallel.ForEach(tiles.ToList(), options, tile =>
        {
            try { foreach (var (set, _) in Host.GetTerrain(place.Map, tile).Groups) foreach (var d in set.Diffuse) Host.PrepareTexture(d, false); }
            catch (Exception) { }
        });
    }

    private GameObject Spawn(uint id, bool solid, Vector3 position, Rotation rotation, float scale, ref int colliders)
    {
        // Named models carry the client's collisions; ground cover uses the explorer's light copy.
        var info = Host.GetModel(id);
        var go = new GameObject(true, $"WoW {id}");
        go.WorldPosition = position; go.WorldRotation = rotation; go.WorldScale = new Vector3(scale);
        // What is drawn uses the client's reduced versions with distance (the scene is static, so this is decided
        // at load); the collision keeps the full model, which the collider never draws.
        float yards = position.Length / Inches;
        int lod = solid || info.Parts.Count == 0 ? 0 : Math.Min(info.Parts.Max(p => p.Lod), yards > 700 ? 2 : yards > LodRadius ? 1 : 0);
        var visual = Resource<Model>(lod > 0 ? $"models/{id}_lod{lod}.vmdl" : ModelPath(id, solid, position));
        var renderer = go.Components.Create<ModelRenderer>();
        renderer.Model = visual;
        // Small or distant objects cast no shadow: shadow cascades redraw everything they touch.
        if (info.Radius * scale < 4 || yards > 350) renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
        if (solid && info.CollisionIndices.Length > 0)
        {
            var c = go.Components.Create<ModelCollider>(); c.Static = true; c.Model = lod > 0 ? Resource<Model>(ModelPath(id, true)) : visual; colliders++;
        }
        return go;
    }

    /// <summary>The model's particle emitters as s&amp;box effects (same mapping as the explorer's AzerothParticles).</summary>
    private int Effects(GameObject model, uint id)
    {
        var data = Host.GetModel(id);
        if (data.Emitters.Count == 0) return 0;
        var slots = ParticleTextureLoader.Slots(data);
        int count = 0;
        foreach (var e in data.Emitters)
        {
            int slot = slots.IndexOf(e.Texture);
            if (slot < 0) continue;
            Sprite sprite;
            try { sprite = Resource<Sprite>($"models/{id}/sprite{slot}"); }
            catch (Exception) { continue; }
            var go = new GameObject(true, "Particules");
            go.SetParent(model, false);
            go.LocalPosition = new Vector3(e.Position.X, e.Position.Y, e.Position.Z) * Inches;
            ParticleSprites.Configure(go, e, sprite, model.WorldScale.x);
            count++;
        }
        return count;
    }

    /// <summary>The walkable surface under the spawn: the terrain, or a building floor (city WMOs sit above the
    /// terrain), the highest one below the place's Top.</summary>
    private float SpawnHeight()
    {
        float start = place.Top == 0 ? float.MaxValue : (place.Top - center.Z) * Inches, best = 0;
        foreach (var item in items.Where(i => i.Sets is not null && new Vector3(i.Position.x, i.Position.y, 0).Length < 600 * Inches))
        {
            ModelData m;
            try { m = Host.GetModel(item.Id); } catch (Exception) { continue; }
                for (int t = 0; t + 2 < m.CollisionIndices.Length; t += 3)
                {
                    Vector3 P(int k) { var v = m.CollisionPositions[m.CollisionIndices[t + k]]; return item.Position + item.Rotation * (new Vector3(v.X, v.Y, v.Z) * Inches * item.Scale); }
                    Vector3 a = P(0), b = P(1), c = P(2);
                    // Vertical ray through the origin: barycentric test in XY.
                    float d = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
                    if (MathF.Abs(d) < 1e-6f) continue;
                    float u = ((b.y - c.y) * (0 - c.x) + (c.x - b.x) * (0 - c.y)) / d, v2 = ((c.y - a.y) * (0 - c.x) + (a.x - c.x) * (0 - c.y)) / d;
                    if (u < 0 || v2 < 0 || u + v2 > 1) continue;
                    float z = u * a.z + v2 * b.z + (1 - u - v2) * c.z;
                    if (z < start && z > best) best = z;
                }
        }
        return best;
    }

    private float Ground(Wdt wdt, NVector3 at)
    {
        var tile = wdt.Tiles.Values.Select(t => (t.Id, C: TileCenter(t.Id))).OrderBy(t => MathF.Abs(t.C.X - at.X) + MathF.Abs(t.C.Y - at.Y)).First();
        var terrain = Host.GetTerrain(place.Map, tile.Id);
        float best = float.MaxValue, height = 0;
        foreach (var p in terrain.Positions)
        {
            float d = MathF.Abs(tile.C.X + p.X - at.X) + MathF.Abs(tile.C.Y + p.Y - at.Y);
            if (d < best) { best = d; height = p.Z; }
        }
        return height;
    }

    internal static NVector3 TileCenter(string id)
    {
        var parts = id.Split('_'); int a = int.Parse(parts[0]), b = int.Parse(parts[1]);
        const float half = 51200f / 3;
        return new NVector3(half - (b + 0.5f) * TerrainTile.TileSize, half - (a + 0.5f) * TerrainTile.TileSize, 0);
    }
}

/// <summary>The sky of one place: the banded sky shader with that zone's noon colours.</summary>
internal sealed class PlaceSkyLoader(MapSceneLoader.Place place) : ResourceLoader<WowForeverMount>
{
    protected override object Load()
    {
        var light = Ambience.At(place.Map, new NVector3(place.X, place.Y, 0));
        var material = Material.Create($"mount://wowforever/materials/sky_{place.Key}.vmat", "shaders/wowforever/sky.shader");
        material.Set("g_vSkyTop", V(light.Top)); material.Set("g_vSkyMiddle", V(light.Middle));
        material.Set("g_vSkyBand1", V(light.Band1)); material.Set("g_vSkyBand2", V(light.Band2));
        material.Set("g_vSkySmog", V(light.Smog)); material.Set("g_vSkyFog", V(light.Fog)); material.Set("g_vSunColor", V(light.Sun));
        return material;
    }

    private static Vector3 V(NVector3 v) => new(v.X, v.Y, v.Z);
}
