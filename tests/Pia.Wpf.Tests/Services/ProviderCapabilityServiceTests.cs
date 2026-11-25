using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class ProviderCapabilityServiceTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();

    private ProviderCapabilityService CreateSut() =>
        new(_ai, NullLogger<ProviderCapabilityService>.Instance);

    private static AiProvider Provider(bool supportsTools = true) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test",
        Endpoint = "https://example.test",
        SupportsToolCalling = supportsTools,
    };

    [Fact]
    public async Task ProbeEmitsCall_ReturnsCapable()
    {
        var provider = Provider();
        _ai.TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>()).Returns(true);

        var sut = CreateSut();
        Assert.Equal(PlanningCapability.Capable, await sut.GetPlanningCapabilityAsync(provider, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProbeDoesNotEmit_ReturnsWeak()
    {
        var provider = Provider();
        _ai.TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>()).Returns(false);

        var sut = CreateSut();
        Assert.Equal(PlanningCapability.Weak, await sut.GetPlanningCapabilityAsync(provider, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SupportsToolCallingFalse_IsWeak_WithoutProbing()
    {
        var provider = Provider(supportsTools: false);

        var sut = CreateSut();
        Assert.Equal(PlanningCapability.Weak, await sut.GetPlanningCapabilityAsync(provider, TestContext.Current.CancellationToken));
        await _ai.DidNotReceive().TestToolCallEmittedAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachesPerId_ProbesOnlyOnce()
    {
        var provider = Provider();
        _ai.TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>()).Returns(true);

        var sut = CreateSut();
        await sut.GetPlanningCapabilityAsync(provider, TestContext.Current.CancellationToken);
        await sut.GetPlanningCapabilityAsync(provider, TestContext.Current.CancellationToken);
        await sut.GetPlanningCapabilityAsync(provider, TestContext.Current.CancellationToken);

        await _ai.Received(1).TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProbeThrows_IsUnknown_AndCachedOnlyUntilItExpires()
    {
        var provider = Provider();
        _ai.TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("boom"));
        var clock = new MovableClock(DateTimeOffset.UnixEpoch);
        var sut = new ProviderCapabilityService(_ai, NullLogger<ProviderCapabilityService>.Instance, clock);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(PlanningCapability.Unknown, await sut.GetPlanningCapabilityAsync(provider, ct));
        clock.Advance(ProviderCapabilityService.UnknownExpiry - TimeSpan.FromSeconds(1));
        Assert.Equal(PlanningCapability.Unknown, await sut.GetPlanningCapabilityAsync(provider, ct));
        await _ai.Received(1).TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>());

        _ai.TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>()).Returns(true);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PlanningCapability.Capable, await sut.GetPlanningCapabilityAsync(provider, ct));
        await _ai.Received(2).TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Capable_NeverExpires()
    {
        var provider = Provider();
        _ai.TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>()).Returns(true);
        var clock = new MovableClock(DateTimeOffset.UnixEpoch);
        var sut = new ProviderCapabilityService(_ai, NullLogger<ProviderCapabilityService>.Instance, clock);
        var ct = TestContext.Current.CancellationToken;

        await sut.GetPlanningCapabilityAsync(provider, ct);
        clock.Advance(TimeSpan.FromDays(1));
        await sut.GetPlanningCapabilityAsync(provider, ct);

        await _ai.Received(1).TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalidate_ForcesReProbe()
    {
        var provider = Provider();
        _ai.TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>()).Returns(true);

        var sut = CreateSut();
        await sut.GetPlanningCapabilityAsync(provider, TestContext.Current.CancellationToken);
        sut.Invalidate(provider.Id);
        await sut.GetPlanningCapabilityAsync(provider, TestContext.Current.CancellationToken);

        await _ai.Received(2).TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>());
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
