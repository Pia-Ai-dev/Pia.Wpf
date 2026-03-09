using System.Windows.Controls;

namespace Pia.Views.SettingsViews;

public partial class DeviceManagementView : UserControl
{
    public DeviceManagementView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.DeviceManagementViewModel vm)
            vm.LoadDevicesCommand.Execute(null);
    }
}
