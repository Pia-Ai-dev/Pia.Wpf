namespace Pia.Services.Consent;

/// <summary>The consent evidence a saved transcript carries in its front matter: labels and times.</summary>
/// <param name="NoticeLanguage">Two-letter language the notice was shown in.</param>
/// <param name="HostAcknowledgedAt">A meeting host's confirmation, standing in for per-speaker consent.</param>
public sealed record ConsentRecord(
    IReadOnlyList<string> SessionIds,
    int NoticeVersion,
    IReadOnlyList<string> NoticePurposes,
    string NoticeLanguage,
    IReadOnlyList<ConsentRecordEntry> Consents,
    DateTimeOffset? HostAcknowledgedAt = null)
{
    public bool IsEmpty => Consents.Count == 0 && HostAcknowledgedAt is null;

    public bool SpansSessions => SessionIds.Distinct(StringComparer.Ordinal).Skip(1).Any();

    /// <summary>Granted speakers only, always under the detected label, whatever they were renamed to.</summary>
    /// <param name="shownAs">What the transcript body prints for a speaker, or <c>null</c> when it prints none of theirs.</param>
    public static ConsentRecord ForSpeakers(
        IReadOnlyList<string> sessionIds,
        int noticeVersion,
        IReadOnlyList<string> noticePurposes,
        string noticeLanguage,
        IEnumerable<SessionSpeakerConsent> speakers,
        Func<SessionSpeakerConsent, string?>? shownAs = null)
    {
        ArgumentNullException.ThrowIfNull(speakers);

        var consents = speakers
            .Where(s => s.Speaker.Evidence is not null)
            .Select(s => new ConsentRecordEntry(
                s.Speaker.DetectedLabel,
                s.Speaker.Evidence!.GrantedAt,
                s.Speaker.RevokedAt,
                shownAs?.Invoke(s),
                s.SessionId))
            .ToList();

        return new ConsentRecord(sessionIds, noticeVersion, noticePurposes, noticeLanguage, consents);
    }

    /// <summary>A meeting's host acknowledgement: no per-speaker consents, and so no labels either.</summary>
    public static ConsentRecord ForHostAcknowledgement(ConsentSessionMarker session, DateTimeOffset acknowledgedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new ConsentRecord(
            [session.SessionId], session.NoticeVersion, session.NoticePurposes, session.NoticeLanguage, [], acknowledgedAt);
    }
}

/// <param name="Label">The detected label, which keys the evidence and the audit trail.</param>
/// <param name="ShownAs">The speaker label the transcript body prints for these utterances; may be a given name.</param>
/// <param name="Session">The session the grant belongs to; rendered only when the record spans several.</param>
public sealed record ConsentRecordEntry(
    string Label,
    DateTimeOffset GrantedAt,
    DateTimeOffset? RevokedAt = null,
    string? ShownAs = null,
    string? Session = null);

/// <summary>One speaker's consent state within the session it was given in.</summary>
public sealed record SessionSpeakerConsent(string SessionId, SpeakerConsentEntry Speaker);
