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

    /// <summary>
    /// Per-speaker identity label ("Speaker 1", "Speaker 2", …) produced by the diarizer,
    /// or null when undiarized. Mutable so an in-session rename can retroactively relabel
    /// existing bubbles.
    /// </summary>
    [ObservableProperty]
    private string? _speakerLabel;

    /// <summary>
    /// What the UI shows for <see cref="SpeakerLabel"/>: auto-generated labels are renumbered 1..k in
    /// first-appearance order, because the raw number is a mint counter that only ever grows. Identity
    /// stays on <see cref="SpeakerLabel"/> — it keys the palette, the consent map and rename.
    /// </summary>
    [ObservableProperty]
    private string? _displayLabel;

    /// <summary>
    /// View-side palette slot (0..4) assigned by the view model from <see cref="SpeakerLabel"/>.
    /// Identity (the label) stays decoupled from the view (this color index).
    /// </summary>
    [ObservableProperty]
    private int _colorIndex;

    public TranscriptBubble(TranscriptSpeaker speaker, DateTimeOffset startTimestamp,
                            string text = "", string? speakerLabel = null, string? displayLabel = null)
    {
        Speaker = speaker;
        StartTimestamp = startTimestamp;
        _endTimestamp = startTimestamp;
        _text = text ?? string.Empty;
        _speakerLabel = speakerLabel;
        _displayLabel = displayLabel ?? speakerLabel;
    }

    public void Append(string text, DateTimeOffset endTimestamp)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Text = string.IsNullOrEmpty(Text) ? text : Text + " " + text;
        if (endTimestamp > EndTimestamp) EndTimestamp = endTimestamp;
    }
}
