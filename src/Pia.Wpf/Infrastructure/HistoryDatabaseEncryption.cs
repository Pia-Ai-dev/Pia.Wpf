using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Pia.Infrastructure;

/// <summary>Keeps history.db encrypted at rest under a random key that DPAPI binds to the Windows user.</summary>
public static class HistoryDatabaseEncryption
{
    private const int SqliteNotADatabase = 26;
    private const int KeyLength = 32;

    // Its own entropy, so a blob lifted from another Pia store cannot stand in for this key.
    private static readonly byte[] Entropy = "Pia.HistoryDb.Key"u8.ToArray();

    private static ReadOnlySpan<byte> PlaintextHeader => "SQLite format 3\0"u8;

    public static string KeyPathFor(string dbPath) => dbPath + ".key";

    /// <summary>The keyed connection string when <paramref name="dbPath"/>'s key file is readable, else the plain one.</summary>
    public static string ConnectionStringFor(string dbPath)
        => TryLoadKey(KeyPathFor(dbPath), null) is { } key ? Keyed(dbPath, key) : Plain(dbPath);

    /// <summary>
    /// Brings <paramref name="dbPath"/> to its encrypted state and returns the connection string to use. Only a
    /// failed migration of a plaintext file falls back to the plain string, and the next launch retries it.
    /// </summary>
    public static string Prepare(string dbPath, ILogger? logger)
    {
        var keyPath = KeyPathFor(dbPath);
        var key = TryLoadKey(keyPath, logger);

        switch (Classify(dbPath))
        {
            case FileState.Missing:
                key ??= TryCreateKey(keyPath, logger);
                return key is null ? Plain(dbPath) : Keyed(dbPath, key);

            case FileState.Plaintext:
                key ??= TryCreateKey(keyPath, logger);
                return key is not null && TryEncrypt(dbPath, key, logger) ? Keyed(dbPath, key) : Plain(dbPath);

            default:
                if (key is not null && Opens(dbPath, key))
                    return Keyed(dbPath, key);

                // A file that cannot be moved stays where it is, and the integrity check reports it.
                if (!TrySetAside(dbPath, keyPath, logger))
                    return key is null ? Plain(dbPath) : Keyed(dbPath, key);

                key ??= TryCreateKey(keyPath, logger);
                return key is null ? Plain(dbPath) : Keyed(dbPath, key);
        }
    }

    private enum FileState { Missing, Plaintext, Opaque }

    private static FileState Classify(string dbPath)
    {
        if (!File.Exists(dbPath))
            return FileState.Missing;

        Span<byte> header = stackalloc byte[16];
        using var stream = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read == 0)
            return FileState.Missing;

        return read == header.Length && header.SequenceEqual(PlaintextHeader) ? FileState.Plaintext : FileState.Opaque;
    }

    private static byte[]? TryLoadKey(string keyPath, ILogger? logger)
    {
        if (!File.Exists(keyPath))
            return null;

        try
        {
            var key = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), Entropy, DataProtectionScope.CurrentUser);
            if (key.Length == KeyLength)
                return key;

            logger?.LogError("History database key has the wrong length ({Length} bytes)", key.Length);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            logger?.LogError(ex, "History database key could not be unprotected");
        }

        return null;
    }

    private static byte[]? TryCreateKey(string keyPath, ILogger? logger)
    {
        var key = RandomNumberGenerator.GetBytes(KeyLength);
        var tempPath = AtomicBinaryWriter.CreateTempPath(keyPath);
        try
        {
            File.WriteAllBytes(tempPath, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
            AtomicBinaryWriter.CommitTempFile(tempPath, keyPath);
            return key;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            AtomicBinaryWriter.DiscardTempFile(tempPath);
            logger?.LogError(ex, "History database key could not be created; the database stays unencrypted this session");
            return null;
        }
    }

    // The x'…' form is a raw key: no KDF, so a fresh connection costs well under a millisecond.
    private static string Password(byte[] key) => $"x'{Convert.ToHexString(key)}'";

    private static string Keyed(string dbPath, byte[] key)
        => new SqliteConnectionStringBuilder { DataSource = dbPath, Password = Password(key) }.ToString();

    private static string Plain(string dbPath) => new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

    private static string Unpooled(string connectionString)
        => new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ToString();

    private static bool Opens(string dbPath, byte[] key)
    {
        try
        {
            using var connection = new SqliteConnection(Unpooled(Keyed(dbPath, key)));
            connection.Open();
            using var probe = connection.CreateCommand();
            probe.CommandText = "SELECT count(*) FROM sqlite_master;";
            probe.ExecuteScalar();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteNotADatabase)
        {
            return false;
        }
        catch (SqliteException)
        {
            // Locked or damaged past page 1: not a key problem, so the integrity check gets to report it.
            return true;
        }
    }

    private static bool TryEncrypt(string dbPath, byte[] key, ILogger? logger)
    {
        var copyPath = dbPath + ".encrypting";
        var backupPath = BackupPathFor(dbPath, DateTime.UtcNow);
        var pendingBackupPath = backupPath + ".tmp";
        try
        {
            DeleteWithSidecars(copyPath);

            // Folds the WAL into the main file, so the copy below is the whole database.
            Execute(Unpooled(Plain(dbPath)), "PRAGMA wal_checkpoint(TRUNCATE);");
            File.Copy(dbPath, copyPath, overwrite: true);

            // The only way back after a rollback to an older build or a lost DPAPI key, so no backup, no conversion.
            WriteBackup(copyPath, Path.GetFileName(dbPath), pendingBackupPath);

            // rekey refuses WAL mode, and on an unencrypted file it encrypts every page.
            Execute(Unpooled(Plain(copyPath)), "PRAGMA journal_mode=DELETE;", $"PRAGMA rekey = \"{Password(key)}\";");
            Execute(Unpooled(Keyed(copyPath, key)), "PRAGMA journal_mode=WAL;");

            if (!QuickCheckPasses(Unpooled(Keyed(copyPath, key))))
                throw new InvalidDataException("The encrypted copy of the history database failed its quick check.");

            // Finalized before the swap: a swap that fails afterwards leaves a valid backup and a plaintext original.
            File.Move(pendingBackupPath, backupPath);

            DeleteSidecars(dbPath);
            AtomicBinaryWriter.CommitTempFile(copyPath, dbPath);
            DeleteWithSidecars(copyPath);

            logger?.LogInformation(
                "History database encrypted at rest; the unencrypted original is kept as Backups\\{Backup} until deleted by hand",
                Path.GetFileName(backupPath));
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger?.LogWarning(ex, "Encrypting the history database failed; it stays unencrypted until the next launch retries");
            TryDeleteWithSidecars(copyPath);
            TryDeleteWithSidecars(pendingBackupPath);
            return false;
        }
    }

    /// <summary>Beside the database, so a test profile's backup stays inside its own temp folder.</summary>
    public static string BackupPathFor(string dbPath, DateTime utc)
        => Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? string.Empty,
            "Backups",
            $"{Path.GetFileNameWithoutExtension(dbPath)}-before-encryption-{utc:yyyyMMddHHmmss}.zip");

    private static void WriteBackup(string sourcePath, string entryName, string zipPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Optimal);
    }

    private static bool TrySetAside(string dbPath, string keyPath, ILogger? logger)
    {
        var asidePath = $"{dbPath}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
        try
        {
            File.Move(dbPath, asidePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogError(ex, "History database could not be opened with its key, nor moved aside");
            return false;
        }

        try
        {
            foreach (var sidecar in new[] { "-wal", "-shm" })
            {
                if (File.Exists(dbPath + sidecar))
                    File.Move(dbPath + sidecar, asidePath + sidecar);
            }

            // Kept beside the copy: if DPAPI recovers later, the pair can still be opened.
            if (File.Exists(keyPath))
                File.Copy(keyPath, KeyPathFor(asidePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Moving the history database's side files aside failed");
        }

        logger?.LogError(
            "History database could not be opened with its key and was set aside as {AsideName}; starting a new one",
            Path.GetFileName(asidePath));
        return true;
    }

    private static void Execute(string connectionString, params string[] statements)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        foreach (var statement in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }

    private static bool QuickCheckPasses(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check(1);";
        return string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (File.Exists(path + suffix))
                File.Delete(path + suffix);
        }
    }

    private static void DeleteWithSidecars(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        DeleteSidecars(path);
    }

    private static void TryDeleteWithSidecars(string path)
    {
        try
        {
            DeleteWithSidecars(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
