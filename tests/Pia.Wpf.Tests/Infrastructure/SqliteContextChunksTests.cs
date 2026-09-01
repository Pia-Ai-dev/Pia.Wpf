using System.IO;
using Microsoft.Data.Sqlite;
using Pia.Infrastructure;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

public class SqliteContextChunksTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SqliteContext _ctx;

    public SqliteContextChunksTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
    }

    [Fact]
    public void GetConnection_CreatesChunksTableAndFtsVirtualTable()
    {
        var connection = _ctx.GetConnection();

        Assert.True(TableExists(connection, "Chunks"));
        Assert.True(TableExists(connection, "ChunksFts"));
    }

    [Fact]
    public void Chunks_InsertAndSelectBackByFilePathAndSlug()
    {
        var connection = _ctx.GetConnection();
        var indexedAt = DateTime.UtcNow.ToString("O");

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO Chunks (FilePath, Heading, Slug, ContentHash, Embedding, IndexedAt)
                VALUES ($filePath, $heading, $slug, $contentHash, NULL, $indexedAt);
                """;
            insert.Parameters.AddWithValue("$filePath", "profile.md");
            insert.Parameters.AddWithValue("$heading", "Preferences");
            insert.Parameters.AddWithValue("$slug", "preferences");
            insert.Parameters.AddWithValue("$contentHash", "abc123");
            insert.Parameters.AddWithValue("$indexedAt", indexedAt);
            insert.ExecuteNonQuery();
        }

        using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT Heading, ContentHash, Embedding, IndexedAt
            FROM Chunks
            WHERE FilePath = $filePath AND Slug = $slug;
            """;
        select.Parameters.AddWithValue("$filePath", "profile.md");
        select.Parameters.AddWithValue("$slug", "preferences");

        using var reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Preferences", reader.GetString(0));
        Assert.Equal("abc123", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal(indexedAt, reader.GetString(3));
        Assert.False(reader.Read());
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE name = $name AND type IN ('table', 'view');";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    public void Dispose()
    {
        _ctx.Dispose();
        TempPath.Remove(_tmpDir);
    }
}
