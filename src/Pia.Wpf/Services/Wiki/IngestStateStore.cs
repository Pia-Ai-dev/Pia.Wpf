using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pia.Services.Interfaces;

namespace Pia.Services.Wiki;

/// <summary>What was last ingested for one source: content hash, outcome, and touched pages.</summary>
public sealed record IngestStateEntry(
    string SourceRef,
    string ContentHash,
    IngestOutcome Outcome,
    IReadOnlyList<string> TouchedPages,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Change-detection state for auto-ingest, in history.db. Opens a dedicated connection per
/// operation (constructed from <see cref="Pia.Infrastructure.SqliteContext.ConnectionString"/> —
/// the documented pattern for background-thread writers) so it NEVER touches the shared
/// single-threaded connection the recall indexer owns. SourceRef is COLLATE NOCASE: Windows paths
/// are case-insensitive, so case-variant rename events must hit the same row. Local-only, like the
/// chunk index — a second device re-ingests, which replace-per-source semantics make convergent.
/// </summary>
public sealed class IngestStateStore
{
    private readonly string _connectionString;
    private volatile bool _schemaEnsured;

    public IngestStateStore(string connectionString) => _connectionString = connectionString;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        if (!_schemaEnsured)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS IngestState (
                    SourceRef TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                    ContentHash TEXT NOT NULL,
                    Outcome TEXT NOT NULL,
                    TouchedPages TEXT NOT NULL DEFAULT '[]',
                    UpdatedAt TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
            _schemaEnsured = true;
        }
        return connection;
    }

    public async Task<IngestStateEntry?> GetAsync(string sourceRef)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT SourceRef, ContentHash, Outcome, TouchedPages, UpdatedAt FROM IngestState WHERE SourceRef = @r";
        command.Parameters.AddWithValue("@r", sourceRef);
        using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadEntry(reader) : null;
    }

    public async Task<IReadOnlyList<IngestStateEntry>> ListAsync()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT SourceRef, ContentHash, Outcome, TouchedPages, UpdatedAt FROM IngestState";
        var entries = new List<IngestStateEntry>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) entries.Add(ReadEntry(reader));
        return entries;
    }

    public async Task UpsertAsync(IngestStateEntry entry)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO IngestState (SourceRef, ContentHash, Outcome, TouchedPages, UpdatedAt)
            VALUES (@r, @h, @o, @p, @u)
            ON CONFLICT(SourceRef) DO UPDATE SET
                ContentHash = excluded.ContentHash,
                Outcome = excluded.Outcome,
                TouchedPages = excluded.TouchedPages,
                UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("@r", entry.SourceRef);
        command.Parameters.AddWithValue("@h", entry.ContentHash);
        command.Parameters.AddWithValue("@o", entry.Outcome.ToString());
        command.Parameters.AddWithValue("@p", JsonSerializer.Serialize(entry.TouchedPages));
        command.Parameters.AddWithValue("@u", entry.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string sourceRef)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM IngestState WHERE SourceRef = @r";
        command.Parameters.AddWithValue("@r", sourceRef);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Drop every state row. Used by the one-time synthesis-pipeline migration so the hash
    /// gate no longer no-ops the fresh re-ingest of every source.</summary>
    public async Task ClearAllAsync()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM IngestState";
        await command.ExecuteNonQueryAsync();
    }

    private static IngestStateEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        Enum.TryParse<IngestOutcome>(reader.GetString(2), out var outcome) ? outcome : IngestOutcome.Success,
        JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
        DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture));
}
