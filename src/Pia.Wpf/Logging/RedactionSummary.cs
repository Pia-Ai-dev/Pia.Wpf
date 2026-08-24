namespace Pia.Logging;

/// <summary>
/// Deterministic rules substitute a value read from this machine; best-effort rules match a shape and will
/// lose to input built to defeat them. The split is public because the export has to state which one a
/// given line went through.
/// </summary>
public enum RedactionTier
{
    Deterministic,
    BestEffort,
}

public sealed record RedactionRuleDescriptor(string Id, RedactionTier Tier, string Covers);

/// <summary>Counts every rule, including the ones that never fired — a zero is evidence, not absence.</summary>
public sealed record RedactionSummary(
    long LinesRead,
    long LinesWritten,
    long RecordsDropped,
    IReadOnlyDictionary<string, long> HitsByRuleId);
