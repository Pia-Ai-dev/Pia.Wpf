using Pia.Services.Interfaces;

namespace Pia.Tests.Services;

/// <summary>
/// An <see cref="IUiDispatcher"/> that runs every action inline, synchronously, on the calling thread.
/// <para>
/// This <b>restores</b> the pre-Batch-12 behaviour rather than changing it. Before the migration these
/// call sites read <c>Application.Current?.Dispatcher</c>, which is null under the xunit host, and every
/// one of them fell back to invoking the action inline — which is why 31 of
/// <c>MeetingAttendeeViewModelTests</c>' methods can assert on state a <c>DispatchToUi</c> action
/// mutated. So every existing assertion keeps holding; the difference is that it now holds because the
/// double says so, deterministically, instead of by accident of a process-global static being null.
/// That distinction becomes load-bearing the moment <c>AssistantViewParseTests</c> creates a real
/// <see cref="System.Windows.Application"/>: from then on the static is non-null for everyone in the
/// process, and the null fallback is gone.
/// </para>
/// <para>
/// Synchronously inline is the contract, not an implementation detail: no <c>Task.Run</c>, no
/// <c>SynchronizationContext.Post</c>, and <c>PostAsync</c> invokes and <i>then</i> returns an already
/// completed task. Anything that hops a thread reintroduces exactly the nondeterminism this double
/// exists to remove.
/// </para>
/// <para>
/// Catches nothing, deliberately: <c>TranscriptOverlayViewModel.DispatchToUi</c> keeps its own
/// try/catch-and-log around the call, so an action's exception still lands where it did before, and
/// swallowing it here would hide a genuine test failure.
/// </para>
/// </summary>
internal sealed class InlineUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();

    public Task PostAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public void PostOrRun(Action action) => action();
}
