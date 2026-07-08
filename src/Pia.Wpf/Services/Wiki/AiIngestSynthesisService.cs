using System.Text;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.Wiki;

/// <summary>
/// Production <see cref="IIngestSynthesizer"/>: re-reads the union of a topic's raw sources and asks the
/// default provider (via <see cref="IAiClientService.SendRequestAsync"/>) to write a single coherent wiki
/// page merged across all of them. Mirrors <see cref="AiIngestExtractionService"/>'s provider selection
/// (<see cref="IProviderService.GetDefaultProviderAsync"/>) and per-source truncation.
///
/// <para><b>Degradation.</b> When no provider is configured, or the model returns blank output, synthesis
/// yields an empty <see cref="SynthesizedPage"/> so the caller skips writing the page (no throw).</para>
///
/// <para><b>Parsing.</b> The model is asked to emit a leading <c>SUMMARY: &lt;one sentence&gt;</c> line
/// followed by a blank line then the body. <see cref="ParseSynthesis"/> splits that defensively: the first
/// <c>SUMMARY:</c> line becomes the index summary and the remainder the body; absent that marker the first
/// non-empty line is used as the summary and the whole text as the body. Never throws.</para>
/// </summary>
public sealed class AiIngestSynthesisService : IIngestSynthesizer
{
    private const int MaxSourceChars = 12000;

    private readonly IAiClientService _aiClient;
    private readonly IProviderService _providers;
    private readonly ILogger<AiIngestSynthesisService> _logger;

    public AiIngestSynthesisService(
        IAiClientService aiClient,
        IProviderService providers,
        ILogger<AiIngestSynthesisService> logger)
    {
        _aiClient = aiClient;
        _providers = providers;
        _logger = logger;
    }

    public async Task<SynthesizedPage> SynthesizeAsync(
        string title, string category, string charter,
        IReadOnlyList<(string Ref, string Text)> sources, CancellationToken ct = default)
    {
        var provider = await _providers.GetDefaultProviderAsync();
        if (provider is null)
        {
            _logger.SensitiveDebug("Ingest synthesis skipped for {Title}: no provider configured", title);
            return new SynthesizedPage(string.Empty, string.Empty);
        }

        var prompt =
            (string.IsNullOrWhiteSpace(charter) ? "" : "Knowledge base context:\n" + charter + "\n\n") +
            $"Write a concise wiki page for the topic \"{title}\" (category: {category}). Synthesize a SINGLE " +
            "coherent explanation across ALL the sources below — merge overlapping facts, reconcile them, and " +
            "note contradictions explicitly. Start with a one-sentence definition, then short prose or bullets. " +
            "Link related topics inline using [[topics/<slug>]] where <slug> is the lowercase-hyphen form of the " +
            "topic name. Do NOT include a title heading or frontmatter. First output a line " +
            "'SUMMARY: <one sentence>' then a blank line then the page body.\n\n" +
            string.Join("\n\n", sources.Select(s => $"--- SOURCE: {s.Ref} ---\n{Truncate(s.Text)}"));

        var result = await _aiClient.SendRequestAsync(provider, prompt, ct);
        var page = ParseSynthesis(result.Text);
        _logger.SensitiveDebug(
            "Ingest synthesized page for {Title} ({BodyLength} body chars)", title, page.Body.Length);
        return page;
    }

    // Split the model output into (Summary, Body). First "SUMMARY:" line → Summary, remainder → Body;
    // absent that marker, Summary = first non-empty line and Body = whole text. Never throws.
    internal static SynthesizedPage ParseSynthesis(string modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
        {
            return new SynthesizedPage(string.Empty, string.Empty);
        }

        var text = modelOutput.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var lines = text.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase))
            {
                var summary = trimmed["SUMMARY:".Length..].Trim();
                var body = string.Join("\n", lines.Skip(i + 1)).Trim();
                return new SynthesizedPage(body, summary);
            }
        }

        var firstNonEmpty = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? string.Empty;
        return new SynthesizedPage(text, firstNonEmpty);
    }

    private static string Truncate(string content) =>
        content.Length <= MaxSourceChars ? content : content[..MaxSourceChars];
}
