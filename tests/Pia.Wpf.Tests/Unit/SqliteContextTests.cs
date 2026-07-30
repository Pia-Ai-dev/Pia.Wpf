using System.IO;
using Pia.Infrastructure;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

/// <summary>
/// Pins the shared connection's durability pragmas (Batch 10 DB1). Moving the chat store onto its own
/// connection converts an intra-connection <c>InvalidOperationException</c> into a cross-connection
/// SQLITE_BUSY, and the shared connection is the side with no error handling at all — so WAL (persistent,
/// per FILE) and a busy timeout (per CONNECTION) are part of that change, not an optimisation.
/// <para>
/// net10.0-windows cannot execute on macOS — these tests are written, not run; execution is deferred to
/// Windows/CI.
/// </para>
/// </summary>
public class SqliteContextTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _dbPath;
    private readonly SqliteContext _ctx;

    public SqliteContextTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _dbPath = Path.Combine(_tmpDir, "history.db");
        _ctx = new SqliteContext(_dbPath);
    }

    [Fact]
    public void GetConnection_FirstOpen_EnablesWal()
    {
        var conn = _ctx.GetConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)cmd.ExecuteScalar();

        Assert.Equal("wal", mode?.ToLowerInvariant());
    }

    [Fact]
    public void GetConnection_FirstOpen_SetsBusyTimeout()
    {
        // Per-connection, so it says nothing about the dedicated handles — each of those sets its own.
        var conn = _ctx.GetConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout;";
        var timeout = Convert.ToInt32(cmd.ExecuteScalar());

        Assert.Equal(3000, timeout);
    }

    [Fact]
    public void GetConnection_WalSurvivesOnASecondConnectionToTheSameFile()
    {
        // journal_mode is persisted in the file header, which is why setting it here also covers
        // AgentRunService / FlowPersistenceStore / AssistantChatService's dedicated connections.
        _ctx.GetConnection();

        using var second = new Microsoft.Data.Sqlite.SqliteConnection(_ctx.ConnectionString);
        second.Open();
        using var cmd = second.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)cmd.ExecuteScalar();

        Assert.Equal("wal", mode?.ToLowerInvariant());
    }

    [Fact]
    public void EnsureSchema_CreatesAgentTimelineEvents_Idempotently()
    {
        // The table lives inside EnsureSchema's CREATE TABLE IF NOT EXISTS command string — which runs on
        // EVERY open — so an existing database gets it on next launch with no MigrateSchema entry, and a
        // second open over the same file is a no-op that must not throw.
        Assert.True(TableExists(_ctx.GetConnection(), "AgentTimelineEvents"));

        using var reopened = new SqliteContext(_dbPath);
        Assert.True(TableExists(reopened.GetConnection(), "AgentTimelineEvents"));
    }

    [Fact]
    public void EnsureSchema_AddsAgentTimelineEvents_ToAPreBatchDatabase()
    {
        // The UPGRADE direction, which the reopen fact above does not cover: a database written before this
        // batch has no AgentTimelineEvents table. Simulated by dropping it, which leaves exactly that shape.
        var conn = _ctx.GetConnection();
        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP TABLE AgentTimelineEvents";
            drop.ExecuteNonQuery();
        }

        Assert.False(TableExists(conn, "AgentTimelineEvents"));

        // Opening it with this build creates the table — no MigrateSchema entry needed, because the DDL lives
        // in EnsureSchema's command string, which runs on every open.
        using var upgraded = new SqliteContext(_dbPath);
        var upgradedConn = upgraded.GetConnection();
        Assert.True(TableExists(upgradedConn, "AgentTimelineEvents"));
        Assert.True(IndexExists(upgradedConn, "IX_AgentTimelineEvents_RunId"));
        Assert.True(IndexExists(upgradedConn, "IX_AgentTimelineEvents_CreatedAt"));

        // …and a third open over the now-current schema is a no-op that must not throw.
        using var again = new SqliteContext(_dbPath);
        Assert.True(TableExists(again.GetConnection(), "AgentTimelineEvents"));
    }

    [Fact]
    public void AgentTimelineEvents_HasExactlyTheMetadataColumns()
    {
        // The audit table's privacy contract as a schema assertion: adding ANY column — an ExtraJson, a Path,
        // an ArgsHash — fails here rather than passing review.
        string[] expected =
        [
            "Id", "SchemaVersion", "RunId", "StepId", "Seq", "Kind", "Surface", "Decision", "Outcome",
            "ToolName", "ToolClass", "PluginId", "ArgsChars", "ResultChars", "DurationMs", "CreatedAt",
        ];

        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "PRAGMA table_info(AgentTimelineEvents)";
        var actual = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            actual.Add(reader.GetString(1));

        Assert.Equal(expected.OrderBy(c => c, StringComparer.Ordinal), actual.OrderBy(c => c, StringComparer.Ordinal));
    }

    private static bool TableExists(Microsoft.Data.Sqlite.SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", table);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private static bool IndexExists(Microsoft.Data.Sqlite.SqliteConnection conn, string index)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@n";
        cmd.Parameters.AddWithValue("@n", index);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    public void Dispose()
    {
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }
}
