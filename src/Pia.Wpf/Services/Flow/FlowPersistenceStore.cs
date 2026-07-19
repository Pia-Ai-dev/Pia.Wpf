using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Models.Flow;

namespace Pia.Services.Flow;

/// <summary>
/// SQLite-backed durable store for Flow. Uses its own dedicated connection (not the shared
/// <see cref="SqliteContext"/> connection) guarded by a lock, because Flow items are published from
/// background threads (pollers, notifiers) and the shared connection has UI-initiated thread affinity.
/// Item content (Title/Body) is sensitive (chat/todo/reminder text); only ids and counts are logged at default level.
/// </summary>
public sealed class FlowPersistenceStore : IFlowPersistenceStore, IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<FlowPersistenceStore> _logger;
    private readonly object _gate = new();
    private SqliteConnection? _connection;
    private bool _disposed;

    public FlowPersistenceStore(SqliteContext context, ILogger<FlowPersistenceStore> logger)
    {
        _connectionString = context.ConnectionString;
        _logger = logger;
    }

    private SqliteConnection Connection()
    {
        if (_connection is null)
        {
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();

            using var pragma = _connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout=3000;";
            pragma.ExecuteNonQuery();

            EnsureSchema(_connection);
        }
        else if (_connection.State != System.Data.ConnectionState.Open)
        {
            _connection.Open();
        }

        return _connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS FlowItems (
                Id TEXT PRIMARY KEY,
                CreatedAt TEXT NOT NULL,
                Severity INTEGER NOT NULL,
                Source INTEGER NOT NULL,
                Title TEXT NOT NULL,
                Body TEXT NOT NULL DEFAULT '',
                DedupKey TEXT,
                IsRead INTEGER NOT NULL DEFAULT 0,
                ActionKind INTEGER,
                ActionEntityId TEXT,
                ActionLabel TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_FlowItems_DedupKey ON FlowItems(DedupKey);
            CREATE INDEX IF NOT EXISTS IX_FlowItems_CreatedAt ON FlowItems(CreatedAt);
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<FlowItem> ReadAll()
    {
        lock (_gate)
        {
            if (_disposed)
                return Array.Empty<FlowItem>();

            var items = new List<FlowItem>();
            using var command = Connection().CreateCommand();
            command.CommandText = """
                SELECT Id, CreatedAt, Severity, Source, Title, Body, DedupKey, IsRead, ActionKind, ActionEntityId, ActionLabel
                FROM FlowItems
                ORDER BY CreatedAt ASC
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
                items.Add(MapItem(reader));

            _logger.LogDebug("Flow persistence loaded {Count} durable item(s)", items.Count);
            return items;
        }
    }

    public void Upsert(FlowItem item)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            using var command = Connection().CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO FlowItems
                    (Id, CreatedAt, Severity, Source, Title, Body, DedupKey, IsRead, ActionKind, ActionEntityId, ActionLabel)
                VALUES
                    (@Id, @CreatedAt, @Severity, @Source, @Title, @Body, @DedupKey, @IsRead, @ActionKind, @ActionEntityId, @ActionLabel)
                """;

            command.Parameters.AddWithValue("@Id", item.Id.ToString());
            command.Parameters.AddWithValue("@CreatedAt", item.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("@Severity", (int)item.Severity);
            command.Parameters.AddWithValue("@Source", (int)item.Source);
            command.Parameters.AddWithValue("@Title", item.Title);
            command.Parameters.AddWithValue("@Body", item.Body);
            command.Parameters.AddWithValue("@DedupKey", item.DedupKey is not null ? (object)item.DedupKey : DBNull.Value);
            command.Parameters.AddWithValue("@IsRead", item.IsRead ? 1 : 0);

            if (item.Action is { IsReDerivable: true } action && action.EntityId is { } entityId)
            {
                command.Parameters.AddWithValue("@ActionKind", (int)action.Kind);
                command.Parameters.AddWithValue("@ActionEntityId", entityId.ToString());
                command.Parameters.AddWithValue("@ActionLabel", action.Label);
            }
            else
            {
                command.Parameters.AddWithValue("@ActionKind", DBNull.Value);
                command.Parameters.AddWithValue("@ActionEntityId", DBNull.Value);
                command.Parameters.AddWithValue("@ActionLabel", DBNull.Value);
            }

            command.ExecuteNonQuery();
            _logger.LogDebug("Flow persistence upserted item {Id} (source {Source})", item.Id, item.Source);
        }
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            using var command = Connection().CreateCommand();
            command.CommandText = "DELETE FROM FlowItems WHERE Id = @Id";
            command.Parameters.AddWithValue("@Id", id.ToString());
            command.ExecuteNonQuery();
            _logger.LogDebug("Flow persistence deleted item {Id}", id);
        }
    }

    public void DeleteAll()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            using var command = Connection().CreateCommand();
            command.CommandText = "DELETE FROM FlowItems";
            command.ExecuteNonQuery();
            _logger.LogDebug("Flow persistence cleared all items");
        }
    }

    private static FlowItem MapItem(SqliteDataReader reader)
    {
        var label = reader.IsDBNull(10) ? string.Empty : reader.GetString(10);
        FlowAction? action = reader.IsDBNull(8) || reader.IsDBNull(9)
            ? null
            : ReconstructAction((FlowActionKind)reader.GetInt32(8), Guid.Parse(reader.GetString(9)), label);

        return new FlowItem
        {
            Id = Guid.Parse(reader.GetString(0)),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(1)),
            Severity = (FlowSeverity)reader.GetInt32(2),
            Source = (FlowSource)reader.GetInt32(3),
            Title = reader.GetString(4),
            Body = reader.GetString(5),
            DedupKey = reader.IsDBNull(6) ? null : reader.GetString(6),
            IsRead = reader.GetInt32(7) == 1,
            Lifetime = FlowLifetime.Persistent,
            Durable = true,
            Action = action,
        };
    }

    private static FlowAction? ReconstructAction(FlowActionKind kind, Guid entityId, string label) => kind switch
    {
        FlowActionKind.OpenChat => new OpenChatAction(entityId, label),
        FlowActionKind.OpenBriefing => null, // Legacy research-history link; research view removed.
        FlowActionKind.OpenTodo => new OpenTodoAction(entityId, label),
        FlowActionKind.ReminderSnooze => new ReminderSnoozeAction(entityId, label),
        FlowActionKind.ReminderDismiss => new ReminderDismissAction(entityId, label),
        FlowActionKind.OpenRun => new OpenRunAction(entityId, label),
        _ => null, // Invoke is never persisted.
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }
    }
}
