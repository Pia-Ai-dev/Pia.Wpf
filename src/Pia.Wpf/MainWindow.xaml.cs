using Pia.Navigation;
using Pia.ViewModels;
using Pia.Services.Interfaces;
using Wpf.Ui;
using Wpf.Ui.Controls;
using System.Windows;
using INavigationService = Pia.Navigation.INavigationService;

namespace Pia;

public partial class MainWindow : FluentWindow
{
    private readonly INavigationService _navigationService;
    private readonly ISettingsService _settingsService;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        ISettingsService settingsService,
        IContentDialogService contentDialogService,
        ISnackbarService snackbarService,
        IDialogOverlayService dialogOverlayService,
        IServiceProvider serviceProvider)
    {
        _navigationService = navigationService;
        _settingsService = settingsService;

        DataContext = viewModel;
        InitializeComponent();

        // Set scoped service provider for ViewModelLocator
        ViewModelLocator.SetScopedServiceProvider(this, serviceProvider);

        contentDialogService.SetDialogHost(RootContentDialogPresenter);
        snackbarService.SetSnackbarPresenter(RootSnackbarPresenter);
        dialogOverlayService.SetOverlayHost(RootDialogOverlayHost);

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }

        await RestoreWindowStateAsync();
    }

    private async Task RestoreWindowStateAsync()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();

            if (settings.WindowWidth > 0 && settings.WindowHeight > 0)
            {
                Width = settings.WindowWidth;
                Height = settings.WindowHeight;
            }

            if (settings.WindowLeft > 0 && settings.WindowTop > 0)
            {
                Left = settings.WindowLeft;
                Top = settings.WindowTop;
            }
        }
        catch
        {
            // Ignore errors restoring window state
        }
    }

    public void PrepareForExit()
    {
        SaveWindowStateAsync();
    }

    private void SaveWindowStateAsync()
    {
        var width = Width;
        var height = Height;
        var left = Left;
        var top = Top;
        var lastActiveView = (DataContext as MainWindowViewModel)?.CurrentView?.GetType().AssemblyQualifiedName;

        _ = Task.Run(async () =>
        {
            try
            {
                var settings = await _settingsService.GetSettingsAsync();
                settings.WindowWidth = width;
                settings.WindowHeight = height;
                settings.WindowLeft = left;
                settings.WindowTop = top;
                settings.LastActiveView = lastActiveView;
                await _settingsService.SaveSettingsAsync(settings);
            }
            catch
            {
                // Ignore errors saving window state
            }
        });
    }
}
