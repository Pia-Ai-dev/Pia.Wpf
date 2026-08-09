using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Admission is read as a state fact (<c>Task.IsCompleted</c>) rather than timed, which is only sound
/// because <see cref="StubSettings"/> completes the one await before the semaphore synchronously.</summary>
public class ProviderRequestThrottleTests
{
    /// <summary>Hand-written rather than substituted: every <c>IsCompleted</c> assertion below would be a coin
    /// flip if the settings read could ever complete asynchronously.</summary>
    private sealed class StubSettings : ISettingsService
    {
        private readonly AppSettings _settings;

        public StubSettings(int width) => _settings = new AppSettings { MaxParallelRequestsPerProvider = width };

        public event EventHandler<AppSettings>? SettingsChanged;

        /// <summary>Change the width and raise the event the throttle listens on, like a settings save would.</summary>
        public void Save(int width)
        {
            _settings.MaxParallelRequestsPerProvider = width;
            SettingsChanged?.Invoke(this, _settings);
        }

        /// <summary>Applies a new width WITHOUT raising the event — i.e. the cold-start shape.</summary>
        public void SetSilently(int width) => _settings.MaxParallelRequestsPerProvider = width;

        public Task<AppSettings> GetSettingsAsync() => Task.FromResult(_settings);
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public Task SaveDraftAsync(string? draftText) => Task.CompletedTask;
        public Task<string?> GetDraftAsync() => Task.FromResult<string?>(null);
    }

    /// <summary>An <see cref="ISettingsService"/> whose read throws — the failure-isolation fixture.</summary>
    private sealed class ThrowingSettings : ISettingsService
    {
        public event EventHandler<AppSettings>? SettingsChanged;

        public Task<AppSettings> GetSettingsAsync() =>
            throw new InvalidOperationException("settings are unreadable");

        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public Task SaveDraftAsync(string? draftText) => Task.CompletedTask;
        public Task<string?> GetDraftAsync() => Task.FromResult<string?>(null);

        /// <summary>Keeps the compiler honest about the unused event without a pragma.</summary>
        internal void NeverRaised() => SettingsChanged?.Invoke(this, new AppSettings());
    }

    private static AiProvider Provider(string name = "p") => new()
    {
        Name = name,
        Endpoint = "http://localhost",
        ProviderType = AiProviderType.OpenAI,
    };

    private static ProviderRequestThrottle Build(ISettingsService settings) =>
        new(settings, NullLogger<ProviderRequestThrottle>.Instance);

    [Fact]
    public async Task SameProvider_QueuesBehindTheWidth()
    {
        using var throttle = Build(new StubSettings(width: 1));
        var provider = Provider();

        var first = throttle.AcquireAsync(provider, TestContext.Current.CancellationToken);
        Assert.True(first.IsCompleted, "the first request must not queue on an idle provider");

        var second = throttle.AcquireAsync(provider, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted, "a second request to the SAME provider must queue at width 1");

        (await first).Dispose();

        // A transition, so this one is awaited rather than read as a state.
        var permit = await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        permit.Dispose();
    }

    [Fact]
    public async Task DifferentProviders_NeverQueueBehindEachOther()
    {
        using var throttle = Build(new StubSettings(width: 1));

        var a = throttle.AcquireAsync(Provider("a"), TestContext.Current.CancellationToken);
        var b = throttle.AcquireAsync(Provider("b"), TestContext.Current.CancellationToken);

        Assert.True(a.IsCompleted && b.IsCompleted,
            "two providers are two pools — a width of 1 bounds each of them, not the pair");
        Assert.Equal(2, throttle.PoolCount);

        (await a).Dispose();
        (await b).Dispose();
    }

    /// <summary>A pool built with no permits has nothing that could ever release one, so every request to that
    /// provider would queue forever with no error anywhere.</summary>
    [Fact]
    public async Task AZeroSetting_IsClampedToOnePermit_NotToNone()
    {
        using var throttle = Build(new StubSettings(width: 0));

        var permit = throttle.AcquireAsync(Provider(), TestContext.Current.CancellationToken);

        Assert.True(permit.IsCompleted, "a 0 width must clamp to 1, not build a pool that can never admit");
        (await permit).Dispose();
    }

    [Fact]
    public async Task AnAbsurdSetting_IsClampedToTheCap()
    {
        var settings = new StubSettings(width: AppSettings.MaxParallelRequestsPerProviderCap + 50);
        using var throttle = Build(settings);
        var provider = Provider();

        var held = new List<IDisposable>();
        for (var i = 0; i < AppSettings.MaxParallelRequestsPerProviderCap; i++)
        {
            var t = throttle.AcquireAsync(provider, TestContext.Current.CancellationToken);
            Assert.True(t.IsCompleted, $"permit {i} should be inside the cap");
            held.Add(await t);
        }

        Assert.False(throttle.AcquireAsync(provider, TestContext.Current.CancellationToken).IsCompleted,
            "the cap is the ceiling even when the document asks for more");

        foreach (var h in held) h.Dispose();
    }

    /// <summary>The resize inside <c>AcquireAsync</c> only runs on a NEW arrival, so without the settings-changed
    /// event a saturated pool would stay narrow until its whole queue had drained.</summary>
    [Fact]
    public async Task ARaisedSetting_AdmitsAnAlreadyQueuedRequest()
    {
        var settings = new StubSettings(width: 1);
        using var throttle = Build(settings);
        var provider = Provider();

        var held = await throttle.AcquireAsync(provider, TestContext.Current.CancellationToken);
        var queued = throttle.AcquireAsync(provider, TestContext.Current.CancellationToken);
        Assert.False(queued.IsCompleted, "pre-state: the second request really is queued");

        settings.Save(width: 2);

        // Nothing was released — the raise itself is what admitted this request.
        var permit = await queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        permit.Dispose();
        held.Dispose();
    }

    /// <summary>
    /// Cold start: no save has happened this session, so the event has never fired. The next acquire must still
    /// pick the width up.
    /// </summary>
    [Fact]
    public async Task AWidthSavedWithoutAnEvent_IsPickedUpByTheNextAcquire()
    {
        var settings = new StubSettings(width: 1);
        using var throttle = Build(settings);
        var provider = Provider();

        var first = await throttle.AcquireAsync(provider, TestContext.Current.CancellationToken);
        settings.SetSilently(width: 2);

        var second = throttle.AcquireAsync(provider, TestContext.Current.CancellationToken);
        Assert.True(second.IsCompleted, "the acquire-time resize is what covers a width no event announced");

        (await second).Dispose();
        first.Dispose();
    }

    /// <summary>An extra release hands back a permit that was never taken, so the effective width would sit above
    /// the configured one for the lifetime of the process.</summary>
    [Fact]
    public async Task DisposingAPermitTwice_ReleasesOnce()
    {
        using var throttle = Build(new StubSettings(width: 1));
        var provider = Provider();

        var permit = await throttle.AcquireAsync(provider, TestContext.Current.CancellationToken);
        permit.Dispose();
        permit.Dispose();

        var next = await throttle.AcquireAsync(provider, TestContext.Current.CancellationToken);
        Assert.False(throttle.AcquireAsync(provider, TestContext.Current.CancellationToken).IsCompleted,
            "the pool is still one permit wide after the second dispose");

        next.Dispose();
    }

    [Fact]
    public async Task AQueuedRequestThatIsCancelled_DoesNotStrandThePermit()
    {
        using var throttle = Build(new StubSettings(width: 1));
        var provider = Provider();

        var held = await throttle.AcquireAsync(provider, TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        var queued = throttle.AcquireAsync(provider, cts.Token);
        Assert.False(queued.IsCompleted);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        held.Dispose();

        var after = throttle.AcquireAsync(provider, TestContext.Current.CancellationToken);
        Assert.True(after.IsCompleted, "the cancelled waiter must not have consumed the released permit");
        (await after).Dispose();
    }

    /// <summary>A settings fault must never fail an outbound request: the throttle degrades to the compiled
    /// default width.</summary>
    [Fact]
    public async Task AFaultingSettingsRead_DegradesToTheDefaultWidth()
    {
        using var throttle = Build(new ThrowingSettings());
        var provider = Provider();

        var held = new List<IDisposable>();
        for (var i = 0; i < AppSettings.DefaultParallelRequestsPerProvider; i++)
        {
            var t = throttle.AcquireAsync(provider, TestContext.Current.CancellationToken);
            Assert.True(t.IsCompleted, $"permit {i} must be granted despite the unreadable settings");
            held.Add(await t);
        }

        Assert.False(throttle.AcquireAsync(provider, TestContext.Current.CancellationToken).IsCompleted,
            "the degrade is to the DEFAULT width, not to an unbounded pool");

        foreach (var h in held) h.Dispose();
    }
}
