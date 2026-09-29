namespace Pia.Services.Consent;

/// <summary>What a session's evidence folder records about the session itself; its presence marks the folder v2.</summary>
/// <param name="Kind"><see cref="DirectKind"/> or <see cref="TeamsKind"/>.</param>
/// <param name="NoticeLanguage">Two-letter language the notice was shown in.</param>
public sealed record ConsentSessionMarker(
    string SessionId,
    DateTimeOffset StartedAt,
    string Kind,
    int NoticeVersion,
    IReadOnlyList<string> NoticePurposes,
    string NoticeLanguage)
{
    public const string DirectKind = "direct";
    public const string TeamsKind = "teams";

    /// <summary>A scheduled Teams recording, consented to by the routine's host acknowledgement.</summary>
    public static ConsentSessionMarker ForTeams(string sessionId, DateTimeOffset startedAt, string noticeLanguage)
        => new(sessionId, startedAt, TeamsKind, ConsentNotice.TeamsVersion, ConsentNotice.TeamsPurposes, noticeLanguage);
}
