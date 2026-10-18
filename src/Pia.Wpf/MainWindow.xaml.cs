using Microsoft.Extensions.DependencyInjection;
using Pia.Navigation;
using Pia.ViewModels;
using Pia.Services.Interfaces;
using Wpf.Ui;
using Wpf.Ui.Controls;
using System.Windows;
using System.Windows.Interop;
using Pia.Helpers;
using Pia.Native;
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

            // 0,0 is the unsaved default; negative values are legitimate on a monitor left of or above primary.
            if (settings.WindowLeft != 0 || settings.WindowTop != 0)
            {
                Left = settings.WindowLeft;
                Top = settings.WindowTop;
            }

            EnsureReachable();
        }
        catch
        {
            // Ignore errors restoring window state
        }
    }

    /// <summary>A saved position on a since-disconnected monitor would otherwise leave the window unreachable.</summary>
    private void EnsureReachable()
    {
        // An iconic window reports its -32000 parking rect, which would discard a valid saved position.
        if (WindowState == WindowState.Minimized)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || !ScreenCaptureInterop.GetWindowRect(hwnd, out var bounds))
            return;

        if (WindowPlacement.IsReachable(WindowPlacement.ToRect(bounds), WindowPlacement.MonitorWorkAreas()))
            return;

        var placed = WindowPlacement.CenterIn(SystemParameters.WorkArea, new Size(Width, Height));
        Width = placed.Width;
        Height = placed.Height;
        Left = placed.Left;
        Top = placed.Top;
    }

    public void PrepareForExit()
    {
        SaveWindowStateAsync();
    }

    private void SaveWindowStateAsync()
    {
        // Left/Top read -32000 while minimized; RestoreBounds holds the normal-state rect.
        var bounds = WindowState != WindowState.Normal && !RestoreBounds.IsEmpty
            ? RestoreBounds
            : new Rect(Left, Top, Width, Height);
        var width = bounds.Width;
        var height = bounds.Height;
        var left = bounds.Left;
        var top = bounds.Top;
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
