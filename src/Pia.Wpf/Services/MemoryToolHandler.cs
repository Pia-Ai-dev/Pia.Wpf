using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class MemoryToolHandler : IMemoryToolHandler
{
    private readonly IMemoryService _memoryService;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILocalizationService _localizationService;
    private readonly IIngestScheduler _ingestScheduler;
    private readonly ILogger<MemoryToolHandler> _logger;

    public MemoryToolHandler(
        IMemoryService memoryService,
        IEmbeddingService embeddingService,
        ILocalizationService localizationService,
        IIngestScheduler ingestScheduler,
        ILogger<MemoryToolHandler> logger)
    {
        _memoryService = memoryService;
        _embeddingService = embeddingService;
        _localizationService = localizationService;
        _ingestScheduler = ingestScheduler;
        _logger = logger;
    }

    public IList<AITool> GetTools()
    {
        return
        [
            AIFunctionFactory.Create(RecallSchema, "recall",
                "Search the user's memory vault using a natural language query. " +
                "Use this to recall information when the user asks about something personal, " +
                "or to check whether a memory already exists before remembering something new. " +
                "Returns matching sections (each with a tier, file#heading, snippet, and relevance score). " +
                "Hits are only SUMMARIES — the vault is a browsable knowledge base: call read_topic for a " +
                "topic hit's full page and its cited sources, read_source for the primary text a topic cites, " +
                "and browse_index to see the whole map when a search misses."),

            AIFunctionFactory.Create(BrowseIndexSchema, "browse_index",
                "Orient in the memory vault: returns its category → topic/record map (titles plus a ref for " +
                "each), built from the vault's own index. Use it when recall misses or you need to see what " +
                "topics exist. Each entry's ref feeds read_topic."),

            AIFunctionFactory.Create(ReadTopicSchema, "read_topic",
                "Read a whole memory page. Given a ref from recall (a hit's FilePath) or browse_index " +
                "(e.g. 'memory/topics/foo.md'), returns the full page body plus the source documents it cites " +
                "and its outbound topic links. Use this after recall when a topic summary is not enough. The " +
                "returned source refs feed read_source."),

            AIFunctionFactory.Create(ReadSourceSchema, "read_source",
                "Read a raw primary source document (reached only via a topic's cited source refs from " +
                "read_topic). Returns the source text; for a large log or transcript, page through it with " +
                "offset/limit. Use this when a topic's summary is insufficient and you need the original wording. " +
                "To correct this document, call update_source."),

            AIFunctionFactory.Create(UpdateSourceSchema, "update_source",
                "Correct an EXISTING raw source document under sources/ in place (full replacement text, not a " +
                "patch). Get the reference the same way as read_source: a topic's cited source refs from " +
                "read_topic. Shows a diff for approval, then re-ingests so memory topic pages stay in sync. " +
                "To stage a brand-new source instead, call create_source."),

            AIFunctionFactory.Create(CreateSourceSchema, "create_source",
                "Stage a brand-new raw source document under sources/ (e.g. a document the user pasted, " +
                "meeting notes, a decision write-up worth keeping as a primary source — not a personal fact, " +
                "that's remember, and not an unrelated sandbox file, that's write_file). Provide a NEW " +
                "vault-relative path, e.g. 'sources/meeting-notes-2026-08-11.txt'; if it already exists, use " +
                "update_source instead. Shows a diff for approval, then ingests automatically — no separate " +
                "ingest call needed. Works no matter what folder the chat's working directory is scoped to."),

            AIFunctionFactory.Create(RememberSchema, "remember",
                "Store or update a memory in the user's vault. " +
                "Types: personal_profile (user facts), contact_list (people), preference (likes/settings), " +
                "note/project/topic (freeform knowledge). The subject is the record title (e.g. a person's name); " +
                "content is the body as '- key: value' bullet lines. Matching subjects are merged automatically " +
                "to avoid duplicates — recall first if you are unsure of the exact subject."),

            AIFunctionFactory.Create(ForgetSchema, "forget",
                "Remove a memory from the vault. Provide a 'path#heading' reference to remove a single record, " +
                "or a bare 'path' to delete the whole file. Use recall first to obtain the reference.")
        ];
    }

    public async Task<(object? Result, MemoryToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MemoryToolHandler dispatching: {ToolName}", toolCall.Name);
#if DEBUG
        Debug.WriteLine($"[MemoryToolHandler Args] {toolCall.Name}: {JsonSerializer.Serialize(toolCall.Arguments)}");
#endif
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        var (result, pending) = toolCall.Name switch
        {
            "recall" => (await HandleRecall(args, cancellationToken), (MemoryToolCall?)null),
            "browse_index" => (await HandleBrowseIndex(), (MemoryToolCall?)null),
            "read_topic" => (await HandleReadTopic(args), (MemoryToolCall?)null),
            "read_source" => (await HandleReadSource(args), (MemoryToolCall?)null),
            "update_source" => await HandleUpdateSource(args),
            "create_source" => await HandleCreateSource(args),
            "remember" => await HandleRemember(args),
            "forget" => ((object?)null, HandleForget(args)),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", (MemoryToolCall?)null)
        };

        _logger.LogDebug("MemoryToolHandler {ToolName} result: hasResult={HasResult}, hasPending={HasPending}",
            toolCall.Name, result is not null, pending is not null);
        return (result, pending);
    }

    public async Task<object?> ExecutePendingActionAsync(MemoryToolCall pendingAction)
    {
        _logger.LogDebug("Executing memory action: {ToolName}", pendingAction.ToolName);
        try
        {
            var result = await pendingAction.Execute();
            _logger.LogInformation("Memory action completed: {ToolName}", pendingAction.ToolName);
            // Embeddings are NOT regenerated here — the vault watcher/indexer owns reindex on file change.
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute memory tool action: {ToolName}", pendingAction.ToolName);
            return $"Error executing {pendingAction.ToolName}: {ex.Message}";
        }
    }

    // The recall Note is the standing, per-call nudge that turns recall from a terminal answer into the
    // entry point of an orient → read → drill loop. It ships in every recall result, so the model is
    // reminded — right where the hits are — that topic summaries are expandable.
    private const string RecallNote =
        "Hits with tier=topic are SUMMARIES from synthesized topic pages. For a topic's full page and the " +
        "sources it cites, call read_topic(reference) with that hit's FilePath. To read a cited primary " +
        "source, call read_source(reference). Call browse_index to see the whole map when a search misses.";

    private async Task<object?> HandleRecall(IDictionary<string, object?> args, CancellationToken cancellationToken)
    {
        var query = GetStringArg(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return "Error: query parameter is required";

        var hits = await _memoryService.RecallAsync(query);
        _logger.LogInformation("Recall returned {Count} hit(s)", hits.Count);
        _logger.SensitiveDebug("Recall query: {Query}", query);
        // Wrap here (never in RecallAsync — VaultViewModel consumes the service's list directly).
        return new RecallResult(hits, RecallNote);
    }

    private async Task<object?> HandleBrowseIndex()
    {
        var index = await _memoryService.BrowseIndexAsync();
        _logger.LogInformation("browse_index returned {Count} categor(y/ies)", index.Categories.Count);
        return index;
    }

    private async Task<object?> HandleReadTopic(IDictionary<string, object?> args)
    {
        var reference = GetStringArg(args, "reference");
        if (string.IsNullOrWhiteSpace(reference))
            return "Error: reference parameter is required";

        _logger.SensitiveDebug("read_topic reference: {Ref}", reference);
        return await _memoryService.ReadTopicAsync(reference);
    }

    private async Task<object?> HandleReadSource(IDictionary<string, object?> args)
    {
        var reference = GetStringArg(args, "reference");
        if (string.IsNullOrWhiteSpace(reference))
            return "Error: reference parameter is required";

        var offset = GetOptionalIntArg(args, "offset");
        var limit = GetOptionalIntArg(args, "limit");
        _logger.SensitiveDebug("read_source reference: {Ref}", reference);
        return await _memoryService.ReadSourceAsync(reference, offset, limit);
    }

    private async Task<(object? Result, MemoryToolCall? PendingAction)> HandleUpdateSource(
        IDictionary<string, object?> args)
    {
        var reference = GetStringArg(args, "reference");
        if (string.IsNullOrWhiteSpace(reference))
            return ("Error: reference parameter is required", null);

        if (!TryGetRequiredContentArg(args, out var content, out var contentError))
            return (contentError, null);

        _logger.SensitiveDebug("update_source reference: {Ref}", reference);

        // Resolution-only: run the same guard chain as read_source and capture the diff/TOCTOU
        // baseline WITHOUT writing. The committing write is the Execute lambda.
        var preview = await _memoryService.ResolveUpdateSourceAsync(reference);
        if (!preview.CanWrite)
            return (preview.Error, null);

        var rel = preview.Ref;
        var mtime = preview.Mtime;
        var diff = LineDiff.Compute(preview.OldContent, content);

        var pending = new MemoryToolCall(
            ToolName: "update_source",
            Description: _localizationService.Format("Tool_Memory_Desc_UpdateSource", rel),
            OldValue: null,
            NewValue: null,
            TargetObjectId: null,
            Execute: async () =>
            {
                var written = await _memoryService.UpdateSourceAsync(rel, content, mtime);
                if (!written.Success)
                    return written.Error;

                // Re-ingest synchronously (the same manual, always-runs entry point the ingest tool
                // itself uses) so the model gets a deterministic result with stale-page cleanup,
                // instead of depending on the watcher's async debounced pickup.
                var ingestResult = await _ingestScheduler.RunAsync(rel, CancellationToken.None);
                return ingestResult.Outcome switch
                {
                    IngestOutcome.NonTextSkipped =>
                        $"Updated '{rel}', but it is not recognized as a text source so it was not re-ingested.",
                    IngestOutcome.EmptySource =>
                        $"Updated '{rel}' to be empty. Any memory pages it previously contributed to were cleaned up.",
                    IngestOutcome.NoEntities =>
                        $"Updated '{rel}' and re-ingested it, but no entities were found. Any memory pages it " +
                        "previously contributed to were cleaned up.",
                    IngestOutcome.SourceNotFound =>
                        $"Updated '{rel}', but the re-ingest step could not find it afterward. " +
                        $"Try calling ingest(\"{rel}\") to retry.",
                    _ =>
                        $"Updated '{rel}' and re-ingested it into {ingestResult.TouchedPages.Count} memory " +
                        $"page(s): {string.Join(", ", ingestResult.TouchedPages)}. The correction is now " +
                        "reflected via recall.",
                };
            },
            DiffPreview: diff,
            TargetPath: rel);

        return (null, pending);
    }

    private async Task<(object? Result, MemoryToolCall? PendingAction)> HandleCreateSource(
        IDictionary<string, object?> args)
    {
        var reference = GetStringArg(args, "reference");
        if (string.IsNullOrWhiteSpace(reference))
            return ("Error: reference parameter is required", null);

        if (!TryGetRequiredContentArg(args, out var content, out var contentError))
            return (contentError, null);

        _logger.SensitiveDebug("create_source reference: {Ref}", reference);

        // Resolution-only: run the same scope guard as read_source, but require the ref NOT already
        // exist. The committing write is the Execute lambda.
        var preview = await _memoryService.ResolveCreateSourceAsync(reference);
        if (!preview.CanWrite)
            return (preview.Error, null);

        var rel = preview.Ref;
        var diff = LineDiff.Compute(null, content);

        var pending = new MemoryToolCall(
            ToolName: "create_source",
            Description: _localizationService.Format("Tool_Memory_Desc_CreateSource", rel),
            OldValue: null,
            NewValue: null,
            TargetObjectId: null,
            Execute: async () =>
            {
                var written = await _memoryService.CreateSourceAsync(rel, content);
                if (!written.Success)
                    return written.Error;

                // Re-ingest synchronously (the same manual, always-runs entry point the ingest tool
                // itself uses) so the model gets a deterministic result without a separate ingest call.
                var ingestResult = await _ingestScheduler.RunAsync(rel, CancellationToken.None);
                return ingestResult.Outcome switch
                {
                    IngestOutcome.NonTextSkipped =>
                        $"Created '{rel}', but it is not recognized as a text source so it was not ingested.",
                    IngestOutcome.EmptySource =>
                        $"Created '{rel}', but it is empty — nothing to ingest.",
                    IngestOutcome.NoEntities =>
                        $"Created '{rel}' and ingested it, but no entities were found, so no memory pages were written.",
                    IngestOutcome.SourceNotFound =>
                        $"Created '{rel}', but the ingest step could not find it afterward. " +
                        $"Try calling ingest(\"{rel}\") to retry.",
                    _ =>
                        $"Created '{rel}' and ingested it into {ingestResult.TouchedPages.Count} memory " +
                        $"page(s): {string.Join(", ", ingestResult.TouchedPages)}. The content is now " +
                        "available via recall.",
                };
            },
            DiffPreview: diff,
            TargetPath: rel);

        return (null, pending);
    }

    private async Task<(object? Result, MemoryToolCall? PendingAction)> HandleRemember(
        IDictionary<string, object?> args)
    {
        var type = GetStringArg(args, "type");
        var subject = GetStringArg(args, "subject");
        var content = GetStringArg(args, "content");

        // Resolution-only: classify the band WITHOUT writing. The committing write is the Execute lambda.
        var outcome = await _memoryService.ResolveRememberAsync(type, subject, content);

        if (outcome.Band == UpsertBand.Ambiguous)
        {
            // No write, no pending action: the model re-calls remember with a disambiguated subject.
            var sb = new StringBuilder();
            sb.AppendLine("Ambiguous: this could match an existing memory. Re-call remember with a more "
                + "specific subject, or use the exact heading of one of these candidates:");
            foreach (var candidate in outcome.Candidates)
                sb.AppendLine($"  - {candidate}");
            _logger.LogInformation("Remember was ambiguous across {Count} candidate(s)", outcome.Candidates.Count);
            return (sb.ToString(), null);
        }

        var pending = new MemoryToolCall(
            ToolName: "remember",
            Description: _localizationService.Format("Tool_Memory_Desc_Remember", subject),
            OldValue: outcome.Band == UpsertBand.Edit ? outcome.Reference : null,
            NewValue: content,
            TargetObjectId: null,
            Execute: async () =>
            {
                var written = await _memoryService.RememberAsync(type, subject, content);
                return _localizationService.Format("Tool_Memory_Exec_Remembered", written.Reference);
            });

        return (null, pending);
    }

    private MemoryToolCall HandleForget(IDictionary<string, object?> args)
    {
        var reference = GetStringArg(args, "reference");

        return new MemoryToolCall(
            ToolName: "forget",
            Description: _localizationService.Format("Tool_Memory_Desc_Forget", reference),
            OldValue: reference,
            NewValue: null,
            TargetObjectId: null,
            Execute: async () =>
            {
                await _memoryService.ForgetAsync(reference);
                return _localizationService.Format("Tool_Memory_Exec_Forgotten", reference);
            });
    }

    [Description("Search the user's memory vault using a natural language query")]
    private static string RecallSchema(
        [Description("Natural language query to search for in the memory vault")] string query) => "";

    [Description("Return the memory vault's category → topic/record map to orient")]
    private static string BrowseIndexSchema() => "";

    [Description("Read a whole memory page (full body + cited sources + outbound links) by reference")]
    private static string ReadTopicSchema(
        [Description("Vault-relative page ref from recall or browse_index, e.g. 'memory/topics/foo.md'")] string reference) => "";

    [Description("Read a raw primary source under sources/, reached via a topic's cited source refs")]
    private static string ReadSourceSchema(
        [Description("Vault-relative source ref, e.g. 'sources/meeting-notes.txt' (from a topic's cited sources)")] string reference,
        [Description("Optional 1-based line to start from, for paging through a large source")] int? offset = null,
        [Description("Optional max lines to return (default 500, max 2000)")] int? limit = null) => "";

    [Description("Correct an existing raw source under sources/ in place (full replacement text)")]
    private static string UpdateSourceSchema(
        [Description("Vault-relative source ref of an EXISTING source, e.g. 'sources/meeting-notes.txt' (from a topic's cited sources)")] string reference,
        [Description("The full corrected text of the source (replaces the entire file, not a partial patch)")] string content) => "";

    [Description("Stage a brand-new raw source under sources/")]
    private static string CreateSourceSchema(
        [Description("A NEW vault-relative source path, e.g. 'sources/meeting-notes-2026-08-11.txt'; must not already exist (use update_source for that)")] string reference,
        [Description("The full text of the new source document")] string content) => "";

    [Description("Store or update a memory in the vault; matching subjects are merged to avoid duplicates")]
    private static string RememberSchema(
        [Description("Memory type: personal_profile, contact_list, preference, note, project, topic")] string type,
        [Description("Record title (e.g. a person's name or topic). Matching subjects merge into one record.")] string subject,
        [Description("Body content as '- key: value' bullet lines")] string content) => "";

    [Description("Remove a memory from the vault by reference")]
    private static string ForgetSchema(
        [Description("A 'path#heading' reference to remove one record, or a bare 'path' to delete the whole file")] string reference) => "";

    private static string GetStringArg(IDictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var value))
        {
            if (value is JsonElement element)
                return element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : element.GetRawText();
            return value?.ToString() ?? string.Empty;
        }
        return string.Empty;
    }

    // Distinguishes a missing 'content' key from a present-but-empty one — GetStringArg returns ""
    // for both, which would silently wipe a source's content if the model simply forgot the argument.
    private static bool TryGetRequiredContentArg(
        IDictionary<string, object?> args, out string content, out string? error)
    {
        content = string.Empty;
        error = null;

        if (!args.TryGetValue("content", out var raw) || raw is null)
        {
            error = "Error: 'content' is missing. Re-emit the call with the full corrected text as a JSON string.";
            return false;
        }

        if (raw is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null)
            {
                error = "Error: 'content' is missing. Re-emit the call with the full corrected text as a JSON string.";
                return false;
            }
            content = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();
            return true;
        }

        content = raw.ToString() ?? string.Empty;
        return true;
    }

    private static int? GetOptionalIntArg(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n))
                return n;
            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var s))
                return s;
            return null;
        }

        if (value is int i)
            return i;

        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }
}
