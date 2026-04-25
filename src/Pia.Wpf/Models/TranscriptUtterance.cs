namespace Pia.Models;

public enum TranscriptSpeaker
{
    You,
    Them
}

public sealed record TranscriptUtterance(
    TranscriptSpeaker Speaker,
    string Text,
    DateTimeOffset Timestamp);
