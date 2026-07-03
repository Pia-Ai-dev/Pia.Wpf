using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class MemoryToolHandler : IMemoryToolHandler
{
    private readonly IMemoryService _memoryService;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<MemoryToolHandler> _logger;

    public MemoryToolHandler(
        IMemoryService memoryService,
        IEmbeddingService embeddingService,
        ILocalizationService localizationService,
        ILogger<MemoryToolHandler> logger)
    {
        _memoryService = memoryService;
        _embeddingService = embeddingService;
        _localizationService = localizationService;
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
                "Returns matching memory sections (file#heading + snippet + relevance score)."),

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

    private async Task<object?> HandleRecall(IDictionary<string, object?> args, CancellationToken cancellationToken)
    {
        var query = GetStringArg(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return "Error: query parameter is required";

        var hits = await _memoryService.RecallAsync(query);
        _logger.LogInformation("Recall returned {Count} hit(s)", hits.Count);
        return hits;
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
}
