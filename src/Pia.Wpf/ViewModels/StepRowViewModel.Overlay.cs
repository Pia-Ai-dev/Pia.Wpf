using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels;

public sealed partial class StepRowViewModel
{
    /// <summary>A chat turn added or changed this step and it has not started yet.</summary>
    [ObservableProperty]
    private bool _isChangedFromChat;
}
