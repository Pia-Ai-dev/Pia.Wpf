using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent.Biometric;

/// <summary>
/// Sweeps the biometric store on app start (and on demand) and removes any entry
/// whose <see cref="BiometricConsentEntry.ExpiresAt"/> is in the past. Each removal
/// emits a <c>BIOMETRIC_ENTRY_EXPIRED</c> audit event (spec §4.6 retention policy).
/// </summary>
public sealed class BiometricRetentionWorker
{
    private readonly IBiometricConsentStore _store;
    private readonly IConsentAuditLog _auditLog;
    private readonly TimeProvider _clock;
    private readonly ILogger<BiometricRetentionWorker> _logger;

    public BiometricRetentionWorker(
        IBiometricConsentStore store,
        IConsentAuditLog auditLog,
        TimeProvider clock,
        ILogger<BiometricRetentionWorker> logger)
    {
        _store = store;
        _auditLog = auditLog;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var entries = await _store.GetAllAsync(ct).ConfigureAwait(false);
        int removed = 0;
        foreach (var entry in entries)
        {
            if (entry.ExpiresAt > now) continue;
            ct.ThrowIfCancellationRequested();
            if (await _store.RemoveAsync(entry.Id, ct).ConfigureAwait(false))
            {
                removed++;
                _auditLog.Append(new AuditEvent(
                    Guid.NewGuid(), now, "BIOMETRIC_ENTRY_EXPIRED", null,
                    new Dictionary<string, object?>
                    {
                        ["entryId"] = entry.Id,
                        ["grantedAt"] = entry.GrantedAt,
                        ["expiredAt"] = entry.ExpiresAt,
                    }));
                _logger.LogInformation("Biometric entry {Id} expired and removed", entry.Id);
            }
        }
        return removed;
    }
}
