using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Pia.Navigation;
using Pia.ViewModels;

namespace Pia.Views;

public partial class TodoPanelControl : UserControl
{
    public TodoPanelControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        var window = Window.GetWindow(this);
        if (window is null)
            return;

        var scopedProvider = ViewModelLocator.GetScopedServiceProvider(window);
        if (scopedProvider is null)
            return;

        var vm = scopedProvider.GetService<TodoViewModel>();
        DataContext = vm;

        if (vm is not null && vm.PendingCount == 0)
            await vm.LoadTodosAsync();
    }
}
