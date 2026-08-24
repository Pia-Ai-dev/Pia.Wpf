namespace Pia.Models;

/// <summary>
/// The context window assumed for a provider that has none configured. No provider API Pia talks to reports
/// a window, and <see cref="AiProvider.MaxContextWindowTokens"/> is hand-typed, so without this compaction
/// never runs for anyone and an over-window chat fails provider-side instead.
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

    public static int For(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return Fallback;

        foreach (var (fragment, window) in KnownModels)
            if (modelName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return window;

        return Fallback;
    }
}
