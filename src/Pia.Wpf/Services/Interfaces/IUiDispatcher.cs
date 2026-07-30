namespace Pia.Services.Interfaces;

/// <summary>
/// Marshals an <see cref="Action"/> onto the WPF UI thread without the caller naming
/// <c>System.Windows</c>. ViewModels take this instead of reading the process-global
/// <c>Application.Current.Dispatcher</c>, so a ViewModel's threading behaviour becomes a constructor
/// argument: substitutable in tests, greppable in review, and independent of whether some other test
/// in the process happens to have created an <c>Application</c>.
/// <para>
/// <b>Null-Application fallback contract, common to all three members:</b> when there is no live
/// <c>Application</c> — unit tests, and any host that never created one — the implementation runs the
/// action <b>inline on the calling thread</b>. It must never drop the work, and must never queue it to
/// a dispatcher nobody pumps. That is exactly what the migrated call sites did before this interface
/// existed, so the fallback preserves behaviour rather than introducing it.
/// </para>
/// <para>
/// Member names mirror <c>UiThreadViewModel</c>'s <c>Post</c>/<c>PostAsync</c>/<c>PostOrRun</c> so the
/// two idioms read identically. There is deliberately <b>no</b> <c>IsOnUiThread</c> probe: every call
/// site in the tree is one of these three shapes, nothing needs the boolean itself, and exposing it
/// would invite a check-then-act race.
/// </para>
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// Queues <paramref name="action"/> onto the UI thread and returns immediately. Always a queue —
    /// even when the caller is already on the UI thread — so the caller's remaining statements still
    /// run first. Runs inline when there is no live <c>Application</c>.
    /// <para>
    /// Fire-and-forget: nothing can observe a failure, so the implementation logs one rather than
    /// letting it escape into an event handler. Use from handlers that cannot await. Do not use where
    /// the next statement reads state the action mutates — that is <see cref="PostAsync"/>.
    /// </para>
    /// </summary>
    void Post(Action action);

    /// <summary>
    /// Queues <paramref name="action"/> onto the UI thread and completes when it has run, so an
    /// awaiting caller observes the mutation applied on return. Runs inline and returns
    /// <see cref="Task.CompletedTask"/> when there is no live <c>Application</c>.
    /// <para>
    /// An exception thrown by <paramref name="action"/> faults the returned task and therefore reaches
    /// the awaiting caller's <c>try</c>/<c>catch</c>. That is the deliberate asymmetry with
    /// <see cref="Post"/> and <see cref="PostOrRun"/>, which log instead — and it is what preserves
    /// the existing error handling at the four awaited call sites. <b>Always await it.</b> An
    /// unawaited call compiles clean in a non-async method and silently loses the exception.
    /// </para>
    /// </summary>
    Task PostAsync(Action action);

    /// <summary>
    /// Runs <paramref name="action"/> inline when the caller is already on the UI thread, or when
    /// there is no live <c>Application</c>; otherwise queues it fire-and-forget exactly like
    /// <see cref="Post"/>. Avoids a redundant re-queue for the common already-on-the-UI-thread case.
    /// Failures are logged, not rethrown — same contract as <see cref="Post"/>.
    /// </summary>
    void PostOrRun(Action action);
}
