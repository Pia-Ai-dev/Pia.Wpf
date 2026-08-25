using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services.Wiki;

/// <summary>
/// Production <see cref="IIngestExtractor"/>: drives charter-grounded notable-topic discovery via
/// <see cref="IAiClientService.SendRequestAsync"/> and parses the model output defensively into
/// <see cref="ExtractedTopic"/> records.
///
/// <para><b>Provider selection.</b> There is no dedicated "ingest provider" setting, so ingest follows the
/// Assistant mode's provider — the same model the user talks to. When none is configured, discovery
/// degrades gracefully: no topics are returned (ingest then no-ops rather than throwing).</para>
///
/// <para><b>Parsing.</b> The prompt asks for a small JSON array of <c>{subject, category}</c> objects;
/// we parse that first and fall back to a line-oriented format (each bare line → a topic with the default
/// <c>concept</c> category) so a model that ignores the JSON instruction still yields topics. All parsing
/// is best-effort and never throws on malformed output — a parse failure yields an empty topic list.</para>
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

    public async Task<IReadOnlyList<ExtractedTopic>> DiscoverTopicsAsync(
        string content, string charter, CancellationToken ct = default)
    {
        var provider = await _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant);
        if (provider is null)
        {
            _logger.SensitiveDebug("Ingest topic discovery skipped: no provider configured");
            return [];
        }

        var charterBlock = string.IsNullOrWhiteSpace(charter)
            ? ""
            : "This knowledge base is about:\n" + charter + "\n\n";

        var prompt =
            charterBlock +
            "List the NOTABLE topics in the document below that deserve their own wiki page — real people, " +
            "organizations, products, named concepts, technologies, or regulations that carry meaning for this " +
            "knowledge base. DO NOT include generic dictionary/legal-boilerplate terms (e.g. \"Use\", " +
            "\"Software\", \"Documentation\", \"Agreement\", \"Scope\"), generic verbs, or section labels. " +
            "Emit exactly ONE topic per real-world entity: merge aliases, abbreviations, and expanded forms of " +
            "the same thing into a single entry, and use its canonical common short name as the subject (e.g. " +
            "\"Pia\", not \"Pia (Personal Intelligent Assistant)\" and not a separate \"Personal Intelligent " +
            "Assistant\" entry). Do NOT put parenthetical aliases, expansions, or descriptions in the subject. " +
            "Respond with a JSON array of objects, each {\"subject\": name, \"category\": one of " +
            "person|organization|product|concept|regulation|technology|other}. JSON only.\n\n" +
            Truncate(content);

        var result = await _aiClient.SendRequestAsync(provider, prompt, ct, mode: nameof(WindowMode.Assistant));
        var topics = ParseTopics(result.Text);
        _logger.SensitiveDebug("Ingest discovered {Count} topics from model output", topics.Count);
        return topics;
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

    // Defensive parse: JSON array of {subject, category} first; fall back to bare "Subject" lines
    // (each whole line → a topic with the default "concept" category). Never throws on malformed output.
    internal static IReadOnlyList<ExtractedTopic> ParseTopics(string modelOutput)
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
                    var list = new List<ExtractedTopic>();
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var subject = ReadString(el, "subject");
                        if (string.IsNullOrWhiteSpace(subject))
                        {
                            continue;
                        }

                        var category = ReadString(el, "category");
                        if (string.IsNullOrWhiteSpace(category))
                        {
                            category = "concept";
                        }

                        list.Add(new ExtractedTopic(subject.Trim(), category.Trim()));
                    }

                    // A parsed array is authoritative even when empty ("[]" means
                    // "nothing notable"); only non-JSON output falls through.
                    return list;
                }
            }
            catch (JsonException)
            {
                // Fall through to the line-oriented parser below.
            }
        }

        return ParseTopicLines(modelOutput);
    }

    // Fallback: each non-empty line becomes a topic with the default "concept" category.
    private static IReadOnlyList<ExtractedTopic> ParseTopicLines(string text)
    {
        var list = new List<ExtractedTopic>();
        foreach (var line in SplitNonEmptyLines(text))
        {
            list.Add(new ExtractedTopic(line, "concept"));
        }

        return list;
    }

    // Normalizes line endings and yields trimmed, non-empty lines with leading bullet markers stripped.
    private static IEnumerable<string> SplitNonEmptyLines(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimStart('-', '*', ' ', '\t').Trim();
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }

    private static string Truncate(string content) =>
        content.Length <= MaxSourceChars ? content : content[..MaxSourceChars];
}
