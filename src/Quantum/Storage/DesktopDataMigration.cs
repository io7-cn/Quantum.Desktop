using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace Quantum.Storage;

/// <summary>Copies legacy state before any preferences, database or plugin host is opened.</summary>
internal static class DesktopDataMigration
{
    internal const string CompletionFile = ".legacy-storage-v1";
    internal const string ModulesCompletionFile = ".bundled-modules-v1";

    public static void MigrateModules(string destinationRoot, params string[] bundledRoots)
    {
        destinationRoot = Path.GetFullPath(destinationRoot);
        RejectLinks(destinationRoot);
        var parent = Path.GetDirectoryName(destinationRoot)!;
        Directory.CreateDirectory(parent);
        using var migrationLock = new FileStream(
            destinationRoot + ".migration.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var backup = destinationRoot + ".migration-backup";
        RejectLinks(backup);
        if (Directory.Exists(backup) && !Directory.Exists(destinationRoot))
        {
            Directory.Move(backup, destinationRoot);
        }

        if (File.Exists(Path.Combine(destinationRoot, ModulesCompletionFile)))
        {
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            return;
        }

        if (Directory.Exists(backup))
        {
            throw new IOException($"Resolve the interrupted plugin migration at '{backup}' before retrying.");
        }

        var staging = Path.Combine(parent, $".quantum-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            CopyDirectory(destinationRoot, staging);
            foreach (var source in bundledRoots.Distinct(StringComparer.Ordinal))
            {
                CopyDirectory(Path.GetFullPath(source), staging);
            }

            File.WriteAllText(Path.Combine(staging, ModulesCompletionFile), "1\n");
            if (Directory.Exists(destinationRoot))
            {
                Directory.Move(destinationRoot, backup);
            }

            try
            {
                Directory.Move(staging, destinationRoot);
            }
            catch
            {
                if (Directory.Exists(backup))
                {
                    Directory.Move(backup, destinationRoot);
                }

                throw;
            }

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    public static void Migrate(string legacyRoot, string destinationRoot, params string[] bundledModules)
    {
        legacyRoot = Path.GetFullPath(legacyRoot);
        destinationRoot = Path.GetFullPath(destinationRoot);
        RejectLinks(destinationRoot);
        var parent = Path.GetDirectoryName(destinationRoot)!;
        Directory.CreateDirectory(parent);
        // A second startup must not observe or publish a partial migration.
        using var migrationLock = new FileStream(
            destinationRoot + ".migration.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(Path.Combine(destinationRoot, CompletionFile)))
        {
            return;
        }

        if (Directory.Exists(destinationRoot) && Directory.EnumerateFileSystemEntries(destinationRoot).Any())
        {
            throw new IOException($"Cannot migrate into nonempty data directory '{destinationRoot}'. Back it up and resolve the conflict first.");
        }

        var stagingRoot = Path.Combine(parent, $".quantum-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            CopyDirectory(legacyRoot, stagingRoot, skipCache: true);
            foreach (var source in bundledModules.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
            {
                CopyDirectory(source, Path.Combine(stagingRoot, "Data", "Modules"));
            }
            File.WriteAllText(Path.Combine(stagingRoot, CompletionFile), "1\n");
            if (Directory.Exists(destinationRoot))
            {
                Directory.Delete(destinationRoot); // Only the empty directory checked above.
            }

            Directory.Move(stagingRoot, destinationRoot);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private static void CopyDirectory(
        string source, string destination, bool skipCache = false, bool snapshotDatabase = false)
    {
        FileAttributes attributes;
        try
        {
            // An absent optional source has no data to traverse. Unlike Directory.Exists,
            // this probe preserves access and IO failures instead of treating them as absence.
            attributes = File.GetAttributes(source);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        RejectLinks(source);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            return;
        }

        if (snapshotDatabase)
        {
            var files = Directory.EnumerateFiles(source).Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!files.Contains("quantum.db") && (files.Contains("quantum.db-wal") || files.Contains("quantum.db-shm")))
            {
                throw new IOException($"The database is missing beside SQLite recovery files in '{source}'. Restore a complete backup before retrying.");
            }
        }

        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            RejectLinks(entry);
            var name = Path.GetFileName(entry);
            // Windows host names are case-insensitive; preserve the original names in the copied tree.
            var hostName = name.ToLowerInvariant();
            var target = Path.Combine(destination, name);
            if (Directory.Exists(entry))
            {
                if (!skipCache || hostName != "cache")
                {
                    CopyDirectory(entry, target, snapshotDatabase: skipCache && hostName == "data");
                }
            }
            else if (!snapshotDatabase || hostName is not ("quantum.db-wal" or "quantum.db-shm"))
            {
                if (File.Exists(target))
                {
                    using var sourceStream = File.OpenRead(entry);
                    using var targetStream = File.OpenRead(target);
                    if (sourceStream.Length != targetStream.Length
                        || !SHA256.HashData(sourceStream).AsSpan().SequenceEqual(SHA256.HashData(targetStream)))
                    {
                        throw new IOException($"Legacy files conflict at '{target}'. Original files were preserved.");
                    }
                }
                else if (snapshotDatabase && hostName == "quantum.db")
                {
                    // SQLite backup includes committed WAL state and takes a consistent snapshot.
                    using var original = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = entry,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false
                    }.ToString());
                    using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = target,
                        Pooling = false
                    }.ToString());
                    original.Open();
                    backup.Open();
                    original.BackupDatabase(backup);
                }
                else
                {
                    File.Copy(entry, target);
                }
            }
        }
    }

    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Data paths cannot follow a symbolic link or junction: '{current}'.");
            }
        }
    }
}
