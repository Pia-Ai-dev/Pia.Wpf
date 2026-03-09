using Microsoft.Data.Sqlite;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class HistoryService : IHistoryService
{
    private readonly SqliteContext _context;

    public HistoryService(SqliteContext context)
    {
        _context = context;
    }

    public async Task AddSessionAsync(OptimizationSession session)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Sessions (Id, OriginalText, OptimizedText, TemplateId, TemplateName,
                                  ProviderId, ProviderName, WasTranscribed, CreatedAt, TokensUsed, ProcessingTimeMs)
            VALUES (@Id, @OriginalText, @OptimizedText, @TemplateId, @TemplateName,
                    @ProviderId, @ProviderName, @WasTranscribed, @CreatedAt, @TokensUsed, @ProcessingTimeMs)
            """;

        command.Parameters.AddWithValue("@Id", session.Id.ToString());
        command.Parameters.AddWithValue("@OriginalText", session.OriginalText);
        command.Parameters.AddWithValue("@OptimizedText", session.OptimizedText);
        command.Parameters.AddWithValue("@TemplateId", session.TemplateId.ToString());
        command.Parameters.AddWithValue("@TemplateName", session.TemplateName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ProviderId", session.ProviderId.ToString());
        command.Parameters.AddWithValue("@ProviderName", session.ProviderName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@WasTranscribed", session.WasTranscribed ? 1 : 0);
        command.Parameters.AddWithValue("@CreatedAt", session.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@TokensUsed", session.TokensUsed);
        command.Parameters.AddWithValue("@ProcessingTimeMs", session.ProcessingTimeMs);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<OptimizationSession>> GetSessionsAsync(int offset = 0, int limit = 50)
    {
        return await SearchSessionsAsync(offset: offset, limit: limit);
    }

    public async Task<IReadOnlyList<OptimizationSession>> SearchSessionsAsync(
        string? searchText = null,
        Guid? templateId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int offset = 0,
        int limit = 50)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();

        var whereClause = BuildWhereClause(command, searchText, templateId, fromDate, toDate);

        command.CommandText = $"""
            SELECT Id, OriginalText, OptimizedText, TemplateId, TemplateName,
                   ProviderId, ProviderName, WasTranscribed, CreatedAt, TokensUsed, ProcessingTimeMs
            FROM Sessions
            {whereClause}
            ORDER BY CreatedAt DESC
            LIMIT @Limit OFFSET @Offset
            """;

        command.Parameters.AddWithValue("@Limit", limit);
        command.Parameters.AddWithValue("@Offset", offset);

        var sessions = new List<OptimizationSession>();
        using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            sessions.Add(MapSession(reader));
        }

        return sessions.AsReadOnly();
    }

    public async Task<OptimizationSession?> GetSessionAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, OriginalText, OptimizedText, TemplateId, TemplateName,
                   ProviderId, ProviderName, WasTranscribed, CreatedAt, TokensUsed, ProcessingTimeMs
            FROM Sessions
            WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return MapSession(reader);
        }

        return null;
    }

    public async Task DeleteSessionAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Sessions WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> GetSessionCountAsync()
    {
        return await GetSessionCountAsync(null, null, null, null);
    }

    public async Task<int> GetSessionCountAsync(
        string? searchText = null,
        Guid? templateId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();

        var whereClause = BuildWhereClause(command, searchText, templateId, fromDate, toDate);

        command.CommandText = $"SELECT COUNT(*) FROM Sessions {whereClause}";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Builds WHERE clause for session queries based on provided filter parameters.
    /// Adds necessary parameters to the command and returns the WHERE clause string.
    /// </summary>
    private static string BuildWhereClause(
        SqliteCommand command,
        string? searchText,
        Guid? templateId,
        DateTime? fromDate,
        DateTime? toDate)
    {
        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            conditions.Add("(OriginalText LIKE @SearchText OR OptimizedText LIKE @SearchText)");
            command.Parameters.AddWithValue("@SearchText", $"%{searchText}%");
        }

        if (templateId.HasValue)
        {
            conditions.Add("TemplateId = @TemplateId");
            command.Parameters.AddWithValue("@TemplateId", templateId.Value.ToString());
        }

        if (fromDate.HasValue)
        {
            conditions.Add("CreatedAt >= @FromDate");
            command.Parameters.AddWithValue("@FromDate", fromDate.Value.ToString("O"));
        }

        if (toDate.HasValue)
        {
            // Include the entire end day by using end-of-day
            var endOfDay = toDate.Value.Date.AddDays(1).AddTicks(-1);
            conditions.Add("CreatedAt <= @ToDate");
            command.Parameters.AddWithValue("@ToDate", endOfDay.ToString("O"));
        }

        return conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";
    }

    private static OptimizationSession MapSession(SqliteDataReader reader)
    {
        return new OptimizationSession
        {
            Id = Guid.Parse(reader.GetString(0)),
            OriginalText = reader.GetString(1),
            OptimizedText = reader.GetString(2),
            TemplateId = Guid.Parse(reader.GetString(3)),
            TemplateName = reader.IsDBNull(4) ? null : reader.GetString(4),
            ProviderId = Guid.Parse(reader.GetString(5)),
            ProviderName = reader.IsDBNull(6) ? null : reader.GetString(6),
            WasTranscribed = reader.GetInt32(7) == 1,
            CreatedAt = DateTime.Parse(reader.GetString(8)),
            TokensUsed = reader.GetInt32(9),
            ProcessingTimeMs = reader.GetInt64(10)
        };
    }
}
