using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services;
using Pia.Tests.Views;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Pins the three member semantics <see cref="UiDispatcherService"/>'s callers actually depend on. Until
/// this file existed they were asserted only in comments: every migrated ViewModel is tested through
/// <see cref="InlineUiDispatcher"/>, whose <c>Post</c> and <c>PostOrRun</c> are the same one-liner, so the
/// queue-vs-inline distinction the design leans on was enforced by nothing. Collapsing <c>Post</c> into
/// <c>PostOrRun</c> — which would run <c>VoiceModeViewModel</c>'s silence-timer lambda, and with it
/// <c>TransitionToProcessingAsync</c>, synchronously inside <c>Timer.Elapsed</c> — kept the whole suite
/// green. Now it does not.
/// <para>
/// Every body runs INSIDE <see cref="WpfStaHost.Run{T}"/>, i.e. on the host's STA thread with a live
/// <c>Application</c> and <c>CheckAccess() == true</c>. That is deliberate: it is the only configuration
/// where queue-vs-inline is observable, and it needs no cross-thread wait, so nothing here can hang
/// (<c>Run</c> is bounded, and every drain is an explicit <see cref="WpfStaHost.Pump"/>).
/// </para>
/// <para>
/// NOT covered, and not coverable: the null-<c>Application</c> fallback — <c>Application.Current</c> is
/// process-wide and, once the host has created it, can never be unset. That branch is exercised instead by
/// every other test in the suite, indirectly, through <see cref="InlineUiDispatcher"/>. The LOG LINE that
/// <c>Post</c>/<c>PostOrRun</c> write for a queued failure is not asserted either (it is written from a
/// thread-pool continuation, and waiting on one would trade a real assertion for a flake); what is asserted
/// is the observable half — that the failure does not reach the caller and does not break the pump.
/// </para>
/// <para>
/// Authored on macOS, where the test host cannot execute. NEVER RUN. If the first Windows run reds
/// <see cref="PostAsync_WhenTheActionThrows_FaultsTheReturnedTask"/>, that is not a flake: it would mean
/// <c>Dispatcher.InvokeAsync</c> does not deliver the action's exception on the operation's task, which is
/// the single assumption <c>PostAsync</c> is built on, and four awaited call sites would need rethinking.
/// </para>
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
        var (ranImmediately, ranAfterPump) = WpfStaHost.Run(() =>
        {
            var sut = CreateSut();
            var ran = false;
            sut.Post(() => ran = true);
            var immediately = ran;
            WpfStaHost.Pump();
            return (immediately, ran);
        });

        Assert.False(ranImmediately,
            "Post must ALWAYS queue, even on the UI thread: VoiceModeViewModel posts its silence-timer " +
            "transition specifically so TransitionToProcessingAsync does not run inside Timer.Elapsed right " +
            "after StopSilenceMonitor(). If this fails, Post has been collapsed into PostOrRun.");
        Assert.True(ranAfterPump, "the queued action must still run — Post may never drop work.");
    }

    [Fact]
    public void PostAsync_OnTheUiThread_QueuesAndCompletesWithTheMutationApplied()
    {
        var (completedImmediately, statusAfterPump, valueAfterPump) = WpfStaHost.Run(() =>
        {
            var sut = CreateSut();
            var value = 0;
            var task = sut.PostAsync(() => value = 42);
            var immediately = task.IsCompleted;
            WpfStaHost.Pump();
            return (immediately, task.Status.ToString(), value);
        });

        Assert.False(completedImmediately, "PostAsync queues; it does not run the action inline.");
        Assert.Equal("RanToCompletion", statusAfterPump);
        Assert.Equal(42, valueAfterPump);
    }

    [Fact]
    public void PostAsync_WhenTheActionThrows_FaultsTheReturnedTask()
    {
        var (isFaulted, exceptionType, message) = WpfStaHost.Run(() =>
        {
            var sut = CreateSut();

            // A net for the duration, so a WRONG assumption fails this test instead of killing the test
            // PROCESS: App's own DispatcherUnhandledException handler is installed in OnStartup, which the
            // host never runs. If InvokeAsync routed a queued failure to Dispatcher.UnhandledException the
            // way the legacy BeginInvoke does, an unhandled exception here would take ~2157 tests with it.
            DispatcherUnhandledExceptionEventHandler net = (_, e) => e.Handled = true;
            WpfStaHost.StaDispatcher.UnhandledException += net;
            try
            {
                var task = sut.PostAsync(() => throw new InvalidOperationException("boom"));
                WpfStaHost.Pump();

                // Reading .Exception also MARKS IT OBSERVED, which is the point of the whole member.
                var inner = task.Exception?.InnerException;
                return (task.IsFaulted, inner?.GetType().Name, inner?.Message);
            }
            finally
            {
                WpfStaHost.StaDispatcher.UnhandledException -= net;
            }
        });

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
        var reached = WpfStaHost.Run(() =>
        {
            var sut = CreateSut();

            DispatcherUnhandledExceptionEventHandler net = (_, e) => e.Handled = true;
            WpfStaHost.StaDispatcher.UnhandledException += net;
            try
            {
                var ran = false;
                sut.Post(() =>
                {
                    ran = true;
                    throw new InvalidOperationException("boom");
                });

                // Pump() throwing would fail this test: a fire-and-forget failure must not escape into the
                // dispatcher frame either. It is logged instead (see UiDispatcherService.LogIfFaulted).
                WpfStaHost.Pump();
                return ran;
            }
            finally
            {
                WpfStaHost.StaDispatcher.UnhandledException -= net;
            }
        });

        Assert.True(reached,
            "the queued action must have run; Post's contract is that its failure is logged, never rethrown " +
            "into an audio or timer thread and never dropped on the floor.");
    }
}
