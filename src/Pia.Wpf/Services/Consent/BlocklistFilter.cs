using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent;

/// <summary>
/// Default implementation of <see cref="IBlocklistFilter"/>. Wraps a
/// <see cref="VoiceEmbeddingBlocklist"/> and resolves the embedding for a speaker label
/// from the consent state manager (which stores the latest observed embedding per
/// speaker — see <see cref="SpeakerConsentEntry.Embedding"/>).
/// </summary>
public sealed class BlocklistFilter : IBlocklistFilter
{
    private readonly VoiceEmbeddingBlocklist _blocklist;
    private readonly IConsentStateManager _consentMgr;
    private readonly IConsentAuditLog _auditLog;
    private readonly ILogger<BlocklistFilter> _logger;

    public BlocklistFilter(
        VoiceEmbeddingBlocklist blocklist,
        IConsentStateManager consentMgr,
        IConsentAuditLog auditLog,
        ILogger<BlocklistFilter> logger)
    {
        _blocklist = blocklist;
        _consentMgr = consentMgr;
        _auditLog = auditLog;
        _logger = logger;
    }

    public void BlockSpeaker(string speakerLabel)
    {
        if (!_consentMgr.TryGet(speakerLabel, out var entry) || entry.Embedding is not { } emb)
        {
            _logger.LogWarning(
                "BlockSpeaker({Label}) had no embedding to block — speaker will not be matched by voice",
                speakerLabel);
            return;
        }
        _blocklist.Add(emb);
        _logger.LogInformation(
            "Blocklist added embedding for {Label}; size={Count}, threshold={Threshold:F2}",
            speakerLabel, _blocklist.Count, _blocklist.Threshold);
    }

    public bool ShouldDrop(float[] embedding)
    {
        if (!_blocklist.ShouldDrop(embedding)) return false;
        _auditLog.Append(new AuditEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, "DENIED_SPEAKER_BLOCKED", null, null));
        return true;
    }

    /// <summary>Reset between meetings — blocklist must not survive sessions.</summary>
    public void Reset() => _blocklist.Clear();
}
