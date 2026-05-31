using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pia.Infrastructure;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services;

public class AssistantChatService : IAssistantChatService
{
    private readonly SqliteContext _context;

    public event EventHandler<AssistantChatChangedEventArgs>? ChatsChanged;

    public AssistantChatService(SqliteContext context)
    {
        _context = context;
    }

    private void OnChatsChanged(Guid id, AssistantChatChangeKind kind) =>
        ChatsChanged?.Invoke(this, new AssistantChatChangedEventArgs { Id = id, Kind = kind });

    public Task SaveAsync(SyncAssistantChat chat, CancellationToken ct = default) =>
        SaveCoreAsync(chat, raiseEvent: true, ct);

    public Task SaveFromRemoteAsync(SyncAssistantChat chat, CancellationToken ct = default) =>
        SaveCoreAsync(chat, raiseEvent: false, ct);

    private async Task SaveCoreAsync(SyncAssistantChat chat, bool raiseEvent, CancellationToken ct)
    {
        var connection = _context.GetConnection();
        using var transaction = connection.BeginTransaction();

        using (var upsertChat = connection.CreateCommand())
        {
            upsertChat.Transaction = transaction;
            upsertChat.CommandText = """
                INSERT INTO AssistantChats
                    (Id, SchemaVersion, Title, CreatedAt, UpdatedAt, LastAccessedAt, WindowMode, ProviderId, ExtraJson)
                VALUES
                    (@Id, @SchemaVersion, @Title, @CreatedAt, @UpdatedAt, @LastAccessedAt, @WindowMode, @ProviderId, @ExtraJson)
                ON CONFLICT(Id) DO UPDATE SET
                    SchemaVersion = excluded.SchemaVersion,
                    Title = excluded.Title,
                    UpdatedAt = excluded.UpdatedAt,
                    LastAccessedAt = excluded.LastAccessedAt,
                    WindowMode = excluded.WindowMode,
                    ProviderId = excluded.ProviderId,
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
                    (Id, ChatId, Ordinal, Role, Content, ThinkingContent, Timestamp, Tokens, ModelName)
                VALUES
                    (@Id, @ChatId, @Ordinal, @Role, @Content, @ThinkingContent, @Timestamp, @Tokens, @ModelName)
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
            await insertMessage.ExecuteNonQueryAsync(ct);
        }

        await ReplaceFtsRowAsync(connection, transaction, chat, ct);

        transaction.Commit();
        if (raiseEvent)
            OnChatsChanged(chat.Id, AssistantChatChangeKind.Upserted);
    }

    public async Task<SyncAssistantChat?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var connection = _context.GetConnection();

        SyncAssistantChat? chat;
        using (var getChat = connection.CreateCommand())
        {
            getChat.CommandText = """
                SELECT Id, SchemaVersion, Title, CreatedAt, UpdatedAt, LastAccessedAt, WindowMode, ProviderId, ExtraJson
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

    public async Task<IReadOnlyList<SyncAssistantChat>> SearchAsync(
        string? searchText = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? providerId = null,
        int offset = 0,
        int limit = 50,
        CancellationToken ct = default)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();

        var conditions = new List<string>();
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
            SELECT Id, SchemaVersion, Title, CreatedAt, UpdatedAt, LastAccessedAt, WindowMode, ProviderId, ExtraJson
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
        var connection = _context.GetConnection();
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
        if (raiseEvent)
            OnChatsChanged(id, AssistantChatChangeKind.Deleted);
    }

    public async Task TouchLastAccessedAsync(Guid id, CancellationToken ct = default)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AssistantChats SET LastAccessedAt = @Now WHERE Id = @Id";
        command.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> DeleteAllAsync(CancellationToken ct = default)
    {
        var connection = _context.GetConnection();

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
            return deletedIds.AsReadOnly();
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
        foreach (var id in deletedIds)
            OnChatsChanged(id, AssistantChatChangeKind.Deleted);
        return deletedIds.AsReadOnly();
    }

    public async Task<IReadOnlyList<Guid>> EvictOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        var connection = _context.GetConnection();

        List<Guid> evictedIds;
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT Id FROM AssistantChats WHERE LastAccessedAt < @Cutoff";
            select.Parameters.AddWithValue("@Cutoff", cutoffUtc.ToString("O"));

            evictedIds = [];
            using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                evictedIds.Add(Guid.Parse(reader.GetString(0)));
        }

        if (evictedIds.Count == 0) return evictedIds.AsReadOnly();

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
        foreach (var id in evictedIds)
            OnChatsChanged(id, AssistantChatChangeKind.Deleted);
        return evictedIds.AsReadOnly();
    }

    public async Task<DateTime?> GetMaxUpdatedAtAsync(CancellationToken ct = default)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(UpdatedAt) FROM AssistantChats";

        var result = await command.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return null;
        return DateTime.Parse((string)result).ToUniversalTime();
    }

    public async Task<IReadOnlyList<Guid>> GetAllIdsAsync(CancellationToken ct = default)
    {
        var connection = _context.GetConnection();
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

    private static async Task<List<SyncAssistantChatMessage>> GetMessagesAsync(
        SqliteConnection connection, Guid chatId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Role, Content, ThinkingContent, Timestamp, Tokens, ModelName
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
            ExtensionData = reader.IsDBNull(8) ? null : DeserializeExtensionData(reader.GetString(8)),
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
}
