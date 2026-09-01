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
/// the first balanced <c>[...]</c> span that actually parses wins, and a gated line-oriented fallback
/// (short, unpunctuated lines only) still yields topics from a model that ignores the JSON instruction.
/// All parsing is best-effort and never throws on malformed output — a parse failure yields an empty
/// topic list.</para>
/// </summary>
public sealed class AiIngestExtractionService : IIngestExtractor
{
    private const int MaxSourceChars = 12000;
    private const int MaxSubjectChars = 120;
    private const int MaxFallbackTopics = 20;
    private const int MaxFallbackSubjectChars = 60;
    private const int MaxFallbackSubjectWords = 6;

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

    // Every balanced [...] span, in order, so prose around the JSON ("Here is the JSON: [...]") is
    // tolerated. Deliberately NOT first-'[' to last-']': a PII placeholder the model echoes ahead of
    // the array ("[Person_1]") captured that whole span and made it unparseable, dropping the run into
    // the line fallback — which then wrote a raw JSON object into a page title. Bracket depth is
    // tracked outside string literals so a '[' inside a subject cannot end the span early.
    private static IEnumerable<string> EnumerateJsonArrays(string text)
    {
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '[':
                    if (depth++ == 0)
                    {
                        start = i;
                    }

                    break;
                case ']' when depth > 0 && --depth == 0:
                    yield return text[start..(i + 1)];
                    break;
            }
        }
    }

    // Defensive parse: the first [...] span that parses as a JSON array of {subject, category} wins;
    // else the gated line fallback. Never throws on malformed output.
    internal static IReadOnlyList<ExtractedTopic> ParseTopics(string modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
        {
            return [];
        }

        foreach (var json in EnumerateJsonArrays(modelOutput))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                continue; // not the array — try the next balanced span
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var list = new List<ExtractedTopic>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var subject = ReadString(el, "subject");
                    if (!IsPlausibleSubject(subject))
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

        return ParseTopicLines(modelOutput);
    }

    // A subject becomes a filename and a frontmatter title, so anything structural is a parse
    // artifact rather than a topic — a raw '{"subject": …},' line once reached disk as a title.
    private static bool IsPlausibleSubject(string subject)
    {
        var trimmed = subject.Trim();
        return trimmed.Length > 0
            && trimmed.Length <= MaxSubjectChars
            && trimmed[0] is not ('{' or '[' or '<');
    }

    // Fallback for a model that ignored the JSON instruction: each line becomes a topic. Gated,
    // because ungated it turned prose — or a pretty-printed JSON array's own lines — into one page
    // (and one synthesis call) per line. A topic name is short, unpunctuated and not a sentence.
    private static IReadOnlyList<ExtractedTopic> ParseTopicLines(string text)
    {
        var list = new List<ExtractedTopic>();
        foreach (var line in SplitNonEmptyLines(text))
        {
            if (list.Count == MaxFallbackTopics)
            {
                break;
            }

            if (LooksLikeTopicName(line))
            {
                list.Add(new ExtractedTopic(line, "concept"));
            }
        }

        return list;
    }

    private static bool LooksLikeTopicName(string line) =>
        IsPlausibleSubject(line)
        && line.Length <= MaxFallbackSubjectChars
        && line[^1] is not ('.' or ':' or '!' or '?' or ',' or ';')
        && line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= MaxFallbackSubjectWords;

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
