using System.Text.Json.Nodes;
using Sandbox;
using Sandbox.Mounting;

namespace MiniBox.WowForever;

/// <summary>What a playable character can look like, from the client's own customization tables: ChrModel
/// (race and sex) → ChrCustomizationOption (skin colour, face, hair style...) → ChrCustomizationChoice →
/// ChrCustomizationElement, each element switching geosets (ChrCustomizationGeoset) or painting a texture layer
/// (ChrCustomizationMaterial → ChrModelTextureLayer) into the character's texture atlas, whose sections come from
/// CharComponentTextureSections. Games compose the atlas from these layers; the mount serves every texture readable.</summary>
internal sealed class AppearanceCatalog
{
    public const uint ChrModelFile = 3384313, RaceModelFile = 3490304, LayoutsFile = 1360262, SectionsFile = 1360263,
        LayersFile = 3548976, OptionsFile = 3384247, ChoicesFile = 3450554, ElementsFile = 3512765, GeosetsFile = 3456171,
        MaterialsFile = 3459652, TextureFileDataFile = 982459, CategoriesFile = 3526439, ClassesFile = 1361031,
        StartOutfitFile = 1237591;

    public sealed record Layer(int TextureType, int Order, int Blend, uint Sections);
    public sealed class Look
    {
        public uint ChrModel; public int Race, Sex; public uint Layout;
        public JsonObject Json;
        /// <summary>The texture each texture type starts with (first choice of each option).</summary>
        public Dictionary<int, uint> Defaults = new();
        /// <summary>Geoset groups the customization switches (hair styles, beards, ears...).</summary>
        public HashSet<int> Groups = new();
    }

    public readonly Dictionary<string, Look> Looks = new();
    // The class as the client names it for this sex (ChrClasses Name_male / Name_female), else its general name.
    private static string ClassName(Db2 classes, uint cls, int sex)
    {
        string name = classes.String(cls, sex == 1 ? 3 : 2);
        return string.IsNullOrEmpty(name) ? classes.String(cls, 0) : name;
    }

    /// <summary>Classes each race could be in the original game (ChrRaces → ChrClasses ids): warrior 1, paladin 2,
    /// hunter 3, rogue 4, priest 5, shaman 7, mage 8, warlock 9, druid 11.</summary>
    private static readonly Dictionary<int, uint[]> ClassicClasses = new()
    {
        [1] = new uint[] { 1, 2, 4, 5, 8, 9 }, [2] = new uint[] { 1, 3, 4, 7, 9 }, [3] = new uint[] { 1, 2, 3, 4, 5 }, [4] = new uint[] { 1, 3, 4, 5, 11 },
        [5] = new uint[] { 1, 4, 5, 8, 9 }, [6] = new uint[] { 1, 3, 7, 11 }, [7] = new uint[] { 1, 4, 8, 9 }, [8] = new uint[] { 1, 3, 4, 5, 7, 8 },
        [10] = new uint[] { 2, 3, 4, 5, 8, 9 }, [11] = new uint[] { 1, 2, 3, 5, 7, 8 },
    };
    /// <summary>ChrRaces ids of the playable races, when a model is not listed in ChrRaceXChrModel.</summary>
    private static readonly Dictionary<string, int> RaceIds = new()
    {
        ["human"] = 1, ["orc"] = 2, ["dwarf"] = 3, ["nightelf"] = 4, ["scourge"] = 5, ["tauren"] = 6, ["gnome"] = 7, ["troll"] = 8, ["bloodelf"] = 10, ["draenei"] = 11,
    };
    /// <summary>Textures the games compose on the CPU (served uncompressed as textures/&lt;fdid&gt;).</summary>
    public readonly HashSet<uint> Readable = new();
    /// <summary>Material resource → its texture files, shared with the items.</summary>
    public Dictionary<uint, List<uint>> MaterialFiles;

    public static AppearanceCatalog Read(WowForeverMount host, IEnumerable<CharacterPreset> presets)
    {
        var catalog = new AppearanceCatalog();
        var models = host.Table(ChrModelFile); var raceModels = host.Table(RaceModelFile); var layouts = host.Table(LayoutsFile);
        var sections = host.Table(SectionsFile); var layers = host.Table(LayersFile); var options = host.Table(OptionsFile);
        var choices = host.Table(ChoicesFile); var elements = host.Table(ElementsFile); var geosets = host.Table(GeosetsFile);
        var materials = host.Table(MaterialsFile); var textureFiles = host.Table(TextureFileDataFile);
        var displays = host.Table(CreatureCatalog.DisplayInfoFile); var modelData = host.Table(CreatureCatalog.ModelDataFile);
        catalog.MaterialFiles = textureFiles.Ids.GroupBy(f => textureFiles.Relation(f)).Where(g => g.Key != 0).ToDictionary(g => g.Key, g => g.OrderBy(f => f).ToList());
        // Option categories (body, head, hair...) with their icons: UiTextureAtlasElement names of interface pieces.
        Db2 atlasElements = null;
        try { atlasElements = host.Table(UiAtlases.ElementFile); } catch (Exception) { }
        Db2 categories = null, classes = null, outfits = null;
        try { categories = host.Table(CategoriesFile); } catch (Exception) { }
        try { classes = host.Table(ClassesFile); } catch (Exception) { }
        try { outfits = host.Table(StartOutfitFile); } catch (Exception) { }
        var choicesByOption = choices.Ids.GroupBy(c => choices.UInt(c, 2)).ToDictionary(g => g.Key, g => g.OrderBy(c => choices.UInt(c, 5)).ToList());
        var elementsByChoice = elements.Ids.GroupBy(e => elements.UInt(e, 0)).ToDictionary(g => g.Key, g => g.ToList());
        // A texture target paints into the layer that lists it, on the model's texture layout.
        var layersByTarget = new Dictionary<(uint Layout, uint Target), Layer>();
        foreach (var l in layers.Ids)
            for (int t = 0; t < 2; t++)
                if (layers.UInt(l, 7, t) is var target and not 0)
                    layersByTarget.TryAdd((layers.Relation(l), target), new Layer(layers.Int(l, 0), layers.Int(l, 1), layers.Int(l, 3), layers.UInt(l, 4)));
        foreach (var preset in presets)
        {
            // The ChrModel whose creature display shows this preset's model file.
            uint chrModel = models.Ids.FirstOrDefault(m => modelData.UInt(displays.UInt(models.UInt(m, 4), 1), 2) == preset.Model);
            if (chrModel == 0) continue;
            var look = new Look { ChrModel = chrModel, Sex = models.Int(chrModel, 3), Layout = models.UInt(chrModel, 5) };
            // ChrRaceXChrModel: the race is a relation, also kept as the first column; ChrModelID is the second.
            uint row = raceModels.Ids.FirstOrDefault(r => raceModels.UInt(r, 1) == chrModel);
            look.Race = (int)(raceModels.Relation(row) != 0 ? raceModels.Relation(row) : raceModels.UInt(row, 0));
            if (look.Race == 0) RaceIds.TryGetValue(preset.Key.Split('_')[0], out look.Race);
            var json = new JsonObject
            {
                ["Key"] = preset.Key, ["Race"] = look.Race, ["Sex"] = look.Sex,
                // How the client's creation screen shows this model: its scale there and the camera's step back.
                ["CustomizeScale"] = models.Float(chrModel, 11), ["CameraDistanceOffset"] = models.Float(chrModel, 13),
                ["Layout"] = new JsonObject { ["Width"] = layouts.Int(look.Layout, 0), ["Height"] = layouts.Int(look.Layout, 1) },
                ["Sections"] = new JsonArray(sections.Ids.Where(s => sections.UInt(s, 0) == look.Layout).OrderBy(s => sections.Int(s, 1))
                    .Select(s => (JsonNode)new JsonObject { ["Type"] = sections.Int(s, 1), ["X"] = sections.Int(s, 2), ["Y"] = sections.Int(s, 3), ["Width"] = sections.Int(s, 4), ["Height"] = sections.Int(s, 5) }).ToArray()),
            };
            var optionList = new JsonArray();
            foreach (var option in options.Ids.Where(o => options.UInt(o, 4) == chrModel).OrderBy(o => options.Int(o, 5)))
            {
                var choiceList = new JsonArray();
                foreach (var choice in choicesByOption.GetValueOrDefault(option) ?? new())
                {
                    var parts = new JsonArray();
                    foreach (var element in elementsByChoice.GetValueOrDefault(choice) ?? new())
                    {
                        var part = new JsonObject();
                        if (elements.UInt(element, 1) is var related and not 0) part["Related"] = related;
                        if (elements.UInt(element, 2) is var geoset and not 0)
                        {
                            part["Geoset"] = geosets.Int(geoset, 0) * 100 + geosets.Int(geoset, 1);
                            look.Groups.Add(geosets.Int(geoset, 0));
                        }
                        if (elements.UInt(element, 4) is var material and not 0
                            && layersByTarget.TryGetValue((look.Layout, materials.UInt(material, 0)), out var layer)
                            && catalog.MaterialFiles.TryGetValue(materials.UInt(material, 1), out var files))
                        {
                            uint file = files[0];
                            catalog.Readable.Add(file);
                            part["Texture"] = $"mount://wowforever/textures/{file}.vtex"; part["File"] = file;
                            part["TextureType"] = layer.TextureType; part["Layer"] = layer.Order; part["Blend"] = layer.Blend; part["SectionMask"] = layer.Sections;
                        }
                        if (part.Count > 0) parts.Add(part);
                    }
                    choiceList.Add(new JsonObject { ["Id"] = choice, ["Name"] = choices.String(choice, 0), ["Elements"] = parts });
                }
                if (choiceList.Count > 0)
                    optionList.Add(new JsonObject { ["Id"] = option, ["Name"] = options.String(option, 0), ["Category"] = options.UInt(option, 6), ["Choices"] = choiceList });
            }
            json["Options"] = optionList;
            if (categories is not null)
                json["Categories"] = new JsonArray(optionList.Select(o => (uint)o["Category"]).Distinct().Where(categories.Has).OrderBy(c => categories.Int(c, 3))
                    .Select(c => (JsonNode)new JsonObject
                    {
                        ["Id"] = c, ["Name"] = categories.String(c, 0),
                        ["Icon"] = atlasElements?.String(categories.UInt(c, 1), 0).ToLowerInvariant(), ["IconSelected"] = atlasElements?.String(categories.UInt(c, 2), 0).ToLowerInvariant(),
                    }).ToArray());
            // The classes this race and sex can be, each with its starting outfit (CharStartOutfit; ChrClassRaceSex is
            // empty in this client).
            if (classes is not null)
            {
                var classList = new JsonArray();
                // CharStartOutfit gives each race, class and sex its outfit; when this client lacks it, the classes
                // of the original game (by ChrRaces id) are offered without outfit.
                var rows = outfits?.Ids.Where(o => outfits.Relation(o) == look.Race && outfits.Int(o, 1) == look.Sex).OrderBy(o => outfits.UInt(o, 0)).ToList() ?? new();
                var ids = rows.Count > 0 ? rows.Select(o => outfits.UInt(o, 0)) : ClassicClasses.GetValueOrDefault(look.Race) ?? Array.Empty<uint>();
                foreach (uint cls in ids.Distinct())
                {
                    if (!classes.Has(cls)) continue;
                    var items = new JsonArray();
                    if (rows.FirstOrDefault(o => outfits.UInt(o, 0) == cls) is var outfit and not 0)
                        for (int k = 0; k < 24; k++) if (outfits.UInt(outfit, 5, k) is var item and not 0) items.Add(item);
                    classList.Add(new JsonObject { ["Id"] = cls, ["Name"] = ClassName(classes, cls, look.Sex), ["File"] = classes.String(cls, 1).ToLowerInvariant(), ["Items"] = items });
                }
                json["Classes"] = classList;
            }
            // Starting textures per type (hair colour, extra skin layers) from the first choice of each option.
            foreach (var option in optionList)
                if (option["Choices"]?.AsArray().FirstOrDefault() is JsonObject first)
                    foreach (var part in first["Elements"].AsArray().OfType<JsonObject>())
                        if (part["TextureType"] is { } type && (int)type != 1)
                            look.Defaults.TryAdd((int)type, (uint)part["File"]);
            look.Json = json;
            catalog.Looks[preset.Key] = look;
        }
        return catalog;
    }
}

/// <summary>Wearable items of the client: ItemSparse (name, slot, quality) → ItemModifiedAppearance → ItemAppearance
/// → ItemDisplayInfo, with its attached models (ModelFileData, per race and sex through ComponentModelFileData),
/// their textures, the geosets it switches on the body and the texture pieces it paints into the atlas sections
/// (ItemDisplayInfoMaterialRes, per race and sex through ComponentTextureFileData).</summary>
internal sealed class ItemCatalog
{
    public const uint SparseFile = 1572924, ModifiedAppearanceFile = 982457, AppearanceFile = 982462, DisplayFile = 1266429,
        DisplayMaterialFile = 1280614, DisplayModelMaterialFile = 4050937, ModelFileDataFile = 1337833,
        ComponentModelFile = 1349053, ComponentTextureFile = 1278239, HelmetGeosetFile = 2821752;
    /// <summary>Inventory types worn on a character (head, shoulders, shirt, chest, waist, legs, feet, wrists, hands,
    /// weapons, shield, ranged, back, tabard, robe, held in off hand).</summary>
    private static readonly HashSet<int> Worn = new() { 1, 3, 4, 5, 6, 7, 8, 9, 10, 13, 14, 15, 16, 17, 19, 20, 21, 22, 23, 25, 26 };

    public sealed record Item(uint Id, string Name, int Slot, int Quality, uint Display);
    public readonly List<Item> Items = new();
    public readonly Dictionary<uint, JsonObject> Displays = new();
    /// <summary>Attached model files with their texture: items/models/&lt;file&gt;_&lt;texture&gt;.</summary>
    public readonly HashSet<(uint File, uint Texture)> Models = new();
    public readonly HashSet<uint> Readable = new();

    /// <summary>Item models for sandbox spawning: objets/&lt;category&gt;/&lt;name&gt;_&lt;id&gt;, the first model of each item.</summary>
    public IEnumerable<(string Path, uint File, uint Texture)> Spawnable()
    {
        foreach (var item in Items)
        {
            if (!Displays.TryGetValue(item.Display, out var json) || json["Models"]?.AsArray().FirstOrDefault() is not JsonObject set) continue;
            var variant = set["Variants"]?.AsArray().OfType<JsonObject>().FirstOrDefault();
            if (variant is null) continue;
            var model = variant["Path"]!.GetValue<string>();
            var parts = System.IO.Path.GetFileNameWithoutExtension(model).Split('_');
            string category = item.Slot switch { 1 => "tetes", 3 => "epaules", 14 => "boucliers", 15 or 25 or 26 => "distance", 22 or 23 => "main_gauche", _ => "armes" };
            var slug = new string(item.Name.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
                .Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
            yield return ($"objets/{category}/{slug}_{item.Id}", uint.Parse(parts[0]), uint.Parse(parts[1]));
        }
    }

    public static ItemCatalog Read(WowForeverMount host, Dictionary<uint, List<uint>> materialFiles)
    {
        var catalog = new ItemCatalog();
        var sparse = host.Table(SparseFile); var modified = host.Table(ModifiedAppearanceFile); var appearances = host.Table(AppearanceFile);
        var displays = host.Table(DisplayFile); var displayMaterials = host.Table(DisplayMaterialFile); var modelFiles = host.Table(ModelFileDataFile);
        var componentModels = host.Table(ComponentModelFile); var componentTextures = host.Table(ComponentTextureFile);
        Db2 modelMaterials = null;
        try { modelMaterials = host.Table(DisplayModelMaterialFile); } catch (Exception) { }
        var appearanceByItem = modified.Ids.GroupBy(m => modified.UInt(m, 1))
            .ToDictionary(g => g.Key, g => g.OrderBy(m => modified.UInt(m, 2)).ThenBy(m => modified.UInt(m, 4)).Select(m => modified.UInt(m, 3)).First());
        var filesByResource = modelFiles.Ids.GroupBy(f => modelFiles.UInt(f, 4)).Where(g => g.Key != 0).ToDictionary(g => g.Key, g => g.ToList());
        var piecesByDisplay = displayMaterials.Ids.GroupBy(p => displayMaterials.Relation(p)).ToDictionary(g => g.Key, g => g.ToList());
        var modelMaterialsByDisplay = modelMaterials?.Ids.GroupBy(p => modelMaterials.Relation(p)).ToDictionary(g => g.Key, g => g.ToList()) ?? new();
        // Helmets hide geoset groups (hair, ears, beards) per race: HelmetGeosetVis → HelmetGeosetData.
        var helmetHides = new Dictionary<uint, List<(int Race, int Group)>>();
        try
        {
            var helmets = host.Table(HelmetGeosetFile);
            foreach (var row in helmets.Ids)
            {
                if (!helmetHides.TryGetValue(helmets.Relation(row), out var list)) helmetHides[helmets.Relation(row)] = list = new();
                list.Add((helmets.Int(row, 0), helmets.Int(row, 1)));
            }
        }
        catch (Exception) { }

        JsonObject Variant(uint file, Db2 component, bool position)
        {
            var v = new JsonObject { ["File"] = file };
            if (component.Has(file))
            {
                v["Sex"] = component.Int(file, 0); v["Race"] = component.Int(file, 2);
                if (position) v["Position"] = component.Int(file, 3);
            }
            return v;
        }

        foreach (var id in sparse.Ids)
        {
            // ItemSparse: five strings first (Display_lang is the fifth), then the numeric fields.
            int slot = (int)sparse.SparseUInt(id, 5, 65);
            if (!Worn.Contains(slot) || !appearanceByItem.TryGetValue(id, out var appearance)) continue;
            uint display = appearances.UInt(appearance, 1);
            if (display == 0 || !displays.Has(display)) continue;
            catalog.Items.Add(new Item(id, sparse.SparseString(id, 4), slot, (int)sparse.SparseUInt(id, 5, 66), display));
            if (catalog.Displays.ContainsKey(display)) continue;
            var json = new JsonObject { ["Display"] = display };
            json["Geosets"] = new JsonArray(Enumerable.Range(0, 6).Select(i => (JsonNode)displays.Int(display, 13, i)).ToArray());
            var hides = new JsonArray();
            for (int sex = 0; sex < 2; sex++)
                foreach (var (race, group) in helmetHides.GetValueOrDefault(displays.UInt(display, 15, sex)) ?? new())
                    hides.Add(new JsonObject { ["Sex"] = sex, ["Race"] = race, ["Group"] = group });
            if (hides.Count > 0) json["HideGroups"] = hides;
            // The display's own textures: attached models wear them, a cloak paints the body's cape with the first.
            var ownTextures = new JsonArray();
            for (int index = 0; index < 2; index++)
                if (displays.UInt(display, 11, index) is var own and not 0 && materialFiles.TryGetValue(own, out var ownFiles))
                {
                    catalog.Readable.Add(ownFiles[0]);
                    ownTextures.Add($"mount://wowforever/textures/{ownFiles[0]}.vtex");
                }
            json["Textures"] = ownTextures;
            var models = new JsonArray();
            for (int index = 0; index < 2; index++)
            {
                uint resource = displays.UInt(display, 10, index);
                if (resource == 0 || !filesByResource.TryGetValue(resource, out var files)) continue;
                uint texture = 0;
                if (displays.UInt(display, 11, index) is var material and not 0 && materialFiles.TryGetValue(material, out var textures)) texture = textures[0];
                else foreach (var m in modelMaterialsByDisplay.GetValueOrDefault(display) ?? new())
                    if (modelMaterials.Int(m, 2) == index && materialFiles.TryGetValue(modelMaterials.UInt(m, 0), out var alt)) { texture = alt[0]; break; }
                var variants = new JsonArray();
                foreach (var file in files)
                {
                    if (!host.Storage.Exists(file)) continue;
                    catalog.Models.Add((file, texture));
                    var v = Variant(file, componentModels, true);
                    v["Path"] = $"mount://wowforever/items/models/{file}_{texture}.vmdl";
                    variants.Add(v);
                }
                if (variants.Count > 0) models.Add(new JsonObject { ["Index"] = index, ["Variants"] = variants });
            }
            json["Models"] = models;
            var pieces = new JsonArray();
            foreach (var piece in piecesByDisplay.GetValueOrDefault(display) ?? new())
            {
                if (!materialFiles.TryGetValue(displayMaterials.UInt(piece, 1), out var files)) continue;
                var variants = new JsonArray();
                foreach (var file in files)
                {
                    catalog.Readable.Add(file);
                    var v = Variant(file, componentTextures, false);
                    v["Path"] = $"mount://wowforever/textures/{file}.vtex";
                    variants.Add(v);
                }
                pieces.Add(new JsonObject { ["Section"] = displayMaterials.Int(piece, 0), ["Variants"] = variants });
            }
            json["Pieces"] = pieces;
            catalog.Displays[display] = json;
        }
        return catalog;
    }
}

/// <summary>An item's attached model (weapon, shield, helm, shoulder, quiver...), animated with its texture.</summary>
internal sealed class ItemModelLoader(uint file, uint texture) : WorkerLoader<CharacterData>
{
    protected override CharacterData Prepare()
    {
        var data = CharacterData.Read(file, Host.Storage.Read(file), Host.Storage.TryRead, type => type == 2 ? texture : 0, _ => true, CharacterData.Creature, false);
        foreach (var t in data.Geometry.Parts.Select(p => p.Texture).Distinct()) Host.PrepareTexture(t, false);
        return data;
    }
    protected override IEnumerable<(uint, bool)> Textures(CharacterData data) => data.Geometry.Parts.Select(p => (p.Texture, false)).Distinct();
    protected override object Create(CharacterData data) => SkinnedModel.Build(Host, Path, data, t => Host.GetTexture(t));
}

/// <summary>A texture uncompressed, so games can read its pixels and paint it into a character's atlas.</summary>
internal sealed class ReadableTextureLoader(uint file) : ResourceLoader<WowForeverMount>
{
    protected override object Load() => Host.ParticleTexture(file);
}
