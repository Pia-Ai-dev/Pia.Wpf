namespace Pia.Models;

public enum TranscriptSpeaker
{
    You,
    Them
}

/// <summary>
/// One transcribed speech segment as it leaves a transcription engine.
/// </summary>
/// <param name="Speaker">Which side of the conversation produced the audio.</param>
/// <param name="Text">The transcribed text. Never empty — the engine drops blank results before emitting.</param>
/// <param name="Timestamp">When the segment finished transcribing (local clock).</param>
/// <param name="SpeakerLabel">
/// Diarizer label, or <c>null</c> when the segment was not diarized: no diarizer was attached, the
/// segment was shorter than the diarization minimum, or the diarizer threw and the engine swallowed it.
/// </param>
/// <param name="SegmentId">Monotonic diarizer segment id, <c>null</c> whenever <paramref name="SpeakerLabel"/> is.</param>
/// <param name="DurationSeconds">
/// Length of the audio the text was transcribed from, in seconds, or <c>null</c> when the producer did
/// not measure it. Feeds the per-speaker voice statistics; never used for ordering or merging.
/// </param>
public sealed record TranscriptUtterance(
    TranscriptSpeaker Speaker,
    string Text,
    DateTimeOffset Timestamp,
    string? SpeakerLabel = null,
    long? SegmentId = null,
    double? DurationSeconds = null);
