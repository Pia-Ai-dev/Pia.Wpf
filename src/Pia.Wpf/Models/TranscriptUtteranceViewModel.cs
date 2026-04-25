using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.Models;

/// <summary>
/// View-side wrapper for a <see cref="TranscriptUtterance"/>. Holds a back-reference
/// to the owning view-model so that renaming the counterpart broadcasts a single
/// PropertyChanged for <see cref="DisplayName"/> and updates every existing bubble.
/// </summary>
public partial class TranscriptUtteranceViewModel : ObservableObject
{
    private readonly Func<string> _counterpartNameAccessor;

    public TranscriptUtterance Utterance { get; }

    public TranscriptUtteranceViewModel(TranscriptUtterance utterance, Func<string> counterpartNameAccessor)
    {
        Utterance = utterance;
        _counterpartNameAccessor = counterpartNameAccessor;
    }

    public TranscriptSpeaker Speaker => Utterance.Speaker;
    public string Text => Utterance.Text;
    public DateTimeOffset Timestamp => Utterance.Timestamp;
    public bool IsYou => Utterance.Speaker == TranscriptSpeaker.You;

    public string DisplayName => Utterance.Speaker == TranscriptSpeaker.You
        ? "you"
        : _counterpartNameAccessor();

    public void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));
}
