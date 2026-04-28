namespace Pia.Services.Consent.Revocation;

public sealed record RevocationEvidence(
    string SpeakerLabel,
    DateTimeOffset RevokedAt,
    bool TranscriptRedacted,
    bool SummaryDeleted,
    IReadOnlyList<string> ProvidersDeletionRequested,
    IReadOnlyList<string> ProvidersDeletionOutstanding);

public interface IRevocationService
{
    Task<RevocationEvidence> RevokeAsync(string speakerLabel, CancellationToken ct);
}

public interface IPersistedTranscriptStore
{
    /// <summary>Redact this speaker's segments in any persisted transcript files for the
    /// current session. Returns true if at least one file was modified.</summary>
    Task<bool> RedactSpeakerAsync(string speakerLabel, CancellationToken ct);
}

public interface ICachedSummaryStore
{
    /// <summary>Delete any summary cached locally for the current session. Returns true if
    /// a cached summary was found and removed.</summary>
    Task<bool> DeleteCurrentSummaryAsync(CancellationToken ct);
}

public interface IProviderDeletionClient
{
    string ProviderId { get; }
    /// <summary>True iff this provider exposes a deletion API. False ⇒ caller emits
    /// <c>OUTSTANDING_PROVIDER_DELETION</c> instead of calling.</summary>
    bool SupportsDeletion { get; }
    Task RequestDeletionAsync(string speakerLabel, CancellationToken ct);
}

public sealed class NoOpTranscriptStore : IPersistedTranscriptStore
{
    public Task<bool> RedactSpeakerAsync(string speakerLabel, CancellationToken ct) => Task.FromResult(false);
}

public sealed class NoOpSummaryStore : ICachedSummaryStore
{
    public Task<bool> DeleteCurrentSummaryAsync(CancellationToken ct) => Task.FromResult(false);
}
