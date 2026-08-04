namespace Pia.Services.Consent;

/// <summary>
/// Append-only metadata audit trail for consent decisions. NEVER receives transcript text or an
/// extracted name — see <see cref="AuditEvent"/>.
/// </summary>
public interface IConsentAuditLog : IAsyncDisposable
{
    /// <summary>
    /// Fire-and-forget: queues the event for a background writer. Must never throw and never block the
    /// caller — the forward loop is the privacy boundary and cannot await disk I/O per utterance. An
    /// overflowing queue drops the event and logs that it did, because a silently missing audit line is
    /// worse than a noisy one.
    /// </summary>
    void Append(AuditEvent evt);
}
