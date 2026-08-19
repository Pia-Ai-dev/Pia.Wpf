using System.IO;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

/// <summary>The check runs before <c>EnsureSchema</c>, so a damaged file is diagnosed by a read-only pragma.</summary>
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

    /// <summary>A check per <c>GetConnection()</c> call would be a full file scan per query.</summary>
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

    /// <summary>A logger is optional — many callers construct this type by path alone.</summary>
    [Fact]
    public void WithoutALogger_TheCheckStillRuns()
    {
        using var ctx = new SqliteContext(NewDbPath());

        ctx.GetConnection();

        Assert.Equal("ok", ctx.IntegrityStatus);
    }

    /// <summary>The schema pass may itself throw on such a file; the claim is only that the damage was named.</summary>
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

        // Disposing returns the connection to the driver's POOL rather than closing it, so without this the side
        // files stay locked and the pooled handle would serve pre-damage pages back.
        SqlitePool.ClearFor($"Data Source={path}");

        // WAL side files would otherwise re-supply the pages this test is about to destroy.
        foreach (var side in new[] { path + "-wal", path + "-shm" })
            if (File.Exists(side)) File.Delete(side);

        // Page 1 (the header and sqlite_master root) is left intact, so the file still opens with a readable
        // schema — which is what makes the damage the check's job rather than the connection's.
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

    /// <summary>Such a file opens and throws on its first statement, so the check must precede the WAL pragma.</summary>
    [Fact]
    public void AFileThatIsNotADatabaseAtAll_IsStillDiagnosed()
    {
        var path = NewDbPath("garbage.db");
        File.WriteAllBytes(path, Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray());

        var logger = new RecordingLogger();
        using var ctx = new SqliteContext(path, logger);
        try
        {
            ctx.GetConnection();
        }
        catch
        {
            // Expected: the WAL pragma (or the schema pass) cannot proceed on this file. The point is the order.
        }

        Assert.Equal(1, logger.Integrity(LogLevel.Warning)); // "could not run", with the SQLite error attached
        Assert.Null(ctx.IntegrityStatus);                    // no verdict is claimed — none could be reached
        Assert.Equal(0, logger.Integrity(LogLevel.Information));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }
}
