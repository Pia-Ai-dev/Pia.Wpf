using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pia.Infrastructure;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services;

/// <summary>
/// SQLite-backed store for assistant chats. Uses its own dedicated connection (not the shared
/// <see cref="SqliteContext"/> connection) guarded by a gate, because chat rows are written from three
/// different thread classes: the WPF UI thread (<c>ChatSessionManager.PersistAsync</c>), the headless run
/// pool (<c>HeadlessTurnExecutor.PersistChatAsync</c> — up to two concurrent runs at the slot cap, plus
/// <c>BackgroundAssistantTurnRunner</c>) and the hosted-service pool (<c>AssistantChatSyncService</c>,
/// <c>AssistantChatRetentionService</c>). The tables live in <see cref="SqliteContext"/>'s canonical schema
/// (not redefined here); the ctor forces the shared connection once at composition time so that schema
/// exists before this service's handle opens.
/// <para>
/// BOTH HALVES ARE REQUIRED. The dedicated connection alone fixes nothing observable: the two exceptions
/// this class used to raise are intra-ADO.NET, not SQLite-level — "SqliteConnection does not support nested
/// transactions" from a second <c>BeginTransaction</c>, and "Execute requires the command to have a
/// transaction object ... pending local transaction" from any UNTRANSACTED command issued while a
/// transaction is pending. Both are properties of ONE <see cref="SqliteConnection"/> object shared by two
/// threads, so five callers on a private handle would collide exactly as before. What the dedicated
/// connection buys is blast radius: the longest-held transaction in the app (upsert + DELETE-all +
/// N-INSERT + FTS replace) leaves the shared connection, which is what protects the ten other services on
/// it. The gate is what makes this class itself safe.
/// </para>
/// <para>
/// THE GATE COVERS EVERY PUBLIC METHOD, READS INCLUDED. The untransacted readers (<see cref="GetAsync"/>,
/// <see cref="SearchAsync"/>, <see cref="TouchLastAccessedAsync"/>, <see cref="EvictOlderThanAsync"/>'s
/// pre-select, <see cref="GetMaxUpdatedAtAsync"/>, <see cref="GetAllIdsAsync"/>) are the MAJORITY of the
/// collision surface and were the only user-visible symptom: a pool-thread <c>ChatsChanged</c> posts
/// <see cref="SearchAsync"/> from the history view model, which threw and was swallowed, leaving a silently
/// stale history list. A gate over writes only would leave that half broken.
/// </para>
/// <para>
/// DELIBERATE DEVIATION from the two in-tree precedents (<c>AgentRunService</c> and
/// <c>FlowPersistenceStore</c> both use a plain <c>lock</c>): this class is async throughout (ten
/// <c>await Execute*Async</c> sites and two static async helpers), you cannot <c>await</c> inside a
/// <c>lock</c>, and — decisively — <c>ChatSessionManager.PersistAsync</c> runs on the WPF UI thread, so a
/// <c>lock</c> would BLOCK the message pump for the duration of a headless step's full replace. That
/// violates "the user's Send never blocks on a headless step's persistence". An awaited
/// <see cref="SemaphoreSlim"/> makes the UI thread yield instead.
/// <br/>
/// Cost of that choice, so a future reader does not call it a bug: <c>WaitAsync(ct)</c> can now throw
/// <see cref="OperationCanceledException"/> where a caller previously always completed. The only caller
/// passing a real token is <c>AssistantChatSyncService</c>'s stopping token, i.e. the blast radius is "a
/// chat write is abandoned during shutdown" — already the behaviour when the process exits.
/// </para>
/// <para>
/// <see cref="ChatsChanged"/> is ALWAYS raised after the gate is released. Subscribers re-enter this
/// service and do slow work on the raising thread (<c>HeadlessRunLauncher.OnChatsChanged</c> does a
/// recursive <c>Directory.Delete</c>; the history/chip view models post a <see cref="SearchAsync"/> back),
/// so raising under the gate would hold a write gate across file I/O and deadlock any subscriber that
/// awaits a chat read on the raising thread.
/// </para>
/// </summary>
public class AssistantChatService : IAssistantChatService, IDisposable
{
    private readonly string _connectionString;
    private readonly IAgentRunService _runService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _disposed;

    public event EventHandler<AssistantChatChangedEventArgs>? ChatsChanged;

    public AssistantChatService(SqliteContext context, IAgentRunService runService)
    {
        _connectionString = context.ConnectionString;
        _runService = runService;

        // Force the shared context to open + run EnsureSchema/MigrateSchema (which create AssistantChats,
        // AssistantChatMessages, AssistantChatsFts and the PRAGMA-detected WorkingDirectory column, and can
        // DROP + re-backfill the FTS table inside their own transaction) BEFORE our dedicated connection
        // ever opens. Done at composition time rather than lazily from a background thread because
        // SqliteContext.GetConnection() is not itself synchronized. Until now this class got that ordering
        // BY ACCIDENT — its IAgentRunService dependency happened to prime the context — which would break
        // silently the moment that dependency is mocked or the registration order changes.
        context.GetConnection();
    }

    /// <summary>
    /// The dedicated handle. <c>busy_timeout</c> is per-connection so it must be set here as well;
    /// <c>journal_mode=WAL</c> is a persistent per-FILE setting applied where the file is first opened
    /// (<see cref="SqliteContext.GetConnection"/>). Only ever called while holding <see cref="_gate"/>.
    /// </summary>
    private SqliteConnection Connection()
    {
        if (_connection is null)
        {
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();

            using var pragma = _connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout=3000;";
            pragma.ExecuteNonQuery();
        }
        else if (_connection.State != System.Data.ConnectionState.Open)
        {
            _connection.Open();
        }

        return _connection;
    }

    private void OnChatsChanged(Guid id, AssistantChatChangeKind kind) =>
        ChatsChanged?.Invoke(this, new AssistantChatChangedEventArgs { Id = id, Kind = kind });

    public Task SaveAsync(SyncAssistantChat chat, CancellationToken ct = default) =>
        SaveCoreAsync(chat, raiseEvent: true, preserveNewerLastAccessed: false, ct);

    public Task SaveFromRemoteAsync(SyncAssistantChat chat, CancellationToken ct = default) =>
        // Remote LastAccessedAt is day-truncated on the wire; never let it regress a
        // more precise local value or retention could evict up to a day early.
        SaveCoreAsync(chat, raiseEvent: false, preserveNewerLastAccessed: true, ct);

    private async Task SaveCoreAsync(SyncAssistantChat chat, bool raiseEvent, bool preserveNewerLastAccessed, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return;
            await SaveUnderGateAsync(chat, preserveNewerLastAccessed, ct);
        }
        finally
        {
            _gate.Release();
        }

        // Outside the gate — subscribers re-enter this service and do recursive directory deletion.
        if (raiseEvent)
            OnChatsChanged(chat.Id, AssistantChatChangeKind.Upserted);
    }

    private async Task SaveUnderGateAsync(SyncAssistantChat chat, bool preserveNewerLastAccessed, CancellationToken ct)
    {
        var connection = Connection();
        using var transaction = connection.BeginTransaction();

        using (var upsertChat = connection.CreateCommand())
        {
            upsertChat.Transaction = transaction;
            // Timestamps are stored as fixed-width ISO-8601 UTC ("O"), so SQLite's
            // lexicographic max() is chronological.
            var lastAccessedSet = preserveNewerLastAccessed
                ? "max(LastAccessedAt, excluded.LastAccessedAt)"
                : "excluded.LastAccessedAt";
            upsertChat.CommandText = $"""
                INSERT INTO AssistantChats
                    (Id, SchemaVersion, Title, CreatedAt, UpdatedAt, LastAccessedAt, WindowMode, ProviderId, WorkingDirectory, ExtraJson)
                VALUES
                    (@Id, @SchemaVersion, @Title, @CreatedAt, @UpdatedAt, @LastAccessedAt, @WindowMode, @ProviderId, @WorkingDirectory, @ExtraJson)
                ON CONFLICT(Id) DO UPDATE SET
                    SchemaVersion = excluded.SchemaVersion,
                    Title = excluded.Title,
                    UpdatedAt = excluded.UpdatedAt,
                    LastAccessedAt = {lastAccessedSet},
                    WindowMode = excluded.WindowMode,
                    ProviderId = excluded.ProviderId,
                    WorkingDirectory = excluded.WorkingDirectory,
                    ExtraJson = excluded.ExtraJson
                """;
            upsertChat.Parameters.AddWithValue("@Id", chat.Id.ToString());
            upsertChat.Parameters.AddWithValue("@SchemaVersion", chat.SchemaVersion);
            upsertChat.Parameters.AddWithValue("@Title", (object?)chat.Title ?? DBNull.Value);
            upsertChat.Parameters.AddWithValue("@CreatedAt", chat.CreatedAt.ToString("O"));
            upsertChat.Parameters.AddWithValue("@UpdatedAt", chat.UpdatedAt.ToString("O"));
            upsertChat.Parameters.AddWithValue("@LastAccessedAt", chat.LastAccessedAt.ToString("O"));
            upsertChat.Parameters.AddWithValue("@WindowMode", chat.WindowMode);
            upsertChat.Parameters.AddWithValue("@ProviderId", (object?)chat.ProviderId?.ToString() ?? DBNull.Value);
            upsertChat.Parameters.AddWithValue("@WorkingDirectory", (object?)chat.WorkingDirectory ?? DBNull.Value);
            upsertChat.Parameters.AddWithValue("@ExtraJson", (object?)SerializeExtensionData(chat.ExtensionData) ?? DBNull.Value);
            await upsertChat.ExecuteNonQueryAsync(ct);
        }

        using (var deleteMessages = connection.CreateCommand())
        {
            deleteMessages.Transaction = transaction;
            deleteMessages.CommandText = "DELETE FROM AssistantChatMessages WHERE ChatId = @ChatId";
            deleteMessages.Parameters.AddWithValue("@ChatId", chat.Id.ToString());
            await deleteMessages.ExecuteNonQueryAsync(ct);
        }

        var ordinal = 0;
        foreach (var msg in chat.Messages)
        {
            using var insertMessage = connection.CreateCommand();
            insertMessage.Transaction = transaction;
            insertMessage.CommandText = """
                INSERT INTO AssistantChatMessages
                    (Id, ChatId, Ordinal, Role, Content, ThinkingContent, Timestamp, Tokens, ModelName, PersonaId, PersonaName, PersonaEmoji)
                VALUES
                    (@Id, @ChatId, @Ordinal, @Role, @Content, @ThinkingContent, @Timestamp, @Tokens, @ModelName, @PersonaId, @PersonaName, @PersonaEmoji)
                """;
            insertMessage.Parameters.AddWithValue("@Id", msg.Id.ToString());
            insertMessage.Parameters.AddWithValue("@ChatId", chat.Id.ToString());
            insertMessage.Parameters.AddWithValue("@Ordinal", ordinal++);
            insertMessage.Parameters.AddWithValue("@Role", msg.Role);
            insertMessage.Parameters.AddWithValue("@Content", msg.Content);
            insertMessage.Parameters.AddWithValue("@ThinkingContent", (object?)msg.ThinkingContent ?? DBNull.Value);
            insertMessage.Parameters.AddWithValue("@Timestamp", msg.Timestamp.ToString("O"));
            insertMessage.Parameters.AddWithValue("@Tokens", (object?)msg.Tokens ?? DBNull.Value);
            insertMessage.Parameters.AddWithValue("@ModelName", (object?)msg.ModelName ?? DBNull.Value);
            insertMessage.Parameters.AddWithValue("@PersonaId", (object?)msg.Persona?.Id.ToString() ?? DBNull.Value);
            insertMessage.Parameters.AddWithValue("@PersonaName", (object?)msg.Persona?.Name ?? DBNull.Value);
            insertMessage.Parameters.AddWithValue("@PersonaEmoji", (object?)msg.Persona?.Emoji ?? DBNull.Value);
            await insertMessage.ExecuteNonQueryAsync(ct);
        }

        await ReplaceFtsRowAsync(connection, transaction, chat, ct);

        transaction.Commit();
    }

    public async Task<SyncAssistantChat?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return null;

            var connection = Connection();

            SyncAssistantChat? chat;
            using (var getChat = connection.CreateCommand())
            {
                getChat.CommandText = """
                    SELECT Id, SchemaVersion, Title, CreatedAt, UpdatedAt, LastAccessedAt, WindowMode, ProviderId, WorkingDirectory, ExtraJson
                    FROM AssistantChats WHERE Id = @Id
                    """;
                getChat.Parameters.AddWithValue("@Id", id.ToString());

                using var reader = await getChat.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return null;
                chat = MapChat(reader);
            }

            chat.Messages = await GetMessagesAsync(connection, id, ct);
            return chat;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SyncAssistantChat>> SearchAsync(
        string? searchText = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? providerId = null,
        int offset = 0,
        int limit = 50,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return Array.Empty<SyncAssistantChat>();
            return await SearchUnderGateAsync(searchText, fromDate, toDate, providerId, offset, limit, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<SyncAssistantChat>> SearchUnderGateAsync(
        string? searchText,
        DateTime? fromDate,
        DateTime? toDate,
        Guid? providerId,
        int offset,
        int limit,
        CancellationToken ct)
    {
        var connection = Connection();
        using var command = connection.CreateCommand();

        var conditions = new List<string>();

        // Hide message-less chats from the history list. A failed/empty headless turn leaves a
        // stub AssistantChats row up front (the FK target its AgentRun needs — §16 R1) that never
        // receives messages; such stubs should not clutter history. Real chats always have ≥1
        // message. The stub stays reachable via its run (FlowAction.OpenRun, milestone 1.4).
        conditions.Add("""
            EXISTS (SELECT 1 FROM AssistantChatMessages WHERE AssistantChatMessages.ChatId = AssistantChats.Id)
            """);

        if (fromDate.HasValue)
        {
            conditions.Add("UpdatedAt >= @FromDate");
            command.Parameters.AddWithValue("@FromDate", fromDate.Value.ToString("O"));
        }
        if (toDate.HasValue)
        {
            var endOfDay = toDate.Value.Date.AddDays(1).AddTicks(-1);
            conditions.Add("UpdatedAt <= @ToDate");
            command.Parameters.AddWithValue("@ToDate", endOfDay.ToString("O"));
        }
        if (providerId.HasValue)
        {
            conditions.Add("ProviderId = @ProviderId");
            command.Parameters.AddWithValue("@ProviderId", providerId.Value.ToString());
        }
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var ftsQuery = BuildFtsQuery(searchText);
            if (!string.IsNullOrEmpty(ftsQuery))
            {
                conditions.Add("""
                    Id IN (SELECT ChatId FROM AssistantChatsFts WHERE AssistantChatsFts MATCH @Search)
                    """);
                command.Parameters.AddWithValue("@Search", ftsQuery);
            }
        }

        var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";

        command.CommandText = $"""
            SELECT Id, SchemaVersion, Title, CreatedAt, UpdatedAt, LastAccessedAt, WindowMode, ProviderId, WorkingDirectory, ExtraJson
            FROM AssistantChats
            {whereClause}
            ORDER BY UpdatedAt DESC
            LIMIT @Limit OFFSET @Offset
            """;
        command.Parameters.AddWithValue("@Limit", limit);
        command.Parameters.AddWithValue("@Offset", offset);

        var chats = new List<SyncAssistantChat>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            chats.Add(MapChat(reader));
        }
        return chats.AsReadOnly();
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default) =>
        DeleteCoreAsync(id, raiseEvent: true, ct);

    public Task DeleteFromRemoteAsync(Guid id, CancellationToken ct = default) =>
        DeleteCoreAsync(id, raiseEvent: false, ct);

    private async Task DeleteCoreAsync(Guid id, bool raiseEvent, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return;
            await DeleteUnderGateAsync(id, ct);
        }
        finally
        {
            _gate.Release();
        }

        if (raiseEvent)
            OnChatsChanged(id, AssistantChatChangeKind.Deleted);
    }

    private async Task DeleteUnderGateAsync(Guid id, CancellationToken ct)
    {
        var connection = Connection();
        using var transaction = connection.BeginTransaction();

        using (var deleteChat = connection.CreateCommand())
        {
            deleteChat.Transaction = transaction;
            // ON DELETE CASCADE removes messages.
            deleteChat.CommandText = "DELETE FROM AssistantChats WHERE Id = @Id";
            deleteChat.Parameters.AddWithValue("@Id", id.ToString());
            await deleteChat.ExecuteNonQueryAsync(ct);
        }

        using (var deleteFts = connection.CreateCommand())
        {
            deleteFts.Transaction = transaction;
            deleteFts.CommandText = "DELETE FROM AssistantChatsFts WHERE ChatId = @ChatId";
            deleteFts.Parameters.AddWithValue("@ChatId", id.ToString());
            await deleteFts.ExecuteNonQueryAsync(ct);
        }

        transaction.Commit();
    }

    public async Task TouchLastAccessedAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return;

            var connection = Connection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE AssistantChats SET LastAccessedAt = @Now WHERE Id = @Id";
            command.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("@Id", id.ToString());
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Guid>> DeleteAllAsync(CancellationToken ct = default)
    {
        // The ids are collected UNDER the gate; the per-id events are raised after it is released.
        List<Guid> deletedIds;
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return Array.Empty<Guid>();
            deletedIds = await DeleteAllUnderGateAsync(ct);
        }
        finally
        {
            _gate.Release();
        }

        foreach (var id in deletedIds)
            OnChatsChanged(id, AssistantChatChangeKind.Deleted);
        return deletedIds.AsReadOnly();
    }

    private async Task<List<Guid>> DeleteAllUnderGateAsync(CancellationToken ct)
    {
        var connection = Connection();

        using var transaction = connection.BeginTransaction();

        List<Guid> deletedIds;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT Id FROM AssistantChats";
            deletedIds = [];
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                deletedIds.Add(Guid.Parse(reader.GetString(0)));
        }

        if (deletedIds.Count == 0)
        {
            transaction.Commit();
            return deletedIds;
        }

        using (var deleteChats = connection.CreateCommand())
        {
            deleteChats.Transaction = transaction;
            deleteChats.CommandText = "DELETE FROM AssistantChats";
            await deleteChats.ExecuteNonQueryAsync(ct);
        }

        using (var deleteFts = connection.CreateCommand())
        {
            deleteFts.Transaction = transaction;
            deleteFts.CommandText = "DELETE FROM AssistantChatsFts";
            await deleteFts.ExecuteNonQueryAsync(ct);
        }

        transaction.Commit();
        return deletedIds;
    }

    public async Task<IReadOnlyList<Guid>> EvictOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        // Three phases on purpose: the pre-select takes the gate briefly, the cross-service run lookup runs
        // with NO gate held (never hold two service gates at once — AgentRunService takes its own lock), and
        // the delete loop re-takes it for its transaction.
        List<Guid> evictedIds;
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return Array.Empty<Guid>();

            var connection = Connection();
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT Id FROM AssistantChats WHERE LastAccessedAt < @Cutoff";
            select.Parameters.AddWithValue("@Cutoff", cutoffUtc.ToString("O"));

            evictedIds = [];
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                evictedIds.Add(Guid.Parse(reader.GetString(0)));
        }
        finally
        {
            _gate.Release();
        }

        // §16 R17 / §2.4: keep chats that bear a Planned agent run (a durable, resumable run must
        // outlive stale-chat eviction). Runs outside the delete transaction AND outside our gate; the
        // returned list then contains only actually-deleted ids so the retention sync never deletes a
        // skipped chat.
        if (evictedIds.Count > 0)
        {
            var retained = new List<Guid>(evictedIds.Count);
            foreach (var id in evictedIds)
                if (!await _runService.ChatHasPlannedRunAsync(id, ct))
                    retained.Add(id);
            evictedIds = retained;
        }

        if (evictedIds.Count == 0) return evictedIds.AsReadOnly();

        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return Array.Empty<Guid>();
            await EvictUnderGateAsync(evictedIds, ct);
        }
        finally
        {
            _gate.Release();
        }

        foreach (var id in evictedIds)
            OnChatsChanged(id, AssistantChatChangeKind.Deleted);
        return evictedIds.AsReadOnly();
    }

    private async Task EvictUnderGateAsync(List<Guid> evictedIds, CancellationToken ct)
    {
        var connection = Connection();
        using var transaction = connection.BeginTransaction();

        foreach (var id in evictedIds)
        {
            using var deleteChat = connection.CreateCommand();
            deleteChat.Transaction = transaction;
            deleteChat.CommandText = "DELETE FROM AssistantChats WHERE Id = @Id";
            deleteChat.Parameters.AddWithValue("@Id", id.ToString());
            await deleteChat.ExecuteNonQueryAsync(ct);

            using var deleteFts = connection.CreateCommand();
            deleteFts.Transaction = transaction;
            deleteFts.CommandText = "DELETE FROM AssistantChatsFts WHERE ChatId = @ChatId";
            deleteFts.Parameters.AddWithValue("@ChatId", id.ToString());
            await deleteFts.ExecuteNonQueryAsync(ct);
        }

        transaction.Commit();
    }

    public async Task<DateTime?> GetMaxUpdatedAtAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return null;

            var connection = Connection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(UpdatedAt) FROM AssistantChats";

            var result = await command.ExecuteScalarAsync(ct);
            if (result is null or DBNull) return null;
            return DateTime.Parse((string)result).ToUniversalTime();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Guid>> GetAllIdsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return Array.Empty<Guid>();

            var connection = Connection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM AssistantChats";

            var ids = new List<Guid>();
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (Guid.TryParse(reader.GetString(0), out var id))
                    ids.Add(id);
            }
            return ids.AsReadOnly();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<List<SyncAssistantChatMessage>> GetMessagesAsync(
        SqliteConnection connection, Guid chatId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Role, Content, ThinkingContent, Timestamp, Tokens, ModelName, PersonaId, PersonaName, PersonaEmoji
            FROM AssistantChatMessages
            WHERE ChatId = @ChatId
            ORDER BY Ordinal ASC
            """;
        command.Parameters.AddWithValue("@ChatId", chatId.ToString());

        var messages = new List<SyncAssistantChatMessage>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            messages.Add(new SyncAssistantChatMessage
            {
                Id = Guid.Parse(reader.GetString(0)),
                Role = reader.GetString(1),
                Content = reader.GetString(2),
                ThinkingContent = reader.IsDBNull(3) ? null : reader.GetString(3),
                Timestamp = DateTime.Parse(reader.GetString(4)),
                Tokens = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                ModelName = reader.IsDBNull(6) ? null : reader.GetString(6),
                Persona = reader.IsDBNull(7)
                    ? null
                    : new SyncMessagePersona
                    {
                        Id = Guid.Parse(reader.GetString(7)),
                        Name = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                        Emoji = reader.IsDBNull(9) ? null : reader.GetString(9),
                    },
            });
        }
        return messages;
    }

    private static async Task ReplaceFtsRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncAssistantChat chat,
        CancellationToken ct)
    {
        using (var deleteFts = connection.CreateCommand())
        {
            deleteFts.Transaction = transaction;
            deleteFts.CommandText = "DELETE FROM AssistantChatsFts WHERE ChatId = @ChatId";
            deleteFts.Parameters.AddWithValue("@ChatId", chat.Id.ToString());
            await deleteFts.ExecuteNonQueryAsync(ct);
        }

        var body = string.Join("\n\n", chat.Messages.Select(m => m.Content));
        using var insertFts = connection.CreateCommand();
        insertFts.Transaction = transaction;
        insertFts.CommandText = """
            INSERT INTO AssistantChatsFts (ChatId, Title, Body)
            VALUES (@ChatId, @Title, @Body)
            """;
        insertFts.Parameters.AddWithValue("@ChatId", chat.Id.ToString());
        insertFts.Parameters.AddWithValue("@Title", chat.Title ?? string.Empty);
        insertFts.Parameters.AddWithValue("@Body", body);
        await insertFts.ExecuteNonQueryAsync(ct);
    }

    private static SyncAssistantChat MapChat(SqliteDataReader reader)
    {
        return new SyncAssistantChat
        {
            Id = Guid.Parse(reader.GetString(0)),
            SchemaVersion = reader.GetInt32(1),
            Title = reader.IsDBNull(2) ? null : reader.GetString(2),
            CreatedAt = DateTime.Parse(reader.GetString(3)),
            UpdatedAt = DateTime.Parse(reader.GetString(4)),
            LastAccessedAt = DateTime.Parse(reader.GetString(5)),
            WindowMode = reader.GetString(6),
            ProviderId = reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
            WorkingDirectory = reader.IsDBNull(8) ? null : reader.GetString(8),
            ExtensionData = reader.IsDBNull(9) ? null : DeserializeExtensionData(reader.GetString(9)),
        };
    }

    private static string? SerializeExtensionData(Dictionary<string, JsonElement>? data)
    {
        if (data is null || data.Count == 0) return null;
        return JsonSerializer.Serialize(data);
    }

    private static Dictionary<string, JsonElement>? DeserializeExtensionData(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        }
        catch (JsonException)
        {
            // Corrupted ExtraJson row — drop the unknown fields rather than fail the read.
            return null;
        }
    }

    private static string BuildFtsQuery(string searchText)
    {
        // Per-token prefix match: "hello wor" -> hello* wor*. Quoting each
        // token (phrase query) requires an exact-token match, so partially
        // typed words never matched — strip FTS5 operator chars and append *.
        var tokens = searchText
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeFtsToken)
            .Where(t => t.Length > 0)
            .Select(t => t + "*");
        return string.Join(' ', tokens);
    }

    private static string SanitizeFtsToken(string token)
    {
        // Lowercase, then keep only letters/digits. Lowercasing neutralises
        // FTS5's uppercase boolean operators (AND/OR/NOT) — without it,
        // a user typing "OR" produces "OR*" which is a syntax error.
        var sb = new StringBuilder(token.Length);
        foreach (var ch in token)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Takes the gate and sets <c>_disposed</c> BEFORE closing the handle (FlowPersistenceStore's ordering),
    /// so an in-flight headless step's persist either finished under the gate or sees <c>_disposed</c> and
    /// no-ops — it can never reach a half-disposed connection. Blocking here is fine: disposal happens at
    /// shutdown, not on the UI hot path.
    /// </summary>
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
