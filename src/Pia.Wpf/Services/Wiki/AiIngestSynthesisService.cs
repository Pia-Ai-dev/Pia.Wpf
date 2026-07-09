using System.Text;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
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
    private readonly Func<ITokenMapService> _tokenMapFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<AiIngestSynthesisService> _logger;

    public AiIngestSynthesisService(
        IAiClientService aiClient,
        IProviderService providers,
        Func<ITokenMapService> tokenMapFactory,
        ISettingsService settings,
        ILogger<AiIngestSynthesisService> logger)
    {
        _aiClient = aiClient;
        _providers = providers;
        _tokenMapFactory = tokenMapFactory;
        _settings = settings;
        _logger = logger;
    }

    public async Task<SynthesizedPage> SynthesizeAsync(
        string title, string category, string charter,
        IReadOnlyList<(string Ref, string Text)> sources,
        IReadOnlyCollection<string> knownSlugs, CancellationToken ct = default)
    {
        var provider = await _providers.GetDefaultProviderAsync();
        if (provider is null)
        {
            _logger.SensitiveDebug("Ingest synthesis skipped for {Title}: no provider configured", title);
            return new SynthesizedPage(string.Empty, string.Empty);
        }

        var settings = await _settings.GetSettingsAsync();
        var tokenizationEnabled = settings.Privacy.TokenizationEnabled;

        var prompt =
            (string.IsNullOrWhiteSpace(charter) ? "" : "Knowledge base context:\n" + charter + "\n\n") +
            $"Write a concise wiki page for the topic \"{title}\" (category: {category}). Synthesize a SINGLE " +
            "coherent explanation across ALL the sources below — merge overlapping facts, reconcile them, and " +
            "note contradictions explicitly. If the sources name this topic by multiple aliases, abbreviations, " +
            "or expanded forms, treat them as the SAME entity and describe it once under its canonical name — " +
            "do NOT restate it as if it were several distinct things. Start with a one-sentence definition, then " +
            "short prose or bullets. " +
            BuildLinkInstruction(knownSlugs, tokenizationEnabled) +
            "Preserve any bracketed placeholder tokens (e.g. " +
            "[Person_1], [Email_2]) EXACTLY as written — never lowercase, translate, rephrase, or invent them. " +
            "Do NOT include a title heading or frontmatter. First output a line 'SUMMARY: <one sentence>' then a " +
            "blank line then the page body.\n\n" +
            string.Join("\n\n", sources.Select(s => $"--- SOURCE: {s.Ref} ---\n{Truncate(s.Text)}"));

        var result = await SendWithReidentificationAsync(provider, prompt, tokenizationEnabled, ct);
        var page = ParseSynthesis(result.Text);
        _logger.SensitiveDebug(
            "Ingest synthesized page for {Title} ({BodyLength} body chars)", title, page.Body.Length);
        return page;
    }

    // Ingest runs off any chat turn, so no TokenMapAmbient is set. Without one, the TokenizingAiClientService
    // decorator has no per-turn map to re-identify against, and any PII placeholder the model emits in its
    // REWRITTEN prose would be persisted to the topic page verbatim (a privacy leak of masked-then-unmasked
    // intent). We publish THIS run's map as the ambient turn map around the call (so the decorator tokenizes
    // the prompt / detokenizes the response against it), then run a mangle-tolerant re-identification pass on
    // the result to recover tokens the decorator's strict regex missed (the model may lowercase or re-punctuate
    // a token, e.g. [person-1], when weaving it into prose). No-op when tokenization is disabled.
    private async Task<AiCompletionResult> SendWithReidentificationAsync(
        AiProvider provider, string prompt, bool tokenizationEnabled, CancellationToken ct)
    {
        if (!tokenizationEnabled)
        {
            return await _aiClient.SendRequestAsync(provider, prompt, ct);
        }

        var tokenMap = _tokenMapFactory();
        try
        {
            await tokenMap.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize token map for ingest synthesis");
        }

        var previousAmbient = TokenMapAmbient.Current;
        TokenMapAmbient.Current = tokenMap;
        AiCompletionResult result;
        try
        {
            result = await _aiClient.SendRequestAsync(provider, prompt, ct);
        }
        finally
        {
            TokenMapAmbient.Current = previousAmbient;
        }

        return result with { Text = tokenMap.DetokenizeLoose(result.Text) };
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

    // Grounded link instruction: the model may link ONLY to topic pages that exist (or will by the end of
    // this run), using the exact slug. This curbs invented dead links at generation time; WikiLinkReconciler
    // is the deterministic backstop. With no known slugs there is nothing to link, so forbid links outright.
    //
    // Privacy: the slug list is derived from page titles (often person names) in a hyphenated form the PII
    // tokenizer CANNOT mask — it matches whole values ("Aylin Demir"), not slugs ("aylin-demir") — and it
    // spans the WHOLE vault, not just this source, so embedding it would ship a roster-wide cleartext leak
    // past tokenization. When tokenization is on we therefore withhold the explicit list and fall back to a
    // generic instruction; WikiLinkReconciler still guarantees zero dead links either way.
    private static string BuildLinkInstruction(IReadOnlyCollection<string> knownSlugs, bool tokenizationEnabled)
    {
        if (knownSlugs.Count == 0)
        {
            return "Do NOT output any [[...]] wiki-links — no linkable topic pages exist. ";
        }

        if (tokenizationEnabled)
        {
            return "Link related topics inline using [[topics/<slug>]], where <slug> is the lowercase-hyphen " +
                "form of the topic name, but ONLY for topics that plausibly have their own page. ";
        }

        return "Link related topics inline ONLY when the topic's slug appears in the list below, using that " +
            "EXACT slug: [[topics/<slug>]] (optionally [[topics/<slug>|display text]]). NEVER invent a link " +
            "to a topic whose slug is not in the list. Known topic slugs: " +
            string.Join(", ", knownSlugs) + ". ";
    }

    private static string Truncate(string content) =>
        content.Length <= MaxSourceChars ? content : content[..MaxSourceChars];
}
