namespace MiniBox.WowForever;

/// <summary>The client's own texts in the installed language (WowStorage.Locale): its interface strings
/// (GlobalStrings: BACK, CUSTOMIZE, HEADSLOT...), map, zone and race names. Nothing is translated here.</summary>
internal static class GameStrings
{
    public const uint GlobalStringsFile = 1394440, ChrRacesFile = 1305311;

    /// <summary>Every interface string of the client, by its tag.</summary>
    public static Dictionary<string, string> Global(WowForeverMount host)
    {
        var table = host.Table(GlobalStringsFile);
        var strings = new Dictionary<string, string>();
        foreach (var id in table.Ids)
            if (table.String(id, 0) is { Length: > 0 } tag) strings.TryAdd(tag, table.String(id, 1) ?? "");
        return strings;
    }

    /// <summary>The map's name (Map.db2 MapName), else the build-time title.</summary>
    public static string Map(WowForeverMount host, Tables.Map map)
    {
        try { return host.Table(LoadingScreenLoader.MapFile).String((uint)map.Id, 1) is { Length: > 0 } name ? name : map.Title; }
        catch (Exception) { return map.Title; }
    }

    /// <summary>A zone or subzone's name (AreaTable.db2 AreaName).</summary>
    public static string Area(WowForeverMount host, uint area, string fallback)
    {
        try { return host.Table(AreaLoader.AreaTableFile).String(area, 1) is { Length: > 0 } name ? name : fallback; }
        catch (Exception) { return fallback; }
    }

    /// <summary>"Race · sex" as the client names them (ChrRaces Name / Name_female, GlobalStrings MALE / FEMALE).</summary>
    public static string Character(WowForeverMount host, IReadOnlyDictionary<string, string> global, int race, int sex, string fallback)
    {
        try
        {
            var races = host.Table(ChrRacesFile);
            string name = races.String((uint)race, sex == 1 ? 3 : 2);
            if (string.IsNullOrEmpty(name)) name = races.String((uint)race, 2);
            if (string.IsNullOrEmpty(name)) return fallback;
            return global.TryGetValue(sex == 1 ? "FEMALE" : "MALE", out var label) ? $"{name} · {label}" : name;
        }
        catch (Exception) { return fallback; }
    }
}
