using System.IO;
using Microsoft.Data.Sqlite;
using Pia.Infrastructure;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

/// <summary>
/// Verifies the PRAGMA-detect migration that adds <c>AssistantChats.WorkingDirectory</c> to
/// databases that predate the column: the column is added without data loss, and a pre-existing
/// row survives with a null WorkingDirectory.
/// </summary>
public class AssistantChatsMigrationTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _dbPath;

    public AssistantChatsMigrationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaMigrationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _dbPath = Path.Combine(_tmpDir, "history.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void MigrateSchema_AddsWorkingDirectory_PreservingExistingRow()
    {
        var chatId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("O");

        // Seed an OLD-schema AssistantChats table (no WorkingDirectory column) + a row.
        using (var seed = new SqliteConnection($"Data Source={_dbPath}"))
        {
            seed.Open();
            using (var create = seed.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE AssistantChats (
                        Id              TEXT PRIMARY KEY,
                        SchemaVersion   INTEGER NOT NULL DEFAULT 1,
                        Title           TEXT,
                        CreatedAt       TEXT NOT NULL,
                        UpdatedAt       TEXT NOT NULL,
                        LastAccessedAt  TEXT NOT NULL,
                        WindowMode      TEXT NOT NULL,
                        ProviderId      TEXT,
                        ExtraJson       TEXT
                    );
                    """;
                create.ExecuteNonQuery();
            }
            using (var insert = seed.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO AssistantChats (Id, SchemaVersion, Title, CreatedAt, UpdatedAt, LastAccessedAt, WindowMode)
                    VALUES (@Id, 1, 'pre-migration', @Now, @Now, @Now, 'Assistant')
                    """;
                insert.Parameters.AddWithValue("@Id", chatId);
                insert.Parameters.AddWithValue("@Now", now);
                insert.ExecuteNonQuery();
            }
        }
        SqliteConnection.ClearAllPools();

        // Open via SqliteContext — GetConnection() runs EnsureSchema()/MigrateSchema(), which adds the column.
        using var ctx = new SqliteContext(_dbPath);
        var conn = ctx.GetConnection();

        // The column now exists.
        var hasColumn = false;
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(AssistantChats)";
            using var r = pragma.ExecuteReader();
            while (r.Read())
            {
                if (r.GetString(1) == "WorkingDirectory") { hasColumn = true; break; }
            }
        }
        Assert.True(hasColumn, "WorkingDirectory column should be added by the migration.");

        // The pre-existing row survived, with a null WorkingDirectory.
        using var select = conn.CreateCommand();
        select.CommandText = "SELECT Title, WorkingDirectory FROM AssistantChats WHERE Id = @Id";
        select.Parameters.AddWithValue("@Id", chatId);
        using var reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("pre-migration", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
    }
}
