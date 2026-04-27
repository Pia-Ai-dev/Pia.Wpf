namespace Pia.Models;

public enum TranscriptSpeaker
{
    You,
    Them
}

public enum TranscriptChannel
{
    /// Normal transcript content destined for the meeting transcript sink.
    Regular,
    /// Routed to the consent classifier instead of the transcript — never user-visible.
    ConsentClassification,
}

public sealed record TranscriptUtterance(
    TranscriptSpeaker Speaker,
    string Text,
    DateTimeOffset Timestamp,
    string? SpeakerLabel = null,
    TranscriptChannel Channel = TranscriptChannel.Regular);
