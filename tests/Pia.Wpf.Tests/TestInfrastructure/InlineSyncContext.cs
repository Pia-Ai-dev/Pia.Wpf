namespace Pia.Tests.TestInfrastructure;

// Runs posted callbacks on the calling thread, so a VM under test needs no dispatcher pump.
internal sealed class InlineSyncContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => d(state);

    public override void Send(SendOrPostCallback d, object? state) => d(state);
}
