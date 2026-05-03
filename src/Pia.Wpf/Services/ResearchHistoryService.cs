using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Search;

namespace Pia.Services;

public class ResearchHistoryService : IResearchHistoryService
{
    private readonly SqliteContext _context;
    private readonly IEmbeddingService _embeddingService;
    private readonly SyncDeleteTrackerService _deleteTracker;
    private readonly ILogger<ResearchHistoryService> _logger;

    public event EventHandler? SessionsChanged;

    public ResearchHistoryService(
        SqliteContext context,
        IEmbeddingService embeddingService,
        SyncDeleteTrackerService deleteTracker,
        ILogger<ResearchHistoryService> logger)
    {
        _context = context;
        _embeddingService = embeddingService;
        _deleteTracker = deleteTracker;
        _logger = logger;
    }

    private void OnSessionsChanged() => SessionsChanged?.Invoke(this, EventArgs.Empty);

    public async Task AddEntryAsync(ResearchHistoryEntry entry)
    {
        // Only embed completed entries — failure-marker text like "Run failed: timeout"
        // would otherwise burn embedding compute and pollute vector search.
        if (entry.Embedding is null
            && !string.IsNullOrWhiteSpace(entry.SynthesizedResult)
            && entry.Status == "Completed")
        {
            try
            {
                if (await _embeddingService.EnsureAvailableAsync())
                {
                    var text = entry.Query + "\n\n" + entry.SynthesizedResult;
                    var floats = await _embeddingService.GenerateEmbeddingAsync(text);
                    entry.Embedding = _embeddingService.FloatsToBytes(floats);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Embedding generation failed for research history entry {EntryId}; saving without embedding.",
                    entry.Id);
            }
        }

        if (entry.UpdatedAt == default)
            entry.UpdatedAt = entry.CreatedAt;

        await InsertAsync(entry);
        OnSessionsChanged();
    }

    private async Task InsertAsync(ResearchHistoryEntry entry)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ResearchSessions (Id, Query, SynthesizedResult, StepsJson, ProviderId, ProviderName,
                                          Status, StepCount, CreatedAt, CompletedAt, UpdatedAt, ScheduledJobId, Embedding)
            VALUES (@Id, @Query, @SynthesizedResult, @StepsJson, @ProviderId, @ProviderName,
                    @Status, @StepCount, @CreatedAt, @CompletedAt, @UpdatedAt, @ScheduledJobId, @Embedding)
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
        command.Parameters.AddWithValue("@UpdatedAt", entry.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("@ScheduledJobId", entry.ScheduledJobId.HasValue ? (object)entry.ScheduledJobId.Value.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("@Embedding", entry.Embedding is null ? DBNull.Value : (object)entry.Embedding);

        await command.ExecuteNonQueryAsync();
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
                   Status, StepCount, CreatedAt, CompletedAt, UpdatedAt, ScheduledJobId, Embedding
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
                   Status, StepCount, CreatedAt, CompletedAt, UpdatedAt, ScheduledJobId, Embedding
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
        _deleteTracker.TrackDeletion("researchSessions", id);
        OnSessionsChanged();
    }

    public async Task<IReadOnlyList<ResearchHistoryEntry>> GetModifiedSinceAsync(DateTime since)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Query, SynthesizedResult, StepsJson, ProviderId, ProviderName,
                   Status, StepCount, CreatedAt, CompletedAt, UpdatedAt, ScheduledJobId, Embedding
            FROM ResearchSessions
            WHERE UpdatedAt >= @Since
            """;
        command.Parameters.AddWithValue("@Since", since.ToString("O"));

        var entries = new List<ResearchHistoryEntry>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            entries.Add(MapEntry(reader));
        return entries.AsReadOnly();
    }

    /// <summary>
    /// Inserts an entry pulled from sync. Skips local embedding generation — the local device
    /// will compute its own embedding lazily on first vector-search use, since embeddings are
    /// machine-specific and not synced.
    /// </summary>
    public async Task UpsertFromSyncAsync(ResearchHistoryEntry entry)
    {
        if (entry.UpdatedAt == default)
            entry.UpdatedAt = entry.CreatedAt;

        var existing = await GetEntryAsync(entry.Id);
        if (existing is null)
        {
            await InsertAsync(entry);
            OnSessionsChanged();
            _logger.LogInformation("Imported research session {Id} from sync", entry.Id);
            return;
        }

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ResearchSessions
            SET Query=@Query, SynthesizedResult=@SynthesizedResult, StepsJson=@StepsJson,
                ProviderId=@ProviderId, ProviderName=@ProviderName, Status=@Status,
                StepCount=@StepCount, CompletedAt=@CompletedAt, UpdatedAt=@UpdatedAt,
                ScheduledJobId=@ScheduledJobId
            WHERE Id=@Id
            """;
        command.Parameters.AddWithValue("@Id", entry.Id.ToString());
        command.Parameters.AddWithValue("@Query", entry.Query);
        command.Parameters.AddWithValue("@SynthesizedResult", entry.SynthesizedResult);
        command.Parameters.AddWithValue("@StepsJson", entry.StepsJson);
        command.Parameters.AddWithValue("@ProviderId", entry.ProviderId.ToString());
        command.Parameters.AddWithValue("@ProviderName", entry.ProviderName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Status", entry.Status);
        command.Parameters.AddWithValue("@StepCount", entry.StepCount);
        command.Parameters.AddWithValue("@CompletedAt", entry.CompletedAt.ToString("O"));
        command.Parameters.AddWithValue("@UpdatedAt", entry.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("@ScheduledJobId", entry.ScheduledJobId.HasValue ? (object)entry.ScheduledJobId.Value.ToString() : DBNull.Value);
        await command.ExecuteNonQueryAsync();
        OnSessionsChanged();
        _logger.LogInformation("Updated research session {Id} from sync", entry.Id);
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

    public async Task UpdateEmbeddingAsync(Guid id, byte[] embedding)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ResearchSessions SET Embedding = @Embedding WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Embedding", embedding);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ResearchHistoryEntry>> VectorSearchAsync(
        float[] queryEmbedding, int topK = 10, float threshold = 0.2f)
    {
        var all = await GetAllWithEmbeddingsAsync();

        var ranked = VectorSearchHelper.RankByCosine(
            all,
            e => e.Embedding is null ? null : _embeddingService.BytesToFloats(e.Embedding),
            queryEmbedding,
            topK,
            threshold).ToList();

        return ranked.AsReadOnly();
    }

    public async Task<IReadOnlyList<ResearchHistoryEntry>> HybridSearchAsync(
        string query, float[]? queryEmbedding = null, int topK = 10)
    {
        var resultDict = new Dictionary<Guid, (ResearchHistoryEntry Entry, float Score)>();

        // Tier 1: text LIKE search on query and result (uses existing SearchEntriesAsync)
        var textHits = await SearchEntriesAsync(searchText: query, fromDate: null, toDate: null, offset: 0, limit: topK * 2);
        foreach (var e in textHits)
            resultDict[e.Id] = (e, 0.6f);

        // Tier 2: vector
        if (queryEmbedding is not null)
        {
            var vectorHits = await VectorSearchAsync(queryEmbedding, topK, threshold: 0.2f);
            foreach (var e in vectorHits)
            {
                if (resultDict.TryGetValue(e.Id, out var existing))
                    resultDict[e.Id] = (e, Math.Max(existing.Score, 0.8f));
                else
                    resultDict[e.Id] = (e, 0.8f);
            }
        }

        return resultDict.Values
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Entry)
            .ToList()
            .AsReadOnly();
    }

    private async Task<IReadOnlyList<ResearchHistoryEntry>> GetAllWithEmbeddingsAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Query, SynthesizedResult, StepsJson, ProviderId, ProviderName,
                   Status, StepCount, CreatedAt, CompletedAt, UpdatedAt, ScheduledJobId, Embedding
            FROM ResearchSessions
            WHERE Embedding IS NOT NULL
            """;
        var entries = new List<ResearchHistoryEntry>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            entries.Add(MapEntry(reader));
        return entries.AsReadOnly();
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
            CompletedAt = DateTime.Parse(reader.GetString(9)),
            UpdatedAt = DateTime.Parse(reader.GetString(10)),
            ScheduledJobId = reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)),
            Embedding = reader.IsDBNull(12) ? null : (byte[])reader[12]
        };
    }
}
