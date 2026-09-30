using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Pia.Shared.Knowledge;

namespace Pia.Services.KnowledgeManager;

/// <summary>Reads answer inline; every write is a pending action the user confirms, because it changes what the whole group finds.</summary>
public class KnowledgeManagerToolHandler : IKnowledgeManagerToolHandler
{
    private const string NotConnected =
        "You are not signed in to a Pia server, so the knowledge bases cannot be reached.";

    private const string ServerUnavailable =
        "Your Pia server could not answer, so nothing was read or changed — try again.";

    private const string NoLongerManager =
        "The server no longer lets you manage knowledge bases, so nothing was read or changed.";

    private const string UnknownKb =
        "That knowledge base is not one you can manage. Call list_knowledge_bases for the ids.";

    private const string UnknownDocument =
        "That document is not in this knowledge base. Call list_kb_documents for the ids.";

    private const string NoFilesFolder =
        "No assistant files folder is configured. Ask the user to set one under Settings → Assistant.";

    private const string NoKnowledgeBases = "Your group has no knowledge bases you can manage.";
    private const string BadKbId = "kb_id must be an id from list_knowledge_bases.";
    private const string BadDocumentId = "document_id must be an id from list_kb_documents.";

    private const string SharedNote =
        "A shared knowledge base is also used by other groups: say so before changing it.";

    private const string TruncatedNote =
        "The document is longer than 32 KB, so only its start is shown. Call download_kb_document to save the " +
        "whole text into the files folder, then read it with read_file.";

    private const string DownloadNote = "Saved as a new file; an existing file is never overwritten. Read it with read_file.";
    private const string PromptNote = "This text tells the assistant when to search this knowledge base.";

    private readonly IKnowledgeManagerApiClient _api;
    private readonly IKnowledgeManagerSurfaceCache _surface;
    private readonly IFilesToolHandler _files;
    private readonly ILocalizationService _localization;
    private readonly ILogger<KnowledgeManagerToolHandler> _logger;

    public KnowledgeManagerToolHandler(
        IKnowledgeManagerApiClient api,
        IKnowledgeManagerSurfaceCache surface,
        IFilesToolHandler files,
        ILocalizationService localization,
        ILogger<KnowledgeManagerToolHandler> logger)
    {
        _api = api;
        _surface = surface;
        _files = files;
        _localization = localization;
        _logger = logger;
    }

    public bool IsAvailable => _surface.IsAvailable;

    public IList<AITool> GetTools()
    {
        if (!IsAvailable) return [];

        return
        [
            AIFunctionFactory.Create(ListKnowledgeBasesSchema, "list_knowledge_bases"),
            AIFunctionFactory.Create(GetKnowledgeBaseStatsSchema, "get_knowledge_base_stats"),
            AIFunctionFactory.Create(ListKbDocumentsSchema, "list_kb_documents"),
            AIFunctionFactory.Create(GetKbPromptSchema, "get_kb_prompt"),
            AIFunctionFactory.Create(ReadKbDocumentSchema, "read_kb_document"),
            AIFunctionFactory.Create(DownloadKbDocumentSchema, "download_kb_document"),
            AIFunctionFactory.Create(UploadKbDocumentSchema, "upload_kb_document"),
            AIFunctionFactory.Create(UpdateKbDocumentSchema, "update_kb_document"),
            AIFunctionFactory.Create(SetKbPromptSchema, "set_kb_prompt"),
            AIFunctionFactory.Create(DeleteKbDocumentSchema, "delete_kb_document"),
        ];
    }

    public async Task<(object? Result, KbManagerToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("KnowledgeManagerToolHandler dispatching: {ToolName}", toolCall.Name);
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        return toolCall.Name switch
        {
            "list_knowledge_bases" => (await ListKnowledgeBasesAsync(cancellationToken), null),
            "get_knowledge_base_stats" => (await GetStatsAsync(args, cancellationToken), null),
            "list_kb_documents" => (await ListDocumentsAsync(args, cancellationToken), null),
            "get_kb_prompt" => (await GetPromptAsync(args, cancellationToken), null),
            "read_kb_document" => (await ReadDocumentAsync(args, cancellationToken), null),
            "download_kb_document" => (await DownloadDocumentAsync(args, cancellationToken), null),
            "upload_kb_document" => await PrepareUploadAsync(args, cancellationToken),
            "update_kb_document" => await PrepareUpdateAsync(args, cancellationToken),
            "set_kb_prompt" => await PrepareSetPromptAsync(args, cancellationToken),
            "delete_kb_document" => await PrepareDeleteAsync(args, cancellationToken),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", (KbManagerToolCall?)null),
        };
    }

    private async Task<object> ListKnowledgeBasesAsync(CancellationToken ct)
    {
        var result = await _api.ListKnowledgeBasesAsync(ct);
        if (!result.IsOk) return Refusal(result);
        if (result.Value!.Count == 0) return NoKnowledgeBases;

        return new
        {
            knowledge_bases = result.Value.Select(kb => new
            {
                kb_id = kb.Id,
                name = kb.Name,
                document_count = kb.DocumentCount,
                shared = kb.Shared,
                has_prompt = kb.HasPrompt,
            }).ToList(),
            note = SharedNote,
        };
    }

    private async Task<object> GetStatsAsync(IDictionary<string, object?> args, CancellationToken ct)
    {
        if (!TryGetGuid(args, "kb_id", out var kbId)) return BadKbId;

        var result = await _api.GetStatsAsync(kbId, ct);
        if (!result.IsOk) return Refusal(result);

        var s = result.Value!;
        return new
        {
            kb_id = kbId,
            documents = s.DocumentCount,
            pending = s.PendingCount,
            processing = s.ProcessingCount,
            ready = s.ReadyCount,
            failed = s.FailedCount,
            size_bytes = s.SizeBytes,
            byte_limit = s.ByteLimit,
            document_limit = s.DocumentLimit,
            chunks = s.ChunkCount,
            indexed_tokens = s.IndexedTokens,
            retrievals = s.RetrievalCount,
            last_retrieved_at = Stamp(s.LastRetrievedAt),
            embedding_tokens_this_month = s.EmbeddingTokensThisMonth,
        };
    }

    private async Task<object> ListDocumentsAsync(IDictionary<string, object?> args, CancellationToken ct)
    {
        if (!TryGetGuid(args, "kb_id", out var kbId)) return BadKbId;

        var result = await _api.ListDocumentsAsync(kbId, ct);
        if (!result.IsOk) return Refusal(result);
        if (result.Value!.Count == 0) return "This knowledge base has no documents.";

        return new
        {
            documents = result.Value.Select(d => new
            {
                document_id = d.Id,
                title = d.Title,
                status = d.Status,
                content_type = d.ContentType,
                size_bytes = d.SizeBytes,
                error = d.Error,
                chunks = d.ChunkCount,
                retrievals = d.RetrievalCount,
                last_retrieved_at = Stamp(d.LastRetrievedAt),
                updated_at = Stamp(d.UpdatedAt),
            }).ToList(),
        };
    }

    private async Task<object> GetPromptAsync(IDictionary<string, object?> args, CancellationToken ct)
    {
        if (!TryGetGuid(args, "kb_id", out var kbId)) return BadKbId;

        var result = await _api.GetPromptAsync(kbId, ct);
        if (!result.IsOk) return Refusal(result);

        return new { kb_id = kbId, prompt = result.Value!.Prompt ?? string.Empty, note = PromptNote };
    }

    private async Task<object> ReadDocumentAsync(IDictionary<string, object?> args, CancellationToken ct)
    {
        if (!TryGetGuid(args, "kb_id", out var kbId)) return BadKbId;
        if (!TryGetGuid(args, "document_id", out var documentId)) return BadDocumentId;

        var result = await _api.GetContentAsync(kbId, documentId, ct);
        if (!result.IsOk) return Refusal(result, documentScoped: true);

        var (text, truncated) = TruncateUtf8(result.Value!.Content, KbManagerLimits.MaxInlineContentBytes);
        return new
        {
            kb_id = kbId,
            document_id = documentId,
            content_type = result.Value.ContentType,
            truncated,
            content = text,
            note = truncated ? TruncatedNote : null,
        };
    }

    private async Task<object> DownloadDocumentAsync(IDictionary<string, object?> args, CancellationToken ct)
    {
        if (!TryGetGuid(args, "kb_id", out var kbId)) return BadKbId;
        if (!TryGetGuid(args, "document_id", out var documentId)) return BadDocumentId;

        var root = _files.ResolveToolRoot();
        if (root is null) return NoFilesFolder;

        var document = await FindDocumentAsync(kbId, documentId, ct);
        if (document.Refusal is { } refusal) return refusal;

        var content = await _api.GetContentAsync(kbId, documentId, ct);
        if (!content.IsOk) return Refusal(content, documentScoped: true);

        if (!KbManagerLocalFiles.TrySaveNew(
                root, GetString(args, "path"), document.Value!.Title, content.Value!.ContentType,
                content.Value.Content, out var saved, out var error))
            return error;

        return new { path = saved, size_bytes = Encoding.UTF8.GetByteCount(content.Value.Content), note = DownloadNote };
    }

    private Task<(object?, KbManagerToolCall?)> PrepareUploadAsync(IDictionary<string, object?> args, CancellationToken ct)
        => Task.FromResult<(object?, KbManagerToolCall?)>(("Not implemented", null));

    private Task<(object?, KbManagerToolCall?)> PrepareUpdateAsync(IDictionary<string, object?> args, CancellationToken ct)
        => Task.FromResult<(object?, KbManagerToolCall?)>(("Not implemented", null));

    private Task<(object?, KbManagerToolCall?)> PrepareSetPromptAsync(IDictionary<string, object?> args, CancellationToken ct)
        => Task.FromResult<(object?, KbManagerToolCall?)>(("Not implemented", null));

    private Task<(object?, KbManagerToolCall?)> PrepareDeleteAsync(IDictionary<string, object?> args, CancellationToken ct)
        => Task.FromResult<(object?, KbManagerToolCall?)>(("Not implemented", null));

    private async Task<(KbManagerKnowledgeBase? Value, string? Refusal)> FindKnowledgeBaseAsync(
        Guid kbId, CancellationToken ct)
    {
        var result = await _api.ListKnowledgeBasesAsync(ct);
        if (!result.IsOk) return (null, Refusal(result));

        var kb = result.Value!.FirstOrDefault(k => k.Id == kbId);
        return kb is null ? (null, UnknownKb) : (kb, null);
    }

    private async Task<(KbManagerDocument? Value, string? Refusal)> FindDocumentAsync(
        Guid kbId, Guid documentId, CancellationToken ct)
    {
        var result = await _api.ListDocumentsAsync(kbId, ct);
        if (!result.IsOk) return (null, Refusal(result));

        var document = result.Value!.FirstOrDefault(d => d.Id == documentId);
        return document is null ? (null, UnknownDocument) : (document, null);
    }

    private string Refusal<T>(KbManagerResult<T> result, bool documentScoped = false)
    {
        switch (result.Status)
        {
            case KbManagerCallStatus.NotConnected:
                return NotConnected;
            case KbManagerCallStatus.Forbidden:
                _surface.Hide();
                return NoLongerManager;
            case KbManagerCallStatus.NotFound:
                return documentScoped ? UnknownDocument : UnknownKb;
            case KbManagerCallStatus.TooLarge:
                return "That content is over the 10 MB limit for one document, so nothing was sent.";
            case KbManagerCallStatus.Conflict:
                return ConflictSentence(result.Error);
            case KbManagerCallStatus.Invalid:
                return $"The server refused the request: {result.Error?.Message ?? result.Error?.Code ?? "invalid input"}.";
            default:
                return ServerUnavailable;
        }
    }

    private static string ConflictSentence(KbManagerError? error)
    {
        if (error?.Code == KbManagerErrorCodes.QuotaExceeded)
        {
            var limit = error.Limit?.ToString("N0", CultureInfo.InvariantCulture) ?? "?";
            var current = error.Current?.ToString("N0", CultureInfo.InvariantCulture) ?? "?";
            return $"Quota exceeded: {QuotaLabel(error.Resource)} limit is {limit}, and this would make {current}. Nothing was changed.";
        }

        if (error?.Code == KbManagerErrorCodes.DuplicateContent)
        {
            return error.DocumentId is Guid id
                ? $"That content is already in this knowledge base as document {id}. Nothing was changed."
                : "That content is already in this knowledge base. Nothing was changed.";
        }

        return $"The server refused the change: {error?.Message ?? error?.Code ?? "conflict"}.";
    }

    private static string QuotaLabel(string? resource) => resource switch
    {
        "KnowledgeDocuments" => "the document",
        "KnowledgeStorageBytes" => "the storage (bytes)",
        "MonthlyEmbeddingTokens" => "this month's indexing-token",
        _ => "a",
    };

    internal static (string Text, bool Truncated) TruncateUtf8(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return (text, false);

        var bytes = 0;
        var i = 0;
        while (i < text.Length)
        {
            var width = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(text.AsSpan(i, width));
            if (bytes + size > maxBytes) break;
            bytes += size;
            i += width;
        }

        return (text[..i], true);
    }

    private static string? Stamp(DateTime? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static bool TryGetGuid(IDictionary<string, object?> args, string key, out Guid value)
    {
        value = Guid.Empty;
        return GetString(args, key) is { } text && Guid.TryParse(text.Trim(), out value);
    }

    /// <summary>Keeps an empty string: <c>set_kb_prompt</c> clears the prompt with one.</summary>
    private static string? GetString(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => element.GetString(),
                _ => element.GetRawText(),
            };
        }

        return value.ToString();
    }

    // Schema methods: the parameter signature and [Description] attributes ARE the tool metadata for
    // AIFunctionFactory. The bodies are never invoked — dispatch is by name in HandleToolCallAsync.
    [Description("List the knowledge bases the user manages for their group on the Pia server, with document counts. 'shared' means other groups use it too.")]
    private static string ListKnowledgeBasesSchema() => "";

    [Description("Show one knowledge base's statistics: documents per status, storage and document limits, indexed chunks and tokens, how often it was searched, and this month's indexing tokens.")]
    private static string GetKnowledgeBaseStatsSchema(
        [Description("kb_id from list_knowledge_bases")] string kb_id) => "";

    [Description("List the documents in one knowledge base with their status, size, chunk count and how often they were retrieved.")]
    private static string ListKbDocumentsSchema(
        [Description("kb_id from list_knowledge_bases")] string kb_id) => "";

    [Description("Read the description prompt that tells the assistant when to search this knowledge base.")]
    private static string GetKbPromptSchema(
        [Description("kb_id from list_knowledge_bases")] string kb_id) => "";

    [Description("Read one document's stored text inline. Up to 32 KB is returned; a longer document is marked truncated — use download_kb_document for the whole text.")]
    private static string ReadKbDocumentSchema(
        [Description("kb_id from list_knowledge_bases")] string kb_id,
        [Description("document_id from list_kb_documents")] string document_id) => "";

    [Description("Save one document's whole text as a new file in the assistant files folder. Never overwrites: a taken name gets ' (1)'. Returns the saved path.")]
    private static string DownloadKbDocumentSchema(
        [Description("kb_id from list_knowledge_bases")] string kb_id,
        [Description("document_id from list_kb_documents")] string document_id,
        [Description("Optional folder or .txt/.md file path inside the files folder; defaults to its root with the document title as the name")] string? path = null) => "";

    [Description("Add a .txt or .md file from the assistant files folder to a knowledge base as a new document. The user confirms first. The content is stored unencrypted and becomes searchable for everyone whose group uses the knowledge base.")]
    private static string UploadKbDocumentSchema(
        [Description("kb_id from list_knowledge_bases")] string kb_id,
        [Description("Path of a .txt, .md or .markdown file inside the files folder, at most 10 MB")] string path,
        [Description("Optional document title; defaults to the file name")] string? title = null) => "";

    [Description("Replace the text of an existing document, keeping its id and usage history. Pass exactly one of path (a file in the files folder, up to 10 MB) or content (inline text, up to 32 KB). The user confirms first.")]
    private static string UpdateKbDocumentSchema(
        [Description("kb_id from list_knowledge_bases")] string kb_id,
        [Description("document_id from list_kb_documents")] string document_id,
        [Description("Path of a .txt, .md or .markdown file inside the files folder")] string? path = null,
        [Description("Inline replacement text, up to 32 KB")] string? content = null) => "";

    [Description("Change the description prompt that tells the assistant when to search this knowledge base, up to 8,000 characters; an empty string clears it. The user confirms first.")]
    private static string SetKbPromptSchema(
        [Description("kb_id from list_knowledge_bases")] string kb_id,
        [Description("The new description prompt")] string prompt) => "";

    [Description("Remove a document from a knowledge base for everyone who searches it. The user confirms first.")]
    private static string DeleteKbDocumentSchema(
        [Description("kb_id from list_knowledge_bases")] string kb_id,
        [Description("document_id from list_kb_documents")] string document_id) => "";
}
