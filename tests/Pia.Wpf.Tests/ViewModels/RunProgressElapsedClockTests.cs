using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The plan turn raises no run events for its whole 12–42 s, so an elapsed time read off the persisted ledger
/// sits frozen beside a static skeleton and the card reads as hung.
/// </summary>
public sealed class RunProgressElapsedClockTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();

    public RunProgressElapsedClockTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{(string)ci[0]}|{string.Join(",", (object[])ci[1])}");
    }

    private RunProgressViewModel CreateVm()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance);
    }

    private void Stub(AgentRunState state, long wallClockMs) =>
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            State = state,
            Plan = [],
            LedgerJson = $$"""{"inputTokens":0,"outputTokens":0,"wallClockMs":{{wallClockMs}},"perStep":[]}""",
        });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WhilePlanning_TheElapsedTimeAdvancesWithoutARunEvent()
    {
        Stub(AgentRunState.Planning, 4000);
        var vm = CreateVm();
        await vm.RefreshAsync();
        var atProjection = vm.SubLine;

        await Task.Delay(1100, Ct);
        vm.AdvanceElapsedClock();

        Assert.NotEqual(atProjection, vm.SubLine);
        vm.Dispose();
    }

    /// <summary>The projection itself must stay exactly where the ledger put it: folding the elapsed delta in
    /// here would move the figure a few milliseconds mid-projection, which is enough to change its rounding.</summary>
    [Fact]
    public async Task AtProjectionTime_TheFigureIsThePersistedOne()
    {
        Stub(AgentRunState.Planning, 4000);
        var vm = CreateVm();

        await vm.RefreshAsync();

        Assert.Contains("Run_Sub_Elapsed|4s", vm.SubLine);
        vm.Dispose();
    }

    /// <summary>A run parked on the user must not tick: that would count their think time as latency — one run
    /// in the analysis sat 3 min 16 s at a tool-approval prompt.</summary>
    [Fact]
    public async Task WhileWaitingOnTheUser_TheClockHolds()
    {
        Stub(AgentRunState.WaitingForInput, 4000);
        var vm = CreateVm();
        await vm.RefreshAsync();
        var atProjection = vm.SubLine;

        await Task.Delay(1100, Ct);
        vm.AdvanceElapsedClock();

        Assert.Equal(atProjection, vm.SubLine);
        vm.Dispose();
    }

    [Fact]
    public async Task OnceSettled_TheClockHolds()
    {
        Stub(AgentRunState.Completed, 4000);
        var vm = CreateVm();
        await vm.RefreshAsync();
        var atProjection = vm.SubLine;

        await Task.Delay(1100, Ct);
        vm.AdvanceElapsedClock();

        Assert.Equal(atProjection, vm.SubLine);
        vm.Dispose();
    }
}
