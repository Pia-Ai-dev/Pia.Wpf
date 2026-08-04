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
            // T2-14 added five, and each one is still metadata under 03 §3 — stated, because a diff that just
            // widened this list would read as a weakening of the privacy contract:
            //   ToolCallId  — a BOUNDED provider-generated correlation token, never derived from user content.
            //                 It is metadata BECAUSE AgentTimelineScope.SanitizeCallId enforces a
            //                 tool-identifier charset and a 128-char cap on every arm, which is what keeps an
            //                 argument, a path or a JSON blob out of the column; an unbounded raw CallId would
            //                 NOT belong here.
            //   Round       — an int: which provider tool-loop round dispatched the call.
            //   StepOrdinal — an int: Seq's per-step sibling, allocated in memory like Seq.
            //   RequestedAt/DecidedAt — instants, exactly as CreatedAt already is.
            // Still no ExtraJson, no Path, no ArgsHash, and no column that can hold an argument or a result.
            "ToolCallId", "Round", "StepOrdinal", "RequestedAt", "DecidedAt",
        ];

        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "PRAGMA table_info(AgentTimelineEvents)";
        var actual = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            actual.Add(reader.GetString(1));

        Assert.Equal(expected.OrderBy(c => c, StringComparer.Ordinal), actual.OrderBy(c => c, StringComparer.Ordinal));
    }

    /// <summary>
    /// THE EXISTING-USER FACT for T2-14: a profile created before the five correlation columns existed must
    /// open, gain them, and keep every row it already had.
    /// <para>
    /// This is the half a CREATE-TABLE-only change passes and a MigrateSchema-only change also passes: the
    /// column-list test above covers the fresh-install path (the CREATE TABLE literal), and only this one
    /// covers the ALTER path. Getting one of the two right is the classic half-fix on this table.
    /// </para>
    /// <para>
    /// The pre-T2-14 shape is reproduced by DROPping the five columns rather than by pasting an old CREATE
    /// TABLE: pasting one would silently stop being the real old shape the moment any OTHER column changed,
    /// while a DROP is defined against whatever this build actually creates.
    /// </para>
    /// </summary>
    [Fact]
    public void EnsureSchema_AddsTheCorrelationColumns_ToAPreT214Database()
    {
        var legacyRunId = Guid.NewGuid();
        var legacyRowId = Guid.NewGuid();
        var legacyStepId = Guid.NewGuid();
        var legacyCreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        // ---- arrange: rewind this database to the pre-T2-14 shape and put a row in it ----
        {
            // NOT a `using`: this is the context's OWN shared connection, and the context closes it in Dispose.
            var conn = _ctx.GetConnection();
            foreach (var col in new[] { "ToolCallId", "Round", "StepOrdinal", "RequestedAt", "DecidedAt" })
            {
                using var drop = conn.CreateCommand();
                drop.CommandText = $"ALTER TABLE AgentTimelineEvents DROP COLUMN {col}";
                drop.ExecuteNonQuery();
            }

            // No AgentRuns parent, so the FK would reject this INSERT — switched off for the fixture only.
            // The subject here is the SCHEMA migration, not referential integrity (which T-FK covers).
            using (var off = conn.CreateCommand())
            {
                off.CommandText = "PRAGMA foreign_keys=OFF;";
                off.ExecuteNonQuery();
            }

            using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO AgentTimelineEvents
                    (Id, SchemaVersion, RunId, StepId, Seq, Kind, Surface, Decision, Outcome,
                     ToolName, ToolClass, PluginId, ArgsChars, ResultChars, DurationMs, CreatedAt)
                VALUES (@Id, 1, @RunId, @StepId, 7, 1, 2, 3, 1, 'write_file', 2, NULL, 42, 99, 5, @CreatedAt);
                """;
            insert.Parameters.AddWithValue("@Id", legacyRowId.ToString());
            insert.Parameters.AddWithValue("@RunId", legacyRunId.ToString());
            insert.Parameters.AddWithValue("@StepId", legacyStepId.ToString());
            insert.Parameters.AddWithValue("@CreatedAt", legacyCreatedAt.ToString("O"));
            insert.ExecuteNonQuery();
        }

        _ctx.Dispose();

        // ---- act: the next launch. EnsureSchema + MigrateSchema run against the existing file ----
        using var reopened = new SqliteContext(_dbPath);
        var conn2 = reopened.GetConnection();

        // ---- assert 1: the five columns are there. CREATE TABLE IF NOT EXISTS is a no-op on an existing
        // table, so the ONLY thing that can have added them is MigrateSchema's ALTER pass.
        var columns = new List<string>();
        using (var pragma = conn2.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(AgentTimelineEvents)";
            using var r = pragma.ExecuteReader();
            while (r.Read()) columns.Add(r.GetString(1));
        }
        Assert.Contains("ToolCallId", columns);
        Assert.Contains("Round", columns);
        Assert.Contains("StepOrdinal", columns);
        Assert.Contains("RequestedAt", columns);
        Assert.Contains("DecidedAt", columns);

        // ---- assert 2: the legacy row survived with EVERY value intact, and reads NULL in the new columns.
        // "Not recorded" is what NULL means on a v1 row, and SchemaVersion staying 1 is what says so.
        using var read = conn2.CreateCommand();
        read.CommandText = """
            SELECT SchemaVersion, StepId, Seq, ToolName, ArgsChars, ResultChars, DurationMs, CreatedAt,
                   ToolCallId, Round, StepOrdinal, RequestedAt, DecidedAt
            FROM AgentTimelineEvents WHERE Id = @Id;
            """;
        read.Parameters.AddWithValue("@Id", legacyRowId.ToString());
        using var row = read.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal(1, row.GetInt32(0));                          // still a v1 row, deliberately
        Assert.Equal(legacyStepId.ToString(), row.GetString(1));
        Assert.Equal(7L, row.GetInt64(2));
        Assert.Equal("write_file", row.GetString(3));
        Assert.Equal(42, row.GetInt32(4));
        Assert.Equal(99, row.GetInt32(5));
        Assert.Equal(5L, row.GetInt64(6));
        Assert.Equal(legacyCreatedAt.ToString("O"), row.GetString(7));
        Assert.True(row.IsDBNull(8));
        Assert.True(row.IsDBNull(9));
        Assert.True(row.IsDBNull(10));
        Assert.True(row.IsDBNull(11));
        Assert.True(row.IsDBNull(12));

        // ---- assert 3: idempotent. A THIRD launch must not attempt the ALTERs again (SQLite errors on a
        // duplicate column name, and MigrateSchema has no try/catch — an unguarded ALTER would take the whole
        // app's startup down on every launch after the first).
        reopened.Dispose();
        using var third = new SqliteContext(_dbPath);
        Assert.NotNull(third.GetConnection());
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
