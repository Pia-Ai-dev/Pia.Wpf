using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.Models;

/// <summary>
/// View-side aggregate of one speaker's consecutive utterances within a rolling time
/// window. The live transcription view binds to a collection of these instead of raw
/// <see cref="TranscriptUtterance"/> events so that a continuous monologue produces one
/// growing bubble rather than a flurry of short ones.
/// </summary>
public sealed partial class TranscriptBubble : ObservableObject
{
    public TranscriptSpeaker Speaker { get; }

    public DateTimeOffset StartTimestamp { get; }

    [ObservableProperty]
    private DateTimeOffset _endTimestamp;

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isListening;

    public TranscriptBubble(TranscriptSpeaker speaker, DateTimeOffset startTimestamp, string text = "")
    {
        Speaker = speaker;
        StartTimestamp = startTimestamp;
        _endTimestamp = startTimestamp;
        _text = text ?? string.Empty;
    }

    public void Append(string text, DateTimeOffset endTimestamp)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Text = string.IsNullOrEmpty(Text) ? text : Text + " " + text;
        if (endTimestamp > EndTimestamp) EndTimestamp = endTimestamp;
    }
}
