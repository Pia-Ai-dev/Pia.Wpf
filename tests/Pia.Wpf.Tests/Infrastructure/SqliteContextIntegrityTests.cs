using System.IO;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Xunit;

namespace Pia.Wpf.Tests.Infrastructure;

/// <summary>
/// T2-13b (hermes #13's second half): the shared history database is checked once, on its first open, and the
/// answer reaches the support log.
/// <para>
/// The fact worth the file is <see cref="ADamagedDatabase_IsDiagnosedEvenWhenTheSchemaPassFails"/> — it pins the
/// ORDERING decision, which is the whole of this item. The check runs BEFORE <c>EnsureSchema</c>, so a damaged
/// file is diagnosed by a read-only pragma instead of by whatever cryptic error the first <c>ALTER</c> or FTS
/// rebuild happens to throw while writing to it.
/// </para>
/// </summary>
public class SqliteContextIntegrityTests : IDisposable
{
    private readonly string _tmpDir;

    public SqliteContextIntegrityTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaIntegrity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    private string NewDbPath(string name = "history.db") => Path.Combine(_tmpDir, name);

    /// <summary>Captures what the check actually said, because the log line IS the shipped surface.</summary>
    private sealed class RecordingLogger : ILogger<SqliteContext>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        public int Integrity(LogLevel level) =>
            Entries.Count(e => e.Level == level && e.Message.Contains("integrity check"));
    }

    [Fact]
    public void AFreshDatabase_ChecksOut()
    {
        var logger = new RecordingLogger();
        using var ctx = new SqliteContext(NewDbPath(), logger);

        ctx.GetConnection();

        Assert.Equal("ok", ctx.IntegrityStatus);
        Assert.Equal(1, logger.Integrity(LogLevel.Information));
        Assert.Equal(0, logger.Integrity(LogLevel.Error));
    }

    /// <summary>
    /// ONCE per process, not once per caller. Ten services share this connection and call
    /// <c>GetConnection()</c> on every operation; a check that ran per call would be a full file scan per query.
    /// </summary>
    [Fact]
    public void TheCheckRunsOnce_HoweverOftenTheConnectionIsAskedFor()
    {
        var logger = new RecordingLogger();
        using var ctx = new SqliteContext(NewDbPath(), logger);

        ctx.GetConnection();
        ctx.GetConnection();
        ctx.GetConnection();

        Assert.Equal(1, logger.Integrity(LogLevel.Information));
    }

    /// <summary>
    /// A logger is optional (sixty-odd test sites construct this type by path alone), and the check must not
    /// depend on one: the status is still recorded.
    /// </summary>
    [Fact]
    public void WithoutALogger_TheCheckStillRuns()
    {
        using var ctx = new SqliteContext(NewDbPath());

        ctx.GetConnection();

        Assert.Equal("ok", ctx.IntegrityStatus);
    }

    /// <summary>
    /// THE ordering fact. A page of a populated table is zeroed, then the file is opened fresh.
    /// <para>
    /// The schema pass that follows the check may well throw on such a file — it reads and writes the very pages
    /// that are gone — and that is precisely why the diagnosis cannot live inside it. The assertion is therefore
    /// on <c>IntegrityStatus</c> after a <c>GetConnection()</c> whose own outcome is allowed to be an exception:
    /// the damaged file is NAMED as damaged either way.
    /// </para>
    /// </summary>
    [Fact]
    public void ADamagedDatabase_IsDiagnosedEvenWhenTheSchemaPassFails()
    {
        var path = NewDbPath("damaged.db");
        int pageSize;

        // Build a real, populated database through the production schema, then close it so nothing is cached.
        using (var seed = new SqliteContext(path))
        {
            var connection = seed.GetConnection();
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO Todos (Id, Title, Priority, Status, CreatedAt, UpdatedAt, SortOrder)
                    SELECT lower(hex(randomblob(16))), 'row ' || value, 1, 0, '2026-01-01', '2026-01-01', value
                    FROM (WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM n WHERE value < 2000)
                          SELECT value FROM n);
                    """;
                insert.ExecuteNonQuery();
            }

            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA page_size;";
            pageSize = Convert.ToInt32(pragma.ExecuteScalar());
        }

        // Disposing the context returns its connection to Microsoft.Data.Sqlite's POOL rather than closing the
        // handle, so without this the side files below are still locked (and the pooled handle would serve the
        // pre-damage pages back on the next open).
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // WAL side files would otherwise re-supply the pages this test is about to destroy.
        foreach (var side in new[] { path + "-wal", path + "-shm" })
            if (File.Exists(side)) File.Delete(side);

        // Zero one page well inside the file, leaving page 1 (the header and sqlite_master root) intact — so the
        // file still OPENS and still has a readable schema, which is what makes the damage the check's job
        // rather than the connection's.
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > pageSize * 6, "the seeded database is too small to damage meaningfully");
        Array.Clear(bytes, pageSize * 4, pageSize);
        File.WriteAllBytes(path, bytes);

        var logger = new RecordingLogger();
        using var ctx = new SqliteContext(path, logger);
        try
        {
            ctx.GetConnection();
        }
        catch
        {
            // The schema pass on a damaged file is allowed to fail; this test is about what was reported first.
        }

        Assert.NotNull(ctx.IntegrityStatus);
        Assert.NotEqual("ok", ctx.IntegrityStatus);
        Assert.Equal(1, logger.Integrity(LogLevel.Error));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }
}
