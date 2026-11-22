namespace Quantum.Storage;

internal sealed record DesktopStoragePaths(string DataDirectory, string CacheDirectory)
{
    public string DatabasePath => Path.Combine(DataDirectory, "quantum.db");
    public string ModulesDirectory => Path.Combine(DataDirectory, "Modules");
    public string ShadowDirectory => Path.Combine(CacheDirectory, "PluginShadow");
    public string BrowserDirectory => Path.Combine(CacheDirectory, "WebView2");
    public string TemporaryDirectory => Path.Combine(CacheDirectory, "Temp");

    public static DesktopStoragePaths ForWindows(DesktopBuildProfile profile, string localAppData)
    {
        var root = Path.Combine(localAppData, profile.Publisher, profile.ApplicationId);
        return new(Path.Combine(root, "Data"), Path.Combine(root, "Cache"));
    }

    public static DesktopStoragePaths ForMac(DesktopBuildProfile profile, string library, string cache)
        => profile.IsPreview
            ? new(Path.Combine(library, "Application Support", profile.Publisher, profile.ApplicationId, "Data"),
                Path.Combine(cache, profile.Publisher, profile.ApplicationId))
            : new(library, cache);

    public string ResolveModulesDirectory(DesktopBuildProfile profile, string? configuredPath, string bundledPath, bool debug)
        => profile.IsPreview ? ModulesDirectory
            : !string.IsNullOrWhiteSpace(configuredPath) ? Path.GetFullPath(configuredPath)
            : debug && Directory.Exists(bundledPath) ? bundledPath : ModulesDirectory;

    public void Prepare()
    {
        foreach (var path in new[] { DataDirectory, CacheDirectory, ModulesDirectory, ShadowDirectory, BrowserDirectory, TemporaryDirectory })
        {
            DesktopDataMigration.RejectLinks(path);
            Directory.CreateDirectory(path);
        }
    }
}
