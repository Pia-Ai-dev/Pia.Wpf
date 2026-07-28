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
    private readonly SqliteContext _ctx;

    public SqliteContextTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
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

    public void Dispose()
    {
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }
}
