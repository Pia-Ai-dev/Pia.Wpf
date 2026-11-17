using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Services.Interfaces;

namespace Pia.Services;

public sealed class RunMessageRecordStore : IRunMessageRecordStore, IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<RunMessageRecordStore> _logger;
    private readonly object _gate = new();
    private SqliteConnection? _connection;
    private bool _disposed;

    public RunMessageRecordStore(SqliteContext context, ILogger<RunMessageRecordStore> logger)
    {
        _connectionString = context.ConnectionString;
        _logger = logger;
        // Forces EnsureSchema, which owns the table, before this connection opens.
        context.GetConnection();
    }

    public event EventHandler<RunMessageRecord>? Recorded;

    private SqliteConnection Connection()
    {
        if (_connection is null)
        {
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();
            using var pragma = _connection.CreateCommand();
            pragma.CommandText = SqliteContext.ConnectionPragmas;
            pragma.ExecuteNonQuery();
        }
        else if (_connection.State != System.Data.ConnectionState.Open)
        {
            _connection.Open();
        }

        return _connection;
    }

    public Task RecordAsync(RunMessageRecord record, CancellationToken ct = default)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return Task.CompletedTask;

                using var insert = Connection().CreateCommand();
                insert.CommandText = """
                    INSERT OR REPLACE INTO AgentRunMessages (MessageId, RunId, StepId, StepOrdinal, CreatedAt)
                    VALUES (@MessageId, @RunId, @StepId, @StepOrdinal, @CreatedAt)
                    """;
                insert.Parameters.AddWithValue("@MessageId", record.MessageId.ToString());
                insert.Parameters.AddWithValue("@RunId", record.RunId.ToString());
                insert.Parameters.AddWithValue("@StepId", (object?)record.StepId?.ToString() ?? DBNull.Value);
                insert.Parameters.AddWithValue("@StepOrdinal", (object?)record.StepOrdinal ?? DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedAt", record.CreatedAt.ToUniversalTime().ToString("O"));
                insert.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record message {MessageId} of run {RunId}", record.MessageId, record.RunId);
            return Task.CompletedTask;
        }

        try
        {
            Recorded?.Invoke(this, record);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A run-message listener failed for run {RunId}", record.RunId);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RunMessageRecord>> GetByChatAsync(Guid chatId, CancellationToken ct = default)
    {
        var records = new List<RunMessageRecord>();
        try
        {
            lock (_gate)
            {
                if (_disposed) return Task.FromResult<IReadOnlyList<RunMessageRecord>>(records);

                using var read = Connection().CreateCommand();
                read.CommandText = """
                    SELECT m.MessageId, m.RunId, m.StepId, m.StepOrdinal, m.CreatedAt
                    FROM AgentRunMessages m JOIN AgentRuns r ON r.Id = m.RunId
                    WHERE r.ChatId = @ChatId
                    ORDER BY m.CreatedAt
                    """;
                read.Parameters.AddWithValue("@ChatId", chatId.ToString());
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(new RunMessageRecord(
                        Guid.Parse(reader.GetString(0)),
                        Guid.Parse(reader.GetString(1)),
                        reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                        reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the run messages of chat {ChatId}", chatId);
        }

        return Task.FromResult<IReadOnlyList<RunMessageRecord>>(records);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }
    }
}
