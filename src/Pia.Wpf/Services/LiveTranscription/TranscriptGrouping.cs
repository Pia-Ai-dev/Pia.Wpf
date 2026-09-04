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
    /// How long after a run's last word a blank utterance may still inherit its label. The bubble window
    /// is measured from the start, so on a long run it stops bounding adjacency at all.
    /// </summary>
    public const int InheritanceAdjacencySeconds = 8;

    /// <summary>
    /// Whether <paramref name="last"/> should absorb this utterance rather than start a new bubble: same
    /// speaker, same label, still inside the rolling window. An unlabeled segment (too short to diarize —
    /// "ja", "genau", laughter) inherits the run's label instead of splitting it, but only while it is
    /// still next to that run — a "genau" belongs to whoever just stopped talking, not to whoever last
    /// held the floor.
    /// </summary>
    public static bool ShouldReuse(
        TranscriptBubble? last, TranscriptSpeaker speaker, DateTimeOffset timestamp, string? speakerLabel)
    {
        if (last is null) return false;
        if (last.Speaker != speaker) return false;
        if ((timestamp - last.StartTimestamp).TotalSeconds >= BubbleWindowSeconds) return false;

        if (string.Equals(last.SpeakerLabel, speakerLabel, StringComparison.Ordinal)) return true;

        return string.IsNullOrWhiteSpace(speakerLabel)
            && !string.IsNullOrWhiteSpace(last.SpeakerLabel)
            && (timestamp - last.EndTimestamp).TotalSeconds < InheritanceAdjacencySeconds;
    }
}
