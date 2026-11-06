namespace Quantum.Storage;

/// <summary>MAUI's Catalyst Library and cache locations are shared roots, never deletion targets.</summary>
internal static class MacLocalData
{
    public static string[] GetOwnedPaths(string libraryDirectory, string cacheDirectory)
    {
        DesktopDataMigration.RejectLinks(libraryDirectory);
        DesktopDataMigration.RejectLinks(cacheDirectory);
        var paths = new List<string>();
        foreach (var name in new[]
        {
            "quantum.db", "quantum.db-wal", "quantum.db-shm", "Modules", "Modules.migration-backup",
            "Modules.migration.lock", "MobileBridgeJournal"
        })
        {
            paths.Add(Path.Combine(libraryDirectory, name));
        }

        paths.Add(Path.Combine(cacheDirectory, "PluginShadow"));
        paths.Add(Path.Combine(cacheDirectory, "io.quantum.desktop"));
        var logs = Path.Combine(libraryDirectory, "Logs");
        DesktopDataMigration.RejectLinks(logs);
        if (Directory.Exists(logs))
        {
            paths.AddRange(Directory.EnumerateFiles(logs, "quantum-*.log"));
        }

        return paths.ToArray();
    }

    public static void ValidateCleanup(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            DesktopDataMigration.RejectLinks(path);
            if (Directory.Exists(path))
            {
                ValidateCleanup(Directory.EnumerateFileSystemEntries(path));
            }
        }
    }

    public static void Clear(string libraryDirectory, string cacheDirectory)
    {
        var paths = GetOwnedPaths(libraryDirectory, cacheDirectory);
        ValidateCleanup(paths);
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
