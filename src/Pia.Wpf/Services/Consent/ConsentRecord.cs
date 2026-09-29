namespace Pia.Services.Consent;

/// <summary>The consent evidence a saved transcript carries in its front matter: labels and times, never names.</summary>
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

    /// <summary>Granted speakers only, always under the detected label, whatever they were renamed to.</summary>
    public static ConsentRecord ForSpeakers(
        IReadOnlyList<string> sessionIds,
        int noticeVersion,
        IReadOnlyList<string> noticePurposes,
        string noticeLanguage,
        IEnumerable<SpeakerConsentEntry> speakers)
    {
        ArgumentNullException.ThrowIfNull(speakers);

        var consents = speakers
            .Where(s => s.Evidence is not null)
            .Select(s => new ConsentRecordEntry(s.DetectedLabel, s.Evidence!.GrantedAt, s.RevokedAt))
            .ToList();

        return new ConsentRecord(sessionIds, noticeVersion, noticePurposes, noticeLanguage, consents);
    }
}

public sealed record ConsentRecordEntry(string Label, DateTimeOffset GrantedAt, DateTimeOffset? RevokedAt = null);
