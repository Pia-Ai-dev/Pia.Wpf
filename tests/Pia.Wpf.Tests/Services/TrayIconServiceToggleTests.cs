using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>A minimized window sits in the taskbar, so the tray toggle has to restore it, not hide it.</summary>
public class TrayIconServiceToggleTests
{
    [Fact]
    public void ToggleWindow_WhenMinimized_ShowsInsteadOfHiding()
    {
        var windows = Create(out var tray);
        windows.IsVisible(WindowMode.Assistant).Returns(true);
        windows.IsMinimized(WindowMode.Assistant).Returns(true);

        tray.ToggleWindow(WindowMode.Assistant);

        windows.Received(1).ShowWindow(WindowMode.Assistant);
        windows.DidNotReceive().HideWindow(WindowMode.Assistant);
    }

    [Fact]
    public void ToggleWindow_WhenShown_Hides()
    {
        var windows = Create(out var tray);
        windows.IsVisible(WindowMode.Assistant).Returns(true);
        windows.IsMinimized(WindowMode.Assistant).Returns(false);

        tray.ToggleWindow(WindowMode.Assistant);

        windows.Received(1).HideWindow(WindowMode.Assistant);
        windows.DidNotReceive().ShowWindow(WindowMode.Assistant);
    }

    [Fact]
    public async Task ToggleDefaultWindow_WhenMinimized_ShowsInsteadOfHiding()
    {
        var windows = Create(out var tray, out var settings);
        settings.GetSettingsAsync().Returns(new AppSettings { DefaultWindowMode = WindowMode.Assistant });
        windows.IsVisible(WindowMode.Assistant).Returns(true);
        windows.IsMinimized(WindowMode.Assistant).Returns(true);

        await tray.ToggleDefaultWindowAsync();

        windows.Received(1).ShowWindow(WindowMode.Assistant);
        windows.DidNotReceive().HideWindow(WindowMode.Assistant);
    }

    private static IWindowManagerService Create(out TrayIconService tray) =>
        Create(out tray, out _);

    private static IWindowManagerService Create(out TrayIconService tray, out ISettingsService settings)
    {
        var windows = Substitute.For<IWindowManagerService>();
        settings = Substitute.For<ISettingsService>();

        tray = new TrayIconService(
            Substitute.For<IWindowTrackingService>(),
            windows,
            settings,
            Substitute.For<INativeHotkeyServiceFactory>(),
            Substitute.For<ILocalizationService>(),
            Substitute.For<ISelectedTextService>(),
            Substitute.For<IFastPathOptimizer>());

        return windows;
    }
}
