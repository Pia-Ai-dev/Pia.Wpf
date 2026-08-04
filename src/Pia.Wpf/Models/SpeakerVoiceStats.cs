namespace Pia.Models;

/// <summary>
/// One measured speech sample: the duration of a single utterance, attributed to a speaker.
/// Produced inside the privacy boundary (the consent forward loop) and deliberately carries NO text —
/// statistics must be derivable without retaining transcript content.
/// </summary>
/// <param name="Speaker">Which side of the conversation the sample came from.</param>
/// <param name="SpeakerLabel">Diarizer label, or <c>null</c> for the undiarized microphone side.</param>
/// <param name="DurationSeconds">Length of the transcribed audio, in seconds. Never negative.</param>
public readonly record struct VoiceSample(TranscriptSpeaker Speaker, string? SpeakerLabel, double DurationSeconds);

/// <summary>
/// Aggregated speaking statistics for one speaker over one session, computed from
/// <see cref="VoiceSample"/>s. Text-free by construction.
/// </summary>
/// <param name="Speaker">Which side of the conversation these statistics describe.</param>
/// <param name="SpeakerLabel">Diarizer label, or <c>null</c> for the undiarized microphone side.</param>
/// <param name="UtteranceCount">Number of measured utterances attributed to this speaker.</param>
/// <param name="TotalSpeechSeconds">Sum of the measured utterance durations, in seconds.</param>
/// <param name="MeanUtteranceSeconds">
/// <paramref name="TotalSpeechSeconds"/> divided by <paramref name="UtteranceCount"/>; 0 when the count is 0.
/// </param>
/// <param name="ShareOfMeasuredSpeech">
/// This speaker's share of all measured speech, in <c>[0,1]</c>. 0 when nothing was measured at all.
/// It is a share of MEASURED speech only — dropped (unconsented, unlabeled) audio is never measured,
/// so this is not a share of wall-clock time.
/// </param>
public sealed record SpeakerVoiceStats(
    TranscriptSpeaker Speaker,
    string? SpeakerLabel,
    int UtteranceCount,
    double TotalSpeechSeconds,
    double MeanUtteranceSeconds,
    double ShareOfMeasuredSpeech);
