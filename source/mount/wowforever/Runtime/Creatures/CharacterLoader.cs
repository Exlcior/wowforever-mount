using Sandbox;

namespace MiniBox.WowForever;

/// <summary>A playable character: the source skeleton and locomotion, and its look. The plain model shows the unequipped
/// default (base geosets, composed skin, first hair colour) for any game; the dressable one carries every geoset with
/// a material of its own ("_g&lt;geoset&gt;_"), so a game hides the variants it does not wear (runtime body groups are
/// not honoured by the engine for built models).</summary>
internal sealed class CharacterLoader(CharacterPreset preset, AppearanceCatalog.Look look, bool dressable = false) : WorkerLoader<CharacterData>
{
    private BlpImage atlas;
    private byte[] atlasData;
    private int atlasMips;

    /// <summary>Every geoset of the model (or only the default ones), and the choices of each group, default first.</summary>
    public static (CharacterData Data, SortedDictionary<int, List<int>> Groups) ReadModel(WowForeverMount host, CharacterPreset preset, AppearanceCatalog.Look look, bool all = true)
    {
        var data = CharacterData.Read(preset.Model, host.Storage.Read(preset.Model), host.Storage.TryRead,
            // The cape (texture type 2) keeps its pieces with a stand-in texture: games paint the cloak they equip.
            type => type is 1 or 2 ? preset.Skin : look?.Defaults.GetValueOrDefault(type) ?? 0, _ => true, CharacterData.Locomotion, true);
        var visible = CharacterData.DefaultGeosets(data.Geometry.Parts.Any(p => p.Geoset == 3202));
        var groups = SkinnedModel.BodyGroups(data.Geometry, visible);
        if (!all) data.Geometry.Parts.RemoveAll(p => p.Geoset != 0 && !visible(p.Geoset));
        return (data, groups);
    }

    protected override CharacterData Prepare()
    {
        var (data, _) = ReadModel(Host, preset, look, dressable);
        atlas = preset.Compose(Host.Storage.TryRead);
        atlasData = atlas.RgbaChain(out atlasMips);
        foreach (var texture in data.Geometry.Parts.Select(p => p.Texture).Distinct()) Host.PrepareTexture(texture, false);
        return data;
    }
    protected override IEnumerable<(uint, bool)> Textures(CharacterData data) => data.Geometry.Parts.Select(p => (p.Texture, false)).Distinct();
    protected override object Create(CharacterData data)
    {
        var bodyTexture=Texture.Create(atlas.Width,atlas.Height).WithData(atlasData).WithMips(atlasMips).WithStaticUsage().Finish();
        atlas=null; atlasData=null;
        return SkinnedModel.Build(Host, Path, data, texture => texture == preset.Skin ? bodyTexture : Host.GetTexture(texture), dressable);
    }
}

/// <summary>How a character can be dressed: its customization options, atlas sections and body groups (JSON).</summary>
internal sealed class CharacterInfoLoader(CharacterPreset preset, AppearanceCatalog.Look look) : WorkerLoader<string>
{
    protected override object Create(string json) => WowForeverMount.DataPrefab(Name, json);
    protected override string Prepare()
    {
        var info = look?.Json.DeepClone().AsObject() ?? new System.Text.Json.Nodes.JsonObject { ["Key"] = preset.Key };
        var groups = new System.Text.Json.Nodes.JsonObject();
        foreach (var (group, choices) in CharacterLoader.ReadModel(Host, preset, look).Groups.Where(g => g.Value.Count > 1))
            groups["g" + group] = new System.Text.Json.Nodes.JsonArray(choices.Select(c => (System.Text.Json.Nodes.JsonNode)c).ToArray());
        info["BodyGroups"] = groups;
        info["Skin"] = preset.Skin;
        info["Model"] = $"mount://wowforever/characters/{preset.Key}.vmdl";
        info["Dressable"] = $"mount://wowforever/characters/{preset.Key}_dressable.vmdl";
        return info.ToJsonString();
    }
}
