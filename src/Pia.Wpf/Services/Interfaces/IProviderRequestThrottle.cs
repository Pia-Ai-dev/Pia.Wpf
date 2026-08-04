using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// T1-2 — how many requests this device may have IN FLIGHT against one provider at once (plan §18.3 item 3).
/// A keyed semaphore: jobs on the same provider queue behind each other, jobs on different providers run
/// fully parallel.
/// <para>
/// WHY it exists at all: §18.4 states the dependency plainly — "the global cap + per-provider throttle are
/// what prevent the provider/disk stampede §17.5 warned about, so raising parallelism above 1 is only safe
/// WITH the throttle in place". Since T1-1 the run pool is a user setting up to
/// <see cref="AppSettings.MaxParallelBackgroundRunsCap"/>, and <c>HeadlessRunLauncher._childSlots</c> adds a
/// fixed 2 children per delegating parent on top of it, so the number of concurrent requests one provider key
/// can see is a product of two knobs and a fan-out — not a number anybody reads off a settings page.
/// </para>
/// <para>
/// The permit covers exactly ONE outbound round-trip, never a whole method: <c>AiClientService</c>'s tool
/// loop acquires per ROUND and releases before it dispatches the round's tool calls. That is not an
/// optimization — a permit held across tool dispatch would be held across an interactive approval card, so
/// one human staring at a dialog would stop every background run on that provider.
/// </para>
/// </summary>
public interface IProviderRequestThrottle
{
    /// <summary>
    /// Queue for permission to call <paramref name="provider"/>, and hand back the permit as an
    /// <see cref="IDisposable"/> — dispose it (idempotently) the moment the round-trip is over.
    /// <para>
    /// Throws only <see cref="OperationCanceledException"/>, and only from the wait: everything else about
    /// this call is failure-isolated, because a throttle that could fail a request would be a worse outage
    /// than the stampede it prevents.
    /// </para>
    /// </summary>
    Task<IDisposable> AcquireAsync(AiProvider provider, CancellationToken ct);
}
