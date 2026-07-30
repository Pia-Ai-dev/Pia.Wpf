using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using System.Windows;

namespace Pia.Services;

/// <summary>
/// The only place in the codebase that reads <c>Application.Current.Dispatcher</c> on behalf of a
/// ViewModel. Re-read <b>per call</b>, never cached in a field: DI resolution order versus
/// <c>Application</c> construction is not something a ViewModel should depend on, and a cached null
/// would be permanent.
/// </summary>
public sealed class UiDispatcherService : IUiDispatcher
{
    private readonly ILogger<UiDispatcherService> _logger;

    public UiDispatcherService(ILogger<UiDispatcherService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        try
        {
            if (dispatcher is null)
                action();
            else
                LogIfFaulted(dispatcher.InvokeAsync(action).Task, "Post");
        }
        catch (Exception ex)
        {
            // Nothing can observe a fire-and-forget failure, so log it rather than let it escape into
            // an event handler on an audio/timer thread. This catch covers the marshal call itself (a
            // shut-down dispatcher throws) and the inline fallback; the QUEUED execution is covered by
            // LogIfFaulted, which is a separate mechanism for a reason — see its remarks.
            _logger.LogError(ex, "UI dispatch failed (Post)");
        }
    }

    /// <inheritdoc />
    public Task PostAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return Task.CompletedTask;
        }

        // No try/catch on purpose: the awaited path must propagate. DispatcherOperation.Task faults
        // with the action's exception, which is what `await dispatcher.InvokeAsync(...)` did before
        // this class existed — and what the callers' own try/catch and SafeFireAndForget rely on.
        return dispatcher.InvokeAsync(action).Task;
    }

    /// <inheritdoc />
    public void PostOrRun(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        try
        {
            if (dispatcher is null || dispatcher.CheckAccess())
                action();
            else
                LogIfFaulted(dispatcher.InvokeAsync(action).Task, "PostOrRun");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UI dispatch failed (PostOrRun)");
        }
    }

    /// <summary>
    /// Observes a QUEUED operation so a failure is logged instead of silently lost.
    /// </summary>
    /// <remarks>
    /// This is not belt-and-braces, it closes a real gap. <c>Dispatcher.InvokeAsync</c> uses async
    /// semantics: it CAPTURES an exception thrown by the queued action on the operation's task. The
    /// legacy <c>BeginInvoke</c> — which is what <c>VoiceModeViewModel</c> and
    /// <c>TranscriptOverlayViewModel.DispatchToUi</c> called before Batch 12 — instead let it reach
    /// <c>Dispatcher.UnhandledException</c>, where <c>App.OnStartup</c>'s handler shows a dialog and marks
    /// it handled. Discarding the operation would therefore drop the exception entirely: no dialog, no log
    /// line, nothing — worse than either pre-Batch-12 behaviour, and impossible to notice in a support
    /// bundle. Logging keeps the fire-and-forget contract (never rethrow into an audio or timer thread)
    /// while leaving a trace. That the operation's task holds the exception at all is the same fact
    /// <see cref="PostAsync"/> depends on; the two members cannot disagree about it.
    /// <para>
    /// Continued on <see cref="TaskScheduler.Default"/> deliberately: a dispatcher that is shutting down
    /// is exactly when this fires, so the log must not be queued back to it.
    /// </para>
    /// </remarks>
    private void LogIfFaulted(Task queued, string member) =>
        queued.ContinueWith(
            faulted => _logger.LogError(faulted.Exception, "UI dispatch failed ({Member}, queued)", member),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
