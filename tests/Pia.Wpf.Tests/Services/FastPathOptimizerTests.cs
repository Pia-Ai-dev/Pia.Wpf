using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class FastPathOptimizerTests
{
    private readonly IWindowManagerService _windowManager = Substitute.For<IWindowManagerService>();
    private readonly IWindowTrackingService _windowTracking = Substitute.For<IWindowTrackingService>();
    private readonly ISelectedTextService _selectedText = Substitute.For<ISelectedTextService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    [Fact]
    public async Task RunAsync_WhenAlreadyRunning_SecondRunIsNoOp()
    {
        var handle = new FakeFastPathHandle();
        var captureCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureGate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _windowManager.ShowOptimizeAndGetViewModelAsync().Returns(handle);
        _selectedText.CaptureAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            captureCalled.TrySetResult(true);
            return captureGate.Task;
        });

        var sut = CreateSut();
        var first = sut.RunAsync();
        await captureCalled.Task;

        var second = sut.RunAsync();
        captureGate.SetResult(null);

        await Task.WhenAll(first, second);

        await _windowManager.Received(1).ShowOptimizeAndGetViewModelAsync();
    }

    [Fact]
    public async Task RunAsync_WhenNoCapturedText_ShowsSnackbarAndKeepsWindowOpen()
    {
        var handle = new FakeFastPathHandle();
        _windowManager.ShowOptimizeAndGetViewModelAsync().Returns(handle);
        _selectedText.CaptureAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        await CreateSut().RunAsync();

        Assert.Equal("Msg_FastPath_NoContent", handle.LastSnackbarKey);
        Assert.Equal(0, handle.OptimizeCalls);
        Assert.Equal(0, handle.AcceptCalls);
        _windowManager.DidNotReceive().HideWindow(WindowMode.Optimize);
    }

    [Fact]
    public async Task RunAsync_WhenOptimizeAndAcceptSucceed_HidesWindow()
    {
        var handle = new FakeFastPathHandle { OptimizeResult = true, AcceptResult = true };
        _windowManager.ShowOptimizeAndGetViewModelAsync().Returns(handle);
        _selectedText.CaptureAsync(Arg.Any<CancellationToken>()).Returns("selected text");
        _settings.GetSettingsAsync().Returns(new AppSettings { DefaultOutputAction = OutputAction.CopyToClipboard });

        await CreateSut().RunAsync();

        Assert.Equal("selected text", handle.CapturedInput);
        Assert.Equal(1, handle.OptimizeCalls);
        Assert.Equal(1, handle.AcceptCalls);
        _windowManager.Received(1).HideWindow(WindowMode.Optimize);
    }

    [Fact]
    public async Task RunAsync_WhenOptimizeFails_KeepsWindowOpenAndDoesNotAccept()
    {
        var handle = new FakeFastPathHandle { OptimizeResult = false };
        _windowManager.ShowOptimizeAndGetViewModelAsync().Returns(handle);
        _selectedText.CaptureAsync(Arg.Any<CancellationToken>()).Returns("selected text");

        await CreateSut().RunAsync();

        Assert.Equal(1, handle.OptimizeCalls);
        Assert.Equal(0, handle.AcceptCalls);
        _windowManager.DidNotReceive().HideWindow(WindowMode.Optimize);
    }

    [Fact]
    public async Task RunAsync_WhenPasteModeHasNoTrackedWindow_ShowsSnackbarAndKeepsComparisonView()
    {
        var handle = new FakeFastPathHandle { OptimizeResult = true };
        _windowManager.ShowOptimizeAndGetViewModelAsync().Returns(handle);
        _selectedText.CaptureAsync(Arg.Any<CancellationToken>()).Returns("selected text");
        _settings.GetSettingsAsync().Returns(new AppSettings { DefaultOutputAction = OutputAction.PasteToPreviousWindow });
        _windowTracking.HasTrackedWindow.Returns(false);

        await CreateSut().RunAsync();

        Assert.True(handle.IsComparisonView);
        Assert.Equal("Msg_FastPath_NoTargetWindow", handle.LastSnackbarKey);
        Assert.Equal(0, handle.AcceptCalls);
        _windowManager.DidNotReceive().HideWindow(WindowMode.Optimize);
    }

    [Fact]
    public async Task RunAsync_WhenInputAlreadyHasText_ShowsInsertAnywaySnackbarAndDoesNotOptimize()
    {
        var handle = new FakeFastPathHandle { InputText = "existing draft" };
        _windowManager.ShowOptimizeAndGetViewModelAsync().Returns(handle);
        _selectedText.CaptureAsync(Arg.Any<CancellationToken>()).Returns("captured selection");

        await CreateSut().RunAsync();

        Assert.Equal(1, handle.InsertAnywaySnackbarShownCount);
        Assert.Equal("captured selection", handle.InsertAnywayCapturedText);
        Assert.NotNull(handle.InsertAnywayCallback);
        Assert.Equal(0, handle.OptimizeCalls);
        Assert.Equal(0, handle.AcceptCalls);
        Assert.Equal("existing draft", handle.InputText); // input untouched until user clicks
        _windowManager.DidNotReceive().HideWindow(WindowMode.Optimize);
    }

    [Fact]
    public async Task InsertAnywayCallback_ReplacesInputAndRunsPipeline()
    {
        var handle = new FakeFastPathHandle { InputText = "existing draft", OptimizeResult = true, AcceptResult = true };
        _windowManager.ShowOptimizeAndGetViewModelAsync().Returns(handle);
        _selectedText.CaptureAsync(Arg.Any<CancellationToken>()).Returns("captured selection");
        _settings.GetSettingsAsync().Returns(new AppSettings { DefaultOutputAction = OutputAction.CopyToClipboard });

        var sut = CreateSut();
        await sut.RunAsync();
        Assert.NotNull(handle.InsertAnywayCallback);

        await handle.InsertAnywayCallback!();

        Assert.Equal("captured selection", handle.CapturedInput);
        Assert.Equal(1, handle.OptimizeCalls);
        Assert.Equal(1, handle.AcceptCalls);
        await _windowManager.Received(2).ShowOptimizeAndGetViewModelAsync(); // initial + continuation
        _windowManager.Received(1).HideWindow(WindowMode.Optimize);
    }

    [Fact]
    public async Task InsertAnywayCallback_WhenAnotherRunActive_IsNoOp()
    {
        var handle = new FakeFastPathHandle { InputText = "existing draft", OptimizeResult = true, AcceptResult = true };
        _windowManager.ShowOptimizeAndGetViewModelAsync().Returns(handle);
        _selectedText.CaptureAsync(Arg.Any<CancellationToken>()).Returns("captured selection");
        _settings.GetSettingsAsync().Returns(new AppSettings { DefaultOutputAction = OutputAction.CopyToClipboard });

        var sut = CreateSut();
        await sut.RunAsync();
        Assert.NotNull(handle.InsertAnywayCallback);

        // Start a second RunAsync that blocks inside CaptureAsync - keeps _isRunning held.
        var captureGate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _selectedText.CaptureAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            captureCalled.TrySetResult(true);
            return captureGate.Task;
        });

        var blocking = sut.RunAsync();
        await captureCalled.Task;

        // While the blocking run holds the guard, the Insert-Anyway click should be a no-op.
        var beforeOptimizeCalls = handle.OptimizeCalls;
        var beforeAcceptCalls = handle.AcceptCalls;
        await handle.InsertAnywayCallback!();

        Assert.Equal(beforeOptimizeCalls, handle.OptimizeCalls);
        Assert.Equal(beforeAcceptCalls, handle.AcceptCalls);

        captureGate.SetResult(null);
        await blocking;
    }

    private FastPathOptimizerService CreateSut()
    {
        return new FastPathOptimizerService(
            NullLogger<FastPathOptimizerService>.Instance,
            _windowManager,
            _windowTracking,
            _selectedText,
            _settings);
    }

    private sealed class FakeFastPathHandle : IOptimizeFastPathHandle
    {
        public Task ReadyAsync { get; } = Task.CompletedTask;
        public string InputText { get; set; } = string.Empty;
        public string CapturedInput { get; private set; } = string.Empty;
        public string OptimizedText { get; private set; } = string.Empty;
        public bool IsComparisonView { get; private set; }
        public bool IsOptimizing { get; set; }
        public bool OptimizeResult { get; set; }
        public bool AcceptResult { get; set; }
        public int OptimizeCalls { get; private set; }
        public int AcceptCalls { get; private set; }
        public string? LastSnackbarKey { get; private set; }
        public string? InsertAnywayCapturedText { get; private set; }
        public Func<Task>? InsertAnywayCallback { get; private set; }
        public int InsertAnywaySnackbarShownCount { get; private set; }

        public Task ShowOptimizingDialogAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void PrepareForFastPath()
        {
            OptimizedText = string.Empty;
            IsComparisonView = false;
        }

        public Task<bool> RunFastPathOptimizeAsync(CancellationToken externalCt = default)
        {
            OptimizeCalls++;
            CapturedInput = InputText;
            IsComparisonView = OptimizeResult;
            OptimizedText = OptimizeResult ? "optimized text" : string.Empty;
            return Task.FromResult(OptimizeResult);
        }

        public Task<bool> RunFastPathAcceptAsync()
        {
            AcceptCalls++;
            if (AcceptResult)
            {
                InputText = string.Empty;
                OptimizedText = string.Empty;
                IsComparisonView = false;
            }

            return Task.FromResult(AcceptResult);
        }

        public void ShowFastPathSnackbar(string messageKey)
        {
            LastSnackbarKey = messageKey;
        }

        public void ShowFastPathInsertAnywaySnackbar(string capturedText, Func<Task> onInsertAnyway)
        {
            InsertAnywaySnackbarShownCount++;
            InsertAnywayCapturedText = capturedText;
            InsertAnywayCallback = onInsertAnyway;
            LastSnackbarKey = "Msg_SelectionNotPastedInputNotEmpty";
        }
    }
}
