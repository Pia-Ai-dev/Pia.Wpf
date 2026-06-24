namespace Pia.Models.Flow;

/// <summary>
/// A single "needs your attention" item in Flow (design §5). Carries a severity, a source, a
/// relative timestamp, an optional typed deep-link <see cref="Action"/>, and a <see cref="Lifetime"/>.
/// </summary>
public sealed class FlowItem
{
    public required Guid Id { get; init; }

    /// <summary>Stamped at publish; bumped to "now" when a dedup re-publish updates the item in place (newer state wins).</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    public required FlowSeverity Severity { get; set; }

    public required FlowSource Source { get; init; }

    public required string Title { get; set; }

    public required string Body { get; set; }

    /// <summary>Dedup key (chatId / todoId / reminderId / jobId). Null for snackbar/in-app items (exempt from dedup).</summary>
    public string? DedupKey { get; init; }

    public required FlowLifetime Lifetime { get; set; }

    public bool IsRead { get; set; }

    public FlowAction? Action { get; set; }

    /// <summary>
    /// Whether this item is written to SQLite and reloaded on restart. The invariant (enforced by
    /// FlowService) is: Durable ⇒ Lifetime.IsPersistent AND DedupKey != null AND Action is null or re-derivable.
    /// </summary>
    public bool Durable { get; set; }
}
