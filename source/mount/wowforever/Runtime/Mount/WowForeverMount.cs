using System.Text.Json;
using System.Text.Json.Nodes;
using Sandbox;
using Sandbox.Mounting;
namespace MiniBox.WowForever;

using NVector3 = System.Numerics.Vector3;

/// <summary>World of Warcraft Forever, read on demand from the player's own installation.
/// Paths: world.prefab (catalogue), maps/&lt;map&gt;/&lt;tile&gt;/{terrain,terrain_lod1,terrain_lod2}.vmdl and placements.prefab,
/// models/&lt;FileDataID&gt;.vmdl and .prefab (description). Data prefabs carry JSON in their "WowData" metadata.</summary>
public sealed class WowForeverMount : BaseGameMount
{
    public override string Ident => "wowforever";
    public override string Title => "World of Warcraft Forever";
    public WowStorage Storage { get; private set; }
    public string LastError { get; private set; }
    private string root;
    private readonly object gate = new();
    private readonly Dictionary<uint, Texture> textures = new();
    private readonly Dictionary<uint, BlpImage> images = new();
    private readonly Queue<uint> imageOrder = new();
    private readonly Dictionary<string, Material> materials = new();
    private readonly Dictionary<string, Wdt> wdts = new();
    private readonly Dictionary<string, TerrainTile> terrains = new();
    private readonly Queue<string> terrainOrder = new();
    private readonly Dictionary<uint, ModelData> models = new();
    private readonly Queue<uint> modelOrder = new();
    internal readonly Dictionary<uint, string> ModelTypes = new();
    /// <summary>Client path (without extension) of every world model the installation holds.</summary>
    internal readonly Dictionary<uint, string> NamedModels = new();
    internal CreatureCatalog Creatures { get; private set; }
    private const uint EmotesFile = 1343602;
    private readonly Dictionary<uint, Db2> tables = new();

    /// <summary>A client table (WDC5), read once from the installation.</summary>
    internal Db2 Table(uint fileDataId)
    {
        lock (tables) if (tables.TryGetValue(fileDataId, out var cached)) return cached;
        var table = new Db2(Storage.Read(fileDataId));
        lock (tables) tables[fileDataId] = table;
        return table;
    }

    private static IEnumerable<(uint Id, string Path)> ModelNames()
    {
        using var stream = typeof(WowForeverMount).Assembly.GetManifestResourceStream("wowforever.model_names.txt.gz");
        using var reader = new StreamReader(new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress));
        while (reader.ReadLine() is { } line)
        {
            int split = line.IndexOf(';');
            if (split > 0 && uint.TryParse(line.AsSpan(0, split), out var id)) yield return (id, line[(split + 1)..]);
        }
    }

    protected override void Initialize(InitializeContext context)
    {
        root = WowStorage.Locate();
        IsInstalled = root is not null;
    }

    protected override Task Mount(MountContext context)
    {
        if (root is null) return Task.CompletedTask;
        try
        {
            Storage = WowStorage.Open(root);
            var index = WorldIndex.Build(this);
            // Data the explorer and the maps read: hidden from the Sandbox spawn menu, still visible to developers.
            Hidden(context, ResourceType.PrefabFile, "world", new DataLoader(() => index.Catalog));
            Hidden(context, ResourceType.PrefabFile, "ambience", new DataLoader(() =>
            {
                // Built locally by tools/mount_data/prepare_ambience.py; without it the world keeps a neutral light.
                using var stream = typeof(WowForeverMount).Assembly.GetManifestResourceStream("wowforever.ambience.json");
                return stream is null ? "{\"Continents\":{},\"Params\":{}}" : new StreamReader(stream).ReadToEnd();
            }));
            Hidden(context, ResourceType.Material, "materials/sky", new SkyMaterialLoader());
            // The client's interface texts in the installed language, for screens laid out like the client's.
            var global = new Dictionary<string, string>();
            try { global = GameStrings.Global(this); } catch (Exception error) { Log.Warning($"Interface strings unavailable: {error.Message}"); }
            string strings = System.Text.Json.JsonSerializer.Serialize(global);
            Hidden(context, ResourceType.PrefabFile, "strings", new DataLoader(() => strings));
            string places = System.Text.Json.JsonSerializer.Serialize(MapSceneLoader.Places.Select(p => new { p.Key, p.Map, p.X, p.Y, Z = p.Top, Title = GameStrings.Area(this, p.Area, p.Title) }));
            Hidden(context, ResourceType.PrefabFile, "places", new DataLoader(() => places));
            using var characterStream = typeof(WowForeverMount).Assembly.GetManifestResourceStream("wowforever.characters.json");
            var characters = System.Text.Json.JsonSerializer.Deserialize<List<CharacterPreset>>(characterStream)
                .Where(c => Storage.Exists(c.Model) && Storage.Exists(c.Skin)).ToArray();
            // Playable characters, dressable: customization from the client's tables, then every wearable item.
            AppearanceCatalog appearance = null; ItemCatalog items = null;
            try { appearance = AppearanceCatalog.Read(this, characters); items = ItemCatalog.Read(this, appearance.MaterialFiles); }
            catch (Exception error) { Log.Warning($"Character customization unavailable: {error.Message}"); }
            foreach (var character in characters)
            {
                var look = appearance?.Looks.GetValueOrDefault(character.Key);
                context.Add(ResourceType.Model, "characters/" + character.Key, new CharacterLoader(character, look));
                Hidden(context, ResourceType.Model, "characters/" + character.Key + "_dressable", new CharacterLoader(character, look, dressable: true));
                Hidden(context, ResourceType.PrefabFile, "characters/" + character.Key, new CharacterInfoLoader(character, look));
            }
            Hidden(context, ResourceType.PrefabFile, "characters", new DataLoader(() => System.Text.Json.JsonSerializer.Serialize(characters.Select(c => new
            {
                c.Key, Title = GameStrings.Character(this, global, appearance?.Looks.GetValueOrDefault(c.Key)?.Race ?? 0, appearance?.Looks.GetValueOrDefault(c.Key)?.Sex ?? 0, c.Title), Path = "mount://wowforever/characters/" + c.Key + ".vmdl", Info = "mount://wowforever/characters/" + c.Key + ".prefab",
                Race = appearance?.Looks.GetValueOrDefault(c.Key)?.Race ?? 0, Sex = appearance?.Looks.GetValueOrDefault(c.Key)?.Sex ?? 0,
            }))));
            if (items is not null)
            {
                Hidden(context, ResourceType.PrefabFile, "items", new DataLoader(() => System.Text.Json.JsonSerializer.Serialize(items.Items.Select(i => new { i.Id, i.Name, i.Slot, i.Quality, i.Display }))));
                foreach (var (display, json) in items.Displays)
                {
                    string text = json.ToJsonString();
                    Hidden(context, ResourceType.PrefabFile, $"items/{display}", new DataLoader(() => text));
                }
                foreach (var (file, texture) in items.Models) Hidden(context, ResourceType.Model, $"items/models/{file}_{texture}", new ItemModelLoader(file, texture));
                // Every item with a model in the spawn menu (weapons, shields, helms, shoulders...), under its client name.
                foreach (var (path, file, texture) in items.Spawnable()) context.Add(ResourceType.Model, path, new ItemModelLoader(file, texture));
                foreach (var file in appearance.Readable.Concat(items.Readable).Distinct()) Hidden(context, ResourceType.Texture, $"textures/{file}", new ReadableTextureLoader(file));
                Log.Info($"Characters: {appearance.Looks.Count} customizable, {items.Items.Count} items, {items.Displays.Count} item displays, {items.Models.Count} item models, {appearance.Readable.Count + items.Readable.Count} readable textures");
            }
            foreach (var tile in index.Tiles)
            {
                string folder = $"maps/{tile.Map}/{tile.Id}";
                Hidden(context, ResourceType.Model, folder + "/terrain", new TerrainLoader(tile.Map, tile.Id, 0));
                Hidden(context, ResourceType.Model, folder + "/terrain_collision", new TerrainLoader(tile.Map, tile.Id, 0, collide: true));
                Hidden(context, ResourceType.Model, folder + "/terrain_lod1", new TerrainLoader(tile.Map, tile.Id, 1));
                Hidden(context, ResourceType.Model, folder + "/terrain_lod2", new TerrainLoader(tile.Map, tile.Id, 2));
                Hidden(context, ResourceType.PrefabFile, folder + "/placements", new PlacementLoader(tile.Map, tile.Id));
            }
            foreach (var (id, type) in ModelTypes)
            {
                Hidden(context, ResourceType.Model, $"models/{id}", new ModelLoader(id));
                Hidden(context, ResourceType.Model, $"models/{id}_collision", new ModelLoader(id, collide: true, staticCollision: true, nativeLods: true));
                // The client's reduced versions for distant copies (HLOD); a model without them serves its full level.
                Hidden(context, ResourceType.Model, $"models/{id}_lod1", new ModelLoader(id, lod: 1));
                Hidden(context, ResourceType.Model, $"models/{id}_lod2", new ModelLoader(id, lod: 2));
                Hidden(context, ResourceType.PrefabFile, $"models/{id}", new ModelInfoLoader(id));
                if (type == "m2")
                    for (int slot = 0; slot < ParticleTextureLoader.MaxSlots; slot++)
                    {
                        Hidden(context, ResourceType.Texture, $"models/{id}/particle{slot}", new ParticleTextureLoader(id, slot));
                        Hidden(context, ResourceType.GameResource, $"models/{id}/sprite{slot}", new SpriteLoader(id, slot));
                    }
            }
            // Playable places for any game (the default Sandbox mode included), each with its sky.
            foreach (var place in MapSceneLoader.Places)
            {
                if (GetWdt(place.Map) is null) continue;
                context.Add(ResourceType.Scene, $"maps/{place.Map}/{place.Key}", new MapSceneLoader(place));
                Hidden(context, ResourceType.Material, $"materials/sky_{place.Key}", new PlaceSkyLoader(place));
            }
            // Every world model of the installed game under its client name, with collisions: the spawn menu's tree.
            int named = 0;
            foreach (var (id, path) in ModelNames())
            {
                if (!Storage.Exists(id)) continue;
                // Creature and character files are animated: they are served through their displays below.
                if (!path.StartsWith("world/")) { NamedModels[id] = path[..path.LastIndexOf('.')]; continue; }
                lock (gate) ModelTypes.TryAdd(id, path.EndsWith(".wmo") ? "wmo" : "m2");
                context.Add(ResourceType.Model, path[..path.LastIndexOf('.')], new ModelLoader(id, collide: true, nativeLods: true));
                NamedModels[id] = path[..path.LastIndexOf('.')];
                named++;
            }
            // Character creation scenes of the client (interface/glues/models/ui_<race>: animated scenery, particles and
            // camera) and the interface textures of its screens (buttons, frames, race icons).
            int glues = 0;
            foreach (var (id, path) in NamedModels.Where(n => n.Value.StartsWith("interface/glues/")).ToList())
            {
                string name = path["interface/glues/".Length..];
                if (name.StartsWith("models/"))
                {
                    string key = name[(name.LastIndexOf('/') + 1)..];
                    lock (gate) ModelTypes.TryAdd(id, "m2");
                    context.Add(ResourceType.Model, $"glues/{key}", new EffectLoader(id)); // spawnable scenery, like the client's screens
                    // Still version for the creation screen: some scenes' bones animate into spikes when sampled.
                    Hidden(context, ResourceType.Model, $"glues/{key}_static", new ModelLoader(id));
                    Hidden(context, ResourceType.PrefabFile, $"glues/{key}", new ModelInfoLoader(id));
                    for (int slot = 0; slot < ParticleTextureLoader.MaxSlots; slot++)
                    {
                        Hidden(context, ResourceType.Texture, $"models/{id}/particle{slot}", new ParticleTextureLoader(id, slot));
                        Hidden(context, ResourceType.GameResource, $"models/{id}/sprite{slot}", new SpriteLoader(id, slot));
                    }
                    glues++;
                }
                else if (!name.StartsWith("loadingscreens/")) Hidden(context, ResourceType.Texture, "ui/" + name, new LoadingScreenLoader(id));
            }
            // Interface atlas pieces of the character creation screen, with their sizes for layouts.
            try
            {
                var atlas = UiAtlases.Read(this);
                foreach (var (name, piece) in atlas) Hidden(context, ResourceType.Texture, $"ui/atlas/{name}", new AtlasPieceLoader(piece));
                string sizes = System.Text.Json.JsonSerializer.Serialize(atlas.ToDictionary(a => a.Key, a => new[] { a.Value.Width, a.Value.Height }));
                Hidden(context, ResourceType.PrefabFile, "ui/atlases", new DataLoader(() => sizes));
                Log.Info($"Interface: {atlas.Count} atlas pieces, {glues} creation scenes");
            }
            catch (Exception error) { Log.Warning($"Interface atlases unavailable: {error.Message}"); }
            // Zone names per tile and each map's loading screen, from the client's tables.
            foreach (var tile in index.Tiles.Where(t => t.Id != "global"))
                Hidden(context, ResourceType.PrefabFile, $"maps/{tile.Map}/{tile.Id}/areas", new AreaLoader(tile.Map, tile.Id));
            // Each map's loading screen as the client composes it: picture, Forever's frame and logo.
            var screens = new System.Text.Json.Nodes.JsonObject(); var skins = new System.Text.Json.Nodes.JsonObject();
            var screenFiles = new HashSet<uint>();
            string ScreenFile(uint file) { screenFiles.Add(file); return $"mount://wowforever/loadingscreens/files/{file}.vtex"; }
            System.Text.Json.Nodes.JsonObject Skin(uint id)
            {
                var skin = new System.Text.Json.Nodes.JsonObject();
                try
                {
                    var table = Table(LoadingScreenLoader.SkinFile);
                    for (int part = 0; part < LoadingScreenLoader.SkinParts.Length; part++)
                        if (table.UInt(id, 0, part) is var file and > 0 && Storage.Exists(file)) skin[LoadingScreenLoader.SkinParts[part]] = ScreenFile(file);
                }
                catch (Exception) { }
                return skin;
            }
            foreach (var map in Tables.Maps)
            {
                LoadingScreenLoader.Screen screen = null;
                try { screen = LoadingScreenLoader.Find(this, map); } catch (Exception) { }
                if (screen is null) continue;
                var entry = new System.Text.Json.Nodes.JsonObject { ["Image"] = ScreenFile(screen.Image) };
                if (screen.Logo != 0) entry["Logo"] = ScreenFile(screen.Logo);
                if (screen.Skin != 0)
                {
                    if (!skins.ContainsKey(screen.Skin.ToString())) skins[screen.Skin.ToString()] = Skin(screen.Skin);
                    entry["Skin"] = screen.Skin.ToString();
                }
                screens[map.Key] = entry;
            }
            foreach (var file in screenFiles) Hidden(context, ResourceType.Texture, $"loadingscreens/files/{file}", new LoadingScreenLoader(file));
            string screenJson = new System.Text.Json.Nodes.JsonObject { ["Screens"] = screens, ["Skins"] = skins }.ToJsonString();
            Hidden(context, ResourceType.PrefabFile, "loadingscreens", new DataLoader(() => screenJson));
            // Every creature, NPC and mount display, animated, under its model's client name.
            int creatures = 0;
            try
            {
                Creatures = CreatureCatalog.Read(this);
                foreach (var display in Creatures.Displays.Values)
                {
                    // In the spawn menu with every animation of the model; the world's NPCs use a lighter copy (_npc).
                    context.Add(ResourceType.Model, display.Path, new CreatureLoader(display, every: true));
                    Hidden(context, ResourceType.Model, display.Path + "_npc", new CreatureLoader(display));
                    creatures++;
                }
                Hidden(context, ResourceType.PrefabFile, "creatures", new DataLoader(() => System.Text.Json.JsonSerializer.Serialize(
                    Creatures.Displays.Values.Select(d => new { d.Id, Path = $"mount://wowforever/{d.Path}.vmdl", d.Scale, d.Npc }))));
                Hidden(context, ResourceType.PrefabFile, "mounts", new DataLoader(() => System.Text.Json.JsonSerializer.Serialize(
                    Creatures.Mounts.Select(m => new { Mount = m.Key, Name = Table(CreatureCatalog.MountFile).String(m.Key, 0), Displays = m.Value.Select(d => new { Id = d, Path = $"mount://wowforever/{Creatures.Displays[d].Path}.vmdl", Creatures.Displays[d].Scale }) }))));
            }
            catch (Exception error) { Log.Warning($"Creatures unavailable: {error.Message}"); }
            // Spell visuals: a searchable list, each spell's phases, and their animated effect models with particles.
            int spells = 0; var visuals = new HashSet<uint>();
            try
            {
                var spellCatalog = SpellCatalog.Read(this);
                foreach (var file in spellCatalog.EffectModels)
                {
                    bool placed;
                    lock (gate) placed = !ModelTypes.TryAdd(file, "m2");
                    Hidden(context, ResourceType.Model, $"effects/{file}", new EffectLoader(file));
                    Hidden(context, ResourceType.PrefabFile, $"effects/{file}", new ModelInfoLoader(file));
                    // Models already placed in the world have their particle textures registered.
                    if (!placed)
                    for (int slot = 0; slot < ParticleTextureLoader.MaxSlots; slot++)
                    {
                        Hidden(context, ResourceType.Texture, $"models/{file}/particle{slot}", new ParticleTextureLoader(file, slot));
                        Hidden(context, ResourceType.GameResource, $"models/{file}/sprite{slot}", new SpriteLoader(file, slot));
                    }
                }
                foreach (var (id, spell) in spellCatalog.Spells)
                {
                    string json = spell.ToJsonString();
                    Hidden(context, ResourceType.PrefabFile, $"spells/{id}", new DataLoader(() => json));
                }
                Hidden(context, ResourceType.PrefabFile, "spells", new DataLoader(() => System.Text.Json.JsonSerializer.Serialize(
                    spellCatalog.Names.GroupBy(n => n.Value).Select(g => new { Id = g.Min(x => x.Key), Name = g.Key }).OrderBy(x => x.Name))));
                spells = spellCatalog.Spells.Count; visuals = spellCatalog.Spells.Keys.ToHashSet();
            }
            catch (Exception error) { Log.Warning($"Spells unavailable: {error.Message}"); }
            // NPCs and monsters of each tile (public Classic positions, the client's looks).
            int populated = 0;
            if (Creatures is not null)
            {
                var spawns = SpawnCatalog.Read();
                spawns.Visuals = visuals;
                try { var emotes = Table(EmotesFile); foreach (var id in emotes.Ids) spawns.EmoteAnimations[id] = (int)emotes.UInt(id, 1); }
                catch (Exception error) { Log.Warning($"Emotes unavailable: {error.Message}"); }
                // The sandbox's bestiary: every creature and NPC, to summon anywhere.
                Hidden(context, ResourceType.PrefabFile, "bestiary", new DataLoader(() => spawns.BestiaryJson(Creatures)));
                foreach (var key in spawns.Tiles.Keys)
                {
                    var parts = key.Split('/');
                    Hidden(context, ResourceType.PrefabFile, $"maps/{parts[0]}/{parts[1]}/creatures", new DataLoader(() => spawns.TileJson(parts[0], parts[1], Creatures)));
                    populated++;
                }
            }
            int fonts = 0;
            try { fonts = GameFonts.Register(this); } catch (Exception error) { Log.Warning($"Fonts unavailable: {error.Message}"); }
            LastError = null; IsMounted = true;
            Log.Info($"World of Warcraft Forever {Storage.Version}: {index.Tiles.Count} tiles, {named} named models, {creatures} creature displays, {populated} populated tiles, {spells} spell visuals, {fonts} fonts ({index.Missing} source files not installed yet)");
        }
        catch (Exception error) { LastError = error.Message; Storage?.Dispose(); Storage = null; context.AddError(error.Message); }
        return Task.CompletedTask;
    }

    private static void Hidden(MountContext context, ResourceType type, string path, ResourceLoader loader)
    {
        loader.Flags |= ResourceFlags.DeveloperOnly;
        context.Add(type, path, loader);
    }

    protected override void Shutdown()
    {
        lock (gate) { textures.Clear(); images.Clear(); chains.Clear(); materials.Clear(); wdts.Clear(); terrains.Clear(); models.Clear(); }
        Storage?.Dispose(); Storage = null; IsMounted = false;
    }

    /// <summary>Longest main-thread creation per loader kind (performance checks), with the slowest path.</summary>
    public static readonly Dictionary<string, (double Ms, string Path)> Timings = new();
    internal static void Record(string kind, double ms, string path)
    {
        lock (Timings) if (!Timings.TryGetValue(kind, out var max) || ms > max.Ms) Timings[kind] = (ms, path);
    }

    internal static PrefabFile DataPrefab(string name, string json)
    {
        var root = new JsonObject
        {
            ["__guid"] = Guid.NewGuid().ToString(),
            ["Name"] = name,
            ["__properties"] = new JsonObject { ["Metadata"] = new JsonObject { ["WowData"] = json } }, // read by PrefabFile.GetMetadata
            ["Components"] = new JsonArray(),
            ["Children"] = new JsonArray(),
        };
        return new PrefabFile { RootObject = root };
    }

    internal Wdt GetWdt(string map)
    {
        lock (gate) if (wdts.TryGetValue(map, out var cached)) return cached;
        var entry = Tables.Maps.First(m => m.Key == map);
        // Modern root manifests omit most name hashes: the WDT FileDataID comes from Map.db2.
        var file = entry.Wdt != 0 ? entry.Wdt : Storage.FileDataIdOf($"world/maps/{entry.Directory}/{entry.Directory}.wdt");
        var data = Storage.TryRead(file);
        var wdt = data is null ? null : Wdt.Read(data);
        lock (gate) wdts[map] = wdt;
        return wdt;
    }

    internal TerrainTile GetTerrain(string map, string tileId)
    {
        string key = map + "/" + tileId;
        lock (gate) if (terrains.TryGetValue(key, out var cached)) return cached;
        var wdt = GetWdt(map) ?? throw new FileNotFoundException("Map not installed: " + map);
        var files = wdt.Tiles.Values.First(t => t.Id == tileId);
        var adt = AdtRoot.Read(Storage.Read(files.Root));
        var tex = AdtTex.Read(Storage.Read(files.Tex0), wdt.Flags);
        var tile = TerrainTile.Build(adt, tex, wdt.Flags);
        lock (gate)
        {
            terrains[key] = tile; terrainOrder.Enqueue(key);
            while (terrainOrder.Count > 16) terrains.Remove(terrainOrder.Dequeue()); // tiles hold their MCAL masks: keep few
        }
        return tile;
    }

    internal ModelData GetModel(uint id)
    {
        lock (gate) if (models.TryGetValue(id, out var cached)) return cached;
        var bytes = Storage.Read(id);
        string type;
        lock (gate) ModelTypes.TryGetValue(id, out type);
        type ??= BitConverter.ToUInt32(bytes, 0) == ChunkId.Lit("MD21") ? "m2" : "wmo";
        var model = type == "m2" ? M2Reader.Read(id, bytes, Storage.TryRead) : WmoReader.Read(id, bytes, Storage.TryRead);
        lock (gate)
        {
            models[id] = model; modelOrder.Enqueue(id);
            while (modelOrder.Count > 2048) models.Remove(modelOrder.Dequeue()); // a whole place is prepared at once
        }
        return model;
    }

    internal Texture GetTexture(uint id, bool alphaOnly = false)
    {
        uint key = alphaOnly ? id | 0x80000000u : id;
        lock (gate) if (textures.TryGetValue(key, out var cached)) return cached;
        var texture = Upload(id, alphaOnly);
        if (texture != Texture.White) lock (gate) textures[key] = texture; // a missing texture may arrive later
        return texture;
    }

    /// <summary>Reads and decodes a BLP; safe on worker threads so loaders can prepare textures off the main thread.</summary>
    internal BlpImage Image(uint id)
    {
        lock (gate) if (images.TryGetValue(id, out var cached)) return cached;
        var data = Storage.TryRead(id);
        BlpImage blp = null;
        try { blp = data is null ? null : BlpImage.Read(data); } catch (InvalidDataException) { }
        if (blp is null) return null; // not cached: a later request may find it (CDN download, transient failure)
        lock (gate)
        {
            images[id] = blp; imageOrder.Enqueue(id);
            while (imageOrder.Count > 1024) images.Remove(imageOrder.Dequeue());
        }
        return blp;
    }

    private readonly Dictionary<uint, (byte[] Data, int Levels)> chains = new();

    /// <summary>Decoded RGBA mip chains, prepared by workers; the engine only uploads them.</summary>
    internal (byte[] Data, int Levels) Chain(uint id, bool alphaOnly)
    {
        uint key = alphaOnly ? id | 0x80000000u : id;
        lock (gate) if (chains.TryGetValue(key, out var cached)) return cached;
        var blp = Image(id);
        if (blp is null) return (null, 0);
        // The whole mip chain is built here on a worker, so the GPU has nothing to generate when the texture appears.
        var data = alphaOnly ? blp.AlphaChain(out int levels) : blp.RgbaChain(out levels);
        lock (gate) chains[key] = (data, levels);
        return (data, levels);
    }

    /// <summary>Worker side: read a texture, and decode it only when it cannot go to the GPU block-compressed.</summary>
    internal void PrepareTexture(uint id, bool alphaOnly)
    {
        if (id == 0) return;
        lock (gate) if (textures.ContainsKey(alphaOnly ? id | 0x80000000u : id)) return;
        var blp = Image(id);
        if (blp is not null && (alphaOnly || Compressed(blp) is null)) Chain(id, alphaOnly);
    }

    /// <summary>DXT mips as the client stores them, like the engine's DDS loader (whole chain, static usage).</summary>
    private const bool UseCompressedUpload = true;

    private static (ImageFormat Format, byte[] Data)? Compressed(BlpImage blp)
    {
        if (!UseCompressedUpload || blp.Format == BlpImage.Kind.Rgba) return null;
        // 1-bit alpha in DXT1 blocks reads as opaque black on the GPU: such cut-outs (vines, leaves) are expanded.
        if (blp.Format == BlpImage.Kind.Dxt1 && blp.HasAlpha) return null;
        int full = 1 + (int)Math.Log2(Math.Max(blp.Width, blp.Height));
        if (blp.Mips.Count != full) return null;
        var format = blp.Format switch { BlpImage.Kind.Dxt1 => ImageFormat.DXT1, BlpImage.Kind.Dxt3 => ImageFormat.DXT3, _ => ImageFormat.DXT5 };
        int expected = 0, w = blp.Width, h = blp.Height, block = format == ImageFormat.DXT1 ? 8 : 16;
        for (int i = 0; i < full; i++) { expected += Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * block; w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
        if (blp.Mips.Sum(m => m.Length) != expected) return null;
        return (format, Enumerable.Reverse(blp.Mips).SelectMany(m => m).ToArray()); // smallest level first
    }

    private Texture Upload(uint id, bool alphaOnly)
    {
        var image = Image(id);
        if (!alphaOnly && image is not null && Compressed(image) is { } dxt)
        {
            try { return Texture.Create(image.Width, image.Height, dxt.Format).WithData(dxt.Data).WithMips(image.Mips.Count).WithStaticUsage().Finish(); }
            catch (Exception) { /* fall back to an expanded image */ }
        }
        var (data, levels) = Chain(id, alphaOnly);
        if (data is null) return Texture.White;
        var blp = Image(id);
        lock (gate) chains.Remove(alphaOnly ? id | 0x80000000u : id); // uploaded once, then owned by the texture
        return Texture.Create(blp.Width, blp.Height).WithData(data).WithMips(levels).WithStaticUsage().Finish();
    }

    /// <summary>Uncompressed RGBA, readable back by the explorer (sprite sheets).</summary>
    internal Texture ParticleTexture(uint id)
    {
        var blp = Image(id);
        if (blp is null) return Texture.White;
        return Texture.Create(blp.Width, blp.Height).WithData(blp.ToRgba()).Finish();
    }

    internal Texture CreateTexture(int width, int height, byte[] rgba) => Texture.Create(width, height).WithData(rgba).Finish();

    /// <summary>Prop material with the client's blend mode (same policy as the converter's material_policy.cjs).
    /// Scrolling textures and unlit surfaces (M2 material flag 1) use the animated prop shader.</summary>
    internal Material PropMaterial(uint texture, int blend, uint flags, System.Numerics.Vector2 scroll = default)
    {
        bool unlit = (flags & 1) != 0;
        string key = $"prop:{texture}:{blend}:{flags & 5}:{scroll.X:0.####}:{scroll.Y:0.####}";
        lock (gate) if (materials.TryGetValue(key, out var cached)) return cached;
        if (blend <= 4 && (unlit || scroll != System.Numerics.Vector2.Zero))
        {
            var animated = Material.Create($"mount://wowforever/materials/{texture}_{blend}_{flags & 5}_anim{materials.Count}.vmat", "shaders/wowforever/prop_anim.shader");
            animated.SetFeature("F_WOW_BLEND", blend);
            if (unlit) animated.SetFeature("F_UNLIT", 1);
            if ((flags & 4) != 0) animated.SetFeature("F_BACKFACES", 1);
            animated.Set("g_tColor", GetTexture(texture));
            animated.Set("g_vUvScroll", new Vector2(scroll.X, scroll.Y));
            lock (gate) materials[key] = animated;
            return animated;
        }
        bool special = blend is 5 or 6 or 7;
        var material = Material.Create($"mount://wowforever/materials/{texture}_{blend}_{flags & 4}.vmat",
            special ? "shaders/wowforever/prop_blend.shader" : "shaders/complex.shader");
        // Features first: they select the shader variant whose inputs are then bound.
        if (blend == 1) material.SetFeature("F_ALPHA_TEST", 1);
        else if (blend > 1 && !special) material.SetFeature("F_TRANSLUCENT", 1);
        if (blend is 3 or 4) material.SetFeature("F_ADDITIVE_BLEND", 1);
        if (special) material.SetFeature("F_WOW_BLEND", blend);
        if ((flags & 4) != 0) material.SetFeature("F_RENDER_BACKFACES", 1);
        material.Set("g_tColor", GetTexture(texture));
        Neutral(material);
        if (blend is 1 or 2 or 4 or 7) material.Set("g_tTranslucency", GetTexture(texture, alphaOnly: true));
        if (blend == 1) material.Set("g_flAlphaTestReference", 0.5f);
        lock (gate) materials[key] = material;
        return material;
    }

    internal void PrefetchLiquid(int type)
    {
        if (!Tables.Liquids.TryGetValue(type, out var l)) l = Tables.Liquids[1];
        foreach (var id in new[] { l.Foam, l.Rim, l.Color, l.Emissive, Tables.OceanBump }) PrepareTexture(id, false);
    }

    internal Material LiquidMaterial(int type)
    {
        string key = "liquid:" + type;
        lock (gate) if (materials.TryGetValue(key, out var cached)) return cached;
        if (!Tables.Liquids.TryGetValue(type, out var l)) l = Tables.Liquids[1];
        var material = Material.Create($"mount://wowforever/materials/liquid_{type}.vmat", "shaders/wowforever/liquid.shader");
        if (l.Molten)
        {
            material.SetFeature("F_MOLTEN", 1);
            material.Set("g_tColor", GetTexture(l.Color)); material.Set("g_tEmissive", GetTexture(l.Emissive));
            material.Set("g_vGlowColor", new Vector4(l.Glow[0], l.Glow[1], l.Glow[2], 1));
        }
        else
        {
            material.Set("g_tFoam", GetTexture(l.Foam)); material.Set("g_tRim", GetTexture(l.Rim)); material.Set("g_tBump", GetTexture(Tables.OceanBump));
            material.Set("g_vShallowColor", new Vector4(l.Shallow[0], l.Shallow[1], l.Shallow[2], 1));
            material.Set("g_vMidColor", new Vector4(l.Mid[0], l.Mid[1], l.Mid[2], 1));
            material.Set("g_vDeepColor", new Vector4(l.Deep[0], l.Deep[1], l.Deep[2], 1));
        }
        lock (gate) materials[key] = material;
        return material;
    }

    private Texture flatNormal;

    /// <summary>complex.shader's other inputs default to a checker when a material is built in code.</summary>
    internal void Neutral(Material material)
    {
        flatNormal ??= Texture.Create(1, 1).WithData(new byte[] { 128, 128, 255, 255 }).Finish();
        material.Set("g_tNormal", flatNormal);
        material.Set("g_tRoughness", Texture.White);
        material.Set("g_tMetalness", Texture.Black);
        material.Set("g_tAmbientOcclusion", Texture.White);
    }

    internal static Vector3 V(NVector3 v) => new(v.X, v.Y, v.Z);
}
