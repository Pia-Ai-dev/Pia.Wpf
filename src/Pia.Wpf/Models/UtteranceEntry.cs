namespace Pia.Models;

/// <summary>One journaled utterance; Label is mutable (reassignments and renames retarget it).</summary>
internal sealed class UtteranceEntry
{
    public required TranscriptSpeaker Speaker { get; init; }
    public required string Text { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string? Label { get; set; }
    public required long? SegmentId { get; init; }
}
