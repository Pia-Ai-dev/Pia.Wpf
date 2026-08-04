using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// T1-2, the keyed semaphore behind <see cref="IProviderRequestThrottle"/>: one
/// <see cref="RunSlotPool"/> per <see cref="AiProvider.Id"/>.
/// <para>
/// WHY <see cref="AiProvider.Id"/> is the key and no new identity field was added: it is already the stable
/// identity of a configured provider everywhere it matters — <c>ProviderService</c> persists it,
/// <c>SyncMapper</c> maps it across devices, Pia Cloud has a fixed constant
/// (<c>ProviderService.PiaCloudProviderId</c>) and <c>ProviderEditModel.ToProvider</c> preserves it through an
/// edit. Keying on the ENDPOINT would have been the other candidate and is worse in both directions: two
/// providers on one endpoint with different keys share a rate limit they do not have, and one provider whose
/// endpoint the user retypes silently gets a second pool.
/// </para>
/// <para>
/// A provider with an unset id (<see cref="Guid.Empty"/> — a hand-built throwaway, e.g. the first-run
/// wizard's connection test) is not special-cased: it shares one pool with every other id-less provider. That
/// is the SAFE direction (more queueing, never less), and the wizard's probes do not come through here anyway
/// (see the exclusion note in <c>AiClientService</c>).
/// </para>
/// <para>
/// WHY <see cref="RunSlotPool"/> rather than a bare <see cref="SemaphoreSlim"/>: it already solves the two
/// problems this needs — a LIVE-RESIZABLE width (so changing the setting reaches a queued request instead of
/// waiting for a restart) that can never over- or under-release.
/// </para>
/// <para>
/// The width is applied from TWO places, exactly as <c>HeadlessRunLauncher._slots</c> is and for the same two
/// reasons: <see cref="OnSettingsChanged"/> covers a raise made WHILE requests are queued, and the
/// <see cref="RunSlotPool.Resize"/> inside <see cref="AcquireAsync"/> covers both cold start (no save has
/// happened this session, so the event has never fired) and a pool created after the last save. The overlap is
/// free — <c>Resize</c> early-returns on an unchanged width. The event arm is not decoration here: without it a
/// raise would not reach an already-queued request at all, because the request that resizes is a NEW arrival
/// and a waiter admitted by a <see cref="RunSlotPool.Release"/> resized long before it queued. A saturated
/// pool with no new arrivals would then stay narrow for as long as its whole queue takes to drain.
/// </para>
/// <para>
/// These pools take NO <see cref="RunSlotPool.Ticket"/>, ever, and are separate instances from the launcher's
/// two pools — the ordering chain that a ticket drives and the hang that mixing ticketed and unticketed waits
/// on ONE pool would cause are per-instance concerns (see <see cref="RunSlotPool.WaitAsync(CancellationToken)"/>).
/// Requests here are unordered on purpose: they arrive from the UI thread, from N run loops and from the tool
/// loop inside each, so there is no "creation order" worth preserving the way there is for a scheduler tick's
/// dispatches.
/// </para>
/// <para>
/// Registered as a SINGLETON and injected into the TRANSIENT <c>AiClientService</c>: the dictionary is the
/// device-wide bound, so a per-request instance would be a throttle that throttles nothing.
/// </para>
/// </summary>
public sealed class ProviderRequestThrottle : IProviderRequestThrottle, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ILogger<ProviderRequestThrottle> _logger;
    private bool _disposed;

    /// <summary>
    /// provider id → its pool. Never pruned: an entry is a few dozen bytes and the key set is the user's
    /// configured provider list (plus one shared <see cref="Guid.Empty"/> bucket), so there is nothing here
    /// that grows with traffic.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, RunSlotPool> _pools = new();

    public ProviderRequestThrottle(ISettingsService settings, ILogger<ProviderRequestThrottle> logger)
    {
        _settings = settings;
        _logger = logger;
        // Deliberately NOT an initial read: the ctor is synchronous and GetSettingsAsync is not, so the width
        // is picked up by the first acquire (and by every save after). Same shape as HeadlessRunLauncher.
        _settings.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>
    /// Apply a saved width to every pool that already exists. Fires on EVERY settings save, so the work is
    /// deliberately trivial: <see cref="RunSlotPool.Resize"/> is synchronous, allocation-free and early-returns
    /// on an unchanged width. Raising admits queued requests at once; lowering never preempts an in-flight one.
    /// </summary>
    private void OnSettingsChanged(object? sender, AppSettings e)
    {
        var width = e.GetMaxParallelRequestsPerProvider();
        foreach (var pool in _pools.Values)
            pool.Resize(width);
    }

    /// <summary>
    /// Unhooks the settings handler. Beside <c>HeadlessRunLauncher.Dispose</c>'s and for the same reason: this
    /// is a singleton, and a live handler on the settings service outlives it (in tests, it pins a per-test
    /// substitute). The pools themselves need no disposal — a <see cref="RunSlotPool"/> holds a
    /// <see cref="SemaphoreSlim"/> with no wait handle allocated, and disposing one out from under a request
    /// still in flight would be strictly worse than letting it be collected.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.SettingsChanged -= OnSettingsChanged;
    }

    /// <summary>The pool count, for tests and diagnostics — how many distinct providers this process has called.</summary>
    public int PoolCount => _pools.Count;

    /// <summary>The current width of <paramref name="providerId"/>'s pool, or null if it has never been called.</summary>
    public int? WidthFor(Guid providerId) => _pools.TryGetValue(providerId, out var pool) ? pool.Width : null;

    public async Task<IDisposable> AcquireAsync(AiProvider provider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var width = await ReadWidthAsync().ConfigureAwait(false);

        // The pool is constructed with the CURRENT width and the fixed hard cap: the cap is the wrapped
        // semaphore's maxCount, so it is enforced by the type rather than only by Resize's clamp.
        var pool = _pools.GetOrAdd(
            provider.Id,
            static (_, w) => new RunSlotPool(w, AppSettings.MaxParallelRequestsPerProviderCap),
            width);

        // Idempotent and cheap on the unchanged path; this is what makes the setting live-resizable without an
        // event subscription (and what applies it on a cold start, where no save has happened this session).
        pool.Resize(width);

        await pool.WaitAsync(ct).ConfigureAwait(false);
        return new Permit(pool);
    }

    /// <summary>
    /// The configured width, clamped on read. Failure-isolated on the same principle as
    /// <c>AgentPlanner.TryGetRosterAsync</c>: <c>GetSettingsAsync</c> already swallows its own faults and
    /// answers defaults, so this catch is defence in depth around another type's public method — but a
    /// throttle that could throw here would fail an outbound request for a settings problem, which is a
    /// strictly worse outage than the stampede it exists to prevent.
    /// </summary>
    private async Task<int> ReadWidthAsync()
    {
        try
        {
            var settings = await _settings.GetSettingsAsync().ConfigureAwait(false);
            return settings.GetMaxParallelRequestsPerProvider();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider throttle could not read its width; using the default");
            return AppSettings.DefaultParallelRequestsPerProvider;
        }
    }

    /// <summary>
    /// One held permit. Release is IDEMPOTENT — a double dispose would hand a permit back that was never
    /// taken, i.e. permanently widen the pool above the configured width (and, at the cap, throw
    /// <see cref="SemaphoreFullException"/> out of whatever happened to be disposing). The interlocked swap
    /// makes that unreachable rather than merely unlikely, which matters because these are disposed from
    /// <c>finally</c> blocks inside an async iterator.
    /// </summary>
    private sealed class Permit : IDisposable
    {
        private RunSlotPool? _pool;

        internal Permit(RunSlotPool pool) => _pool = pool;

        public void Dispose() => Interlocked.Exchange(ref _pool, null)?.Release();
    }
}
