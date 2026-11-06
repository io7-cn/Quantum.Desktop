using Microsoft.Data.Sqlite;
using Quantum.Storage;

namespace Quantum.Tests;

public sealed class DesktopDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quantum-storage-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Migrate_CreatesIndependentDataRootAndDoesNotReplayOldFiles()
    {
        Write("legacy/Data/user.txt", "original");
        Write("legacy/Settings/preferences.dat", "settings");
        Write("legacy/Cache/stale.txt", "cache");
        Write("current/Modules/plugin/plugin.json", "plugin");

        Migrate();
        Assert.Equal("original", Read("data/Data/user.txt"));
        Assert.Equal("settings", Read("data/Settings/preferences.dat"));
        Assert.Equal("plugin", Read("data/Data/Modules/plugin/plugin.json"));
        Assert.False(Directory.Exists(PathOf("data/Cache")));
        Assert.Equal("original", Read("legacy/Data/user.txt"));

        Write("data/Data/user.txt", "new data");
        Write("current/Modules/other/plugin.json", "later source");
        Migrate();
        Assert.Equal("new data", Read("data/Data/user.txt"));
        Assert.False(File.Exists(PathOf("data/Data/Modules/other/plugin.json")));

        // Simulate removing the installation, then starting a reinstalled app.
        Directory.Delete(PathOf("current"), recursive: true);
        Directory.Delete(PathOf("legacy"), recursive: true);
        Migrate();
        Assert.Equal("new data", Read("data/Data/user.txt"));
        Assert.Equal("plugin", Read("data/Data/Modules/plugin/plugin.json"));
    }

    [Theory]
    [InlineData("Data", "quantum.db")]
    [InlineData("DATA", "QUANTUM.DB")]
    public void Migrate_PreservesCommittedSqliteWalAndLeavesSourceUsable(string dataDirectory, string databaseName)
    {
        var sourcePath = PathOf($"legacy/{dataDirectory}/{databaseName}");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        using var original = new SqliteConnection($"Data Source={sourcePath};Pooling=False");
        original.Open();
        using var command = original.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE entries (value TEXT); INSERT INTO entries VALUES ('saved');";
        command.ExecuteNonQuery();
        Assert.True(File.Exists(sourcePath + "-wal"));

        Migrate();

        using var restored = new SqliteConnection($"Data Source={PathOf($"data/{dataDirectory}/{databaseName}")};Pooling=False");
        restored.Open();
        using var query = restored.CreateCommand();
        query.CommandText = "SELECT value FROM entries";
        Assert.Equal("saved", query.ExecuteScalar());
        command.CommandText = "SELECT value FROM entries";
        Assert.Equal("saved", command.ExecuteScalar());
    }

    [Fact]
    public void Migrate_RejectsExistingDestinationWithoutOverwritingIt()
    {
        Write("data/Data/user.txt", "new");
        Write("legacy/Data/user.txt", "old");
        Assert.Throws<IOException>(Migrate);
        Assert.Equal("new", Read("data/Data/user.txt"));
        Assert.Equal("old", Read("legacy/Data/user.txt"));
    }

    [Fact]
    public void Migrate_ConflictingPluginsLeaveNoPublishedPartialStateAndCanBeRetried()
    {
        Write("legacy/Data/Modules/plugin/plugin.json", "one");
        Write("current/Modules/plugin/plugin.json", "two");
        Assert.Throws<IOException>(Migrate);
        Assert.False(Directory.Exists(PathOf("data")));
        Assert.Empty(Directory.GetDirectories(_root, ".quantum-migration-*"));
        Assert.Equal("one", Read("legacy/Data/Modules/plugin/plugin.json"));

        Write("current/Modules/plugin/plugin.json", "one");
        Migrate();
        Assert.Equal("one", Read("data/Data/Modules/plugin/plugin.json"));
    }

    [Fact]
    public void Migrate_InvalidDatabaseDoesNotCreateEmptyReplacement()
    {
        Write("legacy/Data/quantum.db", "invalid database");
        Assert.Throws<SqliteException>(Migrate);
        Assert.False(Directory.Exists(PathOf("data")));
        Assert.Equal("invalid database", Read("legacy/Data/quantum.db"));
    }

    [Theory]
    [InlineData("Data", "quantum.db-wal")]
    [InlineData("Data", "quantum.db-shm")]
    [InlineData("DATA", "QUANTUM.DB-WAL")]
    [InlineData("DATA", "QUANTUM.DB-SHM")]
    public void Migrate_RejectsOrphanedSqliteFilesWithoutPublishingEmptyData(string dataDirectory, string fileName)
    {
        var source = $"legacy/{dataDirectory}/{fileName}";
        Write(source, "recovery data");
        Assert.Throws<IOException>(Migrate);
        Assert.False(Directory.Exists(PathOf("data")));
        Assert.Equal("recovery data", Read(source));
        Assert.Empty(Directory.GetDirectories(_root, ".quantum-migration-*"));
    }

    [Fact]
    public void Migrate_ImportsInstalledPluginsBeforeAnInstallerReplacesTheirSource()
    {
        Write("legacy/Settings/preferences.dat", "settings");
        Write("current/Modules/bundled/plugin.json", "bundled");
        Write("old-install/current/Modules/personal/plugin.json", "personal");
        DesktopDataMigration.Migrate(PathOf("legacy"), PathOf("data"),
            PathOf("current/Modules"), PathOf("old-install/current/Modules"));
        Directory.Delete(PathOf("old-install"), recursive: true);
        Assert.Equal("personal", Read("data/Data/Modules/personal/plugin.json"));
        Assert.Equal("bundled", Read("data/Data/Modules/bundled/plugin.json"));
        Assert.Equal("settings", Read("data/Settings/preferences.dat"));
    }

    [Fact]
    public void Migrate_FirstInstallCreatesCompletionMarker()
    {
        Migrate();
        Assert.True(File.Exists(PathOf("data/.legacy-storage-v1")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOptionalSourceUnderLinkedAncestorDoesNotBlockMigration(bool modulesOnly)
    {
        Write("source-target/container/keep.txt", "outside the optional source");
        Write("legacy/Data/user.txt", "saved data");
        CreateDirectoryLink(PathOf("linked-parent"), PathOf("source-target"));
        var missing = PathOf("linked-parent/container/optional-source");
        try
        {
            if (modulesOnly)
            {
                Write("data/Modules/personal/data.txt", "installed plugin");
                DesktopDataMigration.MigrateModules(PathOf("data/Modules"), missing);
                Assert.Equal("installed plugin", Read("data/Modules/personal/data.txt"));
                Assert.True(File.Exists(PathOf("data/Modules/.bundled-modules-v1")));
            }
            else
            {
                DesktopDataMigration.Migrate(PathOf("legacy"), PathOf("data"), missing);
                Assert.Equal("saved data", Read("data/Data/user.txt"));
                Assert.True(File.Exists(PathOf("data/.legacy-storage-v1")));
            }

            Assert.False(Directory.Exists(missing));
            Assert.Equal("saved data", Read("legacy/Data/user.txt"));
            Assert.Equal("outside the optional source", Read("source-target/container/keep.txt"));
        }
        finally
        {
            Directory.Delete(PathOf("linked-parent"));
        }
    }

    [Fact]
    public void ExistingOptionalSourceUnderLinkedAncestorStillStopsMigration()
    {
        Write("source-target/container/present-source/plugin/data.txt", "plugin data");
        Write("legacy/Data/user.txt", "saved data");
        CreateDirectoryLink(PathOf("linked-parent"), PathOf("source-target"));
        try
        {
            Assert.Throws<IOException>(() => DesktopDataMigration.Migrate(
                PathOf("legacy"), PathOf("data"), PathOf("linked-parent/container/present-source")));
            Assert.False(Directory.Exists(PathOf("data")));
            Assert.Equal("plugin data", Read("source-target/container/present-source/plugin/data.txt"));
            Assert.Equal("saved data", Read("legacy/Data/user.txt"));
        }
        finally
        {
            Directory.Delete(PathOf("linked-parent"));
        }
    }

    [Fact]
    public void Migrate_PreservesPluginFilesNamedLikeHostDatabaseOrCache()
    {
        Write("current/Modules/plugin/quantum.db", "plugin-owned file");
        Write("current/Modules/plugin/quantum.db-wal", "plugin-owned sidecar");
        Write("current/Modules/plugin/Cache/data.txt", "plugin-owned data");
        Migrate();
        Assert.Equal("plugin-owned file", Read("data/Data/Modules/plugin/quantum.db"));
        Assert.Equal("plugin-owned sidecar", Read("data/Data/Modules/plugin/quantum.db-wal"));
        Assert.Equal("plugin-owned data", Read("data/Data/Modules/plugin/Cache/data.txt"));
    }

    [Fact]
    public void Migrate_RejectsConcurrentMigration()
    {
        Directory.CreateDirectory(_root);
        using var other = new FileStream(PathOf("data.migration.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(Migrate);
        Assert.False(Directory.Exists(PathOf("data")));
    }

    [Fact]
    public void Migrate_RejectsLinkedSourceAndDestinationAncestors()
    {
        // Junction creation needs no developer mode or symlink privilege on Windows.
        Directory.CreateDirectory(PathOf("outside"));
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", PathOf("legacy"), PathOf("outside") },
                CreateNoWindow = true,
                RedirectStandardOutput = true
            })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(PathOf("legacy"), PathOf("outside"));
        }

        try
        {
            Assert.Throws<IOException>(Migrate);
            Assert.Throws<IOException>(() => DesktopDataMigration.Migrate(
                PathOf("missing"), PathOf("legacy/child"), PathOf("current/Modules")));
        }
        finally
        {
            Directory.Delete(PathOf("legacy"));
        }
        Assert.True(Directory.Exists(PathOf("outside")));
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                CreateNoWindow = true,
                RedirectStandardOutput = true
            })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }

    private void Migrate() => DesktopDataMigration.Migrate(PathOf("legacy"), PathOf("data"), PathOf("current/Modules"));

    private string PathOf(string relative) => Path.Combine(_root, relative);

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
