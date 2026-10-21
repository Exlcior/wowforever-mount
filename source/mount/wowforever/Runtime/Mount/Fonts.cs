namespace MiniBox.WowForever;

/// <summary>The client's own interface fonts (Friz Quadrata, Morpheus, Arial Narrow, Skurri), read from the
/// installation and handed to the engine's font manager, so games use them by family name. Without the mount the
/// interface falls back to the engine's fonts: nothing is shipped with the project.</summary>
internal static class GameFonts
{
    /// <summary>fonts/frizqt__.ttf, morpheus.ttf, arialn.ttf, skurri.ttf (FileDataIDs of the client).</summary>
    private static readonly uint[] Files = { 615960, 615962, 615958, 615961 };

    public static int Register(WowForeverMount host)
    {
        // FontManager is internal to the engine: reached by reflection, like the resource paths of mounted sprites.
        var type = typeof(Sandbox.Texture).Assembly.GetType("Sandbox.FontManager");
        var manager = type?.GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);
        var load = type?.GetMethod("Load", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new[] { typeof(Stream) }, null);
        if (manager is null || load is null) return 0;
        var fonts = type.GetField("_loadedFonts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(manager) as System.Collections.IDictionary;
        var before = fonts?.Keys.Cast<object>().ToHashSet() ?? new HashSet<object>();
        int loaded = 0;
        foreach (var file in Files)
        {
            var bytes = host.Storage.TryRead(file);
            if (bytes is null) continue;
            try { load.Invoke(manager, new object[] { new MemoryStream(bytes) }); loaded++; }
            catch (System.Reflection.TargetInvocationException) { } // an unreadable font leaves the engine's own
        }
        // A game load clears every font but the engine's menu fonts: the mount's stay loaded as long as it is mounted.
        if (fonts is not null)
            lock (fonts)
                foreach (var key in fonts.Keys.Cast<object>().Where(k => !before.Contains(k)).ToList())
                {
                    var font = fonts[key];
                    font?.GetType().GetProperty("IsMenu")?.SetValue(font, true);
                    fonts[key] = font; // a struct entry is a copy: written back
                }
        return loaded;
    }
}
