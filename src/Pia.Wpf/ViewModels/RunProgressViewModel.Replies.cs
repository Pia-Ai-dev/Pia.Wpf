using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels;

public sealed partial class RunProgressViewModel
{
    [ObservableProperty]
    private string? _stoppedNote;
}
