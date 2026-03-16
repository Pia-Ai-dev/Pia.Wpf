using Microsoft.Data.Sqlite;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class ResearchHistoryService : IResearchHistoryService
{
    private readonly SqliteContext _context;

    public event EventHandler? SessionsChanged;

    public ResearchHistoryService(SqliteContext context)
    {
        _context = context;
    }

    private void OnSessionsChanged() => SessionsChanged?.Invoke(this, EventArgs.Empty);

    public async Task AddEntryAsync(ResearchHistoryEntry entry)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ResearchSessions (Id, Query, SynthesizedResult, StepsJson, ProviderId, ProviderName,
                                          Status, StepCount, CreatedAt, CompletedAt)
            VALUES (@Id, @Query, @SynthesizedResult, @StepsJson, @ProviderId, @ProviderName,
                    @Status, @StepCount, @CreatedAt, @CompletedAt)
            """;

        command.Parameters.AddWithValue("@Id", entry.Id.ToString());
        command.Parameters.AddWithValue("@Query", entry.Query);
        command.Parameters.AddWithValue("@SynthesizedResult", entry.SynthesizedResult);
        command.Parameters.AddWithValue("@StepsJson", entry.StepsJson);
        command.Parameters.AddWithValue("@ProviderId", entry.ProviderId.ToString());
        command.Parameters.AddWithValue("@ProviderName", entry.ProviderName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Status", entry.Status);
        command.Parameters.AddWithValue("@StepCount", entry.StepCount);
        command.Parameters.AddWithValue("@CreatedAt", entry.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@CompletedAt", entry.CompletedAt.ToString("O"));

        await command.ExecuteNonQueryAsync();
        OnSessionsChanged();
    }

    public async Task<IReadOnlyList<ResearchHistoryEntry>> SearchEntriesAsync(
        string? searchText = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int offset = 0,
        int limit = 50)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();

        var whereClause = BuildWhereClause(command, searchText, fromDate, toDate);

        command.CommandText = $"""
            SELECT Id, Query, SynthesizedResult, StepsJson, ProviderId, ProviderName,
                   Status, StepCount, CreatedAt, CompletedAt
            FROM ResearchSessions
            {whereClause}
            ORDER BY CreatedAt DESC
            LIMIT @Limit OFFSET @Offset
            """;

        command.Parameters.AddWithValue("@Limit", limit);
        command.Parameters.AddWithValue("@Offset", offset);

        var entries = new List<ResearchHistoryEntry>();
        using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            entries.Add(MapEntry(reader));
        }

        return entries.AsReadOnly();
    }

    public async Task<ResearchHistoryEntry?> GetEntryAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Query, SynthesizedResult, StepsJson, ProviderId, ProviderName,
                   Status, StepCount, CreatedAt, CompletedAt
            FROM ResearchSessions
            WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return MapEntry(reader);
        }

        return null;
    }

    public async Task DeleteEntryAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ResearchSessions WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync();
        OnSessionsChanged();
    }

    public async Task<int> GetEntryCountAsync(
        string? searchText = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();

        var whereClause = BuildWhereClause(command, searchText, fromDate, toDate);

        command.CommandText = $"SELECT COUNT(*) FROM ResearchSessions {whereClause}";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private static string BuildWhereClause(
        SqliteCommand command,
        string? searchText,
        DateTime? fromDate,
        DateTime? toDate)
    {
        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            conditions.Add("(Query LIKE @SearchText OR SynthesizedResult LIKE @SearchText)");
            command.Parameters.AddWithValue("@SearchText", $"%{searchText}%");
        }

        if (fromDate.HasValue)
        {
            conditions.Add("CreatedAt >= @FromDate");
            command.Parameters.AddWithValue("@FromDate", fromDate.Value.ToString("O"));
        }

        if (toDate.HasValue)
        {
            var endOfDay = toDate.Value.Date.AddDays(1).AddTicks(-1);
            conditions.Add("CreatedAt <= @ToDate");
            command.Parameters.AddWithValue("@ToDate", endOfDay.ToString("O"));
        }

        return conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";
    }

    private static ResearchHistoryEntry MapEntry(SqliteDataReader reader)
    {
        return new ResearchHistoryEntry
        {
            Id = Guid.Parse(reader.GetString(0)),
            Query = reader.GetString(1),
            SynthesizedResult = reader.GetString(2),
            StepsJson = reader.GetString(3),
            ProviderId = Guid.Parse(reader.GetString(4)),
            ProviderName = reader.IsDBNull(5) ? null : reader.GetString(5),
            Status = reader.GetString(6),
            StepCount = reader.GetInt32(7),
            CreatedAt = DateTime.Parse(reader.GetString(8)),
            CompletedAt = DateTime.Parse(reader.GetString(9))
        };
    }
}
