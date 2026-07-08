namespace Pia.Services.Interfaces;

/// <summary>One notable topic discovered in a source: page title + a coarse category
/// (person/organization/product/concept/regulation/technology/other) used only for index grouping.
/// A DTO record — lives in <c>Pia.Services.Interfaces</c> so it is excluded from the
/// naming-convention rule.</summary>
public record ExtractedTopic(string Subject, string Category);

/// <summary>
/// The model-backed discovery step of the ingest pipeline, abstracted so ingest is testable without an
/// API key (stub this with fixed topics in tests). The production implementation
/// (<c>AiIngestExtractionService</c>) calls <see cref="IAiClientService.SendRequestAsync"/> with a
/// charter-grounded notability prompt and parses the result defensively. Page BODY content is no longer
/// extracted here — the <see cref="IIngestSynthesizer"/> re-reads the raw sources to write it.
/// </summary>
public interface IIngestExtractor
{
    /// <summary>Discover the notable topics in <paramref name="content"/>, grounded in
    /// <paramref name="charter"/> (may be empty). Returns [] when nothing is notable.</summary>
    Task<IReadOnlyList<ExtractedTopic>> DiscoverTopicsAsync(
        string content, string charter, CancellationToken ct = default);
}
