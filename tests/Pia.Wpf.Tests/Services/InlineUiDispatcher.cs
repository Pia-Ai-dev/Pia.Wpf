using Pia.Services.Interfaces;

namespace Pia.Tests.Services;

/// <summary>Runs every action inline and synchronously on the calling thread, catching nothing; anything that
/// hops a thread reintroduces the nondeterminism this double exists to remove.</summary>
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
