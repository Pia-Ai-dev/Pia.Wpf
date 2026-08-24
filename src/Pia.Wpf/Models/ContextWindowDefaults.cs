namespace Pia.Models;

/// <summary>
/// The context window assumed for a provider that has none configured — without it compaction never runs
/// and an over-window chat fails provider-side instead.
/// <para>
/// <b>The broad source is <see cref="OpenRouterContextWindows"/>, and it serves every provider type</b>, not
/// only OpenRouter: 422 models, reachable by full id (<c>anthropic/claude-opus-5</c>) and by the bare vendor
/// name a direct provider actually carries (<c>gpt-4o</c>, <c>o3-mini</c>, <c>deepseek-chat</c>). An
/// OpenRouter provider additionally re-reads its real value from the API whenever it is saved.
/// </para>
/// <para>
/// The table below is an override layer for what that catalogue cannot answer — not the main event. Read
/// <see cref="For"/> for the order.
/// </para>
/// </summary>
public static class ContextWindowDefaults
{
    /// <summary>Assumed for a model nothing resolves. Deliberately generous: guessing low compacts a chat
    /// that did not need it, and compaction keeps no summary of what it drops.</summary>
    public const int Fallback = 128_000;

    /// <summary>
    /// Vendor-documented windows for models the catalogue does not list, matched as a case-insensitive
    /// substring so a dated or namespaced id resolves too.
    /// <para>
    /// Deliberately almost empty, and a test holds it there. Eight Claude rows sat here until the catalogue
    /// was measured against them and agreed on all eight — a duplicate that later drifts is worse than no
    /// row, and a long table here reads as though the catalogue were not doing the work.
    /// </para>
    /// </summary>
    private static readonly (string Fragment, int Window)[] VendorDocumented =
    [
        // Project Glasswing — not offered through OpenRouter, so nothing else knows it.
        ("claude-mythos-5", 1_000_000),
    ];

    /// <summary>
    /// Three tiers, most authoritative first: a vendor-documented override, then the OpenRouter catalogue
    /// (which knows far more models but reports what ITS route serves), then the floor. Not gated on provider
    /// type — a model id is a model id, and the catalogue is the only broad source there is.
    /// </summary>
    public static int For(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return Fallback;

        foreach (var (fragment, window) in VendorDocumented)
            if (modelName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return window;

        // Anything the catalogue cannot resolve unambiguously falls through on purpose, so a conflicting
        // basename takes the generous floor rather than one of its candidates.
        return OpenRouterContextWindows.TryGet(modelName, out var known) ? known : Fallback;
    }
}
