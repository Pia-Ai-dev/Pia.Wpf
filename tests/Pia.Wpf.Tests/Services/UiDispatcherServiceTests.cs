using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Pia.Tests.Views;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Every body runs on the host's STA thread with a live <c>Application</c> and <c>CheckAccess() == true</c> — the
/// only configuration where <c>Post</c>'s queueing is distinguishable from <c>PostOrRun</c>'s inline run.
/// </summary>
[Collection("WpfApplicationStatic")]
public class UiDispatcherServiceTests
{
    private static UiDispatcherService CreateSut() =>
        new(NullLogger<UiDispatcherService>.Instance);

    [Fact]
    public void PostOrRun_OnTheUiThread_RunsInlineBeforeItReturns()
    {
        var ranBeforeReturn = WpfStaHost.Run(() =>
        {
            var sut = CreateSut();
            var ran = false;
            sut.PostOrRun(() => ran = true);
            return ran;
        });

        Assert.True(ranBeforeReturn,
            "PostOrRun must run the action INLINE when the caller already holds the UI thread — that is the " +
            "whole reason TranscriptOverlayViewModel.DispatchToUi uses it, and nine callers plus the " +
            "SpeakerModelDownloadUi show-once guard read state the action mutates right after the call.");
    }

    [Fact]
    public void Post_OnTheUiThread_QueuesAndRunsOnTheNextPump()
    {
        // Run(act) → Pump() → Run(observe): Pump() drains from the TEST thread now, so the queued action
        // cannot be observed inside the same host operation that queued it. See WpfStaHost.Pump.
        var ranBox = new bool[1];
        var ranImmediately = WpfStaHost.Run(() =>
        {
            var sut = CreateSut();
            sut.Post(() => ranBox[0] = true);
            return ranBox[0];
        });
        WpfStaHost.Pump();
        var ranAfterPump = WpfStaHost.Run(() => ranBox[0]);

        Assert.False(ranImmediately,
            "Post must ALWAYS queue, even on the UI thread: VoiceModeViewModel posts its silence-timer " +
            "transition specifically so TransitionToProcessingAsync does not run inside Timer.Elapsed right " +
            "after StopSilenceMonitor(). If this fails, Post has been collapsed into PostOrRun.");
        Assert.True(ranAfterPump, "the queued action must still run — Post may never drop work.");
    }

    [Fact]
    public void PostAsync_OnTheUiThread_QueuesAndCompletesWithTheMutationApplied()
    {
        var valueBox = new int[1];
        Task? posted = null;
        var completedImmediately = WpfStaHost.Run(() =>
        {
            var sut = CreateSut();
            posted = sut.PostAsync(() => valueBox[0] = 42);
            return posted.IsCompleted;
        });
        WpfStaHost.Pump();
        var (statusAfterPump, valueAfterPump) =
            WpfStaHost.Run(() => (posted!.Status.ToString(), valueBox[0]));

        Assert.False(completedImmediately, "PostAsync queues; it does not run the action inline.");
        Assert.Equal("RanToCompletion", statusAfterPump);
        Assert.Equal(42, valueAfterPump);
    }

    [Fact]
    public void PostAsync_WhenTheActionThrows_FaultsTheReturnedTask()
    {
        // A net spanning the pump: App's own DispatcherUnhandledException handler is installed in OnStartup,
        // which the host never runs, so a queued failure routed there would kill the whole test process.
        DispatcherUnhandledExceptionEventHandler net = (_, e) => e.Handled = true;
        WpfStaHost.Run(() =>
        {
            WpfStaHost.StaDispatcher.UnhandledException += net;
            return 0;
        });

        bool isFaulted;
        string? exceptionType, message;
        try
        {
            Task? posted = null;
            WpfStaHost.Run(() =>
            {
                var sut = CreateSut();
                posted = sut.PostAsync(() => throw new InvalidOperationException("boom"));
                return 0;
            });
            WpfStaHost.Pump();

            (isFaulted, exceptionType, message) = WpfStaHost.Run(() =>
            {
                // Reading .Exception also MARKS IT OBSERVED, which is the point of the whole member.
                var inner = posted!.Exception?.InnerException;
                return (posted.IsFaulted, inner?.GetType().Name, inner?.Message);
            });
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                WpfStaHost.StaDispatcher.UnhandledException -= net;
                return 0;
            });
        }

        Assert.True(isFaulted,
            "PostAsync must fault its task with the action's exception. Four awaited call sites depend on it: " +
            "AssistantViewModel's working-directory apply (SafeFireAndForget's logger is the only sink), the " +
            "TTS init inside Task.Run (its catch is the only sink), the persona refresh, and the follow-up " +
            "suggestions. If this is red, those failures are silent in production.");
        Assert.Equal("InvalidOperationException", exceptionType);
        Assert.Equal("boom", message);
    }

    [Fact]
    public void Post_WhenTheActionThrows_RunsItAndDoesNotReachTheCaller()
    {
        DispatcherUnhandledExceptionEventHandler net = (_, e) => e.Handled = true;
        WpfStaHost.Run(() =>
        {
            WpfStaHost.StaDispatcher.UnhandledException += net;
            return 0;
        });

        bool reached;
        try
        {
            var ranBox = new bool[1];
            WpfStaHost.Run(() =>
            {
                var sut = CreateSut();
                sut.Post(() =>
                {
                    ranBox[0] = true;
                    throw new InvalidOperationException("boom");
                });
                return 0;
            });

            // Pump() throwing would fail this test: a fire-and-forget failure must not escape into the
            // drain either. It is logged instead (see UiDispatcherService.LogIfFaulted).
            WpfStaHost.Pump();
            reached = WpfStaHost.Run(() => ranBox[0]);
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                WpfStaHost.StaDispatcher.UnhandledException -= net;
                return 0;
            });
        }

        Assert.True(reached,
            "the queued action must have run; Post's contract is that its failure is logged, never rethrown " +
            "into an audio or timer thread and never dropped on the floor.");
    }
}
