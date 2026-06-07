using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.Wiki;

/// <summary>
/// Production <see cref="IIngestExtractor"/>: drives summarize / extract via
/// <see cref="IAiClientService.SendRequestAsync"/> and parses the model output defensively into
/// <see cref="ExtractedEntity"/> records.
///
/// <para><b>Provider selection.</b> There is no dedicated "ingest provider" setting; we use
/// <see cref="IProviderService.GetDefaultProviderAsync"/>, which returns the configured default provider
/// or — when none is explicitly defaulted — the first configured provider. This is a deliberate
/// simplification for Task 7.1 (a richer per-feature provider override is out of scope). When no provider
/// is configured at all, extraction degrades gracefully: the summary is empty and no entities are
/// returned (ingest then no-ops rather than throwing).</para>
///
/// <para><b>Parsing.</b> The extract prompt asks for a small JSON array of <c>{subject, facts}</c>
/// objects; we parse that first and fall back to a line-oriented <c>Subject: facts</c> format so a model
/// that ignores the JSON instruction still yields entities. All parsing is best-effort and never throws
/// on malformed output — a parse failure yields an empty entity list.</para>
/// </summary>
public sealed class AiIngestExtractionService : IIngestExtractor
{
    private const int MaxSourceChars = 12000;

    private readonly IAiClientService _aiClient;
    private readonly IProviderService _providers;
    private readonly ILogger<AiIngestExtractionService> _logger;

    public AiIngestExtractionService(
        IAiClientService aiClient,
        IProviderService providers,
        ILogger<AiIngestExtractionService> logger)
    {
        _aiClient = aiClient;
        _providers = providers;
        _logger = logger;
    }

    public async Task<string> SummarizeAsync(string content, CancellationToken ct = default)
    {
        var provider = await _providers.GetDefaultProviderAsync();
        if (provider is null)
        {
            _logger.SensitiveDebug("Ingest summarize skipped: no provider configured");
            return string.Empty;
        }

        var prompt =
            "Summarize the following document in one short paragraph. Respond with the summary text only.\n\n" +
            Truncate(content);

        var result = await _aiClient.SendRequestAsync(provider, prompt, ct);
        var summary = result.Text.Trim();
        _logger.SensitiveDebug("Ingest produced summary: {Summary}", summary);
        return summary;
    }

    public async Task<IReadOnlyList<ExtractedEntity>> ExtractEntitiesAsync(string content, CancellationToken ct = default)
    {
        var provider = await _providers.GetDefaultProviderAsync();
        if (provider is null)
        {
            _logger.SensitiveDebug("Ingest extract skipped: no provider configured");
            return [];
        }

        var prompt =
            "Extract the salient entities (people, organizations, concepts) from the document below. " +
            "Respond with a JSON array of objects, each with a \"subject\" (the entity name) and a " +
            "\"facts\" string (a few '- key: value' bullet lines about it). Respond with JSON only.\n\n" +
            Truncate(content);

        var result = await _aiClient.SendRequestAsync(provider, prompt, ct);
        var entities = ParseEntities(result.Text);
        _logger.SensitiveDebug("Ingest extracted {Count} entities from model output", entities.Count);
        return entities;
    }

    // Defensive parse: JSON array of {subject, facts} first; fall back to "Subject: facts" lines.
    internal static IReadOnlyList<ExtractedEntity> ParseEntities(string modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
        {
            return [];
        }

        var json = ExtractJsonArray(modelOutput);
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<ExtractedEntity>();
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var subject = ReadString(el, "subject");
                        var facts = ReadString(el, "facts");
                        if (!string.IsNullOrWhiteSpace(subject))
                        {
                            list.Add(new ExtractedEntity(subject.Trim(), facts.Trim()));
                        }
                    }

                    if (list.Count > 0)
                    {
                        return list;
                    }
                }
            }
            catch (JsonException)
            {
                // Fall through to the line-oriented parser below.
            }
        }

        return ParseLines(modelOutput);
    }

    private static string ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    // Grab the first balanced [...] span so prose around the JSON ("Here is the JSON: [...]") is tolerated.
    private static string? ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    // Fallback: each "Subject: facts" line becomes an entity; bare lines become subjects with no facts.
    private static IReadOnlyList<ExtractedEntity> ParseLines(string text)
    {
        var list = new List<ExtractedEntity>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimStart('-', '*', ' ', '\t').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                var subject = line[..colon].Trim();
                var facts = line[(colon + 1)..].Trim();
                if (subject.Length > 0)
                {
                    list.Add(new ExtractedEntity(subject, facts));
                }
            }
            else
            {
                list.Add(new ExtractedEntity(line, string.Empty));
            }
        }

        return list;
    }

    private static string Truncate(string content) =>
        content.Length <= MaxSourceChars ? content : content[..MaxSourceChars];
}
