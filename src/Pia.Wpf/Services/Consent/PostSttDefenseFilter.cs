using Microsoft.Extensions.Logging;
using Pia.Models;

namespace Pia.Services.Consent;

public sealed class PostSttDefenseFilter : IPostSttDefenseFilter
{
    private readonly IConsentStateManager _mgr;
    private readonly IConsentAuditLog _auditLog;
    private readonly ILogger<PostSttDefenseFilter> _logger;
    private int _dropCount;

    public PostSttDefenseFilter(
        IConsentStateManager mgr,
        IConsentAuditLog auditLog,
        ILogger<PostSttDefenseFilter> logger)
    {
        _mgr = mgr;
        _auditLog = auditLog;
        _logger = logger;
    }

    public int DropCount => Volatile.Read(ref _dropCount);

    public PostSttFilterDecision Evaluate(TranscriptUtterance utterance)
    {
        // Mic-side utterances have no speaker label — local user is by definition consenting.
        if (utterance.SpeakerLabel is null) return PostSttFilterDecision.Allow;

        // Consent classification utterances are not transcript content; they short-circuit
        // the filter and are handled by the dialog flow.
        if (utterance.Channel != TranscriptChannel.Regular) return PostSttFilterDecision.Allow;

        var state = _mgr.CurrentState(utterance.SpeakerLabel);
        if (state == ConsentState.Granted) return PostSttFilterDecision.Allow;

        Interlocked.Increment(ref _dropCount);
        _logger.LogWarning(
            "Post-STT defense filter dropped utterance for {Label} (state={State}) — pre-STT gate bug",
            utterance.SpeakerLabel, state);
        _auditLog.Append(new AuditEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, "DROPPED_TRANSCRIPT_NO_CONSENT", utterance.SpeakerLabel,
            new Dictionary<string, object?>
            {
                ["state"] = state.ToString(),
                ["reason"] = "post_stt_filter_caught_race",
            }));
        return PostSttFilterDecision.DropAndAudit;
    }
}
