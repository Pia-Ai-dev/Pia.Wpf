using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels;

public partial class ResearchSettingsViewModel : ObservableObject
{
    public ProvidersSettingsViewModel ProvidersVm { get; }

    public ResearchSettingsViewModel(ProvidersSettingsViewModel providersVm)
    {
        ProvidersVm = providersVm;
    }
}
