using System.Threading.Channels;
using Pia.Models;
using Pia.Services.Consent;

namespace Pia.Services.Interfaces;

/// <summary>
/// Lifecycle of a direct (microphone + system-audio) transcription session.
/// </summary>
public enum DirectTranscriptionState
{
    /// <summary>No session. Nothing is loaded, nothing is captured.</summary>
    Idle,

    /// <summary>Models and the diarizer are being provisioned.</summary>
    Preparing,

    /// <summary>Warmed up and ready to start capturing. Consent map and diarizer are live.</summary>
    Prepared,

    /// <summary>Capture sources and engines are being built and started.</summary>
    Starting,

    /// <summary>Capturing and transcribing.</summary>
    Running,

    /// <summary>Capture is being torn down; the session survives so a resume is fast.</summary>
    Stopping,

    /// <summary>Preparation or start failed. A retry may transition back through <see cref="Preparing"/>.</summary>
    Error
}

/// <summary>
/// Payload of <see cref="IDirectTranscriptionService.SpeakerConsentChanged"/>: one speaker's consent
/// state changed, as observed at the service boundary.
/// </summary>
/// <param name="SpeakerLabel">The diarizer label whose state changed.</param>
/// <param name="OldState">State before the transition.</param>
/// <param name="NewState">State after the transition.</param>
/// <param name="ExtractedName">
/// The name captured from the consent sentence, or <c>null</c>. Sensitive — never log it unguarded.
/// DISPLAY text only: it is NOT necessarily the consent map's key, because a grant-time rename can be
/// refused (a collision with a label that is already taken). Use <paramref name="SpeakerLabel"/> as the key.
/// </param>
/// <param name="OriginalSpeakerLabel">
/// The diarizer label this speaker was first detected (and first surfaced to the UI) under, or <c>null</c>
/// when it is not known to differ from <paramref name="SpeakerLabel"/>.
/// </param>
public sealed record SpeakerConsentChangedEventArgs(
    string SpeakerLabel,
    ConsentState OldState,
    ConsentState NewState,
    string? ExtractedName,
    string? OriginalSpeakerLabel = null);

/// <summary>
/// Payload of <see cref="IDirectTranscriptionService.SpeakingChanged"/>: voice activity started or
/// stopped on one side of the conversation. Drives the level indicator only.
/// </summary>
/// <param name="Speaker">Which side the change concerns.</param>
/// <param name="IsSpeaking"><c>true</c> when speech started, <c>false</c> when it ended.</param>
public sealed record TranscriptionSpeakingChangedEventArgs(TranscriptSpeaker Speaker, bool IsSpeaking);

/// <summary>
/// Transcribes the local microphone and the system audio output into one consent-gated transcript.
/// Only speech from the local user and from speakers who have given spoken consent ever leaves this
/// service — the consent gate is inside the implementation, not in its consumers.
/// </summary>
public interface IDirectTranscriptionService : IAsyncDisposable
{
    /// <summary>Current lifecycle state.</summary>
    DirectTranscriptionState State { get; }

    /// <summary>
    /// Stable for the service lifetime; SingleReader — exactly one consumer ever. Completed only in
    /// <see cref="IAsyncDisposable.DisposeAsync"/>, so it survives a stop/resume cycle unchanged.
    /// </summary>
    ChannelReader<TranscriptUtterance> Utterances { get; }

    /// <summary>
    /// Raised on the thread that performed the transition (the caller's thread for
    /// <see cref="StartAsync"/>/<see cref="StopAsync"/>). Subscribers marshal themselves.
    /// </summary>
    event EventHandler<DirectTranscriptionState>? StateChanged;

    /// <summary>Raised on a background thread when a speaker grants or loses consent.</summary>
    event EventHandler<SpeakerConsentChangedEventArgs>? SpeakerConsentChanged;

    /// <summary>
    /// Raised on a background thread (the engine's segment loop) when the diarizer registers a
    /// previously unseen speaker label.
    /// </summary>
    event EventHandler<string>? SpeakerRegistered;

    /// <summary>Raised on the audio reader thread when voice activity starts or stops on one side.</summary>
    event EventHandler<TranscriptionSpeakingChangedEventArgs>? SpeakingChanged;

    /// <summary>
    /// Raised whenever <see cref="PrepareAsync"/> discards the consent map — which it must, because it also
    /// builds a BRAND-NEW diarizer whose "Speaker 1" is a different voice from the previous one's, so
    /// carrying grants over would hand one person's consent to another. Consumers MUST clear any per-speaker
    /// UI they are showing: without this event a chip could keep reading "consented" for a speaker the gate
    /// has since reverted to Unknown, i.e. the UI would claim a participant is being recorded while their
    /// speech is silently dropped.
    /// </summary>
    event EventHandler? ConsentSessionReset;

    /// <summary>
    /// Idempotent warm-up AND session start: provisions the models, builds the manual diarizer, issues a
    /// new session id and clears the consent map. A speaker-model failure THROWS — this pipeline must not
    /// degrade to an undiarized state, because without labels nothing can be consent-gated.
    /// </summary>
    Task PrepareAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds fresh capture sources, fresh engines and a FRESH raw channel, then starts capturing.
    /// Awaits <see cref="PrepareAsync"/> first when the session is not yet
    /// <see cref="DirectTranscriptionState.Prepared"/>.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses the pipeline in order: sources → engines → raw channel → forward loop. Returns to
    /// <see cref="DirectTranscriptionState.Prepared"/>; the consent map, the diarizer and the shared
    /// speech-to-text engine all survive, so a resume is fast and speakers stay consented.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the session: <see cref="StopAsync"/>, then dispose the shared engine and the diarizer LAST
    /// (native resources), clear the consent map, rotate the session id, and return to
    /// <see cref="DirectTranscriptionState.Idle"/>. Consent does not survive this.
    /// </summary>
    Task EndSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a diarizer label in-session, carrying its consent state and evidence over.
    /// </summary>
    /// <returns>
    /// <c>false</c> when <paramref name="oldLabel"/> is unknown, <paramref name="newLabel"/> is blank, or
    /// the new label is already taken.
    /// </returns>
    bool RenameSpeaker(string oldLabel, string newLabel);

    /// <summary>
    /// Withdraws one speaker's consent for the rest of the session. Their subsequent speech is dropped;
    /// the recorded grant evidence is preserved.
    /// </summary>
    void RevokeSpeaker(string speakerLabel);

    /// <summary>
    /// Per-speaker speaking statistics for CONSENTED speech only — dropped audio is never measured.
    /// Empty when nothing has been measured yet.
    /// </summary>
    IReadOnlyList<SpeakerVoiceStats> GetVoiceStats();
}
