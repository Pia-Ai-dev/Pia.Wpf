using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels;

public sealed partial class StepRowViewModel
{
    /// <summary>The running step's streamed answer, tail only. User content: bound, never stored or logged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLiveText))]
    private string? _liveText;

    public bool HasLiveText => !string.IsNullOrEmpty(LiveText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLiveReasoning))]
    private string? _liveReasoning;

    public bool HasLiveReasoning => !string.IsNullOrEmpty(LiveReasoning);

    public ObservableCollection<RunStepToolRowViewModel> ToolRows { get; } = [];

    internal void ClearLive()
    {
        LiveText = null;
        LiveReasoning = null;
    }
}
