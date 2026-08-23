using Microsoft.Extensions.DependencyInjection;
using Pia.Navigation;
using Pia.ViewModels;
using Pia.Services.Interfaces;
using Wpf.Ui;
using Wpf.Ui.Controls;
using System.Windows;
#if DEBUG
using System.Windows.Input;
#endif
using INavigationService = Pia.Navigation.INavigationService;

namespace Pia;

public partial class MainWindow : FluentWindow
{
    private readonly INavigationService _navigationService;
    private readonly ISettingsService _settingsService;
    private readonly Views.Overlays.PolicyRestartOverlayPresenter _policyRestartOverlay;

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

#if DEBUG
        InputBindings.Add(new KeyBinding(
            viewModel.DumpTourTargetsCommand, Key.F12, ModifierKeys.Control | ModifierKeys.Shift));
#endif

        // Set scoped service provider for ViewModelLocator
        ViewModelLocator.SetScopedServiceProvider(this, serviceProvider);

        contentDialogService.SetDialogHost(RootContentDialogPresenter);
        snackbarService.SetSnackbarPresenter(RootSnackbarPresenter);
        dialogOverlayService.SetOverlayHost(RootDialogOverlayHost);

        // Flow rail: per-window VM resolved from this window's scope (mirrors the singleton store).
        RootFlowView.DataContext = serviceProvider.GetRequiredService<ViewModels.Flow.FlowViewModel>();

        // Resolved here so it seeds the restart flag even in a window opened after the policy landed;
        // armed from OnLoaded, once SetOverlayHost above has a live host to show into.
        _policyRestartOverlay = serviceProvider.GetRequiredService<Views.Overlays.PolicyRestartOverlayPresenter>();

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            if (DataContext is MainWindowViewModel viewModel)
                await viewModel.InitializeAsync();
        }
        finally
        {
            // In a finally: a throw out of the init above is handled process-wide and leaves the window
            // interactive, so arming here is the only thing that keeps the forcing overlay from being lost.
            _policyRestartOverlay.Start();
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
