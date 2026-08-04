using System.Diagnostics.CodeAnalysis;

namespace Pia.Services.Consent;

/// <summary>
/// Payload of <see cref="IConsentStateManager.StateChanged"/>: one observed consent transition.
/// </summary>
/// <param name="SpeakerLabel">The label whose state changed.</param>
/// <param name="OldState">State before the transition.</param>
/// <param name="NewState">State after the transition. Always different from <paramref name="OldState"/>.</param>
/// <param name="ExtractedName">
/// The name captured for this speaker, or <c>null</c>. Sensitive — never log it unguarded.
/// </param>
/// <param name="OriginalSpeakerLabel">
/// The diarizer label this speaker was first detected under, or <c>null</c> when it is not known to differ
/// from <paramref name="SpeakerLabel"/>. Set by the consent forward loop on a grant, because a grant-time
/// rename can move the entry's key: a consumer that keyed its UI off the detection label needs BOTH the
/// old key (to find its row) and the new one (which is now the authoritative consent-map key).
/// </param>
public sealed record ConsentStateChangedEventArgs(
    string SpeakerLabel,
    ConsentState OldState,
    ConsentState NewState,
    string? ExtractedName,
    string? OriginalSpeakerLabel = null);

/// <summary>
/// Session-scoped consent map: which diarizer speaker labels have given spoken consent, with the
/// evidence for each grant. Implementations are thread-safe; the map is a DI singleton, so
/// <see cref="ResetSession"/> is what keeps consent from leaking between sessions in one app run.
///
/// <para>There is deliberately NO confidence threshold on this interface. The single grant threshold
/// lives on the classifier (<c>NamedConsentClassifier.GrantConfidenceThreshold</c>); the manager
/// records what it is told and never re-judges a decision.</para>
/// </summary>
public interface IConsentStateManager
{
    /// <summary>
    /// Raised OUTSIDE the internal lock, on the mutating thread. Subscriber throws are caught and logged,
    /// so a faulty subscriber cannot break the state machine. Not raised by <see cref="Rename"/>.
    /// </summary>
    event EventHandler<ConsentStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Registers the label if unseen (state <see cref="ConsentState.Unknown"/>) and returns a snapshot.
    /// </summary>
    SpeakerConsentEntry GetOrCreate(string speakerLabel);

    /// <summary>
    /// Returns a snapshot for an already-registered label without creating one.
    /// </summary>
    /// <returns><c>true</c> when the label is known; otherwise <c>false</c> and <paramref name="entry"/> is <c>null</c>.</returns>
    bool TryGet(string speakerLabel, [MaybeNullWhen(false)] out SpeakerConsentEntry entry);

    /// <summary>
    /// Allocation-free hot path for the gate. An unknown label yields <see cref="ConsentState.Unknown"/>
    /// (fail closed) and does NOT register the label.
    /// </summary>
    ConsentState CurrentState(string speakerLabel);

    /// <summary>
    /// <see cref="ConsentState.Unknown"/> or <see cref="ConsentState.Revoked"/> becomes
    /// <see cref="ConsentState.Granted"/>, storing the name and evidence. Idempotent for an
    /// already-Granted label: no state change, no event, existing evidence untouched.
    /// </summary>
    void Grant(string speakerLabel, string? extractedName, ConsentEvidence evidence);

    /// <summary>
    /// <see cref="ConsentState.Granted"/> becomes <see cref="ConsentState.Revoked"/>. The grant evidence
    /// is PRESERVED — a revocation must not destroy the proof that consent once existed. No-op when the
    /// label is not currently Granted.
    /// </summary>
    /// <returns>
    /// <c>true</c> when this call performed the Granted -&gt; Revoked transition; <c>false</c> when it was
    /// a no-op. Decided INSIDE the lock: a caller that probes <see cref="CurrentState"/> first and then
    /// revokes has a window in which a concurrent grant lands between the two, so the probe's answer
    /// cannot be used to decide whether an audit event and a persisted revocation record are owed.
    /// </returns>
    bool Revoke(string speakerLabel);

    /// <summary>
    /// Moves the entry to a new key, preserving state and evidence. Raises NO
    /// <see cref="StateChanged"/> — nothing about the consent decision changed.
    /// </summary>
    /// <returns>
    /// <c>false</c> when <paramref name="oldLabel"/> is unknown, <paramref name="newLabel"/> is blank,
    /// or <paramref name="newLabel"/> is already taken; otherwise <c>true</c>.
    /// </returns>
    bool Rename(string oldLabel, string newLabel);

    /// <summary>
    /// Clears every entry. Called by the session's prepare step — consent is session-scoped while the
    /// manager itself is a DI singleton, so without this a second session inherits the first's grants.
    /// </summary>
    void ResetSession();

    /// <summary>
    /// Snapshot of all entries, ordered by <see cref="SpeakerConsentEntry.FirstDetected"/> then
    /// <see cref="SpeakerConsentEntry.SpeakerLabel"/> (ordinal). Safe to enumerate off-thread.
    /// </summary>
    IReadOnlyList<SpeakerConsentEntry> Snapshot();
}
