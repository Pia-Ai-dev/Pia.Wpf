namespace Pia.Models;

/// <summary>
/// The token budget a compaction pass works against, read off an <see cref="AiProvider"/>.
/// <para>
/// This type deliberately references no Microsoft.Agents.AI.Compaction type — the entire
/// experimental (MAAI001) surface stays inside AgentContextCompactor.cs, so this DTO can travel
/// freely through Pia signatures without dragging a suppression along.
/// </para>
/// </summary>
/// <param name="WindowTokens">The model's total context window, in tokens.</param>
/// <param name="MaxOutputTokens">The most output tokens the model can generate per response.</param>
public readonly record struct AgentContextBudget(int WindowTokens, int MaxOutputTokens)
{
    /// <summary>
    /// Reads the budget off a provider, or returns <see langword="null"/> when it has none configured.
    /// <para>
    /// A pure reader on purpose. The policy that an unconfigured provider still gets a window lives in
    /// <c>ProviderService</c>, which stamps <see cref="ContextWindowDefaults"/> as providers are loaded —
    /// so the editor shows the value rather than compaction running against a number nobody can see.
    /// Defaulting here instead gave every bare <see cref="AiProvider"/> in the process a budget, including
    /// stubs that never came from persistence.
    /// </para>
    /// <para>
    /// The validity conditions mirror the ones the compaction strategy's constructor enforces by
    /// throwing (positive window, non-negative output, output strictly below the window). Checking
    /// them here as data means the ordinary "not configured" and "typo'd" cases never reach a
    /// throwing constructor at all; the try/catch in AgentContextCompactor.CompactAsync is the
    /// second line of defence for a budget constructed directly.
    /// </para>
    /// </summary>
    public static AgentContextBudget? From(AiProvider? provider)
    {
        if (provider?.MaxContextWindowTokens is not > 0)
            return null;

        var window = provider.MaxContextWindowTokens.Value;
        var maxOutput = provider.MaxOutputTokens ?? 0;

        if (maxOutput < 0 || maxOutput >= window)
            return null;

        return new AgentContextBudget(window, maxOutput);
    }
}
