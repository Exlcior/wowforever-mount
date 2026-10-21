using System.Text.Json.Nodes;
using Sandbox;
using Sandbox.Mounting;

namespace MiniBox.WowForever;

/// <summary>Zone names of a tile, per terrain chunk (MCNK area ids → AreaTable), for the client's zone banners.</summary>
internal sealed class AreaLoader(string map, string tile) : WorkerLoader<string>
{
    public const uint AreaTableFile = 1353545;
    protected override object Create(string json) => WowForeverMount.DataPrefab(Name, json);
    protected override string Prepare()
    {
        var wdt = Host.GetWdt(map);
        var files = wdt.Tiles.Values.First(t => t.Id == tile);
        var root = Host.Storage.TryRead(files.Root);
        var areas = Host.Table(AreaTableFile);
        var chunks = new JsonArray(); var names = new JsonObject(); var corner = new JsonArray();
        var adt = root is null ? null : AdtRoot.Read(root);
        // Chunk (row, col) covers [corner - (row+1)·33⅓, corner - row·33⅓] in client X (rows) and Y (columns).
        if (adt?.Chunks[0] is { } first) { corner.Add(first.Position.X); corner.Add(first.Position.Y); }
        if (adt is not null)
            foreach (var chunk in adt.Chunks)
            {
                uint id = chunk?.AreaId ?? 0; chunks.Add(id);
                if (id == 0 || names.ContainsKey(id.ToString())) continue;
                // Subzones name their zone through their parent: "Goldshire" in "Elwynn Forest".
                uint zone = id; for (int guard = 0; guard < 8 && areas.UInt(zone, 3) != 0; guard++) zone = areas.UInt(zone, 3);
                names[id.ToString()] = new JsonObject { ["Zone"] = areas.String(zone, 1), ["Area"] = zone == id ? "" : areas.String(id, 1) };
            }
        return new JsonObject { ["Corner"] = corner, ["Chunks"] = chunks, ["Names"] = names }.ToJsonString();
    }
}

/// <summary>A loading screen picture of the client (the map's main image, a piece of its frame or its logo).</summary>
internal sealed class LoadingScreenLoader(uint file) : ResourceLoader<WowForeverMount>
{
    public const uint MapFile = 1349477, LoadingScreensFile = 1266541, SkinFile = 2478235;
    protected override object Load() => Host.GetTexture(file);

    /// <summary>The map's loading screen (Map → LoadingScreens): its main image, the frame it is drawn in
    /// (LoadingScreenSkin: Forever's leather book around the picture) and its logo.</summary>
    public sealed record Screen(uint Image, uint Skin, uint Logo);

    public static Screen Find(WowForeverMount host, Tables.Map map)
    {
        var maps = host.Table(MapFile); var screens = host.Table(LoadingScreensFile);
        uint screen = maps.UInt((uint)map.Id, 11);
        if (screen == 0 || !screens.Ids.Contains(screen)) return null;
        // MainImage, else the wide, 16:9 and narrow pictures of older screens.
        uint image = 0;
        foreach (int field in new[] { 4, 2, 1, 0 }) { uint id = screens.UInt(screen, field); if (id != 0 && host.Storage.Exists(id)) { image = id; break; } }
        if (image == 0) return null;
        uint skin = screens.UInt(screen, 3), logo = screens.UInt(screen, 5);
        // Screens in Forever's frame without a logo of their own show Forever's (camelot160/logo), inferred from the files.
        if (logo == 0 && skin != 0)
            logo = host.NamedModels.FirstOrDefault(n => n.Value.StartsWith("interface/glues/loadingscreens/camelot160/logo/logo_camelot")).Key;
        return new Screen(image, skin, logo != 0 && host.Storage.Exists(logo) ? logo : 0);
    }

    /// <summary>The frame's pieces by their role (LoadingScreenSkin.FileDataID[10]).</summary>
    public static readonly string[] SkinParts =
        { "BottomOuter", "BottomInner", "BottomFill", "LeftBookEnd", "LeftBackground", "LeftInner", "RightBackground", "SideFill", "Gradient", "BottomLeftInner" };
}
