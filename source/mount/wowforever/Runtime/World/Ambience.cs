using System.Text.Json.Nodes;
using Sandbox;
namespace MiniBox.WowForever;

using NVector3 = System.Numerics.Vector3;

/// <summary>The zone light of one place at one hour, blended like the client (and like the explorer's
/// AzerothAmbience): the strongest local lights first, the map's default light takes the rest.
/// Colours are linear.</summary>
internal sealed class Ambience
{
    public NVector3 Direct, Ambient, Top, Middle, Band1, Band2, Smog, Fog, Sun;

    private static JsonNode catalog;

    private static JsonNode Catalog()
    {
        if (catalog is not null) return catalog;
        using var stream = typeof(WowForeverMount).Assembly.GetManifestResourceStream("wowforever.ambience.json");
        return catalog = JsonNode.Parse(new StreamReader(stream).ReadToEnd());
    }

    /// <summary>Neutral daylight for maps without an original light (same values as the explorer).</summary>
    public static Ambience Neutral()
    {
        static NVector3 L(float r, float g, float b) => new(MathF.Pow(r, 2.2f), MathF.Pow(g, 2.2f), MathF.Pow(b, 2.2f));
        var fog = L(0.84f, 0.88f, 0.91f);
        return new Ambience { Direct = L(1, 0.97f, 0.9f), Ambient = L(0.25f, 0.32f, 0.35f), Top = new(0, 0.012f, 0.067f), Middle = new(0.042f, 0.36f, 0.62f),
            Band1 = new(0.32f, 0.72f, 0.91f), Band2 = fog * 1.1f, Smog = fog, Fog = fog, Sun = new(1, 0.93f, 0.73f) };
    }

    public static Ambience At(string map, NVector3 position, float hours = 12)
    {
        var root = Catalog();
        var continent = root["Continents"]?[map];
        if (continent is null) return Neutral();
        var weighted = new List<(int Param, float Weight)>();
        foreach (var light in continent["Lights"]?.AsArray() ?? new JsonArray())
        {
            var p = light["Position"].AsArray();
            float px = (float)p[0], py = (float)p[1], pz = (float)p[2];
            var delta = new NVector3(px - position.X, py - position.Y, pz == 0 ? 0 : pz - position.Z);
            float distance = delta.Length(), inner = (float)light["Inner"], outer = (float)light["Outer"];
            if (distance >= outer) continue;
            weighted.Add(((int)light["Param"], distance <= inner ? 1 : 1 - (distance - inner) / (outer - inner)));
        }
        float remaining = 1; var total = new Ambience(); bool any = false;
        foreach (var (param, weight) in weighted.OrderByDescending(x => x.Weight))
        {
            if (remaining <= 0.001f) break;
            if (Evaluate(root, param, hours) is not { } s) continue;
            float w = weight * remaining; total.Add(s, w); remaining -= w; any = true;
        }
        if (remaining > 0.001f)
        {
            if (continent["Default"] is JsonNode d && Evaluate(root, (int)d, hours) is { } s) total.Add(s, remaining);
            else if (!any) return Neutral();
            else total = Scaled(total, 1 / (1 - remaining));
        }
        return total;
    }

    private static Ambience Evaluate(JsonNode root, int param, float hours)
    {
        var keys = root["Params"]?[param.ToString()]?.AsArray();
        if (keys is null || keys.Count == 0) return null;
        int next = -1;
        for (int i = 0; i < keys.Count; i++) if ((float)keys[i]["Time"] > hours) { next = i; break; }
        var b = keys[next < 0 ? 0 : next]; var a = keys[next <= 0 ? keys.Count - 1 : next - 1];
        float ta = (float)a["Time"], tb = (float)b["Time"], span = (tb - ta + 24) % 24, t = span <= 0 ? 0 : ((hours - ta + 24) % 24) / span;
        var result = Scaled(Key(a), 1 - Math.Clamp(t, 0, 1)); result.Add(Key(b), Math.Clamp(t, 0, 1));
        return result;
    }

    private static Ambience Key(JsonNode k)
    {
        static NVector3 L(JsonNode c) => c is JsonArray a && a.Count == 3 ? new(MathF.Pow((float)a[0], 2.2f), MathF.Pow((float)a[1], 2.2f), MathF.Pow((float)a[2], 2.2f)) : NVector3.Zero;
        return new Ambience { Direct = L(k["Direct"]), Ambient = L(k["Ambient"]), Top = L(k["SkyTop"]), Middle = L(k["SkyMiddle"]), Band1 = L(k["SkyBand1"]),
            Band2 = L(k["SkyBand2"]), Smog = L(k["SkySmog"]), Fog = L(k["SkyFog"]), Sun = L(k["Sun"]) };
    }

    private void Add(Ambience b, float w)
    {
        Direct += b.Direct * w; Ambient += b.Ambient * w; Top += b.Top * w; Middle += b.Middle * w; Band1 += b.Band1 * w;
        Band2 += b.Band2 * w; Smog += b.Smog * w; Fog += b.Fog * w; Sun += b.Sun * w;
    }

    private static Ambience Scaled(Ambience a, float s) { var r = new Ambience(); r.Add(a, s); return r; }

    /// <summary>sRGB colour for light components (the explorer's Gamma()).</summary>
    public static Color Gamma(NVector3 v, float scale = 1) => new(MathF.Pow(v.X, 1 / 2.2f) * scale, MathF.Pow(v.Y, 1 / 2.2f) * scale, MathF.Pow(v.Z, 1 / 2.2f) * scale, 1);
}
