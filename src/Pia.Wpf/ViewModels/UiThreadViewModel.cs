using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels;

/// <summary>
/// Base for ViewModels that receive service events on background threads (e.g. the sync pull loop)
/// and must marshal bound-collection mutations back to the UI thread. Captures the UI
/// <see cref="SynchronizationContext"/> at construction, so a derived VM must be constructed on the
/// UI thread. ViewModels must not reference <c>System.Windows</c> (see the architecture test), which
/// rules out <c>Dispatcher</c>; <see cref="SynchronizationContext"/> is the DI-safe, testable
/// equivalent. When no context was captured (unit tests construct off any thread) the action runs
/// inline, so those tests stay synchronous.
/// <para>
/// This is not the only sanctioned marshal any more: Batch 12 added the injected
/// <c>Pia.Services.Interfaces.IUiDispatcher</c>, whose <c>Post</c>/<c>PostAsync</c>/<c>PostOrRun</c> are
/// named after these and behave the same way (inline when there is nothing to marshal to). Choosing
/// between them: prefer <c>IUiDispatcher</c> when the ViewModel may be constructed OFF the UI thread, or
/// when a test must substitute the marshal itself; prefer this base when the VM is always built on the UI
/// thread and should not grow a constructor parameter. Do not use both in one type.
/// </para>
/// </summary>
public abstract class UiThreadViewModel : ObservableObject
{
    private readonly SynchronizationContext? _sync;

    protected UiThreadViewModel(bool requireUiThread = false)
    {
        _sync = SynchronizationContext.Current;
        if (requireUiThread && _sync is null)
            throw new InvalidOperationException(
                "UiThreadViewModel must be constructed on the UI thread (SynchronizationContext.Current was null).");
    }

    /// <summary>True when a SynchronizationContext was captured at construction.</summary>
    protected bool HasUiContext => _sync is not null;

    /// <summary>
    /// Marshal <paramref name="action"/> onto the captured UI context, fire-and-forget (or run it
    /// inline when no context was captured). Use from event handlers that don't await the result.
    /// </summary>
    protected void Post(Action action)
    {
        if (_sync is not null)
            _sync.Post(_ => action(), null);
        else
            action();
    }

    /// <summary>
    /// Marshal <paramref name="action"/> onto the captured UI context and await its completion (or
    /// run it inline when no context was captured). Await this so callers observe the mutation
    /// applied on return — e.g. a refresh whose caller immediately reads the just-updated collection.
    /// </summary>
    protected Task PostAsync(Action action)
    {
        if (_sync is null)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        _sync.Post(_ =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    /// <summary>
    /// Marshal <paramref name="action"/> onto the captured UI context, but run it inline when there
    /// is no captured context or the caller is already on it — avoiding a redundant re-queue.
    /// </summary>
    protected void PostOrRun(Action action)
    {
        if (_sync is null || _sync == SynchronizationContext.Current)
            action();
        else
            _sync.Post(_ => action(), null);
    }
}
