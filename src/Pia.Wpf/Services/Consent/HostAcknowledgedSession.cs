using Microsoft.Extensions.Logging;
using Pia.Logging;

namespace Pia.Services.Consent;

/// <summary>A Teams session whose consent evidence is the host's acknowledgement, live from its start to <see cref="End"/>.</summary>
public sealed class HostAcknowledgedSession
{
    private readonly IConsentEvidenceStore _evidenceStore;
    private readonly IConsentLiveSessions _liveSessions;

    private HostAcknowledgedSession(
        ConsentSessionMarker marker,
        DateTimeOffset acknowledgedAt,
        IConsentEvidenceStore evidenceStore,
        IConsentLiveSessions liveSessions)
    {
        SessionId = marker.SessionId;
        Record = ConsentRecord.ForHostAcknowledgement(marker, acknowledgedAt);
        _evidenceStore = evidenceStore;
        _liveSessions = liveSessions;
    }

    public string SessionId { get; }

    /// <summary>What a saved transcript of the session carries in its front matter.</summary>
    public ConsentRecord Record { get; }

    /// <summary>Call once capture runs. A failed evidence write loses only the local copy: the saved note still carries the record.</summary>
    public static async Task<HostAcknowledgedSession> StartAsync(
        ConsentSessionMarker marker,
        DateTimeOffset acknowledgedAt,
        IConsentEvidenceStore evidenceStore,
        IConsentLiveSessions liveSessions,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(marker);

        // Live before the folder exists, so a sweep that lists the folder also sees the session running.
        liveSessions.Register(marker.SessionId);
        try
        {
            await evidenceStore.SaveHostAcknowledgementAsync(marker, acknowledgedAt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // By type only: the message carries the folder's path, and so the session id.
            logger.LogWarning("Failed to save a meeting host's acknowledgement as consent evidence ({Error})", ex.GetType().Name);
            logger.SensitiveDebug("Failed to save the host acknowledgement of session {SessionId}: {Exception}", marker.SessionId, ex);
        }

        return new HostAcknowledgedSession(marker, acknowledgedAt, evidenceStore, liveSessions);
    }

    public Task LogCopyAsync(ConsentCopy copy) => _evidenceStore.AppendCopyAsync(SessionId, copy);

    /// <summary>Hands the session to the lifetime rule: without a managed copy its evidence folder goes.</summary>
    public void End() => _liveSessions.Unregister(SessionId);
}
