namespace Pia.Models;

/// <summary>
/// The context window assumed for a provider that has none configured — without it compaction never runs
/// and an over-window chat fails provider-side instead.
/// <para>
/// <b>OpenRouter is the exception and reports one.</b> Its <c>/models</c> endpoint carries
/// <c>top_provider.context_length</c>, so those providers are resolved from
/// <see cref="OpenRouterContextWindows"/> here and refreshed live by <c>ProviderService</c> on save. Every
/// other provider type Pia talks to reports nothing, which is why the rest of this file is a guess with a
/// floor rather than a lookup.
/// </para>
/// </summary>
public static class ContextWindowDefaults
{
    /// <summary>Assumed for a model no row below matches. Deliberately generous: guessing low compacts a chat
    /// that did not need it, and compaction keeps no summary of what it drops.</summary>
    public const int Fallback = 128_000;

    /// <summary>
    /// Matched as a case-insensitive substring, so a dated or namespaced id resolves too
    /// (<c>anthropic/claude-opus-5</c>, <c>claude-haiku-4-5-20251001</c>).
    /// <para>
    /// Add a row only with a source. A wrong value here is worse than no row: too high sends an oversized
    /// request the provider rejects, too low silently evicts context. Everything unsourced takes
    /// <see cref="Fallback"/>, which is a floor for current models rather than a guess about any one of them.
    /// </para>
    /// </summary>
    private static readonly (string Fragment, int Window)[] KnownModels =
    [
        ("claude-fable-5", 1_000_000),
        ("claude-mythos-5", 1_000_000),
        ("claude-opus-5", 1_000_000),
        ("claude-opus-4-8", 1_000_000),
        ("claude-opus-4-7", 1_000_000),
        ("claude-opus-4-6", 1_000_000),
        ("claude-sonnet-5", 1_000_000),
        ("claude-sonnet-4-6", 1_000_000),
        ("claude-haiku-4-5", 200_000),
    ];

    /// <param name="providerType">OpenRouter routes to its own snapshot first: the window its route serves
    /// can be far below what the model advertises, and the family table below carries the advertised figure.</param>
    public static int For(AiProviderType providerType, string? modelName)
    {
        if (providerType == AiProviderType.OpenRouter && OpenRouterContextWindows.TryGet(modelName, out var routed))
            return routed;

        if (string.IsNullOrWhiteSpace(modelName))
            return Fallback;

        foreach (var (fragment, window) in KnownModels)
            if (modelName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return window;

        return Fallback;
    }
}
