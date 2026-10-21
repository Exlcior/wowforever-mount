using TACTSharp;

namespace MiniBox.WowForever;

/// <summary>Read-only access to the player's installed World of Warcraft Forever (local CASC, no download).</summary>
public sealed class WowStorage : IDisposable
{
    public const string Product = "wowf";
    public string Directory { get; }
    public string Version { get; }
    /// <summary>The installed text language (frFR, enUS...): localized DB2 strings, fonts and interface files.</summary>
    public string Locale { get; }
    private readonly BuildInstance build;
    private readonly object gate = new();
    private readonly object readGate = new();
    public long BytesRead { get; private set; }
    public int MissingReads { get; private set; }

    private WowStorage(string directory, BuildInstance build, string version, string locale)
    {
        Directory = directory; this.build = build; Version = version; Locale = locale;
    }

    /// <summary>Finds an install: SBOX_WOWFOREVER_DIR, the Blizzard registry keys, then usual folders.</summary>
    public static string Locate()
    {
        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("SBOX_WOWFOREVER_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft", @"SOFTWARE\Blizzard Entertainment\World of Warcraft" })
            {
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                    if (k?.GetValue("InstallPath") is string p) candidates.Add(Path.GetFullPath(Path.Combine(p, "..")));
                }
                catch { }
            }
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                foreach (var folder in new[] { "Program Files (x86)/World of Warcraft", "Program Files/World of Warcraft", "Programmes/World of Warcraft", "Games/World of Warcraft", "World of Warcraft" })
                    candidates.Add(Path.Combine(drive.RootDirectory.FullName, folder));
        }
        foreach (var candidate in candidates)
        {
            // A path to _forever_ itself points at its parent, which holds .build.info and Data.
            var root = Path.GetFileName(candidate.TrimEnd('/', '\\')).Equals("_forever_", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(candidate.TrimEnd('/', '\\')) : candidate;
            if (root is null) continue;
            var info = Path.Combine(root, ".build.info");
            if (File.Exists(info) && System.IO.Directory.Exists(Path.Combine(root, "Data", "data"))
                && File.ReadLines(info).Any(l => l.EndsWith("|" + Product, StringComparison.Ordinal)))
                return root;
        }
        return null;
    }

    public static WowStorage Open(string root)
    {
        Settings.LogLevel = TSLogLevel.Off;
        var build = new BuildInstance();
        build.Settings.BaseDir = root;
        build.Settings.Product = Product;
        // Like the game client: files the launcher has not written yet are fetched from Blizzard's CDN
        // for this exact build, checked against their content keys, and cached on the player's PC.
        build.Settings.TryCDN = true;
        build.Settings.ForceHTTP1 = true;
        build.Settings.ListfileFallback = false;
        build.Settings.CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sbox-wowforever", "cdn");
        var info = new BuildInfo(Path.Combine(root, ".build.info"), build.Settings, build.cdn);
        var entry = info.Entries.FirstOrDefault(e => e.Product == Product);
        if (string.IsNullOrEmpty(entry.BuildConfig)) throw new InvalidDataException("No World of Warcraft Forever build in .build.info");
        build.Settings.Region = string.IsNullOrEmpty(entry.Branch) ? "eu" : entry.Branch;
        build.cdn.ProductDirectory = entry.CDNPath;
        var header = File.ReadLines(Path.Combine(root, ".build.info")).First().Split('|').Select(h => h.Split('!')[0]).ToList();
        var row = File.ReadLines(Path.Combine(root, ".build.info")).Skip(1).Select(l => l.Split('|')).First(r => r[^1] == Product);
        int hosts = header.IndexOf("CDN Hosts");
        if (hosts >= 0) build.cdn.SetCDNs(row[hosts].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        // Like the client: the text language the launcher installed ("frFR text?" in the build's tags).
        int tags = header.IndexOf("Tags");
        string locale = tags >= 0 ? TextLocale(row[tags]) : "enUS";
        if (Enum.TryParse<RootInstance.LocaleFlags>(locale, out var flag)) build.Settings.Locale = flag; else locale = "enUS";
        build.LoadConfigs(entry.BuildConfig, entry.CDNConfig);
        build.Load();
        return new WowStorage(root, build, entry.Version, locale);
    }

    private static string TextLocale(string tags)
    {
        var words = tags.Split(new[] { ' ', ':' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < words.Length; i++) if (words[i] == "text?") return words[i - 1];
        return "enUS";
    }

    public bool Exists(uint fileDataId) => fileDataId != 0 && build.Root!.FileExists(fileDataId);

    /// <summary>Last read failure, for diagnostics.</summary>
    public string LastReadError { get; private set; }

    /// <summary>Whole decoded file, or null when the installation does not hold it (yet).</summary>
    public byte[] TryRead(uint fileDataId)
    {
        if (fileDataId == 0 || !build.Root!.FileExists(fileDataId)) return null;
        try
        {
            var data = build.OpenFileByFDID(fileDataId); // concurrent reads are the fast path
            lock (gate) BytesRead += data.Length;
            return data;
        }
        catch (Exception) { }
        // Concurrent reads of the same archive or CDN download can fail transiently: retry alone.
        lock (readGate)
        {
            try
            {
                var data = build.OpenFileByFDID(fileDataId);
                lock (gate) BytesRead += data.Length;
                return data;
            }
            catch (Exception error) { lock (gate) { MissingReads++; LastReadError = $"{fileDataId}: {error.GetType().Name} {error.Message}"; } return null; }
        }
    }

    public byte[] Read(uint fileDataId) => TryRead(fileDataId) ?? throw new FileNotFoundException($"FileDataID {fileDataId} is not in the installed game.");

    /// <summary>Resolves a client path through the root manifest's name hashes.</summary>
    public uint FileDataIdOf(string path)
    {
        var entries = build.Root!.GetEntriesByLookup(new Jenkins96().ComputeHash(path));
        return entries.Count > 0 ? entries[0].fileDataID : 0;
    }

    public void Dispose()
    {
        try { build.cdn?.Dispose(); } catch { }
    }
}
