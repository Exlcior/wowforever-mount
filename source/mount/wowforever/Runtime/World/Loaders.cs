using System.Text.Json.Nodes;
using Sandbox;
using Sandbox.Mounting;
namespace MiniBox.WowForever;

using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

internal static class Geometry
{
    public const float Inches = 36f; // one client yard in s&box units, as the explorer uses

    /// <summary>Vertex buffer contents, built on a worker; only the native upload stays on the main thread.</summary>
    public sealed record Surface(Vertex[] Vertices, int[] Indices, BBox Bounds, float Density);

    public static Mesh Mesh(Material material, Surface s)
    {
        var mesh = new Mesh(material) { Bounds = s.Bounds, UvDensity = s.Density };
        mesh.CreateVertexBuffer(s.Vertices.Length, s.Vertices.AsSpan());
        mesh.CreateIndexBuffer(s.Indices.Length, s.Indices.AsSpan());
        return mesh;
    }

    public static Surface Build(NVector3[] p, NVector3[] n, NVector2[] uv, int[] indices, float uvDensity)
    {
        var vertices = new Vertex[p.Length];
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (int i = 0; i < p.Length; i++)
        {
            var pos = new Vector3(p[i].X, p[i].Y, p[i].Z) * Inches;
            var nrm = new Vector3(n[i].X, n[i].Y, n[i].Z).Normal;
            var tangent = Vector3.Cross(MathF.Abs(nrm.z) > 0.9f ? Vector3.Left : Vector3.Up, nrm).Normal;
            vertices[i] = new Vertex { Position = pos, Normal = nrm, TexCoord0 = new Vector4(uv[i].X, uv[i].Y, 0, 0), Tangent = new Vector4(tangent.x, tangent.y, tangent.z, 1), Color = Color.White };
            min = Vector3.Min(min, pos); max = Vector3.Max(max, pos);
        }
        return new Surface(vertices, indices, new BBox(min, max), uvDensity);
    }

    public static Vector3[] Points(NVector3[] p) => p.Select(v => new Vector3(v.X, v.Y, v.Z) * Inches).ToArray();
}

/// <summary>The banded sky dome; AzerothAmbience copies it and feeds the zone colours every frame.</summary>
internal sealed class SkyMaterialLoader : ResourceLoader<WowForeverMount>
{
    protected override object Load() => Material.Create("mount://wowforever/materials/sky.vmat", "shaders/wowforever/sky.shader");
}

/// <summary>JSON data handed to the explorer through a prefab's metadata ("WowData").</summary>
internal sealed class DataLoader(Func<string> json) : ResourceLoader<WowForeverMount>
{
    protected override object Load() => WowForeverMount.DataPrefab(Name, json());
}

/// <summary>Reading and decoding run on a worker; engine objects are created back on the caller's thread.</summary>
internal abstract class WorkerLoader<T> : ResourceLoader<WowForeverMount>
{
    protected abstract T Prepare();
    protected abstract object Create(T prepared);
    /// <summary>Textures the creation will bind; uploaded beforehand, a few per frame.</summary>
    protected virtual IEnumerable<(uint Id, bool Alpha)> Textures(T prepared) => Array.Empty<(uint, bool)>();
    /// <summary>Further main-thread pieces (masks, materials, meshes), each run in its own slice of the frame budget.</summary>
    protected virtual IEnumerable<Action> Steps(T prepared) => Array.Empty<Action>();
    protected override object Load() => Create(Prepare());
    // Engine objects are created on the main thread within a shared per-frame budget, so many loads finishing
    // together spread over several frames instead of stalling one.

    protected override async Task<object> LoadAsync()
    {
        // A caller that blocks the main thread on this load (Model.Load, a scene being deserialized) would never
        // let the frame-budgeted steps below run: such loads are built synchronously instead.
        if (!AsynchronousCaller()) return Load();
        var prepared = await Task.Run(Prepare);
        foreach (var (id, alpha) in Textures(prepared))
        {
            if (id == 0) continue;
            await MountFrameBudget.Slot();
            var upload = System.Diagnostics.Stopwatch.StartNew();
            Host.GetTexture(id, alpha);
            MountFrameBudget.Spent += upload.Elapsed.TotalMilliseconds;
        }
        foreach (var step in Steps(prepared))
        {
            await MountFrameBudget.Slot();
            var piece = System.Diagnostics.Stopwatch.StartNew();
            step();
            MountFrameBudget.Spent += piece.Elapsed.TotalMilliseconds;
        }
        await MountFrameBudget.Slot();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = Create(prepared);
        double ms = watch.Elapsed.TotalMilliseconds;
        MountFrameBudget.Spent += ms;
        WowForeverMount.Record(GetType().Name, ms, Path);
        return result;
    }

    /// <summary>True when the engine reached this loader through its asynchronous path (ResourceLibrary.LoadAsync).</summary>
    internal static bool AsynchronousCaller()
    {
        foreach (var frame in new System.Diagnostics.StackTrace(false).GetFrames())
        {
            var method = frame.GetMethod();
            if (method is null) continue;
            if (method.Name.Contains("TryLoadAsync") || method.DeclaringType?.Name.Contains("TryLoadAsync") == true) return true;
        }
        return false;
    }

}

// Non-generic on purpose: terrain, props and metadata must share one frame allowance.
internal static class MountFrameBudget
{
    private static float frame = -1;
    internal static double Spent;
    internal static async Task Slot()
    {
        while (true)
        {
            if (Time.Now != frame) { frame = Time.Now; Spent = 0; }
            if (Spent < 4) return;
            await GameTask.Yield();
        }
    }
}

internal sealed class PlacementLoader(string map, string tile) : WorkerLoader<string>
{
    protected override object Create(string json) => WowForeverMount.DataPrefab(Name, json);
    protected override string Prepare() => new JsonObject { ["Placements"] = ForTile(Host, map, tile) }.ToJsonString();

    /// <summary>Placements of one tile in the explorer's source frame: ground cover, then obj0/obj1 deduplicated.</summary>
    public static JsonArray ForTile(WowForeverMount host, string map, string tile)
    {
        var list = new JsonArray(); var seen = new HashSet<string>();
        var wdt = host.GetWdt(map);
        if (tile == "global")
        {
            var p = new AdtObj.Placement { Wmo = true, Model = wdt.GlobalWmo, Position = wdt.GlobalWmoPosition, Rotation = wdt.GlobalWmoRotation, DoodadSets = new[] { (int)wdt.GlobalWmoDoodadSet } };
            list.Add(WorldIndex.Placement(p));
            return list;
        }
        var files = wdt.Tiles.Values.First(t => t.Id == tile);
        try
        {
            // Ground cover drawn from the layers' GroundEffect entries.
            var root = host.Storage.TryRead(files.Root); var tex = host.Storage.TryRead(files.Tex0);
            if (root is not null && tex is not null)
            {
                var mapId = Tables.Maps.First(m => m.Key == map).Id;
                foreach (var f in Foliage.Generate(mapId, map, tile, AdtRoot.Read(root), AdtTex.Read(tex, wdt.Flags))) list.Add(f);
            }
        }
        catch (InvalidDataException) { }
        foreach (var file in new[] { files.Obj0, files.Obj1 })
        {
            var bytes = file == 0 ? null : host.Storage.TryRead(file);
            if (bytes is null) continue;
            foreach (var p in AdtObj.Read(bytes).Placements)
                if (seen.Add((p.Wmo ? "wmo:" : "m2:") + p.UniqueId)) list.Add(WorldIndex.Placement(p));
        }
        return list;
    }
}

/// <summary>Bounds, decor and visibility of one model, as the offline model.json carried them.</summary>
internal sealed class ModelInfoLoader(uint id) : WorkerLoader<string>
{
    protected override object Create(string json) => WowForeverMount.DataPrefab(Name, json);
    protected override string Prepare()
    {
        JsonObject info;
        try
        {
            var m = Host.GetModel(id);
            var doodads = new JsonArray();
            foreach (var d in m.Doodads)
                doodads.Add(new JsonObject
                {
                    ["Id"] = d.Id.ToString(), ["Model"] = d.Model.ToString(),
                    ["Position"] = new JsonArray(d.Position.X, d.Position.Y, d.Position.Z),
                    ["Rotation"] = new JsonArray(d.Rotation.X, d.Rotation.Y, d.Rotation.Z, d.Rotation.W),
                    ["Scale"] = d.Scale, ["Sets"] = new JsonArray(d.Sets.Select(s => (JsonNode)s).ToArray()),
                });
            info = new JsonObject
            {
                ["Id"] = id.ToString(), ["Type"] = m.Type, ["NoDraw"] = m.Parts.Count == 0, ["UnresolvedTextures"] = m.Unresolved,
                ["Lods"] = m.Parts.Count == 0 ? 0 : m.Parts.Max(p => p.Lod),
                ["HasCollision"] = m.CollisionIndices.Length > 0,
                ["Bounds"] = new JsonObject { ["Min"] = new JsonArray(m.Min.X, m.Min.Y, m.Min.Z), ["Max"] = new JsonArray(m.Max.X, m.Max.Y, m.Max.Z), ["Radius"] = m.Radius },
                ["Doodads"] = doodads,
            };
            if (m.Emitters.Count > 0)
            {
                // Sprites reference the model's particle textures by slot (models/<id>/particle<slot>.vtex).
                var slots = ParticleTextureLoader.Slots(m);
                var emitters = new JsonArray();
                foreach (var e in m.Emitters)
                {
                    int slot = slots.IndexOf(e.Texture);
                    if (slot < 0) continue;
                    emitters.Add(new JsonObject
                    {
                        ["Texture"] = $"mount://wowforever/models/{id}/particle{slot}.vtex", ["Rows"] = e.Rows, ["Columns"] = e.Columns,
                        ["Position"] = new JsonArray(e.Position.X, e.Position.Y, e.Position.Z), ["Placed"] = new JsonArray(e.Placed.X, e.Placed.Y, e.Placed.Z), ["Bone"] = e.Bone, ["Blend"] = e.Blend, ["Flags"] = e.Flags,
                        ["Speed"] = e.Speed, ["SpeedVariation"] = e.SpeedVariation, ["VerticalRange"] = e.VerticalRange, ["HorizontalRange"] = e.HorizontalRange,
                        ["Gravity"] = e.Gravity, ["Lifespan"] = e.Lifespan, ["Rate"] = e.Rate, ["AreaWidth"] = e.AreaWidth, ["AreaLength"] = e.AreaLength,
                        ["Colors"] = new JsonArray(e.Colors.Select(c => (JsonNode)new JsonArray(c.X, c.Y, c.Z)).ToArray()),
                        ["Alpha"] = new JsonArray(e.Alpha.Select(a => (JsonNode)a).ToArray()), ["Scale"] = new JsonArray(e.Scale.Select(a => (JsonNode)a).ToArray()),
                    });
                }
                info["Emitters"] = emitters;
            }
            if (m.Billboards.Count > 0)
            {
                var slots = ParticleTextureLoader.Slots(m);
                var cards = new JsonArray();
                foreach (var b in m.Billboards)
                {
                    int slot = slots.IndexOf(b.Texture);
                    if (slot < 0) continue;
                    cards.Add(new JsonObject
                    {
                        ["Texture"] = $"mount://wowforever/models/{id}/particle{slot}.vtex", ["Position"] = new JsonArray(b.Position.X, b.Position.Y, b.Position.Z),
                        ["Size"] = b.Size, ["Blend"] = b.Blend, ["Opacity"] = b.Opacity, ["Unlit"] = b.Unlit,
                    });
                }
                info["Billboards"] = cards;
            }
            if (m.Cameras.Count > 0)
                info["Cameras"] = new JsonArray(m.Cameras.Select(cam => (JsonNode)new JsonObject
                {
                    ["Type"] = cam.Type, ["Position"] = new JsonArray(cam.Position.X, cam.Position.Y, cam.Position.Z),
                    ["Target"] = new JsonArray(cam.Target.X, cam.Target.Y, cam.Target.Z), ["Fov"] = cam.Fov,
                    ["Stand"] = Stand(m, cam.Target) is var stand ? new JsonArray(stand.X, stand.Y, stand.Z) : null,
                }).ToArray());
            if (m.Lights.Count > 0)
                info["Lights"] = new JsonArray(m.Lights.Select(l => (JsonNode)new JsonObject
                {
                    ["Type"] = l.Type, ["Position"] = new JsonArray(l.Position.X, l.Position.Y, l.Position.Z),
                    ["Ambient"] = new JsonArray(l.Ambient.X, l.Ambient.Y, l.Ambient.Z), ["Diffuse"] = new JsonArray(l.Diffuse.X, l.Diffuse.Y, l.Diffuse.Z),
                    ["Start"] = l.Start, ["End"] = l.End,
                }).ToArray());
            if (m.Ribbons.Count > 0)
            {
                var slots = ParticleTextureLoader.Slots(m);
                var ribbons = new JsonArray();
                foreach (var r in m.Ribbons)
                {
                    int slot = slots.IndexOf(r.Texture);
                    if (slot < 0) continue;
                    ribbons.Add(new JsonObject
                    {
                        ["Texture"] = $"mount://wowforever/models/{id}/particle{slot}.vtex", ["Bone"] = r.Bone, ["Blend"] = r.Blend,
                        ["Position"] = new JsonArray(r.Position.X, r.Position.Y, r.Position.Z), ["Placed"] = new JsonArray(r.Placed.X, r.Placed.Y, r.Placed.Z), ["Above"] = r.Above, ["Below"] = r.Below,
                        ["EdgesPerSecond"] = r.EdgesPerSecond, ["Lifetime"] = r.Lifetime, ["Gravity"] = r.Gravity,
                        ["Color"] = new JsonArray(r.Color.X, r.Color.Y, r.Color.Z), ["Alpha"] = r.Alpha,
                    });
                }
                info["Ribbons"] = ribbons;
            }
        }
        catch (Exception error) { info = new JsonObject { ["Id"] = id.ToString(), ["NoDraw"] = true, ["Error"] = error.Message }; }
        return info.ToJsonString();
    }

    // Where a character stands in the scene seen by this camera: on the floor under the camera's aim.
    private static System.Numerics.Vector3 Stand(ModelData m, System.Numerics.Vector3 aim)
    {
        // The highest surface straight below the aim: a vertical ray against the scene's triangles (glows excluded).
        float floor = float.MinValue;
        foreach (var part in m.Parts.Where(p => p.Lod == 0).Concat(m.Faint).Where(p => p.Blend <= 2))
            for (int t = 0; t + 2 < part.Indices.Length; t += 3)
            {
                var a = part.Positions[part.Indices[t]]; var b = part.Positions[part.Indices[t + 1]]; var c = part.Positions[part.Indices[t + 2]];
                float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(d) < 1e-6f) continue;
                float u = ((b.Y - c.Y) * (aim.X - c.X) + (c.X - b.X) * (aim.Y - c.Y)) / d;
                float v = ((c.Y - a.Y) * (aim.X - c.X) + (a.X - c.X) * (aim.Y - c.Y)) / d;
                if (u < 0 || v < 0 || u + v > 1) continue;
                float z = u * a.Z + v * b.Z + (1 - u - v) * c.Z;
                if (z < aim.Z && z > floor) floor = z;
            }
        return new System.Numerics.Vector3(aim.X, aim.Y, floor == float.MinValue ? aim.Z - 1.2f : floor);
    }
}

/// <summary>A particle texture of one model, uncompressed so the explorer can cut its sprite sheet into frames.</summary>
internal sealed class ParticleTextureLoader(uint id, int slot) : ResourceLoader<WowForeverMount>
{
    public const int MaxSlots = 4;
    /// <summary>Particle textures first, then the ribbons' and the camera-facing cards' (particle&lt;slot&gt; paths).</summary>
    public static List<uint> Slots(ModelData m) => m.Emitters.Select(e => e.Texture).Concat(m.Ribbons.Select(r => r.Texture)).Concat(m.Billboards.Select(b => b.Texture))
        .Distinct().Take(MaxSlots).ToList();
    protected override object Load()
    {
        var slots = Slots(Host.GetModel(id));
        return slot < slots.Count ? Host.ParticleTexture(slots[slot]) : Texture.White;
    }
}

/// <summary>An M2 or WMO; parts sharing a material become one mesh. Streaming uses separate visual and
/// collision resources; named assets carry native LODs and collision. Movable M2s use convex pieces,
/// while buildings and static streaming collision preserve the client's exact triangle mesh.</summary>
internal sealed class ModelLoader(uint id, bool collide = false, int lod = 0, bool staticCollision = false, bool nativeLods = false) : WorkerLoader<ModelLoader.Prepared>
{
    internal sealed record Group(int Liquid, uint Texture, int Blend, uint Flags, NVector2 Scroll, int Lod, Geometry.Surface Surface);
    internal sealed record Prepared(List<Group> Groups, List<Vector3[]> Hulls, Vector3[] MeshPoints, int[] MeshIndices, float Radius);

    protected override Prepared Prepare()
    {
        var m = Host.GetModel(id); var groups = new List<Group>();
        // One level per model: the full one, or the client's reduced version (the closest available) for distant copies.
        int level = m.Parts.Where(p => p.Lod <= lod).Select(p => p.Lod).DefaultIfEmpty(0).Max();
        foreach (var group in m.Parts.Where(p => nativeLods ? p.Lod <= 2 : p.Lod == level).GroupBy(p => $"{p.Lod}:" + (p.Liquid != 0 ? $"l{p.Liquid}" : $"{p.Texture}:{p.Blend}:{p.Flags & 5}:{p.Scroll}")))
        {
            var first = group.First();
            if (first.Liquid != 0) Host.PrefetchLiquid(first.Liquid);
            else
            {
                Host.PrepareTexture(first.Texture, false);
                if (first.Blend is 1 or 2 or 4 or 7) Host.PrepareTexture(first.Texture, true);
            }
            var p = new List<NVector3>(); var n = new List<NVector3>(); var uv = new List<NVector2>(); var idx = new List<int>();
            foreach (var part in group)
            {
                int b = p.Count; p.AddRange(part.Positions); n.AddRange(part.Normals); uv.AddRange(part.Uv); idx.AddRange(part.Indices.Select(i => i + b));
            }
            groups.Add(new Group(first.Liquid, first.Texture, first.Blend, first.Flags, first.Scroll, first.Lod, Geometry.Build(p.ToArray(), n.ToArray(), uv.ToArray(), idx.ToArray(), Geometry.Inches)));
        }
        if (!collide) return new Prepared(groups, null, null, null, m.Radius);
        if (m.Type == "wmo" || staticCollision)
        {
            if (m.CollisionIndices.Length > 0)
                return new Prepared(groups, null, Geometry.Points(m.CollisionPositions), m.CollisionIndices, m.Radius);
            // An explicitly non-solid WMO must stay non-solid. Rendering triangles are not a substitute.
            return new Prepared(groups, null, null, null, m.Radius);
        }
        return new Prepared(groups, Pieces(Geometry.Points(m.CollisionPositions), m.CollisionIndices), null, null, m.Radius);
    }

    /// <summary>Connected pieces of a triangle soup (union of shared vertices), each kept as its point set.</summary>
    private static List<Vector3[]> Pieces(Vector3[] points, int[] indices)
    {
        var parent = Enumerable.Range(0, points.Length).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i + 2 < indices.Length; i += 3) { parent[Find(indices[i + 1])] = Find(indices[i]); parent[Find(indices[i + 2])] = Find(indices[i]); }
        var used = indices.Distinct();
        return used.GroupBy(Find).Select(g => g.Select(i => points[i]).ToArray()).Where(h => h.Length >= 4).Take(32).ToList();
    }

    protected override IEnumerable<(uint, bool)> Textures(Prepared prepared) =>
        prepared.Groups.Where(g => g.Liquid == 0).SelectMany(g => g.Blend is 1 or 2 or 4 or 7 ? new[] { (g.Texture, false), (g.Texture, true) } : new[] { (g.Texture, false) });

    private readonly List<Mesh> meshes = new();

    protected override IEnumerable<Action> Steps(Prepared prepared)
    {
        meshes.Clear();
        foreach (var g in prepared.Groups)
            yield return () => meshes.Add(Geometry.Mesh(g.Liquid != 0 ? Host.LiquidMaterial(g.Liquid) : Host.PropMaterial(g.Texture, g.Blend, g.Flags, g.Scroll), g.Surface));
    }

    protected override object Create(Prepared prepared)
    {
        var builder = new ModelBuilder().WithName(Path);
        if (meshes.Count != prepared.Groups.Count) { meshes.Clear(); foreach (var a in Steps(prepared)) a(); } // synchronous Load path
        for (int i = 0; i < meshes.Count; i++) builder.AddMesh(meshes[i], nativeLods ? prepared.Groups[i].Lod : 0);
        if (nativeLods)
        {
            float near = (160 + 6 * prepared.Radius) * Geometry.Inches;
            if (prepared.Groups.Any(g => g.Lod == 1)) builder.WithLodDistance(1, near);
            if (prepared.Groups.Any(g => g.Lod == 2)) builder.WithLodDistance(2, near * 2);
        }
        meshes.Clear();
        if (prepared.Hulls is not null) foreach (var hull in prepared.Hulls) builder.AddCollisionHull(hull);
        if (prepared.MeshPoints is { Length: > 0 }) builder.AddCollisionMesh(prepared.MeshPoints, prepared.MeshIndices);
        return builder.Create();
    }
}

/// <summary>Terrain of one tile: level 0 with its layered materials, liquids and collision;
/// levels 1 and 2 as lattice relief with the client's baked map texture and the liquids.</summary>
internal sealed class TerrainLoader(string map, string tile, int lod, bool collide = false) : WorkerLoader<TerrainLoader.Prepared>
{
    internal sealed record Prepared(TerrainTile Tile, List<(TerrainTile.LayerSet Layers, Geometry.Surface Surface)> Layers, Geometry.Surface Lod, List<(ushort Type, Geometry.Surface Surface)> Liquids);

    protected override Prepared Prepare()
    {
        var t = Host.GetTerrain(map, tile);
        float density = TerrainTile.TileSize * Geometry.Inches;
        var layers = new List<(TerrainTile.LayerSet, Geometry.Surface)>(); Geometry.Surface lodSurface = null;
        if (lod == 0)
            foreach (var (set, indices) in t.Groups)
            {
                foreach (var id in set.Diffuse) Host.PrepareTexture(id, false);
                foreach (var id in set.Height) Host.PrepareTexture(id, true);
                var (p, n, uv, idx) = Subset(t.Positions, t.Normals, t.Uv, indices);
                layers.Add((set, Geometry.Build(p, n, uv, idx, density)));
            }
        else
        {
            Host.PrepareTexture(Host.GetWdt(map).Tiles.Values.First(x => x.Id == tile).MapTexture, false);
            var (p, n, uv, idx) = t.Lod(lod == 1 ? 4 : 16, lod == 1 ? 6 : 24);
            lodSurface = Geometry.Build(p, n, uv, idx, density);
        }
        var liquids = new List<(ushort, Geometry.Surface)>();
        foreach (var (type, p, uv, idx) in t.Liquids)
        {
            Host.PrefetchLiquid(type);
            liquids.Add((type, Geometry.Build(p, Enumerable.Repeat(NVector3.UnitZ, p.Length).ToArray(), uv, idx, Geometry.Inches)));
        }
        return new Prepared(t, layers, lodSurface, liquids);
    }

    protected override IEnumerable<(uint, bool)> Textures(Prepared prepared)
    {
        if (lod != 0) { yield return (Host.GetWdt(map).Tiles.Values.First(x => x.Id == tile).MapTexture, false); yield break; }
        foreach (var (set, _) in prepared.Layers)
        {
            foreach (var id in set.Diffuse) yield return (id, false);
            if (prepared.Tile.HeightBlend) foreach (var id in set.Height) yield return (id, true);
        }
    }

    private readonly List<Mesh> meshes = new();
    private Texture[] masks;

    protected override IEnumerable<Action> Steps(Prepared prepared)
    {
        meshes.Clear();
        var t = prepared.Tile;
        if (lod == 0)
        {
            masks = new Texture[t.Masks.Count];
            for (int k = 0; k < t.Masks.Count; k++) { int i = k; yield return () => masks[i] = Host.CreateTexture(1024, 1024, t.Masks[i]); }
            foreach (var (layers, surface) in prepared.Layers)
                yield return () => meshes.Add(Geometry.Mesh(TerrainMaterial(layers, masks, t.HeightBlend), surface));
        }
        else
            yield return () =>
            {
                var files = Host.GetWdt(map).Tiles.Values.First(x => x.Id == tile);
                var material = Material.Create($"{Path}.vmat", "shaders/complex.shader");
                material.Set("g_tColor", files.MapTexture != 0 ? Host.GetTexture(files.MapTexture) : Texture.White);
                Host.Neutral(material);
                meshes.Add(Geometry.Mesh(material, prepared.Lod));
            };
        foreach (var (type, surface) in prepared.Liquids) yield return () => meshes.Add(Geometry.Mesh(Host.LiquidMaterial(type), surface));
    }

    protected override object Create(Prepared prepared)
    {
        int expected = (lod == 0 ? prepared.Layers.Count : 1) + prepared.Liquids.Count;
        if (meshes.Count != expected) { meshes.Clear(); foreach (var a in Steps(prepared)) a(); } // synchronous Load path
        var builder = new ModelBuilder().WithName(Path);
        foreach (var mesh in meshes) builder.AddMesh(mesh);
        meshes.Clear(); masks = null;
        if (collide && lod == 0)
        {
            // The walkable ground of maps served to Sandbox: the welded lattice without its holes.
            var t = prepared.Tile;
            builder.AddCollisionMesh(Geometry.Points(t.Positions), t.Groups.SelectMany(g => g.Indices).ToArray());
        }
        return builder.Create();
    }

    private Material TerrainMaterial(TerrainTile.LayerSet layers, Texture[] masks, bool heightBlend)
    {
        int count = Math.Min(8, layers.Diffuse.Length);
        var material = Material.Create($"{Path}.layers{layers.Key.GetHashCode():x8}.vmat", "shaders/wowforever/terrain.shader");
        if (count == 0) { material.SetFeature("F_LAYER_COUNT", 1); material.Set("g_tLayer0", Texture.White); return material; }
        bool useHeight = heightBlend && layers.Height.Take(count).Any(h => h != 0);
        material.SetFeature("F_LAYER_COUNT", count);
        material.SetFeature("F_HEIGHT_BLEND", useHeight ? 1 : 0);
        for (int j = 0; j < count; j++)
        {
            material.Set($"g_tLayer{j}", Host.GetTexture(layers.Diffuse[j]));
            if (useHeight) material.Set($"g_tHeight{j}", layers.Height[j] != 0 ? Host.GetTexture(layers.Height[j], alphaOnly: true) : Texture.White);
        }
        for (int k = 0; k < masks.Length && k < 3; k++) material.Set($"g_tBlend{k}", masks[k]);
        float L(float[] v, int i) => i < count ? v[i] : (i == 0 ? 1 : v[0]);
        for (int k = 0; k < 2; k++)
        {
            material.Set($"g_vLayerScale{k}", new Vector4(L(layers.Scale, k * 4), L(layers.Scale, k * 4 + 1), L(layers.Scale, k * 4 + 2), L(layers.Scale, k * 4 + 3)));
            material.Set($"g_vHeightScale{k}", new Vector4(L(layers.HeightScale, k * 4), L(layers.HeightScale, k * 4 + 1), L(layers.HeightScale, k * 4 + 2), L(layers.HeightScale, k * 4 + 3)));
            material.Set($"g_vHeightOffset{k}", new Vector4(L(layers.HeightOffset, k * 4), L(layers.HeightOffset, k * 4 + 1), L(layers.HeightOffset, k * 4 + 2), L(layers.HeightOffset, k * 4 + 3)));
        }
        return material;
    }

    private static (NVector3[], NVector3[], NVector2[], int[]) Subset(NVector3[] p, NVector3[] n, NVector2[] uv, int[] indices)
    {
        var map = new Dictionary<int, int>(); var P = new List<NVector3>(); var N = new List<NVector3>(); var U = new List<NVector2>(); var I = new int[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            if (!map.TryGetValue(indices[i], out int k)) { k = P.Count; map[indices[i]] = k; P.Add(p[indices[i]]); N.Add(n[indices[i]]); U.Add(uv[indices[i]]); }
            I[i] = k;
        }
        return (P.ToArray(), N.ToArray(), U.ToArray(), I);
    }
}
