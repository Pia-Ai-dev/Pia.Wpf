using System.Globalization;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Screen;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The picker never needs a real desktop: every target and every frame here is synthetic.</summary>
public sealed class ScreenCapturePickerViewModelTests
{
    private readonly IScreenCaptureService _screenCapture = Substitute.For<IScreenCaptureService>();
    private readonly EchoLocalization _localization = new();

    private Func<CaptureTarget, CancellationToken, Task<CaptureResult>> _capture;

    public ScreenCapturePickerViewModelTests()
    {
        _capture = (target, _) => Task.FromResult(Ok(target, 1920, 1080));
        _screenCapture.CaptureAsync(Arg.Any<CaptureTarget>(), Arg.Any<CancellationToken>())
            .Returns(ci => _capture(ci.Arg<CaptureTarget>(), ci.Arg<CancellationToken>()));
    }

    // ---- enumeration ---------------------------------------------------------------------------

    [Fact]
    public async Task InitializeAsync_ListsMonitorsThenWindows_InEnumerationOrder()
    {
        var m1 = Monitor(@"\\.\DISPLAY1", primary: true, 2560, 1440);
        var m2 = Monitor(@"\\.\DISPLAY2", primary: false, 1920, 1080);
        var w1 = Window(11, "notepad", "Quarterly numbers");
        var w2 = Window(22, "code", "plan.md");
        var vm = Sut(m1, m2, w1, w2);

        await vm.InitializeAsync();

        Assert.Equal(2, vm.MonitorTargets.Count);
        Assert.Equal(2, vm.WindowTargets.Count);
        Assert.Equal("ScreenCapturePicker_MonitorLabel(1)", vm.MonitorTargets[0].Label);
        Assert.Equal("ScreenCapturePicker_MonitorLabel(2)", vm.MonitorTargets[1].Label);
        Assert.EndsWith("ScreenCapturePicker_PrimaryDisplay", vm.MonitorTargets[0].Detail, StringComparison.Ordinal);
        Assert.Equal("1920×1080", vm.MonitorTargets[1].Detail);
        Assert.Equal("Quarterly numbers", vm.WindowTargets[0].Label);
        Assert.Equal("notepad · 800×600", vm.WindowTargets[0].Detail);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsEmpty);
        Assert.True(vm.HasMonitors);
        Assert.True(vm.HasWindows);
    }

    [Fact]
    public async Task AutomationKey_IsUniqueAcrossMonitorsAndWindows()
    {
        var vm = Sut(
            Monitor(@"\\.\DISPLAY1", primary: true),
            Monitor(@"\\.\DISPLAY2", primary: false),
            Window(11, "notepad", "a"),
            Window(22, "code", "b"));

        await vm.InitializeAsync();

        string[] keys =
        [
            .. vm.MonitorTargets.Select(r => r.AutomationKey),
            .. vm.WindowTargets.Select(r => r.AutomationKey),
        ];

        Assert.Equal(4, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("DISPLAY1", keys[0]);
        Assert.Equal("DISPLAY2", keys[1]);
        Assert.Equal("11", keys[2]);
        Assert.Equal("22", keys[3]);
    }

    [Fact]
    public async Task InitializeAsync_WhenEnumerationThrows_ShowsEmpty_AndDoesNotThrow()
    {
        _screenCapture.EnumerateTargetsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CaptureTarget>>>(_ => throw new InvalidOperationException("boom"));
        var vm = NewVm();

        await vm.InitializeAsync();

        Assert.True(vm.IsEmpty);
        Assert.False(vm.IsLoading);
        Assert.False(vm.CanCapture);
    }

    // ---- previews ------------------------------------------------------------------------------

    [Fact]
    public async Task Previews_RunOneCaptureAtATime_InListOrder()
    {
        var m1 = Monitor(@"\\.\DISPLAY1", primary: true);
        var w1 = Window(11, "notepad", "a");
        var w2 = Window(22, "code", "b");
        var gates = new Dictionary<CaptureTarget, TaskCompletionSource<CaptureResult>>
        {
            [m1] = new(),
            [w1] = new(),
            [w2] = new(),
        };
        var order = new List<nint>();
        _capture = (target, _) =>
        {
            order.Add(target.Hwnd);
            return gates[target].Task;
        };
        var vm = Sut(m1, w1, w2);

        await vm.InitializeAsync();

        Assert.Equal([(nint)0], order);
        gates[m1].SetResult(Ok(m1, 64, 48));
        await WaitFor(() => order.Count == 2);
        Assert.Equal([(nint)0, 11], order);
        gates[w1].SetResult(Ok(w1, 64, 48));
        await WaitFor(() => order.Count == 3);
        Assert.Equal([(nint)0, 11, 22], order);

        gates[w2].SetResult(Ok(w2, 64, 48));
        await vm.PendingPreviews;
    }

    [Fact]
    public async Task Previews_ASuccessfulFrame_BecomesA240PixelFrozenThumbnail()
    {
        var w1 = Window(11, "notepad", "a");
        var vm = Sut(w1);

        await vm.InitializeAsync();
        await vm.PendingPreviews;

        var row = vm.WindowTargets[0];
        Assert.NotNull(row.Thumbnail);
        Assert.Equal(240, row.Thumbnail!.PixelWidth);
        Assert.True(row.Thumbnail.IsFrozen);
        Assert.False(row.IsPreviewPending);
        Assert.Null(row.PreviewHint);
    }

    [Fact]
    public async Task Previews_ARefusedFrame_ShowsTheReasonInsteadOfAThumbnail()
    {
        var w1 = Window(11, "notepad", "a");
        _capture = (target, _) => Task.FromResult(CaptureResult.Failed(target, CaptureFailureReason.UniformFrame));
        var vm = Sut(w1);

        await vm.InitializeAsync();
        await vm.PendingPreviews;

        var row = vm.WindowTargets[0];
        Assert.Equal("Msg_Screen_UniformFrame", row.PreviewHint);
        Assert.Null(row.Thumbnail);
        Assert.False(row.IsPreviewPending);

        // Still selectable: a window that came back blank a second ago may not next time.
        row.IsSelected = true;
        Assert.True(vm.CanCapture);
    }

    [Fact]
    public async Task Previews_MinimizedWindows_AreMarkedWithoutBeingCaptured()
    {
        var w1 = Window(11, "notepad", "a", minimized: true);
        var vm = Sut(w1);

        await vm.InitializeAsync();
        await vm.PendingPreviews;

        var row = vm.WindowTargets[0];
        await _screenCapture.DidNotReceive().CaptureAsync(w1, Arg.Any<CancellationToken>());
        Assert.Equal("Msg_Screen_Minimized", row.PreviewHint);

        row.IsSelected = true;
        Assert.Same(row, vm.SelectedTarget);
        Assert.False(vm.CanCapture);
    }

    [Fact]
    public async Task Previews_AThrowingCapture_MarksTheRow_AndContinuesWithTheNext()
    {
        var w1 = Window(11, "notepad", "a");
        var w2 = Window(22, "code", "b");
        _capture = (target, _) => target.Hwnd == 11
            ? throw new InvalidOperationException("boom")
            : Task.FromResult(Ok(target, 480, 270));
        var vm = Sut(w1, w2);

        await vm.InitializeAsync();
        await vm.PendingPreviews;

        Assert.Equal("Msg_Screen_NativeError", vm.WindowTargets[0].PreviewHint);
        Assert.NotNull(vm.WindowTargets[1].Thumbnail);
    }

    // ---- selection -----------------------------------------------------------------------------

    [Fact]
    public async Task SelectingARow_DeselectsTheOthers_AndEnablesCapture()
    {
        var vm = Sut(Window(11, "notepad", "a"), Window(22, "code", "b"));
        await vm.InitializeAsync();
        await vm.PendingPreviews;
        var row1 = vm.WindowTargets[0];
        var row2 = vm.WindowTargets[1];

        row1.IsSelected = true;
        row2.IsSelected = true;

        Assert.False(row1.IsSelected);
        Assert.Same(row2, vm.SelectedTarget);
        Assert.True(vm.CanCapture);

        row2.IsSelected = false;

        Assert.Null(vm.SelectedTarget);
        Assert.False(vm.CanCapture);
    }

    /// <summary>The arrow keys move a selection but cannot start one, so the list opens with one on.</summary>
    [Fact]
    public async Task InitializeAsync_SelectsTheFirstWindowThatCanBeCaptured()
    {
        var vm = Sut(
            Monitor(@"\\.\DISPLAY1", primary: true, 2560, 1440),
            Window(11, "notepad", "a", minimized: true),
            Window(22, "code", "b"),
            Window(33, "explorer", "c"));

        await vm.InitializeAsync();

        Assert.Same(vm.WindowTargets[1], vm.SelectedTarget);
        Assert.True(vm.CanCapture);
    }

    [Fact]
    public async Task InitializeAsync_WithNothingCapturable_SelectsNothing()
    {
        var vm = Sut(
            Monitor(@"\\.\DISPLAY1", primary: true, 2560, 1440),
            Window(11, "notepad", "a", minimized: true));

        await vm.InitializeAsync();

        Assert.Null(vm.SelectedTarget);
        Assert.False(vm.CanCapture);
    }

    // ---- naming a window for the allowlist ------------------------------------------------------

    [Fact]
    public async Task NamesAWindow_ListsNoDisplays_AndSaysSoInEveryLabel()
    {
        _screenCapture.EnumerateTargetsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<CaptureTarget>>(
                [Monitor(@"\\.\DISPLAY1", primary: true, 2560, 1440), Window(11, "notepad", "a")]);
        var vm = new ScreenCapturePickerViewModel(
            _screenCapture, _localization, NullLogger<ScreenCapturePickerViewModel>.Instance)
        {
            NamesAWindow = true,
        };

        await vm.InitializeAsync();

        Assert.Empty(vm.MonitorTargets);
        Assert.Single(vm.WindowTargets);
        Assert.Equal("ScreenCapturePicker_PickTitle", vm.DialogTitle);
        Assert.Equal("ScreenCapturePicker_Use", vm.ConfirmText);
        Assert.Equal("ScreenCapturePicker_PickHint", vm.Hint);
    }

    [Fact]
    public void Capturing_UsesTheCaptureLabels()
    {
        var vm = NewVm();

        Assert.Equal("ScreenCapturePicker_Title", vm.DialogTitle);
        Assert.Equal("ScreenCapturePicker_Capture", vm.ConfirmText);
        Assert.Equal("ScreenCapturePicker_Hint", vm.Hint);
    }

    // ---- the confirm capture -------------------------------------------------------------------

    [Fact]
    public async Task CaptureSelectedAsync_StopsThePreviewLoop_BeforeItCaptures()
    {
        var m1 = Monitor(@"\\.\DISPLAY1", primary: true);
        var w1 = Window(11, "notepad", "a");
        var monitorPreview = new TaskCompletionSource<CaptureResult>();
        var previewWasSettledFirst = false;
        _capture = (target, ct) =>
        {
            if (target.Kind == CaptureTargetKind.Monitor)
            {
                ct.Register(() => monitorPreview.TrySetCanceled());
                return monitorPreview.Task;
            }

            previewWasSettledFirst = monitorPreview.Task.IsCompleted;
            return Task.FromResult(Ok(target, 64, 48));
        };
        var vm = Sut(m1, w1);

        await vm.InitializeAsync();
        vm.WindowTargets[0].IsSelected = true;
        var result = await vm.CaptureSelectedAsync(TestContext.Current.CancellationToken);

        Assert.True(previewWasSettledFirst);
        Assert.NotNull(result);
        Assert.True(result!.IsSuccess);
        Assert.Equal(64, result.Width);
        Assert.False(vm.IsCapturing);
        Assert.True(vm.PendingPreviews.IsCompleted);
    }

    [Fact]
    public async Task CaptureSelectedAsync_WithNothingSelected_ReturnsNull_AndCapturesNothing()
    {
        var w1 = Window(11, "notepad", "a");
        var vm = Sut(w1);
        await vm.InitializeAsync();
        await vm.PendingPreviews;
        vm.WindowTargets[0].IsSelected = false;
        _screenCapture.ClearReceivedCalls();

        Assert.Null(await vm.CaptureSelectedAsync(TestContext.Current.CancellationToken));
        await _screenCapture.DidNotReceive().CaptureAsync(Arg.Any<CaptureTarget>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CaptureSelectedAsync_AMinimizedRow_ReturnsNull()
    {
        var w1 = Window(11, "notepad", "a", minimized: true);
        var vm = Sut(w1);
        await vm.InitializeAsync();
        await vm.PendingPreviews;
        vm.WindowTargets[0].IsSelected = true;

        Assert.Null(await vm.CaptureSelectedAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CaptureSelectedAsync_ReturnsTheRefusal_NotAnException()
    {
        var w1 = Window(11, "notepad", "a");
        _capture = (target, _) => Task.FromResult(CaptureResult.Failed(target, CaptureFailureReason.Timeout));
        var vm = Sut(w1);
        await vm.InitializeAsync();
        await vm.PendingPreviews;
        vm.WindowTargets[0].IsSelected = true;

        var result = await vm.CaptureSelectedAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result!.IsSuccess);
        Assert.Equal(CaptureFailureReason.Timeout, result.Reason);
    }

    // ---- refresh and cancel --------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_ReEnumerates_AndDropsStaleRows()
    {
        var m1 = Monitor(@"\\.\DISPLAY1", primary: true);
        var w1 = Window(11, "notepad", "a");
        _screenCapture.EnumerateTargetsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<CaptureTarget>>([m1, w1], [m1]);
        var vm = NewVm();
        await vm.InitializeAsync();
        await vm.PendingPreviews;
        var stale = vm.WindowTargets[0];

        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.PendingPreviews;

        Assert.Single(vm.MonitorTargets);
        Assert.Empty(vm.WindowTargets);
        Assert.Null(vm.SelectedTarget);

        stale.IsSelected = true;
        Assert.Null(vm.SelectedTarget);
    }

    [Fact]
    public async Task Cancel_IsIdempotent_AndStopsTheLoop()
    {
        var w1 = Window(11, "notepad", "a");
        var w2 = Window(22, "code", "b");
        var gate = new TaskCompletionSource<CaptureResult>();
        _capture = (target, ct) =>
        {
            if (target.Hwnd != 11) return Task.FromResult(Ok(target, 64, 48));
            ct.Register(() => gate.TrySetCanceled());
            return gate.Task;
        };
        var vm = Sut(w1, w2);
        await vm.InitializeAsync();

        vm.Cancel();
        vm.Cancel();
        await vm.PendingPreviews;

        await _screenCapture.DidNotReceive().CaptureAsync(w2, Arg.Any<CancellationToken>());
        vm.Cancel();
    }

    // ---- helpers -------------------------------------------------------------------------------

    private ScreenCapturePickerViewModel Sut(params CaptureTarget[] targets)
    {
        _screenCapture.EnumerateTargetsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<CaptureTarget>>(targets);
        return NewVm();
    }

    private ScreenCapturePickerViewModel NewVm() => new(
        _screenCapture, _localization, NullLogger<ScreenCapturePickerViewModel>.Instance);

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(5);
        Assert.True(condition());
    }

    private static CaptureTarget Monitor(string deviceId, bool primary, int width = 1920, int height = 1080) =>
        new(CaptureTargetKind.Monitor, 0, deviceId, new PixelRect(0, 0, width, height), "", "", primary, false);

    private static CaptureTarget Window(nint hwnd, string process, string title, bool minimized = false) =>
        new(CaptureTargetKind.Window, hwnd, "", new PixelRect(0, 0, 800, 600), process, title, false, minimized);

    private static CaptureResult Ok(CaptureTarget target, int width, int height)
    {
        var stride = width * 4;
        var frame = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgr32, null, new byte[stride * height], stride);
        frame.Freeze();
        return CaptureResult.Success(target, frame);
    }

    private sealed class EchoLocalization : ILocalizationService
    {
        public TargetLanguage CurrentLanguage => TargetLanguage.EN;
        public CultureInfo Culture => CultureInfo.InvariantCulture;

#pragma warning disable CS0067
        public event EventHandler<TargetLanguage>? LanguageChanged;
#pragma warning restore CS0067

        public void SetLanguage(TargetLanguage language) { }

        public string this[string key] => key;

        public string Format(string key, params object[] args) => $"{key}({string.Join(",", args)})";
    }
}
