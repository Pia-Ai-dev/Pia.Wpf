using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using Pia.Infrastructure;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

public sealed class HistoryDatabaseEncryptionTests : IDisposable
{
    private const string Marker = "needle-7c1e9f";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PiaHistoryCrypt_" + Guid.NewGuid().ToString("N"));
    private readonly string _db;

    public HistoryDatabaseEncryptionTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "history.db");
    }

    public void Dispose() => TempPath.Remove(_dir);

    private static bool HasPlaintextHeader(string path)
        => File.ReadAllBytes(path).AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8);

    private static bool FileContains(string path, string text)
        => File.Exists(path) && Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(text, StringComparison.Ordinal);

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private void InsertTodo(SqliteContext context, string title)
        => Exec(context.GetConnection(),
            $"INSERT INTO Todos (Id, Title, Priority, Status, CreatedAt, UpdatedAt, SortOrder) " +
            $"VALUES (lower(hex(randomblob(16))), '{title}', 1, 0, '2026-01-01', '2026-01-01', 0)");

    private void Release() => SqlitePool.ClearFor($"Data Source={_db}");

    [Fact]
    public void A_new_database_is_created_encrypted_with_a_protected_key_beside_it()
    {
        using (var context = new SqliteContext(_db))
            InsertTodo(context, Marker);
        Release();

        Assert.True(File.Exists(HistoryDatabaseEncryption.KeyPathFor(_db)));
        Assert.False(HasPlaintextHeader(_db));
        Assert.False(FileContains(_db, Marker));
    }

    [Fact]
    public void The_key_is_reused_so_a_second_context_reads_what_the_first_wrote()
    {
        using (var first = new SqliteContext(_db))
            InsertTodo(first, Marker);
        Release();

        using var second = new SqliteContext(_db);
        Assert.Equal(1, Count(second.GetConnection(), $"SELECT count(*) FROM Todos WHERE Title = '{Marker}'"));
    }

    [Fact]
    public void A_plaintext_database_is_encrypted_in_place_including_rows_still_in_its_wal()
    {
        // A crash image: the main file plus a WAL that still holds the newest rows.
        var source = Path.Combine(_dir, "source.db");
        using (var seed = new SqliteConnection($"Data Source={source};Pooling=False"))
        {
            seed.Open();
            Exec(seed, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
            Exec(seed, "CREATE TABLE Legacy (Body TEXT)");
            Exec(seed, $"INSERT INTO Legacy VALUES ('{Marker}')");
            CopyShared(source, _db);
            CopyShared(source + "-wal", _db + "-wal");
        }
        Assert.True(FileContains(_db + "-wal", Marker));

        using (var context = new SqliteContext(_db))
            Assert.Equal(1, Count(context.GetConnection(), $"SELECT count(*) FROM Legacy WHERE Body = '{Marker}'"));
        Release();

        Assert.False(HasPlaintextHeader(_db));
        Assert.False(FileContains(_db, Marker));
        Assert.False(FileContains(_db + "-wal", Marker));
        Assert.False(File.Exists(_db + ".encrypting"));
    }

    private static void CopyShared(string from, string to)
    {
        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = File.Create(to);
        input.CopyTo(output);
    }

    [Fact]
    public void A_copy_left_by_an_interrupted_migration_is_discarded_and_the_migration_reruns()
    {
        using (var seed = new SqliteConnection($"Data Source={_db};Pooling=False"))
        {
            seed.Open();
            Exec(seed, $"CREATE TABLE Legacy (Body TEXT); INSERT INTO Legacy VALUES ('{Marker}');");
        }
        File.WriteAllBytes(_db + ".encrypting", [1, 2, 3]);

        using (var context = new SqliteContext(_db))
            Assert.Equal(1, Count(context.GetConnection(), "SELECT count(*) FROM Legacy"));
        Release();

        Assert.False(File.Exists(_db + ".encrypting"));
        Assert.False(HasPlaintextHeader(_db));
    }

    [Fact]
    public void An_encrypted_database_whose_key_is_gone_is_set_aside_and_replaced()
    {
        using (var context = new SqliteContext(_db))
            InsertTodo(context, Marker);
        Release();
        File.Delete(HistoryDatabaseEncryption.KeyPathFor(_db));

        using (var context = new SqliteContext(_db))
        {
            Assert.Equal(0, Count(context.GetConnection(), "SELECT count(*) FROM Todos"));
            Assert.Equal("ok", context.IntegrityStatus);
        }
        Release();

        Assert.Single(Directory.GetFiles(_dir, "history.db.unreadable-*"), p => !p.EndsWith(".key"));
    }

    [Fact]
    public void A_key_file_that_will_not_unprotect_sets_the_database_aside_with_a_copy_of_that_key()
    {
        using (var context = new SqliteContext(_db))
            InsertTodo(context, Marker);
        Release();
        File.WriteAllBytes(HistoryDatabaseEncryption.KeyPathFor(_db), [9, 9, 9, 9]);

        using (var context = new SqliteContext(_db))
            Assert.Equal(0, Count(context.GetConnection(), "SELECT count(*) FROM Todos"));
        Release();

        var aside = Assert.Single(Directory.GetFiles(_dir, "history.db.unreadable-*"), p => !p.EndsWith(".key"));
        Assert.True(File.Exists(HistoryDatabaseEncryption.KeyPathFor(aside)));
    }

    [Fact]
    public void ConnectionStringFor_is_keyed_once_a_key_exists_and_plain_before()
    {
        Assert.True(string.IsNullOrEmpty(new SqliteConnectionStringBuilder(HistoryDatabaseEncryption.ConnectionStringFor(_db)).Password));

        using (var context = new SqliteContext(_db))
        {
            context.GetConnection();
            Assert.Equal(context.ConnectionString, HistoryDatabaseEncryption.ConnectionStringFor(_db));
        }
        Release();

        Assert.NotEmpty(new SqliteConnectionStringBuilder(HistoryDatabaseEncryption.ConnectionStringFor(_db)).Password);
    }

    [Fact]
    public void Every_handle_runs_with_secure_delete()
    {
        using var context = new SqliteContext(_db);
        Assert.Equal(1, Count(context.GetConnection(), "PRAGMA secure_delete"));
    }

    [Fact]
    public void A_plain_connection_cannot_read_the_encrypted_file()
    {
        using (var context = new SqliteContext(_db))
            InsertTodo(context, Marker);
        Release();

        using var plain = new SqliteConnection($"Data Source={_db};Pooling=False");
        plain.Open();
        var ex = Assert.Throws<SqliteException>(() => Count(plain, "SELECT count(*) FROM sqlite_master"));
        Assert.Equal(26, ex.SqliteErrorCode);
    }
}
