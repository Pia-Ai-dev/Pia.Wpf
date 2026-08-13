namespace Pia.Services.LiveTranscription;

/// <summary>
/// Within-session speaker identification for live transcription. Computes a speaker
/// embedding from a speech segment, matches against known speakers, and either returns
/// the matched display label or registers a new "Speaker N" label for an unseen voice.
/// State is per meeting — call <see cref="Reset"/> on session start/stop.
/// </summary>
public interface ISpeakerIdentificationService : IDisposable
{
    /// <summary>
    /// Compute the embedding for <paramref name="segmentSamples"/> (16 kHz mono float32),
    /// match against the in-session known-speaker manager, and return a stable display label.
    /// New voices are registered as "Speaker 1", "Speaker 2", … in order of first appearance.
    /// </summary>
    string IdentifyOrRegister(float[] segmentSamples, int sampleRate);

    /// <summary>
    /// Compute the segment's embedding and identify-or-register, returning both. Used by
    /// the blocklist filter so the engine pipeline can run a similarity check before the
    /// segment reaches the consent gate / ring buffer.
    /// </summary>
    (string Label, float[] Embedding) IdentifyOrRegisterWithEmbedding(float[] segmentSamples, int sampleRate);

    /// <summary>
    /// Like <see cref="IdentifyOrRegister"/> but also returns the segment id under which the
    /// (adaptive) implementation journals this segment's embedding, so later
    /// <see cref="SpeakersReassigned"/> events can retarget the utterance. The manual
    /// implementation hands out monotonically increasing ids too — they are simply never
    /// reassigned.
    /// </summary>
    SpeakerSegmentResult IdentifyOrRegisterSegment(float[] segmentSamples, int sampleRate);

    /// <summary>
    /// Rename a display label so all subsequent <see cref="IdentifyOrRegister"/> calls for
    /// the same voice return <paramref name="newLabel"/>. Returns true if a label matching
    /// <paramref name="oldLabel"/> existed and was renamed.
    /// </summary>
    bool Rename(string oldLabel, string newLabel);

    /// <summary>
    /// Drop all known-speaker state. Called on meeting start and stop so each meeting begins
    /// with a fresh "Speaker 1" pool.
    /// </summary>
    void Reset();

    /// <summary>
    /// Publish the known head count (the meeting roster size) so an implementation can bound the
    /// number of distinct voices. A ceiling, never a target: 0 leaves detection unconstrained, and
    /// a roster larger than the talkers must not inflate the result. No-op by default.
    /// </summary>
    void SetExpectedSpeakers(int count) { }

    /// <summary>
    /// Raised the first time a new speaker label is registered (Zone C). Fires on the calling
    /// thread, outside the diarization lock. The consent flow subscribes here so it can prompt
    /// even when the pre-STT consent gate would otherwise drop every segment for an Unknown
    /// speaker (which would prevent the utterance pipeline from ever observing the speaker).
    /// </summary>
    event EventHandler<string>? SpeakerRegistered;

    /// <summary>
    /// Raised after a re-cluster pass changed the label of already-emitted segments. Carries only
    /// the changed (SegmentId → new Label) pairs. Never raised by the manual implementation.
    /// Fires on the calling thread, outside the diarization lock.
    /// </summary>
    event EventHandler<IReadOnlyList<SpeakerReassignment>>? SpeakersReassigned;
}

/// <summary>Identify-or-register result carrying the journal id for the segment's embedding.</summary>
public readonly record struct SpeakerSegmentResult(long SegmentId, string Label);

/// <summary>One retroactive label correction produced by an adaptive re-cluster pass.</summary>
public readonly record struct SpeakerReassignment(long SegmentId, string NewLabel);
