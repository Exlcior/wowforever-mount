using System.Text.Json.Nodes;
using Sandbox;
using Sandbox.Mounting;

namespace MiniBox.WowForever;

/// <summary>Spell visuals of the installed client: SpellName → SpellXSpellVisual → SpellVisualEvent →
/// SpellVisualKitEffect (model attach) → SpellVisualKitModelAttach → SpellVisualEffectName (effect model).
/// Each spell becomes phases (precast, cast, impact) of effect models on attachment points.</summary>
internal sealed class SpellCatalog
{
    public const uint NameFile = 1990283, SpellVisualsFile = 1101657, EventsFile = 1685317, KitEffectsFile = 1140480,
        ModelAttachFile = 897953, EffectNameFile = 897948, VisualFile = 897952, MissileFile = 897954;
    public readonly Dictionary<uint, string> Names = new();
    public readonly Dictionary<uint, JsonObject> Spells = new();
    public readonly HashSet<uint> EffectModels = new();

    public static SpellCatalog Read(WowForeverMount host)
    {
        var catalog = new SpellCatalog();
        var names = host.Table(NameFile); var visuals = host.Table(SpellVisualsFile); var events = host.Table(EventsFile);
        var effects = host.Table(KitEffectsFile); var attaches = host.Table(ModelAttachFile); var effectNames = host.Table(EffectNameFile);
        var visualsBySpell = visuals.Ids.GroupBy(v => visuals.Relation(v)).ToDictionary(g => g.Key, g => g.Select(v => visuals.UInt(v, 2)).Distinct().ToList());
        var eventsByVisual = events.Ids.GroupBy(e => events.Relation(e)).ToDictionary(g => g.Key, g => g.ToList());
        var spellVisualsTable = host.Table(VisualFile); var missiles = host.Table(MissileFile);
        var missilesBySet = missiles.Ids.GroupBy(m => missiles.Relation(m)).ToDictionary(g => g.Key, g => g.ToList());
        var effectsByKit = effects.Ids.Where(e => effects.UInt(e, 0) == 2).GroupBy(e => effects.Relation(e)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var spell in names.Ids)
        {
            if (!visualsBySpell.TryGetValue(spell, out var spellVisuals)) continue;
            uint MissileSet(uint visual) => spellVisualsTable.UInt(visual, 12);
            var phases = new JsonArray();
            foreach (var visual in spellVisuals.Take(1))
                foreach (var e in eventsByVisual.GetValueOrDefault(visual) ?? new())
                {
                    uint kit = events.UInt(e, 7), start = events.UInt(e, 0), target = events.UInt(e, 6);
                    var models = new JsonArray();
                    foreach (var k in effectsByKit.GetValueOrDefault(kit) ?? new())
                    {
                        uint attach = effects.UInt(k, 1), effectName = attaches.UInt(attach, 2), file = effectNames.UInt(effectName, 0);
                        if (file == 0 || !host.Storage.Exists(file)) continue;
                        catalog.EffectModels.Add(file);
                        float scale = attaches.Float(attach, 11) * Math.Max(0.01f, effectNames.Float(effectName, 2));
                        models.Add(new JsonObject { ["Model"] = file, ["Path"] = $"mount://wowforever/effects/{file}.vmdl", ["Info"] = $"mount://wowforever/effects/{file}.prefab",
                            ["Slot"] = (int)attaches.UInt(attach, 3), ["Scale"] = float.IsFinite(scale) && scale > 0 ? scale : 1 });
                    }
                    if (models.Count == 0) continue;
                    // Client start events: 1-2 precast, 3 cast, 6 impact, 7 aura start, 11 channel start; the
                    // matching ends (8, 12) only close a state. Target 1 is the caster, 2 the target, 3-4 an area.
                    if (start is 8 or 12) continue;
                    string phase = start is 1 or 2 ? "precast" : start == 3 ? "cast" : start == 7 ? "aura" : start == 11 ? "channel" : "impact";
                    phases.Add(new JsonObject { ["Phase"] = phase, ["Target"] = target == 1 ? "caster" : "target", ["Models"] = models });
                }
            // The projectile: SpellVisual → SpellVisualMissileSet → SpellVisualMissile → effect model and speed.
            foreach (var visual in spellVisuals.Take(1))
                foreach (var m in missilesBySet.GetValueOrDefault(MissileSet(visual)) ?? new())
                {
                    uint effectName = missiles.UInt(m, 3), file = effectNames.UInt(effectName, 0);
                    if (file == 0 || !host.Storage.Exists(file)) continue;
                    catalog.EffectModels.Add(file);
                    float speed = effectNames.Float(effectName, 1);
                    phases.Add(new JsonObject { ["Phase"] = "missile", ["Target"] = "target", ["Speed"] = float.IsFinite(speed) && speed > 1 ? speed : 24,
                        ["Models"] = new JsonArray(new JsonObject { ["Model"] = file, ["Path"] = $"mount://wowforever/effects/{file}.vmdl", ["Info"] = $"mount://wowforever/effects/{file}.prefab",
                            ["Slot"] = (int)missiles.UInt(m, 5), ["Scale"] = Math.Max(0.05f, effectNames.Float(effectName, 2)) }) });
                }
            if (phases.Count == 0) continue;
            catalog.Names[spell] = names.String(spell, 0);
            catalog.Spells[spell] = new JsonObject { ["Id"] = spell, ["Name"] = catalog.Names[spell], ["Phases"] = phases };
        }
        return catalog;
    }
}

/// <summary>A spell effect model (or any client M2 by FileDataID): animated, with its own textures and its stand
/// sequence playing; its particle emitters are described by the matching .prefab.</summary>
internal sealed class EffectLoader(uint file) : WorkerLoader<CharacterData>
{
    protected override CharacterData Prepare()
    {
        var data = CharacterData.Read(file, Host.Storage.Read(file), Host.Storage.TryRead, _ => 0, _ => true, CharacterData.Creature, false);
        foreach (var texture in data.Geometry.Parts.Select(p => p.Texture).Distinct()) Host.PrepareTexture(texture, false);
        return data;
    }
    protected override IEnumerable<(uint, bool)> Textures(CharacterData data) => data.Geometry.Parts.Select(p => (p.Texture, false)).Distinct();
    protected override object Create(CharacterData data) => SkinnedModel.Build(Host, Path, data, texture => Host.GetTexture(texture));
}
