using Microsoft.Data.Sqlite;
using Quantum.Storage;

namespace Quantum.Tests;

public sealed class DesktopStoragePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quantum-profile-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PreviewDatabaseAndPluginWritesLeaveStableFilesUntouched()
    {
        var stable = DesktopStoragePaths.ForWindows(DesktopBuildProfile.Stable, _root);
        var preview = DesktopStoragePaths.ForWindows(DesktopBuildProfile.Preview, _root);
        stable.Prepare();
        preview.Prepare();
        Assert.Equal(Path.Combine(_root, "io7-cn", "io.quantum.desktop", "Data"), stable.DataDirectory);
        await WriteDatabaseAsync(stable.DatabasePath, "production");
        var original = await File.ReadAllBytesAsync(stable.DatabasePath);
        var stablePlugin = Path.Combine(stable.ModulesDirectory, "plugin.json");
        await File.WriteAllTextAsync(stablePlugin, "production plugin");

        await WriteDatabaseAsync(preview.DatabasePath, "test");
        await File.WriteAllTextAsync(Path.Combine(preview.ModulesDirectory, "plugin.json"), "test plugin");
        await File.WriteAllTextAsync(Path.Combine(preview.BrowserDirectory, "profile"), "test browser data");

        Assert.Equal(original, await File.ReadAllBytesAsync(stable.DatabasePath));
        Assert.Equal("production plugin", await File.ReadAllTextAsync(stablePlugin));
        Assert.False(File.Exists(Path.Combine(stable.BrowserDirectory, "profile")));
        Assert.NotEqual(stable.CacheDirectory, preview.CacheDirectory);
        Assert.NotEqual(stable.ShadowDirectory, preview.ShadowDirectory);
        Assert.NotEqual(stable.TemporaryDirectory, preview.TemporaryDirectory);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(preview.DataDirectory)!, DesktopDataMigration.CompletionFile)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreviewNeverLoadsProductionOrBundledModuleDirectories(bool debug)
    {
        var preview = DesktopStoragePaths.ForWindows(DesktopBuildProfile.Preview, _root);
        var bundled = Path.Combine(_root, "installed-app", "Modules");
        Directory.CreateDirectory(bundled);
        Assert.Equal(preview.ModulesDirectory, preview.ResolveModulesDirectory(DesktopBuildProfile.Preview,
            Path.Combine(_root, "production-plugins"), bundled, debug));
    }

    [Fact]
    public void MacPreviewDoesNotUseTheSharedLibraryOrCacheRoot()
    {
        var library = Path.Combine(_root, "Library");
        var cache = Path.Combine(library, "Caches");
        var stable = DesktopStoragePaths.ForMac(DesktopBuildProfile.Stable, library, cache);
        var preview = DesktopStoragePaths.ForMac(DesktopBuildProfile.Preview, library, cache);
        Assert.Equal(library, stable.DataDirectory);
        Assert.Equal(cache, stable.CacheDirectory);
        Assert.NotEqual(stable.DatabasePath, preview.DatabasePath);
        Assert.NotEqual(stable.ModulesDirectory, preview.ModulesDirectory);
        Assert.StartsWith(Path.Combine(library, "Application Support", "io7-cn", "io.quantum.desktop.preview"), preview.DataDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task WriteDatabaseAsync(string path, string value)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE records (value TEXT); INSERT INTO records VALUES ($value);";
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }
}
