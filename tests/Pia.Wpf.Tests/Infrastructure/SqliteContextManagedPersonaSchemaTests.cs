using System.IO;
using Microsoft.Data.Sqlite;
using Pia.Infrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

/// <summary>
/// Schema coverage for the <c>ManagedPersonas</c> table: it exists on a fresh profile, an existing profile
/// gains it on startup without disturbing <c>Personas</c>, and — the invariant that matters — its column
/// names are identical to <c>Personas</c>. That identity is what lets <c>PersonaService</c> reuse one
/// reader/writer pair across both tables, so a column added to one and not the other must fail here.
/// <para>
/// net10.0-windows cannot execute on macOS — these tests are written, not run; execution is deferred to
/// Windows/CI.
/// </para>
/// </summary>
public class SqliteContextManagedPersonaSchemaTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _dbPath;
    private readonly SqliteContext _ctx;

    public SqliteContextManagedPersonaSchemaTests()
    {
        // An explicit temp path, not the parameterless ctor: that one opens the developer's real
        // %LOCALAPPDATA%\Pia\history.db, which would make "fresh profile" untestable.
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaManagedPersonaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _dbPath = Path.Combine(_tmpDir, "history.db");
        _ctx = new SqliteContext(_dbPath);
    }

    [Fact]
    public void FreshProfile_CreatesBothPersonaTables()
    {
        var conn = _ctx.GetConnection();

        Assert.True(TableExists(conn, "Personas"));
        Assert.True(TableExists(conn, "ManagedPersonas"));
    }

    [Fact]
    public void ManagedPersonas_HasExactlyTheSameColumnNamesAsPersonas()
    {
        var conn = _ctx.GetConnection();

        var personas = ColumnNames(conn, "Personas");
        var managed = ColumnNames(conn, "ManagedPersonas");

        Assert.NotEmpty(personas);
        // Set equality, spelled as a sorted comparison so a failure names the offending column.
        Assert.Equal(
            personas.OrderBy(c => c, StringComparer.Ordinal),
            managed.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void ExistingProfile_GainsManagedPersonas_WithoutTouchingPersonas()
    {
        // The UPGRADE direction: a database written before this slice has Personas but no ManagedPersonas.
        // Dropping the table leaves exactly that shape (same simulation as SqliteContextTests uses for
        // AgentTimelineEvents).
        var conn = _ctx.GetConnection();
        using (var seed = conn.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO Personas (Id, Name, SystemPrompt, CreatedAt, UpdatedAt)
                VALUES ('11111111-1111-1111-1111-111111111111', 'PRE_EXISTING', 'prompt', '', '');
                DROP TABLE ManagedPersonas;
                """;
            seed.ExecuteNonQuery();
        }

        Assert.False(TableExists(conn, "ManagedPersonas"));

        using var upgraded = new SqliteContext(_dbPath);
        var upgradedConn = upgraded.GetConnection();

        Assert.True(TableExists(upgradedConn, "ManagedPersonas"));
        Assert.Equal(0L, RowCount(upgradedConn, "ManagedPersonas"));

        // The user's own personas are untouched: the two stores are independent by construction, which is
        // the whole point of managed rows not living in the push source.
        using var select = upgradedConn.CreateCommand();
        select.CommandText = "SELECT Name FROM Personas WHERE Id = '11111111-1111-1111-1111-111111111111';";
        Assert.Equal("PRE_EXISTING", select.ExecuteScalar() as string);
    }

    [Fact]
    public void SchemaCreation_IsIdempotentOverTheSameDatabase()
    {
        // Every statement involved is CREATE TABLE IF NOT EXISTS or PRAGMA-guarded, so a second and third
        // run over an already-current schema must be no-ops that do not throw.
        Assert.True(TableExists(_ctx.GetConnection(), "ManagedPersonas"));

        using var reopened = new SqliteContext(_dbPath);
        Assert.True(TableExists(reopened.GetConnection(), "ManagedPersonas"));

        using var again = new SqliteContext(_dbPath);
        Assert.True(TableExists(again.GetConnection(), "ManagedPersonas"));
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", table);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private static List<string> ColumnNames(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        // PRAGMA takes no bound parameters; the table names passed here are literals from this test.
        cmd.CommandText = $"PRAGMA table_info({table})";
        var names = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names;
    }

    private static long RowCount(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }
}
