using Pia.Models;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// The two rules that decide what a saved transcript looks like — how utterances group into bubbles, and
/// what label each group shows. Shared so the live overlay and the unattended meeting recorder cannot
/// drift into producing differently-shaped transcripts for the same meeting.
/// </summary>
public static class TranscriptGrouping
{
    public const int BubbleWindowSeconds = 25;

    /// <summary>
    /// Whether <paramref name="last"/> should absorb this utterance rather than start a new bubble: same
    /// speaker, same label, still inside the rolling window. An unlabeled segment (too short to diarize —
    /// "ja", "genau", laughter) inherits the in-window run's label instead of splitting it.
    /// </summary>
    public static bool ShouldReuse(
        TranscriptBubble? last, TranscriptSpeaker speaker, DateTimeOffset timestamp, string? speakerLabel)
    {
        if (last is null) return false;
        if (last.Speaker != speaker) return false;
        if ((timestamp - last.StartTimestamp).TotalSeconds >= BubbleWindowSeconds) return false;

        return string.Equals(last.SpeakerLabel, speakerLabel, StringComparison.Ordinal)
            || (string.IsNullOrWhiteSpace(speakerLabel) && !string.IsNullOrWhiteSpace(last.SpeakerLabel));
    }
}
