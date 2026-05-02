using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class ResearchHistoryToolHandler : IResearchHistoryToolHandler
{
    private readonly IResearchHistoryService _history;
    private readonly IEmbeddingService _embedding;
    private readonly ILogger<ResearchHistoryToolHandler> _logger;

    public ResearchHistoryToolHandler(
        IResearchHistoryService history,
        IEmbeddingService embedding,
        ILogger<ResearchHistoryToolHandler> logger)
    {
        _history = history;
        _embedding = embedding;
        _logger = logger;
    }

    public IList<AITool> GetTools()
    {
        return
        [
            AIFunctionFactory.Create(SearchSchema, "search_research_history",
                "Search past research findings (both ad-hoc and from scheduled jobs). Hybrid text + vector search. Returns up to topK matches with previews. The scheduledJobId argument is currently informational only and is not used to filter results."),

            AIFunctionFactory.Create(GetSchema, "get_research_entry",
                "Get the full text of a research history entry by ID.")
        ];
    }

    public async Task<(object? Result, ResearchHistoryToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("ResearchHistoryToolHandler dispatching: {ToolName}", toolCall.Name);
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        return toolCall.Name switch
        {
            "search_research_history" => (await HandleSearch(args, cancellationToken), (ResearchHistoryToolCall?)null),
            "get_research_entry" => (await HandleGet(args), (ResearchHistoryToolCall?)null),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", (ResearchHistoryToolCall?)null)
        };
    }

    public Task<object?> ExecutePendingActionAsync(ResearchHistoryToolCall pendingAction) =>
        pendingAction.Execute();

    private async Task<object?> HandleSearch(IDictionary<string, object?> args, CancellationToken cancellationToken)
    {
        var query = GetStringArg(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return "Provide a search query.";

        var topK = GetIntArg(args, "topK") ?? 5;

        float[]? embedding = null;
        try
        {
            if (await _embedding.EnsureAvailableAsync(cancellationToken: cancellationToken))
                embedding = await _embedding.GenerateEmbeddingAsync(query, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding generation failed for research history search; falling back to text-only search");
        }

        var hits = await _history.HybridSearchAsync(query, embedding, topK);
        if (hits.Count == 0)
            return "No matching research entries.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {hits.Count} entry/entries:");
        foreach (var e in hits)
        {
            var scheduledMarker = e.ScheduledJobId.HasValue ? " (scheduled)" : string.Empty;
            sb.AppendLine($"\n[ID: {e.Id}] {e.CreatedAt:g}{scheduledMarker}");
            sb.AppendLine($"  Query: {e.QueryPreview}");
            sb.AppendLine($"  Result: {e.ResultPreview}");
        }
        return sb.ToString();
    }

    private async Task<object?> HandleGet(IDictionary<string, object?> args)
    {
        var idStr = GetStringArg(args, "id");
        if (!Guid.TryParse(idStr, out var id))
        {
            _logger.LogWarning("get_research_entry called with invalid ID");
            return $"Error: invalid GUID '{idStr}'";
        }

        var entry = await _history.GetEntryAsync(id);
        if (entry is null)
            return $"Error: entry {id} not found.";

        return $"Query: {entry.Query}\n\nResult:\n{entry.SynthesizedResult}";
    }

    // Schema methods - signature only, used by AIFunctionFactory for reflection
    [Description("Search past research findings")]
    private static string SearchSchema(
        [Description("Search query (matched against past queries and results)")] string query,
        [Description("Optional ID of a scheduled job (currently informational only - results are not filtered)")] string? scheduledJobId = null,
        [Description("Optional top-K count (default 5)")] string? topK = null) => "";

    [Description("Get a research history entry by ID")]
    private static string GetSchema(
        [Description("The entry ID")] string id) => "";

    private static string GetStringArg(IDictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var value) && value is not null)
        {
            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Null) return string.Empty;
                return element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : element.GetRawText();
            }
            return value.ToString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static int? GetIntArg(IDictionary<string, object?> args, string key)
    {
        var s = GetStringArg(args, key);
        return int.TryParse(s, out var i) ? i : null;
    }
}
