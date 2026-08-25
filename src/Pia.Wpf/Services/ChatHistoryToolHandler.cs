using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>Read-only search_chats/read_chat tools; both exclude the current chat via TaskAmbient's chat
/// id (not TaskId, the run id on run surfaces) — a null chat id fails open, excluding nothing.</summary>
public class ChatHistoryToolHandler : IChatHistoryToolHandler
{
    private const int SearchLimitDefault = 10;
    private const int SearchLimitMax = 25;
    private const int ReadLimitDefault = 40;
    private const int ReadLimitMax = 100;
    private const int MaxMessageChars = 1500;

    private const string SearchNote =
        "Snippets are excerpts. Call read_chat(chat_id) for the actual conversation before relying on it.";

    private const string CurrentChatRefusal =
        "That is the current conversation; it is already in front of you.";

    private const string NoSearchableWords =
        "That query has no searchable words, so it cannot match anything. Retry with the words you expect " +
        "to appear in the conversation, or omit query to list the most recent chats.";

    private const string UnknownChatId =
        "No stored chat has that id. Pass a chat_id from a search_chats hit — history is not complete, and " +
        "a chat older than the user's retention setting has already been deleted.";

    private readonly IAssistantChatService _chats;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<ChatHistoryToolHandler> _logger;
    private volatile bool _toolsEnabled = true;

    public ChatHistoryToolHandler(
        IAssistantChatService chats,
        ISettingsService settingsService,
        ILogger<ChatHistoryToolHandler> logger)
    {
        _chats = chats;
        _settingsService = settingsService;
        _logger = logger;

        // Settings are loaded and cached before any handler is constructed, so this returns from the
        // in-memory cache (mirrors GitToolHandler).
        try
        {
            var settings = _settingsService.GetSettingsAsync().GetAwaiter().GetResult();
            _toolsEnabled = settings.AssistantChatHistoryToolsEnabled;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load initial chat-history tool settings");
        }

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public bool IsAvailable => _toolsEnabled;

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        _toolsEnabled = settings.AssistantChatHistoryToolsEnabled;
    }

    public IList<AITool> GetTools()
    {
        if (!IsAvailable) return [];

        return
        [
            AIFunctionFactory.Create(SearchChatsSchema, "search_chats"),
            AIFunctionFactory.Create(ReadChatSchema, "read_chat"),
        ];
    }

    public async Task<object?> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("ChatHistoryToolHandler dispatching: {ToolName}", toolCall.Name);
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        return toolCall.Name switch
        {
            "search_chats" => await HandleSearchAsync(args, cancellationToken),
            "read_chat" => await HandleReadAsync(args, cancellationToken),
            _ => $"Unknown tool: {toolCall.Name}",
        };
    }

    private async Task<object?> HandleSearchAsync(IDictionary<string, object?> args, CancellationToken ct)
    {
        if (!TryGetDateArg(args, "from_date", out var fromDate, out var dateError) ||
            !TryGetDateArg(args, "to_date", out var toDate, out dateError))
        {
            return dateError;
        }

        var limit = ClampLimit(GetOptionalIntArg(args, "limit"), SearchLimitDefault, SearchLimitMax);
        var excludeChatId = TaskAmbient.Current?.ChatId;
        var query = GetOptionalStringArg(args, "query");

        List<ChatHit> hits;
        if (string.IsNullOrWhiteSpace(query))
        {
            // The recency path has no excludeChatId parameter, so over-fetch by the one row that can be
            // dropped and trim back to the cap.
            var rows = await _chats.SearchAsync(
                searchText: null, fromDate: fromDate, toDate: toDate, providerId: null,
                offset: 0, limit: limit + 1, ct: ct);

            hits = rows.Where(c => c.Id != excludeChatId)
                .Take(limit)
                .Select(c => new ChatHit(c.Id.ToString(), c.Title, FormatDate(c.UpdatedAt), null, null))
                .ToList();
        }
        else if (!query.Any(char.IsLetterOrDigit))
        {
            return NoSearchableWords;
        }
        else
        {
            var ranked = await _chats.SearchRankedAsync(
                query, fromDate, toDate, providerId: null, excludeChatId: excludeChatId, limit: limit, ct: ct);

            hits = ranked
                .Select(h => new ChatHit(h.Id.ToString(), h.Title, FormatDate(h.UpdatedAt), h.MessageCount, h.Snippet))
                .ToList();
        }

        _logger.LogInformation("search_chats returned {Count} hit(s)", hits.Count);
        _logger.SensitiveDebug("search_chats query: {Query}", query);
        return new SearchEnvelope(hits, SearchNote);
    }

    private async Task<object?> HandleReadAsync(IDictionary<string, object?> args, CancellationToken ct)
    {
        var rawId = GetOptionalStringArg(args, "chat_id");
        if (string.IsNullOrWhiteSpace(rawId) || !Guid.TryParse(rawId, out var chatId))
        {
            return "Error: chat_id is required, and must be the chat_id of a search_chats hit.";
        }

        // Before the read, not after: refusing the current conversation must not touch the store.
        if (TaskAmbient.Current?.ChatId == chatId)
        {
            return CurrentChatRefusal;
        }

        var chat = await _chats.GetAsync(chatId, ct);
        if (chat is null) return UnknownChatId;

        var offset = Math.Max(0, GetOptionalIntArg(args, "offset") ?? 0);
        var limit = ClampLimit(GetOptionalIntArg(args, "limit"), ReadLimitDefault, ReadLimitMax);

        var total = chat.Messages.Count;
        var window = chat.Messages.Skip(offset).Take(limit).ToList();
        var hasMore = offset + window.Count < total;

        var messages = window
            .Select((m, i) => new TranscriptMessage(
                offset + i,
                m.Role,
                FormatTimestamp(m.Timestamp),
                Truncate(m.Content)))
            .ToList();

        _logger.LogInformation(
            "read_chat {ChatId} returned {Count} of {Total} message(s)", chatId, messages.Count, total);

        return new ChatTranscript(
            chat.Id.ToString(),
            chat.Title,
            FormatDate(chat.CreatedAt),
            FormatDate(chat.UpdatedAt),
            total,
            messages,
            hasMore,
            hasMore ? offset + window.Count : null);
    }

    private static string FormatDate(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture);

    private static string Truncate(string content)
    {
        if (content.Length <= MaxMessageChars) return content;

        // Back off one char rather than cut a surrogate pair in half and hand the serializer a lone
        // high surrogate.
        var cut = char.IsHighSurrogate(content[MaxMessageChars - 1]) ? MaxMessageChars - 1 : MaxMessageChars;
        return content[..cut] + "…[truncated]";
    }

    /// <summary>Clamps in BOTH directions: a missing, zero or negative limit becomes the default.</summary>
    private static int ClampLimit(int? requested, int fallback, int max) =>
        requested is null or <= 0 ? fallback : Math.Min(requested.Value, max);

    private static bool TryGetDateArg(
        IDictionary<string, object?> args, string key, out DateTime? value, out string? error)
    {
        value = null;
        error = null;

        var raw = GetOptionalStringArg(args, key);
        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!DateTime.TryParseExact(
                raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            error = $"Error: {key} must be a calendar date written as YYYY-MM-DD.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static string? GetOptionalStringArg(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null) return null;
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }

        var str = value.ToString();
        return string.IsNullOrEmpty(str) ? null : str;
    }

    private static int? GetOptionalIntArg(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n)) return n;
            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed))
                return parsed;
            return null;
        }

        if (value is int i) return i;
        if (value is long l) return (int)l;
        return int.TryParse(value.ToString(), out var fallback) ? fallback : null;
    }

    // Schema methods — the parameter signature and [Description] attributes ARE the tool metadata for
    // AIFunctionFactory. The body is never invoked (dispatch is by tool name in HandleToolCallAsync).
    [Description("Search past conversations with this assistant (not the current one) by keyword and date")]
    private static string SearchChatsSchema(
        [Description("Keywords to look for in past chats. Omit to list the most recent chats instead.")] string? query = null,
        [Description("Only chats updated on or after this date (YYYY-MM-DD)")] string? from_date = null,
        [Description("Only chats updated on or before this date (YYYY-MM-DD)")] string? to_date = null,
        [Description("Max chats to return (default 10, max 25)")] int? limit = null) => "";

    [Description("Read a past conversation's messages by id, from a search_chats hit")]
    private static string ReadChatSchema(
        [Description("chat_id from a search_chats hit")] string chat_id,
        [Description("0-based message index to start at, for paging a long chat")] int? offset = null,
        [Description("Max messages to return (default 40, max 100)")] int? limit = null) => "";

    /// <summary>snake_case: these serialize straight to the provider via <c>FunctionResultContent</c>.
    /// <c>message_count</c>/<c>snippet</c> stay null on the no-query recency path (metadata only).</summary>
    private sealed record ChatHit(
        string chat_id,
        string? title,
        string updated_at,
        int? message_count,
        string? snippet);

    private sealed record SearchEnvelope(IReadOnlyList<ChatHit> chats, string note);

    /// <summary>Carries no thinking content: model-internal scratch is never replayed into another model.</summary>
    private sealed record TranscriptMessage(int index, string role, string timestamp, string content);

    private sealed record ChatTranscript(
        string chat_id,
        string? title,
        string created_at,
        string updated_at,
        int message_count,
        IReadOnlyList<TranscriptMessage> messages,
        bool has_more,
        int? next_offset);
}
