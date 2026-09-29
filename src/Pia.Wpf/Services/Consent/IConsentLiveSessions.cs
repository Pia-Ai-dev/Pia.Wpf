namespace Pia.Services.Consent;

/// <summary>Consent sessions still recording outside direct transcription, whose evidence the sweep must leave alone.</summary>
public interface IConsentLiveSessions
{
    /// <summary>Call before the session's evidence folder is written, so a sweep that lists it also sees it live.</summary>
    void Register(string sessionId);

    /// <summary>Raises <see cref="SessionEnded"/> when the session was registered; a repeat call does nothing.</summary>
    void Unregister(string sessionId);

    IReadOnlyCollection<string> Snapshot();

    event EventHandler<string>? SessionEnded;
}
