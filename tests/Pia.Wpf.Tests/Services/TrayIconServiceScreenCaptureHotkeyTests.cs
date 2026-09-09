using System.Windows.Input;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Registration only — <c>Initialize()</c> is never called, so no tray icon and no host window
/// exist here.</summary>
public class TrayIconServiceScreenCaptureHotkeyTests
{
    private static readonly KeyboardShortcut First = new(KeyModifiers.Control | KeyModifiers.Shift, Key.S, 0x53);
    private static readonly KeyboardShortcut Second = new(KeyModifiers.Control | KeyModifiers.Alt, Key.D, 0x44);

    [Fact]
    public void UpdateScreenCaptureHotkey_RegistersUnderItsOwnId_AndOpensThePickerOnPress()
    {
        var harness = Harness.Create();
        var hotkey = new FakeHotkey();
        harness.Factory.Create(TrayIconService.ScreenCaptureHotkeyId, First).Returns(hotkey);

        var registered = harness.Tray.UpdateScreenCaptureHotkey(First);
        hotkey.Press();

        Assert.True(registered);
        harness.WindowManager.Received(1).ShowAssistantScreenCapturePicker();
    }

    [Fact]
    public void UpdateScreenCaptureHotkey_WhenWindowsRefuses_ReportsFalse_AndKeepsThePreviousOne()
    {
        var harness = Harness.Create();
        var original = new FakeHotkey();
        var reRegistered = new FakeHotkey();
        harness.Factory.Create(TrayIconService.ScreenCaptureHotkeyId, First).Returns(original, reRegistered);
        harness.Factory.Create(TrayIconService.ScreenCaptureHotkeyId, Second).Returns((INativeHotkeyService?)null);
        harness.Tray.UpdateScreenCaptureHotkey(First);

        var registered = harness.Tray.UpdateScreenCaptureHotkey(Second);

        Assert.False(registered);
        Assert.True(original.Disposed);
        harness.Factory.Received(2).Create(TrayIconService.ScreenCaptureHotkeyId, First);
        reRegistered.Press();
        harness.WindowManager.Received(1).ShowAssistantScreenCapturePicker();
    }

    [Fact]
    public void UpdateScreenCaptureHotkey_WhenWindowsRefusesTheFirstEver_ReportsFalse_AndRegistersNothing()
    {
        var harness = Harness.Create();
        harness.Factory.Create(Arg.Any<int>(), Arg.Any<KeyboardShortcut>()).Returns((INativeHotkeyService?)null);

        Assert.False(harness.Tray.UpdateScreenCaptureHotkey(First));
        harness.Factory.Received(1).Create(TrayIconService.ScreenCaptureHotkeyId, First);
    }

    [Fact]
    public void UpdateScreenCaptureHotkey_Null_DisposesTheRegistration_AndReturnsTrue()
    {
        var harness = Harness.Create();
        var hotkey = new FakeHotkey();
        harness.Factory.Create(TrayIconService.ScreenCaptureHotkeyId, First).Returns(hotkey);
        harness.Tray.UpdateScreenCaptureHotkey(First);

        Assert.True(harness.Tray.UpdateScreenCaptureHotkey(null));

        Assert.True(hotkey.Disposed);
        hotkey.Press();
        harness.WindowManager.DidNotReceive().ShowAssistantScreenCapturePicker();
    }

    [Fact]
    public void ScreenCaptureHotkeyId_DoesNotCollideWithTheOtherIds()
    {
        Assert.Equal(101, TrayIconService.ScreenCaptureHotkeyId);
        Assert.DoesNotContain(TrayIconService.ScreenCaptureHotkeyId, Enum.GetValues<WindowMode>().Cast<int>());
    }

    private sealed record Harness(
        TrayIconService Tray, INativeHotkeyServiceFactory Factory, IWindowManagerService WindowManager)
    {
        internal static Harness Create()
        {
            var factory = Substitute.For<INativeHotkeyServiceFactory>();
            var windowManager = Substitute.For<IWindowManagerService>();

            var tray = new TrayIconService(
                Substitute.For<IWindowTrackingService>(),
                windowManager,
                Substitute.For<ISettingsService>(),
                factory,
                Substitute.For<ILocalizationService>(),
                Substitute.For<ISelectedTextService>(),
                Substitute.For<IFastPathOptimizer>());

            return new Harness(tray, factory, windowManager);
        }
    }

    private sealed class FakeHotkey : INativeHotkeyService
    {
        public event Action? HotKeyPressed;

        public bool Disposed { get; private set; }

        public void Press() => HotKeyPressed?.Invoke();

        public void Dispose() => Disposed = true;
    }
}
