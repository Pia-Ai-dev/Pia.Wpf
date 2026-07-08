namespace Pia.Services.Interfaces;

/// <summary>
/// One entity (a wiki/topic page subject) extracted from a source. <see cref="Subject"/> becomes the
/// topic-page title (and its slug the filename); <see cref="Facts"/> is the body content, conventionally
/// a set of <c>- key: value</c> bullet lines (the same shape <see cref="IMemoryService.RememberAsync"/>
/// expects). A DTO record — lives in <c>Pia.Services.Interfaces</c> so it is excluded from the
/// naming-convention rule.
/// </summary>
public record ExtractedEntity(string Subject, string Facts);

/// <summary>One notable topic discovered in a source: page title + a coarse category
/// (person/organization/product/concept/regulation/technology/other) used only for index grouping.</summary>
public record ExtractedTopic(string Subject, string Category);

/// <summary>
/// The model-backed extraction step of the ingest pipeline (Task 7.1), abstracted so ingest is testable
/// without an API key (stub this with fixed entities in tests). The production implementation
/// (<c>AiIngestExtractionService</c>) calls <see cref="IAiClientService.SendRequestAsync"/> with
/// summarize / extract prompts and parses the result defensively.
/// </summary>
public interface IIngestExtractor
{
    /// <summary>Produce a short, one-paragraph summary of the raw source <paramref name="content"/>.</summary>
    Task<string> SummarizeAsync(string content, CancellationToken ct = default);

    /// <summary>
    /// Extract the salient entities (people, organizations, concepts) from the raw source
    /// <paramref name="content"/> as <see cref="ExtractedEntity"/> records. Returns an empty list when
    /// nothing is extractable (never <c>null</c>).
    /// </summary>
    Task<IReadOnlyList<ExtractedEntity>> ExtractEntitiesAsync(string content, CancellationToken ct = default);

    /// <summary>Discover the notable topics in <paramref name="content"/>, grounded in
    /// <paramref name="charter"/> (may be empty). Returns [] when nothing is notable.</summary>
    Task<IReadOnlyList<ExtractedTopic>> DiscoverTopicsAsync(
        string content, string charter, CancellationToken ct = default);
}
