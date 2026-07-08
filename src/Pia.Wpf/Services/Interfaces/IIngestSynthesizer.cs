namespace Pia.Services.Interfaces;

/// <summary>A synthesized topic page: the managed markdown body (prose + [[topics/slug]] links)
/// and a one-line summary for the index.</summary>
public record SynthesizedPage(string Body, string Summary);

/// <summary>
/// The model-backed synthesis step of the ingest pipeline, abstracted so ingest is testable without an
/// API key (stub this with a deterministic body in tests). The production implementation
/// (<c>AiIngestSynthesisService</c>) re-reads the union of a topic's raw sources and writes the page body
/// as prose + <c>[[topics/slug]]</c> links via <see cref="IAiClientService.SendRequestAsync"/>. When no
/// provider is configured, or the model returns nothing, synthesis degrades gracefully to an empty page
/// (the caller then skips writing).
/// </summary>
public interface IIngestSynthesizer
{
    /// <summary>Write the topic page body for <paramref name="title"/> by synthesizing across ALL
    /// <paramref name="sources"/> (each a (ref, rawText) pair). Empty body ⇒ caller skips the page.</summary>
    Task<SynthesizedPage> SynthesizeAsync(
        string title, string category, string charter,
        IReadOnlyList<(string Ref, string Text)> sources, CancellationToken ct = default);
}
