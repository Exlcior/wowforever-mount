using Sandbox;
using Sandbox.Mounting;
namespace MiniBox.WowForever;

/// <summary>A particle texture's sheet cut into frames (rows x columns), played over each particle's life.</summary>
internal sealed class SpriteLoader(uint id, int slot) : ResourceLoader<WowForeverMount>
{
    protected override object Load()
    {
        var sprite = Build();
        // Scenes served by the mount are serialized: the sprite must carry its own path to be found again.
        typeof(Resource).GetProperty("ResourcePath")?.GetSetMethod(true)?.Invoke(sprite, new object[] { Path });
        return sprite;
    }

    private Sprite Build()
    {
        var model = Host.GetModel(id);
        var slots = ParticleTextureLoader.Slots(model);
        if (slot >= slots.Count) return Sprite.FromTexture(Texture.White);
        var emitter = model.Emitters.FirstOrDefault(e => e.Texture == slots[slot]); // ribbon textures have no sheet
        var blp = Host.Image(slots[slot]);
        if (blp is null) return Sprite.FromTexture(Texture.White);
        int rows = Math.Max(1, emitter?.Rows ?? 1), columns = Math.Max(1, emitter?.Columns ?? 1);
        var rgba = blp.ToRgba();
        if (rows * columns == 1) return Sprite.FromTexture(Texture.Create(blp.Width, blp.Height).WithName($"{Path}.vtex").WithData(rgba).WithMips().Finish());
        int w = blp.Width / columns, h = blp.Height / rows;
        var frames = new List<Texture>();
        for (int cell = 0; cell < rows * columns; cell++)
        {
            int cx = cell % columns * w, cy = cell / columns * h; var data = new byte[w * h * 4];
            for (int y = 0; y < h; y++) Array.Copy(rgba, ((cy + y) * blp.Width + cx) * 4, data, y * w * 4, w * 4);
            frames.Add(Texture.Create(w, h).WithName($"{Path}_{cell}.vtex").WithData(data).WithMips().Finish());
        }
        var sprite = Sprite.FromTextures(frames, rows * columns / Math.Clamp(emitter?.Lifespan ?? 1, 0.05f, 30));
        if (sprite.Animations.Count > 0) sprite.Animations[0].LoopMode = Sprite.LoopMode.Loop;
        return sprite;
    }
}

/// <summary>Client particle emitter values mapped onto s&amp;box particle components (shared with the explorer's mapping).</summary>
internal static class ParticleSprites
{
    private const float Inches = Geometry.Inches;

    public static void Configure(GameObject go, ModelData.Emitter e, Sprite sprite, float scale)
    {
        float life = Math.Clamp(e.Lifespan, 0.05f, 30);
        var fx = go.Components.Create<ParticleEffect>(false);
        fx.MaxParticles = Math.Clamp((int)MathF.Ceiling(e.Rate * life * 1.3f) + 2, 4, 160);
        fx.Lifetime = life;
        fx.PreWarm = MathF.Min(life, 4);
        float unit = Inches * scale;
        fx.Scale = Curve(e.Scale, unit * 2); // the client's keys are half widths
        fx.ApplyShape = true; fx.ApplyAlpha = true; fx.ApplyColor = true;
        fx.Alpha = Curve(e.Alpha, 1);
        var g = new Gradient();
        for (int k = 0; k < 3; k++) g.AddColor(k * 0.5f, new Color(e.Colors[k].X, e.Colors[k].Y, e.Colors[k].Z));
        g.AddAlpha(0, 1); g.AddAlpha(1, 1);
        fx.Gradient = new ParticleGradient { Type = ParticleGradient.ValueType.Gradient, Evaluation = ParticleGradient.EvaluationType.Life, GradientA = g };
        fx.Tint = Color.White;
        bool additive = e.Blend >= 3;
        fx.Brightness = additive ? 2 : 1;
        var up = go.WorldRotation.Up;
        float speed = e.Speed * unit, spread = MathF.Sin(Math.Clamp(e.VerticalRange, 0, MathF.PI / 2));
        fx.InitialVelocity = new ParticleVector3 { X = up.x * speed, Y = up.y * speed, Z = up.z * speed };
        fx.StartVelocity = new ParticleFloat(0, MathF.Max(MathF.Abs(speed) * MathF.Max(spread, e.SpeedVariation), 0));
        if (MathF.Abs(e.Gravity) > 1e-3f)
        {
            fx.Force = true; fx.ForceSpace = ParticleEffect.SimulationSpace.World;
            fx.ForceDirection = Vector3.Down * e.Gravity * unit; fx.ForceScale = 1;
        }
        fx.ApplyRotation = true; fx.InitialRoll = new ParticleFloat(0, 360);
        fx.Enabled = true;
        ParticleEmitter emitter;
        var area = new Vector3(e.AreaWidth, e.AreaLength, 0) * unit;
        if (area.Length > 1) { var b = go.Components.Create<ParticleBoxEmitter>(false); b.Size = area + Vector3.Up; emitter = b; }
        else { var s = go.Components.Create<ParticleSphereEmitter>(false); s.Radius = 0; s.Velocity = 0; emitter = s; }
        emitter.Loop = true; emitter.Rate = MathF.Min(e.Rate, 160 / life); emitter.Duration = 1; emitter.Enabled = true;
        var renderer = go.Components.Create<ParticleSpriteRenderer>(false);
        renderer.Sprite = sprite; renderer.Additive = additive; renderer.Lighting = !additive; renderer.Shadows = false;
        renderer.DepthFeather = 4; renderer.Alignment = ParticleSpriteRenderer.BillboardAlignment.LookAtCamera;
        renderer.Enabled = true;
    }

    private static ParticleFloat Curve(float[] keys, float scale)
    {
        if (keys is not { Length: 3 }) return scale;
        var c = new Curve();
        c.AddPoint(0, keys[0] * scale); c.AddPoint(0.5f, keys[1] * scale); c.AddPoint(1, keys[2] * scale);
        return new ParticleFloat { Type = ParticleFloat.ValueType.Curve, Evaluation = ParticleFloat.EvaluationType.Life, CurveA = c };
    }
}
