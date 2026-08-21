using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services;

/// <summary>
/// Reads and writes portable chat archives. Export reuses <see cref="SyncAssistantChat"/> verbatim so
/// a round-trip keeps every persisted column, including fields a newer build added.
/// </summary>
public sealed class ChatArchiveService : IChatArchiveService
{
    private static readonly JsonSerializerOptions ArchiveJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IAssistantChatService _chatService;
    private readonly ILogger<ChatArchiveService> _logger;

    public ChatArchiveService(IAssistantChatService chatService, ILogger<ChatArchiveService> logger)
    {
        _chatService = chatService;
        _logger = logger;
    }

    public async Task<int> ExportAllAsync(string filePath, CancellationToken ct = default)
    {
        var ids = await _chatService.GetAllIdsAsync(ct);
        return await ExportAsync(ids, filePath, ct);
    }

    public async Task<int> ExportAsync(IReadOnlyList<Guid> chatIds, string filePath, CancellationToken ct = default)
    {
        var archive = new PiaChatArchive { App = "Pia", ExportedAt = DateTime.UtcNow };

        foreach (var id in chatIds)
        {
            ct.ThrowIfCancellationRequested();

            var chat = await _chatService.GetAsync(id, ct);
            // Message-less rows are headless-run stubs that history already hides; exporting them
            // would only produce entries the importer has to skip again.
            if (chat is null || chat.Messages.Count == 0)
                continue;

            StripTransportEncryption(chat);
            archive.Chats.Add(chat);
        }

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, archive, ArchiveJson, ct);

        _logger.LogInformation("Exported {Count} assistant chats to a chat archive", archive.Chats.Count);
        return archive.Chats.Count;
    }

    public async Task<ChatImportResult> ImportAsync(string filePath, CancellationToken ct = default)
    {
        using var document = await ReadDocumentAsync(filePath, ct);
        var root = document.RootElement;

        if (IsPiaArchive(root))
        {
            var archive = root.Deserialize<PiaChatArchive>(ArchiveJson);
            var result = await StoreAsync(archive?.Chats ?? [], ct);
            _logger.LogInformation(
                "Imported a Pia chat archive (version {FormatVersion}): {Imported} written, {UpToDate} up to date, {Empty} empty, {Failed} failed",
                archive?.FormatVersion ?? 0, result.Imported, result.SkippedUpToDate, result.SkippedEmpty, result.Failed);
            return result with { Format = ChatArchiveFormat.Pia };
        }

        if (OpenWebUiChatConverter.LooksLikeOpenWebUiExport(root))
        {
            var converted = OpenWebUiChatConverter.Convert(root);
            var result = await StoreAsync(converted.Chats, ct);
            _logger.LogInformation(
                "Imported an Open WebUI export: {Imported} written, {UpToDate} up to date, {Empty} empty, {Failed} failed, {Attachments} attachments dropped",
                result.Imported, result.SkippedUpToDate, result.SkippedEmpty + converted.SkippedEmpty,
                result.Failed, converted.DroppedAttachments);
            return result with
            {
                Format = ChatArchiveFormat.OpenWebUi,
                SkippedEmpty = result.SkippedEmpty + converted.SkippedEmpty,
                DroppedAttachments = converted.DroppedAttachments,
            };
        }

        _logger.LogWarning("Import file matched neither a Pia archive nor an Open WebUI export");
        return new ChatImportResult { Format = ChatArchiveFormat.Unknown };
    }

    private static async Task<JsonDocument> ReadDocumentAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static bool IsPiaArchive(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("format", out var format)
        && format.ValueKind == JsonValueKind.String
        && string.Equals(format.GetString(), PiaChatArchive.FormatMarker, StringComparison.OrdinalIgnoreCase);

    private async Task<ChatImportResult> StoreAsync(IReadOnlyList<SyncAssistantChat> chats, CancellationToken ct)
    {
        var imported = 0;
        var upToDate = 0;
        var empty = 0;
        var failed = 0;
        DateTime? oldest = null;

        // Message ids are a global primary key, so a file that repeats one would abort the write it
        // collides with. Re-keying inside the batch keeps one malformed chat from costing the rest.
        var seenMessageIds = new HashSet<Guid>();

        foreach (var chat in chats)
        {
            ct.ThrowIfCancellationRequested();

            if (chat.Messages.Count == 0)
            {
                empty++;
                continue;
            }

            Normalize(chat, seenMessageIds);

            try
            {
                var existing = await _chatService.GetAsync(chat.Id, ct);
                if (existing is not null && existing.UpdatedAt >= chat.UpdatedAt)
                {
                    upToDate++;
                    continue;
                }

                await _chatService.SaveAsync(chat, ct);
                imported++;
                if (oldest is null || chat.UpdatedAt < oldest)
                    oldest = chat.UpdatedAt;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(ex, "Failed to import chat {ChatId}", chat.Id);
            }
        }

        return new ChatImportResult
        {
            Imported = imported,
            SkippedUpToDate = upToDate,
            SkippedEmpty = empty,
            Failed = failed,
            OldestUpdatedAt = oldest,
        };
    }

    /// <summary>Makes a chat from an untrusted file satisfy the store's NOT NULL and uniqueness rules.</summary>
    private static void Normalize(SyncAssistantChat chat, HashSet<Guid> seenMessageIds)
    {
        StripTransportEncryption(chat);

        if (chat.Id == Guid.Empty)
            chat.Id = Guid.NewGuid();
        if (chat.SchemaVersion <= 0)
            chat.SchemaVersion = 1;
        if (string.IsNullOrWhiteSpace(chat.WindowMode))
            chat.WindowMode = "Assistant";

        if (chat.UpdatedAt == default)
            chat.UpdatedAt = chat.CreatedAt == default ? DateTime.UtcNow : chat.CreatedAt;
        if (chat.CreatedAt == default)
            chat.CreatedAt = chat.UpdatedAt;
        if (chat.LastAccessedAt == default)
            chat.LastAccessedAt = chat.UpdatedAt;

        foreach (var message in chat.Messages)
        {
            if (message.Id == Guid.Empty || !seenMessageIds.Add(message.Id))
            {
                message.Id = Guid.NewGuid();
                seenMessageIds.Add(message.Id);
            }

            if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                message.Role = "assistant";
            if (string.IsNullOrEmpty(message.Content))
                message.Content = string.Empty;
            if (message.Timestamp == default)
                message.Timestamp = chat.UpdatedAt;
        }
    }

    /// <summary>
    /// The local store is plaintext; a file carrying sync's E2EE fields would otherwise be written as a
    /// chat whose content columns are all empty.
    /// </summary>
    private static void StripTransportEncryption(SyncAssistantChat chat)
    {
        chat.EncryptedPayload = null;
        chat.WrappedDek = null;
    }
}
