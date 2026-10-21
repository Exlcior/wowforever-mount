using Sandbox;

namespace MiniBox.WowForever;

/// <summary>Creature displays of the installed client (CreatureDisplayInfo → CreatureModelData), with their
/// texture variations, NPC baked skins (CreatureDisplayInfoExtra → TextureFileData) and mounts (MountXDisplay).
/// Everything is read from the client's own tables at mount time.</summary>
internal sealed class CreatureCatalog
{
    public const uint DisplayInfoFile = 1108759, ModelDataFile = 1365368, DisplayExtraFile = 1264997,
        TextureFileDataFile = 982459, MountFile = 921760, MountDisplayFile = 1576117;

    public sealed record Display(uint Id, uint Model, uint[] Textures, uint Skin, float Scale, string Path, bool Npc);
    public readonly Dictionary<uint, Display> Displays = new();
    /// <summary>Mount id → creature display ids it can show.</summary>
    public readonly Dictionary<uint, List<uint>> Mounts = new();

    public static CreatureCatalog Read(WowForeverMount host)
    {
        var catalog = new CreatureCatalog();
        var info = host.Table(DisplayInfoFile); var models = host.Table(ModelDataFile); var extra = host.Table(DisplayExtraFile);
        var textureFiles = host.Table(TextureFileDataFile);
        // MaterialResourcesID → texture file, for the NPC skins baked by Blizzard.
        var materials = new Dictionary<uint, uint>();
        foreach (var file in textureFiles.Ids) { uint material = textureFiles.Relation(file); if (material != 0) materials.TryAdd(material, file); }
        // Character bodies without a baked NPC skin fall back to the race's default skin.
        var defaultSkins = new Dictionary<uint, uint>();
        using (var stream = typeof(WowForeverMount).Assembly.GetManifestResourceStream("wowforever.characters.json"))
            foreach (var preset in System.Text.Json.JsonSerializer.Deserialize<List<CharacterPreset>>(stream) ?? new()) defaultSkins.TryAdd(preset.Model, preset.Skin);
        foreach (var id in info.Ids)
        {
            uint modelData = info.UInt(id, 1), file = models.UInt(modelData, 2);
            if (file == 0 || !host.Storage.Exists(file)) continue;
            var textures = new uint[3];
            for (int t = 0; t < 3; t++) textures[t] = info.UInt(id, 27, t);
            uint extraId = info.UInt(id, 7), skin = 0;
            if (extraId != 0)
            {
                uint material = extra.UInt(extraId, 6); if (material == 0 || !materials.ContainsKey(material)) material = extra.UInt(extraId, 5);
                materials.TryGetValue(material, out skin);
            }
            bool character = host.NamedModels.TryGetValue(file, out var modelName) && modelName.StartsWith("character/");
            if (skin == 0 && character) defaultSkins.TryGetValue(file, out skin);
            float scale = info.Float(id, 4) * Math.Max(0.01f, models.Float(modelData, 21));
            if (!float.IsFinite(scale) || scale <= 0) scale = 1;
            string name = host.NamedModels.TryGetValue(file, out var path) ? path : $"creature/unnamed/{file}";
            // creature/wolf/wolf → creatures/wolf/wolf_<display>; character/human/male/humanmale → npcs/human/male/humanmale_<display>
            string folder = name.StartsWith("character/") ? "npcs/" + name["character/".Length..] : "creatures/" + (name.StartsWith("creature/") ? name["creature/".Length..] : name);
            catalog.Displays[id] = new Display(id, file, textures, skin, scale, $"{folder}_{id}", character);
        }
        var mountDisplays = host.Table(MountDisplayFile);
        foreach (var row in mountDisplays.Ids)
        {
            uint display = mountDisplays.UInt(row, 0), mount = mountDisplays.Relation(row);
            if (mount == 0 || !catalog.Displays.ContainsKey(display)) continue;
            if (!catalog.Mounts.TryGetValue(mount, out var list)) catalog.Mounts[mount] = list = new List<uint>();
            list.Add(display);
        }
        return catalog;
    }
}

/// <summary>An animated creature, NPC or mount: the client's skeleton, geometry, textures and sequences. With
/// <paramref name="every"/> it carries every animation of the model, for the sandbox's summoned creatures.</summary>
internal sealed class CreatureLoader(CreatureCatalog.Display display, bool every = false) : WorkerLoader<CharacterData>
{
    protected override CharacterData Prepare()
    {
        uint Texture(int type) => type switch
        {
            1 => display.Skin,                                      // NPC body, baked by the client data
            2 or 11 => display.Textures[0], 12 => display.Textures[1], 13 => display.Textures[2],
            _ => 0,
        };
        // Creatures show their base pieces (x00/x01); humanoid NPCs use the character rule.
        Func<int, bool> geosets = display.Npc ? null : g => g == 0 || g % 100 <= 1;
        var data = CharacterData.Read(display.Model, Host.Storage.Read(display.Model), Host.Storage.TryRead, Texture, geosets, CharacterData.Creature, false,
            every ? AnimationNames.Of : null);
        foreach (var texture in data.Geometry.Parts.Select(p => p.Texture).Distinct()) Host.PrepareTexture(texture, false);
        return data;
    }

    protected override IEnumerable<(uint, bool)> Textures(CharacterData data) => data.Geometry.Parts.Select(p => (p.Texture, false)).Distinct();

    protected override object Create(CharacterData data) => SkinnedModel.Build(Host, Path, data, texture => Host.GetTexture(texture));
}

/// <summary>Where NPCs and monsters stand: positions from the public CMaNGOS Classic database (the client holds
/// none, the server places them), embedded as Data/spawns.txt.gz. Their looks come from the client's displays.</summary>
internal sealed class SpawnCatalog
{
    public sealed record Kind(uint Entry, string Name, string Title, int MinLevel, int MaxLevel, int Rank, uint NpcFlags, float Scale, uint[] Displays,
        uint[] Spells, uint[] IdleSpells, uint[] Auras, uint Mount, int Stand, uint Emote, uint[] Items);
    public sealed record Spawn(uint Entry, float X, float Y, float Z, float Yaw, float Wander, uint[] Auras, uint Mount, int Stand, uint Emote);
    public readonly Dictionary<uint, Kind> Kinds = new();
    /// <summary>"map/tile" → spawns standing on that tile.</summary>
    public readonly Dictionary<string, List<Spawn>> Tiles = new();

    public static SpawnCatalog Read()
    {
        var catalog = new SpawnCatalog();
        using var stream = typeof(WowForeverMount).Assembly.GetManifestResourceStream("wowforever.spawns.txt.gz");
        if (stream is null) return catalog;
        using var reader = new StreamReader(new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress));
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        float F(string s) => float.TryParse(s, System.Globalization.NumberStyles.Float, ci, out var v) ? v : 0;
        uint U(string[] p, int i) => i < p.Length && uint.TryParse(p[i], out var v) ? v : 0;
        uint[] L(string[] p, int i) => i < p.Length && p[i].Length > 0 ? p[i].Split(',').Select(s => uint.TryParse(s, out var v) ? v : 0).Where(v => v != 0).ToArray() : Array.Empty<uint>();
        var maps = Tables.Maps.ToDictionary(m => m.Id, m => m.Key);
        while (reader.ReadLine() is { } line)
        {
            var p = line.Split(';');
            if (p[0] == "T" && p.Length >= 10)
                catalog.Kinds[uint.Parse(p[1])] = new Kind(uint.Parse(p[1]), p[2], p[3], int.Parse(p[4]), int.Parse(p[5]), int.Parse(p[6]), uint.Parse(p[7]), F(p[8]),
                    p[9].Split(',').Select(uint.Parse).ToArray(), L(p, 10), L(p, 11), L(p, 12), U(p, 13), (int)U(p, 14), U(p, 15), L(p, 16));
            else if (p[0] == "S" && p.Length >= 8 && maps.TryGetValue(int.Parse(p[1]), out var map))
            {
                float x = F(p[3]), y = F(p[4]);
                const float half = 51200f / 3;
                int b = (int)Math.Floor((half - x) / TerrainTile.TileSize), a = (int)Math.Floor((half - y) / TerrainTile.TileSize);
                string key = $"{map}/{a}_{b}";
                if (!catalog.Tiles.TryGetValue(key, out var list)) catalog.Tiles[key] = list = new List<Spawn>();
                list.Add(new Spawn(uint.Parse(p[2]), x, y, F(p[5]), F(p[6]), F(p[7]), L(p, 8), U(p, 9), (int)U(p, 10), U(p, 11)));
            }
        }
        return catalog;
    }

    /// <summary>Spells with a visual in the installed client (ids served as spells/&lt;id&gt;).</summary>
    public HashSet<uint> Visuals = new();
    /// <summary>The client's emotes (Emotes.db2) to their animation id.</summary>
    public Dictionary<uint, int> EmoteAnimations = new();

    // Server stand states: sitting (on the ground or a chair), sleeping, dead, kneeling.
    private static string Stand(int state) => state switch { 1 or 2 or 4 or 5 or 6 => "sit", 3 => "sleep", 7 => "dead", 8 => "kneel", _ => null };

    private string Pose(int stand, uint emote)
    {
        if (Stand(stand) is { } pose) return pose;
        if (emote == 0 || !EmoteAnimations.TryGetValue(emote, out var animation)) return null;
        foreach (var clip in CharacterData.Creature) if (clip.Id == animation && clip.Loop) return clip.Name;
        return null;
    }

    private System.Text.Json.Nodes.JsonArray Spells(IEnumerable<uint> ids) =>
        new(ids.Where(Visuals.Contains).Distinct().Select(id => (System.Text.Json.Nodes.JsonNode)id).ToArray());

    /// <summary>A tile's creatures as JSON for the explorer and other games: name, level, model path, placement,
    /// and what the server gives them: spells in and out of combat, auras, mount, pose.</summary>
    public string TileJson(string map, string tile, CreatureCatalog displays)
    {
        var list = new System.Text.Json.Nodes.JsonArray();
        if (!Tiles.TryGetValue($"{map}/{tile}", out var spawns)) return list.ToJsonString();
        int i = 0;
        foreach (var s in spawns)
        {
            if (!Kinds.TryGetValue(s.Entry, out var kind)) continue;
            // Kinds with several looks pick one per spawn, steadily.
            var options = kind.Displays.Where(displays.Displays.ContainsKey).ToArray();
            if (options.Length == 0) continue;
            var display = displays.Displays[options[(int)((s.X * 7 + s.Y * 13) % options.Length + options.Length) % options.Length]];
            var json = KindJson(kind, display, displays, s.Stand, s.Emote, s.Mount, s.Auras);
            json["Id"] = $"{map}:{tile}:{i++}";
            json["Model"] = $"mount://wowforever/{display.Path}_npc.vmdl";
            json["Position"] = new System.Text.Json.Nodes.JsonArray(s.X, s.Y, s.Z); json["Yaw"] = s.Yaw * 180 / MathF.PI; json["Wander"] = s.Wander;
            list.Add(json);
        }
        return list.ToJsonString();
    }

    /// <summary>Every creature and NPC of the database with a look in the client, for the sandbox's summons: the
    /// same description as a spawn, with the model carrying every animation.</summary>
    public string BestiaryJson(CreatureCatalog displays)
    {
        var list = new System.Text.Json.Nodes.JsonArray();
        foreach (var kind in Kinds.Values.OrderBy(k => k.Name))
        {
            var display = kind.Displays.Where(displays.Displays.ContainsKey).Select(d => displays.Displays[d]).FirstOrDefault();
            if (display is null) continue;
            var json = KindJson(kind, display, displays, 0, 0, 0, System.Array.Empty<uint>());
            json["Id"] = $"entry:{kind.Entry}";
            list.Add(json);
        }
        return list.ToJsonString();
    }

    private System.Text.Json.Nodes.JsonObject KindJson(Kind kind, CreatureCatalog.Display display, CreatureCatalog displays, int stand, uint emote, uint mount, uint[] auras)
    {
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["Entry"] = kind.Entry, ["Name"] = kind.Name, ["Title"] = kind.Title,
            ["Level"] = kind.MinLevel == kind.MaxLevel ? kind.MinLevel.ToString() : $"{kind.MinLevel}-{kind.MaxLevel}",
            ["Rank"] = kind.Rank, ["NpcFlags"] = kind.NpcFlags, ["Model"] = $"mount://wowforever/{display.Path}.vmdl",
            ["Scale"] = display.Scale * (kind.Scale > 0 ? kind.Scale : 1), ["Npc"] = display.Npc,
            ["Spells"] = Spells(kind.Spells), ["IdleSpells"] = Spells(kind.IdleSpells), ["Auras"] = Spells(auras.Length > 0 ? auras : kind.Auras),
            ["Pose"] = Pose(stand != 0 ? stand : kind.Stand, emote != 0 ? emote : kind.Emote),
            ["Items"] = new System.Text.Json.Nodes.JsonArray(kind.Items.Select(i => (System.Text.Json.Nodes.JsonNode)i).ToArray()),
        };
        uint steedId = mount != 0 ? mount : kind.Mount;
        if (steedId != 0 && displays.Displays.TryGetValue(steedId, out var steed))
        {
            json["Mount"] = $"mount://wowforever/{steed.Path}_npc.vmdl";
            json["MountScale"] = steed.Scale;
        }
        return json;
    }
}

/// <summary>Names of the client's animation ids (AnimationData), from wow.export's list (MIT): Data/animation_names.txt.</summary>
internal static class AnimationNames
{
    private static readonly Lazy<string[]> names = new(() =>
    {
        using var stream = typeof(WowForeverMount).Assembly.GetManifestResourceStream("wowforever.animation_names.txt");
        if (stream is null) return System.Array.Empty<string>();
        return new StreamReader(stream).ReadToEnd().Split('\n').Where(l => !l.StartsWith('#')).Select(l => l.Trim()).ToArray();
    });

    public static string Of(int id) => id < names.Value.Length && names.Value[id].Length > 0 ? names.Value[id] : $"anim_{id}";
}
