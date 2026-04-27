namespace Pia.Models;

/// <summary>
/// Payload for the LiveTranscription overlay's "Save and summarize" event. The view-model
/// raises this once a transcript has been silently written to disk; the assistant view-model
/// consumes it to inject a synthetic chat message that triggers the summarize tool.
/// </summary>
public sealed record MeetingSummarizationRequest(string FilePath, string DisplayPath);
