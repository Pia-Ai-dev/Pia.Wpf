using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// R17: <see cref="WindowManagerService.ShowAgentRun"/> on a stale run (chat cascaded away) retracts the
/// dangling durable Flow item and never dereferences a missing chat / navigates. Exercised via the
/// internal async entry point so the fire-and-forget resolves deterministically.
/// </summary>
public sealed class WindowManagerServiceTests
{
    private static WindowManagerService Create(
        out IAgentRunService runs, out Pia.Services.Flow.IFlowService flow)
    {
        runs = Substitute.For<IAgentRunService>();
        flow = Substitute.For<Pia.Services.Flow.IFlowService>();
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        return new WindowManagerService(
            Substitute.For<IServiceProvider>(),
            NullLogger<WindowManagerService>.Instance,
            runs, flow, loc);
    }

    [Fact]
    public async Task ShowAgentRun_MissingRun_RetractsStaleItem_AndDoesNotThrow()
    {
        var sut = Create(out var runs, out var flow);
        var runId = Guid.NewGuid();
        runs.GetAsync(runId, Arg.Any<CancellationToken>()).Returns((AgentRun?)null);

        await sut.ShowAgentRunAsync(runId); // internal — awaits the async resolve directly

        flow.Received(1).Retract(runId.ToString());
    }
}
