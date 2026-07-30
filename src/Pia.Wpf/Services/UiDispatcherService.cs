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
                dispatcher.InvokeAsync(action);
        }
        catch (Exception ex)
        {
            // Nothing can observe a fire-and-forget failure, so log it rather than let it escape into
            // an event handler on an audio/timer thread. This covers the marshal call itself (a
            // shut-down dispatcher throws) and the inline fallback; it deliberately does NOT wrap the
            // QUEUED execution, so a queued action's exception still surfaces on
            // Dispatcher.UnhandledException exactly as BeginInvoke does today.
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
                dispatcher.InvokeAsync(action);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UI dispatch failed (PostOrRun)");
        }
    }
}
