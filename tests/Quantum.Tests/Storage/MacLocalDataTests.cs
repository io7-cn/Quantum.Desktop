using Quantum.Storage;

namespace Quantum.Tests;

public sealed class MacLocalDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quantum-mac-data-tests", Guid.NewGuid().ToString("N"));
    private string Modules => PathOf("Library/Modules");

    [Fact]
    public void MigrateModules_MergesUserAndBundledPluginsWithoutChangingSources()
    {
        Write("Library/Modules/user/plugin.json", "user");
        Write("bundle/Modules/bundled/plugin.json", "bundled");
        Write("bundle/Resources/Modules/disabled/other/plugin.json", "disabled");
        Migrate();
        Assert.Equal("user", Read("Library/Modules/user/plugin.json"));
        Assert.Equal("bundled", Read("Library/Modules/bundled/plugin.json"));
        Assert.Equal("disabled", Read("Library/Modules/disabled/other/plugin.json"));
        Assert.Equal("bundled", Read("bundle/Modules/bundled/plugin.json"));

        Directory.Delete(PathOf("bundle"), recursive: true);
        Migrate();
        Assert.Equal("user", Read("Library/Modules/user/plugin.json"));
        Assert.Equal("bundled", Read("Library/Modules/bundled/plugin.json"));
    }

    [Fact]
    public void MigrateModules_ConflictsPreserveExistingDataAndRetryAfterResolution()
    {
        Write("Library/Modules/plugin/plugin.json", "current");
        Write("bundle/Modules/plugin/plugin.json", "old");
        Assert.Throws<IOException>(Migrate);
        Assert.Equal("current", Read("Library/Modules/plugin/plugin.json"));
        Assert.Equal("old", Read("bundle/Modules/plugin/plugin.json"));
        Assert.False(File.Exists(Path.Combine(Modules, DesktopDataMigration.ModulesCompletionFile)));
        Assert.Empty(Directory.GetDirectories(PathOf("Library"), ".quantum-migration-*"));

        Write("bundle/Modules/plugin/plugin.json", "current");
        Migrate();
        Assert.True(File.Exists(Path.Combine(Modules, DesktopDataMigration.ModulesCompletionFile)));
    }

    [Fact]
    public void MigrateModules_DoesNotRestorePluginsRemovedAfterMigration()
    {
        Write("bundle/Modules/plugin/plugin.json", "old");
        Migrate();
        Directory.Delete(PathOf("Library/Modules/plugin"), recursive: true);
        Migrate();
        Assert.False(Directory.Exists(PathOf("Library/Modules/plugin")));
    }

    [Fact]
    public void MigrateModules_RecoversInterruptedDirectoryReplacement()
    {
        Write("Library/Modules.migration-backup/user/plugin.json", "saved");
        Write("bundle/Modules/plugin/plugin.json", "new");
        Migrate();
        Assert.Equal("saved", Read("Library/Modules/user/plugin.json"));
        Assert.Equal("new", Read("Library/Modules/plugin/plugin.json"));
        Assert.False(Directory.Exists(Modules + ".migration-backup"));
    }

    [Fact]
    public void MigrateModules_RemovesOldBackupOnlyAfterCompletedPublication()
    {
        Migrate();
        Write("Library/Modules.migration-backup/user/plugin.json", "backup");
        Migrate();
        Assert.False(Directory.Exists(Modules + ".migration-backup"));
        Assert.True(File.Exists(Path.Combine(Modules, DesktopDataMigration.ModulesCompletionFile)));
    }

    [Fact]
    public void MigrateModules_RejectsConcurrentMigration()
    {
        Directory.CreateDirectory(PathOf("Library"));
        using var migrationLock = new FileStream(Modules + ".migration.lock", FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(Migrate);
    }

    [Fact]
    public void Clear_PreservesSharedLibraryCachesAndExternalPluginDirectories()
    {
        foreach (var file in new[]
        {
            "quantum.db", "quantum.db-wal", "quantum.db-shm", "Modules/plugin/plugin.json",
            "MobileBridgeJournal/entry.json", "Logs/quantum-test.log", "Caches/PluginShadow/file",
            "Caches/io.quantum.desktop/update.json"
        })
        {
            Write("Library/" + file, "owned");
        }

        Write("Library/Logs/another-app.log", "keep");
        Write("Library/Caches/another-app/file", "keep");
        Write("Library/Preferences/another-app.plist", "keep");
        Write("external/plugin/plugin.json", "keep");
        MacLocalData.Clear(PathOf("Library"), PathOf("Library/Caches"));

        Assert.True(Directory.Exists(PathOf("Library")));
        Assert.True(Directory.Exists(PathOf("Library/Caches")));
        Assert.Equal("keep", Read("Library/Logs/another-app.log"));
        Assert.Equal("keep", Read("Library/Caches/another-app/file"));
        Assert.Equal("keep", Read("Library/Preferences/another-app.plist"));
        Assert.Equal("keep", Read("external/plugin/plugin.json"));
        Assert.False(File.Exists(PathOf("Library/quantum.db")));
        Assert.False(File.Exists(PathOf("Library/quantum.db-wal")));
        Assert.False(File.Exists(PathOf("Library/quantum.db-shm")));
        Assert.False(Directory.Exists(Modules));
        Assert.False(Directory.Exists(PathOf("Library/MobileBridgeJournal")));
        Assert.False(Directory.Exists(PathOf("Library/Caches/PluginShadow")));
        Assert.False(Directory.Exists(PathOf("Library/Caches/io.quantum.desktop")));
        Assert.False(File.Exists(PathOf("Library/Logs/quantum-test.log")));
    }

    [Fact]
    public void Clear_RejectsLinkedDataBeforeDeletingAnyFiles()
    {
        Directory.CreateDirectory(Modules);
        Write("Library/quantum.db", "saved");
        Write("external/file", "keep");
        var link = Path.Combine(Modules, "linked");
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, PathOf("external") },
                CreateNoWindow = true,
                RedirectStandardOutput = true
            })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, PathOf("external"));
        }

        try
        {
            Assert.Throws<IOException>(Migrate);
            Assert.Throws<IOException>(() => MacLocalData.Clear(PathOf("Library"), PathOf("Library/Caches")));
            Assert.Equal("saved", Read("Library/quantum.db"));
            Assert.Equal("keep", Read("external/file"));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    private void Migrate() => DesktopDataMigration.MigrateModules(
        Modules, PathOf("bundle/Modules"), PathOf("bundle/Resources/Modules"));

    private string PathOf(string relative) => Path.GetFullPath(Path.Combine(_root, relative));
    private string Read(string relative) => File.ReadAllText(PathOf(relative));

    private void Write(string relative, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf(relative))!);
        File.WriteAllText(PathOf(relative), value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
