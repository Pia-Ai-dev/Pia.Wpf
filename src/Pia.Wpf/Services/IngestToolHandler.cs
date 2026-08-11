using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Assistant tool surface for the ingest pipeline (Task 7.1). Exposes a single <c>ingest(source_ref)</c>
/// tool that compiles a RAW source under <c>sources/</c> into <c>memory/topics/</c> wiki pages by
/// dispatching to <see cref="IIngestScheduler.RunAsync"/> — the same serial queue the auto-ingest
/// watcher uses, so a manual run can never race an automatic one. Ingest runs inline — there is no
/// pending-action / confirmation card — so <see cref="HandleToolCallAsync"/> performs the work and
/// returns a human-readable result string.
/// </summary>
public class IngestToolHandler : IIngestToolHandler
{
    private readonly IIngestScheduler _scheduler;
    private readonly ILogger<IngestToolHandler> _logger;

    public IngestToolHandler(IIngestScheduler scheduler, ILogger<IngestToolHandler> logger)
    {
        _scheduler = scheduler;
        _logger = logger;
    }

    public IList<AITool> GetTools()
    {
        return
        [
            AIFunctionFactory.Create(IngestSchema, "ingest",
                "Compile a raw document from the user's vault 'sources/' folder into the memory wiki. " +
                "Provide the vault-relative source path (e.g. 'sources/q2-report.txt'). Extracts the key " +
                "entities and writes one topic page per entity under memory/topics/, updating the index " +
                "and log. Re-ingesting the same source does not create duplicates."),
        ];
    }

    public async Task<object?> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("IngestToolHandler dispatching: {ToolName}", toolCall.Name);
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        if (toolCall.Name != "ingest")
        {
            return $"Unknown tool: {toolCall.Name}";
        }

        var sourceRef = GetStringArg(args, "source_ref");
        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            return "Error: source_ref parameter is required";
        }

        sourceRef = VaultReference.NormalizePath(sourceRef);

        var result = await _scheduler.RunAsync(sourceRef, cancellationToken);
        _logger.SensitiveDebug("Ingest tool compiled {Source} into {Count} page(s)",
            result.SourceRef, result.TouchedPages.Count);

        return result.Outcome switch
        {
            IngestOutcome.SourceNotFound =>
                $"Error: source '{sourceRef}' was not found. Raw files must be inside the vault's sources/ folder. " +
                "To stage a new file, call create_source(reference, content) — it ingests automatically, so no " +
                "separate ingest call is needed afterward.",
            IngestOutcome.NonTextSkipped =>
                $"Skipped: '{sourceRef}' is not a text file. Only text sources (e.g. txt, md, csv, json, html, xml, log) can be ingested.",
            IngestOutcome.EmptySource =>
                $"Skipped: '{sourceRef}' is empty — nothing to ingest.",
            IngestOutcome.NoEntities =>
                $"Ingest ran on '{sourceRef}' but extracted no entities, so no memory pages were written.",
            _ =>
                $"Ingested '{sourceRef}' into {result.TouchedPages.Count} memory page(s): " +
                $"{string.Join(", ", result.TouchedPages)}. The content is now available via recall.",
        };
    }

    [Description("Compile a raw vault source into the memory wiki (one topic page per entity)")]
    private static string IngestSchema(
        [Description("Vault-relative path of the source to ingest. Must be under the sources/ folder, e.g. 'sources/q2-report.txt'; paths elsewhere in the vault (memory/, notes/, …) are refused.")] string source_ref) => "";

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
