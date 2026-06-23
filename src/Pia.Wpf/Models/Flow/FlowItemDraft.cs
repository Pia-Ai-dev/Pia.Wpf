namespace Pia.Models.Flow;

/// <summary>
/// The publish-time input adapters hand to <c>IFlowService.Publish</c>. The service stamps
/// <c>Id</c>/<c>CreatedAt</c>, derives <c>Durable</c>, and enforces the durability invariant (design §5, §6).
/// </summary>
public sealed class FlowItemDraft
{
    public required FlowSeverity Severity { get; init; }

    public required FlowSource Source { get; init; }

    public required string Title { get; init; }

    public string Body { get; init; } = string.Empty;

    /// <summary>Dedup key (entity id). Null for snackbar/in-app items — they are exempt from the one-live-item rule.</summary>
    public string? DedupKey { get; init; }

    public required FlowLifetime Lifetime { get; init; }

    public FlowAction? Action { get; init; }

    /// <summary>
    /// Adapter's request that the item be persisted across restart. The service honours it only when the
    /// durability invariant holds (persistent + entity-backed + re-derivable/no action); otherwise it is forced false.
    /// </summary>
    public bool RequestDurable { get; init; }
}
