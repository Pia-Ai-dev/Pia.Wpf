using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Pia.Models;
using Pia.Services.Interfaces;
using Wpf.Ui.Tray;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Pia.Services;

public class TrayIconService : NotifyIconService, ITrayIconService, IDisposable
{
    private Window? _trayHostWindow;
    private readonly IWindowTrackingService _windowTrackingService;
    private readonly IWindowManagerService _windowManagerService;
    private readonly ISettingsService _settingsService;
    private readonly INativeHotkeyServiceFactory _hotkeyServiceFactory;
    private readonly ILocalizationService _localizationService;
    private readonly ISelectedTextService _selectedTextService;
    private readonly IFastPathOptimizer _fastPathOptimizer;
    private readonly Dictionary<WindowMode, INativeHotkeyService> _hotkeyServices = new();
    private INativeHotkeyService? _fastPathHotkeyService;
    private DateTime _lastHotkeyOpenTime = DateTime.MinValue;
    private const int FastPathHotkeyId = 100;
    private static readonly TimeSpan HotkeyDebounceInterval = TimeSpan.FromMilliseconds(500);
    private MenuItem? _optimizeMenuItem;
    private MenuItem? _assistantMenuItem;
    private MenuItem? _exitMenuItem;

    public TrayIconService(
        IWindowTrackingService windowTrackingService,
        IWindowManagerService windowManagerService,
        ISettingsService settingsService,
        INativeHotkeyServiceFactory hotkeyServiceFactory,
        ILocalizationService localizationService,
        ISelectedTextService selectedTextService,
        IFastPathOptimizer fastPathOptimizer)
    {
        _windowTrackingService = windowTrackingService;
        _windowManagerService = windowManagerService;
        _settingsService = settingsService;
        _hotkeyServiceFactory = hotkeyServiceFactory;
        _localizationService = localizationService;
        _selectedTextService = selectedTextService;
        _fastPathOptimizer = fastPathOptimizer;

        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public void Initialize()
    {
        if (IsRegistered)
            return;

        // The tray library activates this host window on tray-icon clicks.
        // ResizeMode.NoResize removes the resize cursor; off-screen placement
        // ensures it can never appear as a "ghost circle" even if shown.
        _trayHostWindow = new Window
        {
            Style = null,
            AllowsTransparency = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = 1,
            Height = 1,
        };
        _trayHostWindow.Show();
        _trayHostWindow.Hide();

        SetParentWindow(_trayHostWindow);

        Icon = new BitmapImage(
            new Uri("pack://application:,,,/Resources/Icons/Pia.ico"));
        TooltipText = _localizationService["Tray_Tooltip"];

        var contextMenu = new ContextMenu();
        contextMenu.Opened += (_, _) => RefreshMenuItems();

        _optimizeMenuItem = new MenuItem { Header = _localizationService["Tray_OpenOptimize"] };
        _optimizeMenuItem.Click += (_, _) => ToggleWindow(WindowMode.Optimize);

        _assistantMenuItem = new MenuItem { Header = _localizationService["Tray_OpenAssistant"] };
        _assistantMenuItem.Click += (_, _) => ToggleWindow(WindowMode.Assistant);

        _exitMenuItem = new MenuItem { Header = _localizationService["Tray_Exit"] };
        _exitMenuItem.Click += (_, _) => ExitApplication();

        contextMenu.Items.Add(_optimizeMenuItem);
        contextMenu.Items.Add(_assistantMenuItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(_exitMenuItem);

        ContextMenu = contextMenu;

        Register();

        _windowManagerService.WindowVisibilityChanged += (_, _) => UpdateTooltip();

        _ = RegisterAllHotkeysAsync();
    }

    protected override void OnLeftDoubleClick()
    {
        base.OnLeftDoubleClick();
        _ = ToggleDefaultWindowAsync();
    }

    private async Task ToggleDefaultWindowAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        var defaultMode = settings.DefaultWindowMode;

        if (_windowManagerService.IsVisible(defaultMode))
            _windowManagerService.HideWindow(defaultMode);
        else
            _windowManagerService.ShowWindow(defaultMode);
    }

    public void UpdateHotkey(WindowMode mode, KeyboardShortcut? shortcut)
    {
        UnregisterHotkey(mode);

        if (shortcut != null)
            RegisterHotkey(mode, shortcut);
    }

    public void UpdateFastPathHotkey(KeyboardShortcut? shortcut)
    {
        UnregisterFastPathHotkey();

        if (shortcut != null)
            RegisterFastPathHotkey(shortcut);
    }

    public void Dispose()
    {
        _localizationService.LanguageChanged -= OnLanguageChanged;

        foreach (var service in _hotkeyServices.Values)
            service.Dispose();
        _hotkeyServices.Clear();
        UnregisterFastPathHotkey();

        Unregister();

        _trayHostWindow?.Close();
        _trayHostWindow = null;
    }

    private async Task RegisterAllHotkeysAsync()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();

            RegisterHotkey(WindowMode.Optimize, settings.OptimizeHotkey);

            if (settings.AssistantHotkey != null)
                RegisterHotkey(WindowMode.Assistant, settings.AssistantHotkey);

            if (settings.FastPathHotkey != null)
                RegisterFastPathHotkey(settings.FastPathHotkey);
        }
        catch
        {
            // Silently fail if hotkey registration fails during initialization
        }
    }

    private void RegisterHotkey(WindowMode mode, KeyboardShortcut shortcut)
    {
        UnregisterHotkey(mode);

        var service = _hotkeyServiceFactory.Create((int)mode, shortcut);

        if (service != null)
        {
            service.HotKeyPressed += () => OnHotkeyPressed(mode);
            _hotkeyServices[mode] = service;
        }
    }

    private void UnregisterHotkey(WindowMode mode)
    {
        if (_hotkeyServices.Remove(mode, out var existing))
            existing.Dispose();
    }

    private void RegisterFastPathHotkey(KeyboardShortcut shortcut)
    {
        UnregisterFastPathHotkey();

        var service = _hotkeyServiceFactory.Create(FastPathHotkeyId, shortcut);
        if (service is null)
            return;

        service.HotKeyPressed += OnFastPathHotkeyPressed;
        _fastPathHotkeyService = service;
    }

    private void UnregisterFastPathHotkey()
    {
        if (_fastPathHotkeyService is null)
            return;

        _fastPathHotkeyService.HotKeyPressed -= OnFastPathHotkeyPressed;
        _fastPathHotkeyService.Dispose();
        _fastPathHotkeyService = null;
    }

    private void OnFastPathHotkeyPressed()
    {
        _ = _fastPathOptimizer.RunAsync();
    }

    private void OnHotkeyPressed(WindowMode mode)
    {
        if (_windowManagerService.IsVisible(mode))
        {
            if (_windowManagerService.IsInForeground(mode))
            {
                if (_windowManagerService.CanDismissWithHotkey(mode))
                    _windowManagerService.HideWindow(mode);
            }
            else
            {
                _windowTrackingService.TrackWindowAtCursor();
                _ = ShowWindowWithOptionalSelectionAsync(mode);
            }
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastHotkeyOpenTime < HotkeyDebounceInterval)
            return;
        _lastHotkeyOpenTime = now;

        _windowTrackingService.TrackWindowAtCursor();
        _ = ShowWindowWithOptionalSelectionAsync(mode);
    }

    private async Task ShowWindowWithOptionalSelectionAsync(WindowMode mode)
    {
        var captured = await TryCaptureSelectedTextAsync();
        if (!string.IsNullOrEmpty(captured))
            _windowManagerService.ShowWindowWithSelection(mode, captured);
        else
            _windowManagerService.ShowWindow(mode);
    }

    private async Task<string?> TryCaptureSelectedTextAsync()
    {
        AppSettings settings;
        try
        {
            settings = await _settingsService.GetSettingsAsync();
        }
        catch
        {
            return null;
        }

        if (!settings.AutoCaptureSelectedText)
            return null;

        try
        {
            return await _selectedTextService.CaptureAsync();
        }
        catch
        {
            return null;
        }
    }

    private void UpdateTooltip()
    {
        var count = 0;
        foreach (WindowMode mode in Enum.GetValues<WindowMode>())
        {
            if (_windowManagerService.IsVisible(mode))
                count++;
        }

        TooltipText = count > 0
            ? _localizationService.Format("Tray_TooltipWindowCount", count)
            : _localizationService["Tray_TooltipIdle"];
    }

    private void RefreshMenuItems()
    {
        UpdateMenuItem(_optimizeMenuItem, WindowMode.Optimize, "Tray_OpenOptimize", "Tray_CloseOptimize");
        UpdateMenuItem(_assistantMenuItem, WindowMode.Assistant, "Tray_OpenAssistant", "Tray_CloseAssistant");
    }

    private void UpdateMenuItem(MenuItem? menuItem, WindowMode mode, string openKey, string closeKey)
    {
        if (menuItem is null)
            return;

        var isVisible = _windowManagerService.IsVisible(mode);
        menuItem.Header = isVisible
            ? _localizationService[closeKey]
            : _localizationService[openKey];
    }

    private void ToggleWindow(WindowMode mode)
    {
        if (_windowManagerService.IsVisible(mode))
            _windowManagerService.HideWindow(mode);
        else
            _windowManagerService.ShowWindow(mode);
    }

    private void OnLanguageChanged(object? sender, TargetLanguage e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            RefreshMenuItems();
            if (_exitMenuItem is not null)
                _exitMenuItem.Header = _localizationService["Tray_Exit"];
            UpdateTooltip();
        });
    }

    private void ExitApplication()
    {
        _windowManagerService.CloseAndDisposeAll();
        Unregister();
        Application.Current.Shutdown();
    }
}
