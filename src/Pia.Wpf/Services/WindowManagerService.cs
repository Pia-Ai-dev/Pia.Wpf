using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;
using Pia.ViewModels;

namespace Pia.Services;

public partial class WindowManagerService : IWindowManagerService
{
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    private readonly IServiceProvider _rootProvider;
    private readonly ILogger<WindowManagerService> _logger;
    private readonly IAgentRunService _agentRunService;
    private readonly Services.Flow.IFlowService _flowService;
    private readonly ILocalizationService _localizationService;
    private readonly Dictionary<WindowMode, ManagedWindow> _windows = new();
    private bool _isShuttingDown;
    private double _lastWindowLeft = double.NaN;
    private double _lastWindowTop = double.NaN;
    private const double PositionOffset = 30;

    public bool HasOpenWindows => _windows.Values.Any(w => w.Window.Visibility == Visibility.Visible);

    public event EventHandler<ManagedWindow>? WindowOpened;
    public event EventHandler<ManagedWindow>? WindowClosed;
    public event EventHandler? WindowVisibilityChanged;

    public WindowManagerService(
        IServiceProvider rootProvider,
        ILogger<WindowManagerService> logger,
        IAgentRunService agentRunService,
        Services.Flow.IFlowService flowService,
        ILocalizationService localizationService)
    {
        _rootProvider = rootProvider;
        _logger = logger;
        _agentRunService = agentRunService;
        _flowService = flowService;
        _localizationService = localizationService;
    }

    public void ShowWindow(WindowMode mode)
    {
        _logger.LogTrace("ShowWindow {Mode} requested", mode);

        if (_windows.TryGetValue(mode, out var existing))
        {
            _logger.LogTrace(
                "ShowWindow {Mode} reusing existing, state={State}, visibility={Visibility}",
                mode, existing.Window.WindowState, existing.Window.Visibility);

            existing.Window.Show();
            existing.Window.Visibility = Visibility.Visible;
            existing.Window.WindowState = WindowState.Normal;
            existing.Window.Topmost = true;
            existing.Window.Activate();
            existing.Window.Focus();
            existing.Window.Topmost = false;
            WindowVisibilityChanged?.Invoke(this, EventArgs.Empty);

            _logger.LogTrace("ShowWindow {Mode} done (reused)", mode);
            return;
        }

        var scope = _rootProvider.CreateScope();
        var window = scope.ServiceProvider.GetRequiredService<MainWindow>();
        var viewModel = scope.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        viewModel.Mode = mode;
        window.DataContext = viewModel;

        var managed = new ManagedWindow(mode, window, scope);
        _windows[mode] = managed;

        window.Closing += (_, e) =>
        {
            if (_isShuttingDown)
                return;

            e.Cancel = true;
            HideWindow(mode);
        };

        window.StateChanged += (_, _) =>
        {
            if (_isShuttingDown)
                return;

            _logger.LogTrace("Window {Mode} StateChanged to {State}", mode, window.WindowState);

            if (window.WindowState == WindowState.Minimized)
            {
                window.Dispatcher.BeginInvoke(
                    () => HideWindow(mode),
                    DispatcherPriority.ContextIdle);
            }
        };

        if (_windows.Values.Any(w => w != managed && w.Window.Visibility == Visibility.Visible))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            var workArea = SystemParameters.WorkArea;
            var newLeft = _lastWindowLeft + PositionOffset;
            var newTop = _lastWindowTop + PositionOffset;

            if (double.IsNaN(newLeft) || double.IsNaN(newTop)
                || newLeft + window.Width > workArea.Right
                || newTop + window.Height > workArea.Bottom)
            {
                newLeft = workArea.Left + PositionOffset;
                newTop = workArea.Top + PositionOffset;
            }

            window.Left = newLeft;
            window.Top = newTop;
        }

        window.Show();
        window.Topmost = true;
        window.Activate();
        window.Focus();
        window.Topmost = false;

        _lastWindowLeft = window.Left;
        _lastWindowTop = window.Top;

        WindowOpened?.Invoke(this, managed);
        WindowVisibilityChanged?.Invoke(this, EventArgs.Empty);

        _logger.LogTrace("ShowWindow {Mode} done (created)", mode);
    }

    public async Task<IOptimizeFastPathHandle> ShowOptimizeAndGetViewModelAsync()
    {
        ShowWindow(WindowMode.Optimize);

        if (!_windows.TryGetValue(WindowMode.Optimize, out var managed))
            throw new InvalidOperationException("Optimize window was not created");

        var viewModel = managed.Scope.ServiceProvider.GetRequiredService<OptimizeViewModel>();
        var readyTask = viewModel.ReadyAsync;
        var completed = await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromSeconds(3)));
        if (completed == readyTask)
            await readyTask;
        else
            _logger.LogWarning("Timed out waiting for OptimizeViewModel readiness during fast-path startup");

        return viewModel;
    }

    public void ShowWindowWithText(WindowMode mode, string text)
    {
        ShowWindow(mode);

        if (!_windows.TryGetValue(mode, out var managed))
            return;

        var navigationService = managed.Scope.ServiceProvider.GetRequiredService<INavigationService>();

        switch (mode)
        {
            case WindowMode.Assistant:
                navigationService.NavigateTo<AssistantViewModel, string>(text);
                break;
        }
    }

    public void ShowWindowWithSelection(WindowMode mode, string capturedText)
    {
        ShowWindow(mode);

        if (!_windows.TryGetValue(mode, out var managed))
            return;

        var navigationService = managed.Scope.ServiceProvider.GetRequiredService<INavigationService>();
        var payload = new CapturedSelectionPayload(capturedText);

        switch (mode)
        {
            case WindowMode.Optimize:
                navigationService.NavigateTo<OptimizeViewModel, CapturedSelectionPayload>(payload);
                break;
            case WindowMode.Assistant:
                navigationService.NavigateTo<AssistantViewModel, CapturedSelectionPayload>(payload);
                break;
        }
    }

    public void ShowAssistantChat(Guid chatId)
    {
        // Reuse the single assistant window (ShowWindow activates/focuses it), then
        // navigate WITHIN it. Never opens a second window. OnNavigatedToAsync(Guid)
        // routes to the session manager's ActivateAsync, revealing any pending card.
        ShowWindow(WindowMode.Assistant);

        if (!_windows.TryGetValue(WindowMode.Assistant, out var managed))
            return;

        var navigationService = managed.Scope.ServiceProvider.GetRequiredService<INavigationService>();
        navigationService.NavigateTo<AssistantViewModel, Guid>(chatId);
    }

    public void ShowAgentRun(Guid runId) =>
        Pia.Helpers.TaskExtensions.SafeFireAndForget(ShowAgentRunAsync(runId), _logger);

    internal async Task ShowAgentRunAsync(Guid runId)
    {
        var run = await _agentRunService.GetAsync(runId);
        if (run is null)
        {
            // R17: the run cascaded away (its chat was deleted). Retract the stale durable Flow item and
            // show a brief toast — never dereference a missing ChatId.
            _flowService.Retract(runId.ToString());
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null)
                await dispatcher.InvokeAsync(() => ShowStaleRunToast(runId));
            return;
        }

        // Live/completed run: open its hosting chat. The run-progress panel re-embeds via
        // ChatSession.ActiveRunId when the chat re-activates IF still live (OQ1).
        var uiDispatcher = Application.Current?.Dispatcher;
        if (uiDispatcher is not null)
            await uiDispatcher.InvokeAsync(() => ShowAssistantChat(run.ChatId));
        else
            ShowAssistantChat(run.ChatId);
    }

    private void ShowStaleRunToast(Guid runId)
    {
        try
        {
            if (TryFindForegroundSnackbarPresenter() is { } presenter)
                Pia.Helpers.SnackbarActionHelper.ShowSubtleWithAction(
                    presenter,
                    _localizationService["Flow_Run_Title"],
                    _localizationService["Flow_Run_Gone"],
                    _localizationService["Flow_Action_Dismiss"],
                    () => { },
                    Wpf.Ui.Controls.SymbolRegular.Bot24,
                    null,
                    TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show stale-run toast for {RunId}", runId);
        }
    }

    private static Wpf.Ui.Controls.SnackbarPresenter? TryFindForegroundSnackbarPresenter()
    {
        if (Application.Current is null) return null;
        foreach (Window w in Application.Current.Windows)
        {
            if (w.IsActive && w.FindName("RootSnackbarPresenter") is Wpf.Ui.Controls.SnackbarPresenter presenter)
                return presenter;
        }
        return null;
    }

    public void HideWindow(WindowMode mode)
    {
        if (!_windows.TryGetValue(mode, out var managed))
            return;

        var window = managed.Window;

        _logger.LogTrace(
            "HideWindow {Mode} start, state={State}, visibility={Visibility}",
            mode, window.WindowState, window.Visibility);

        window.Visibility = Visibility.Hidden;

        if (window.WindowState != WindowState.Normal)
        {
            window.Dispatcher.BeginInvoke(
                () =>
                {
                    if (window.Visibility == Visibility.Hidden)
                        window.WindowState = WindowState.Normal;
                },
                DispatcherPriority.ContextIdle);
        }

        WindowVisibilityChanged?.Invoke(this, EventArgs.Empty);

        _logger.LogTrace("HideWindow {Mode} done", mode);
    }

    public void HideAllWindows()
    {
        foreach (var mode in _windows.Keys.ToList())
        {
            HideWindow(mode);
        }
    }

    public void CloseAndDisposeAll()
    {
        _isShuttingDown = true;

        foreach (var (_, managed) in _windows)
        {
            managed.Window.PrepareForExit();
            managed.Window.Close();
            WindowClosed?.Invoke(this, managed);
            managed.Dispose();
        }

        _windows.Clear();
    }

    public bool IsVisible(WindowMode mode)
    {
        return _windows.TryGetValue(mode, out var managed)
            && managed.Window.Visibility == Visibility.Visible;
    }

    public bool IsInForeground(WindowMode mode)
    {
        if (!_windows.TryGetValue(mode, out var managed))
            return false;

        var foreground = GetForegroundWindow();
        var windowHandle = new WindowInteropHelper(managed.Window).Handle;
        return foreground == windowHandle;
    }

    public void ShowFirstRunWizard()
    {
        using var scope = _rootProvider.CreateScope();
        var wizard = scope.ServiceProvider.GetRequiredService<Views.FirstRunWizardWindow>();
        wizard.ShowDialog();
    }

    public bool CanDismissWithHotkey(WindowMode mode)
    {
        if (!_windows.TryGetValue(mode, out var managed))
            return false;

        if (mode == WindowMode.Optimize)
        {
            var vm = managed.Scope.ServiceProvider.GetRequiredService<OptimizeViewModel>();
            return string.IsNullOrWhiteSpace(vm.InputText) && !vm.IsComparisonView && !vm.IsOptimizing;
        }

        return false;
    }
}
