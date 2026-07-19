using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// R10: probe-once + cache-per-provider capability. Capable requires SupportsToolCalling AND the
/// strengthened probe emitting a call; a disabled flag is Weak without probing; a transient probe
/// failure is Unknown and is NOT cached (so a retry re-probes). Never hard-blocks.
/// </summary>
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
        Assert.Equal(PlanningCapability.Capable, await sut.GetPlanningCapabilityAsync(provider));
    }

    [Fact]
    public async Task ProbeDoesNotEmit_ReturnsWeak()
    {
        var provider = Provider();
        _ai.TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>()).Returns(false);

        var sut = CreateSut();
        Assert.Equal(PlanningCapability.Weak, await sut.GetPlanningCapabilityAsync(provider));
    }

    [Fact]
    public async Task SupportsToolCallingFalse_IsWeak_WithoutProbing()
    {
        var provider = Provider(supportsTools: false);

        var sut = CreateSut();
        Assert.Equal(PlanningCapability.Weak, await sut.GetPlanningCapabilityAsync(provider));
        await _ai.DidNotReceive().TestToolCallEmittedAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachesPerId_ProbesOnlyOnce()
    {
        var provider = Provider();
        _ai.TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>()).Returns(true);

        var sut = CreateSut();
        await sut.GetPlanningCapabilityAsync(provider);
        await sut.GetPlanningCapabilityAsync(provider);
        await sut.GetPlanningCapabilityAsync(provider);

        await _ai.Received(1).TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProbeThrows_IsUnknown_AndNotCached()
    {
        var provider = Provider();
        _ai.TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("boom"));

        var sut = CreateSut();
        Assert.Equal(PlanningCapability.Unknown, await sut.GetPlanningCapabilityAsync(provider));

        // Not cached — a later successful probe re-evaluates.
        _ai.TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>()).Returns(true);
        Assert.Equal(PlanningCapability.Capable, await sut.GetPlanningCapabilityAsync(provider));
        await _ai.Received(2).TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalidate_ForcesReProbe()
    {
        var provider = Provider();
        _ai.TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>()).Returns(true);

        var sut = CreateSut();
        await sut.GetPlanningCapabilityAsync(provider);
        sut.Invalidate(provider.Id);
        await sut.GetPlanningCapabilityAsync(provider);

        await _ai.Received(2).TestToolCallEmittedAsync(provider, Arg.Any<CancellationToken>());
    }
}
