using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The park's stored call, reachable from the panel. The envelope's 400-capped line stays the collapsed
/// reading; only the disclosure reads the store.
/// </summary>
public sealed class RunProgressViewModelApprovalDetailTests
{
    private const string CappedArgs = "path=canary.md content=hello…";

    private readonly Guid _runId = Guid.NewGuid();
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();
    private readonly IAgentToolExchangeStore _toolCalls = Substitute.For<IAgentToolExchangeStore>();

    public RunProgressViewModelApprovalDetailTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]); // echo the key so the label is assertable
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        StubParkedRun();
        StubRow(null);
    }

    [Fact]
    public async Task ToolApprovalPark_LoadsTheParkedCall_AndKeepsTheCappedLineAsTheCollapsedReading()
    {
        StubRow(Row("write_file", ArgumentsJson(new string('x', 20_000))));

        var vm = CreateVm();
        await vm.ApprovalDetailLoadTask!;

        Assert.True(vm.HasApprovalDetail);
        Assert.Contains("canary.md", vm.ApprovalDetailText);
        Assert.Contains('\n', vm.ApprovalDetailText!);
        Assert.True(vm.IsApprovalDetailShortened);
        // The band's line is the envelope's, untouched by the store read.
        Assert.Equal(CappedArgs, vm.ApprovalToolArguments);
        vm.Dispose();
    }

    /// <summary>The park row is committed by the run's own path, so the first projection can legitimately read
    /// nothing. An attempt-latch would leave the disclosure permanently absent.</summary>
    [Fact]
    public async Task ApprovalDetail_RetriesOnTheNextProjection_WhenTheStoreHasNoRowYet()
    {
        _toolCalls.GetParkedCallAsync(_runId, "write_file", Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<AgentToolExchangeRow?>(null),
            Task.FromResult<AgentToolExchangeRow?>(Row("write_file", ArgumentsJson("hello"))));

        var vm = CreateVm(); // the ctor's own projection is the first read
        await vm.ApprovalDetailLoadTask!;
        Assert.False(vm.HasApprovalDetail);

        await vm.RefreshAsync();
        await vm.ApprovalDetailLoadTask!;

        Assert.True(vm.HasApprovalDetail);
        await _toolCalls.Received(2).GetParkedCallAsync(_runId, "write_file", Arg.Any<CancellationToken>());
        vm.Dispose();
    }

    [Fact]
    public async Task ApprovalDetail_LoadedAfterTheParkCleared_IsNotApplied()
    {
        var gate = new TaskCompletionSource<AgentToolExchangeRow?>();
        _toolCalls.GetParkedCallAsync(_runId, "write_file", Arg.Any<CancellationToken>()).Returns(gate.Task);

        var vm = CreateVm();
        StubRun(AgentRunState.Running);
        await vm.RefreshAsync();

        gate.SetResult(Row("write_file", ArgumentsJson("hello")));
        await vm.ApprovalDetailLoadTask!;

        Assert.False(vm.IsToolApprovalPause);
        Assert.False(vm.HasApprovalDetail);
        vm.Dispose();
    }

    [Fact]
    public async Task ApprovalDetail_IsClearedWhenTheRunLeavesThePark()
    {
        StubRow(Row("write_file", ArgumentsJson("hello")));

        var vm = CreateVm();
        await vm.ApprovalDetailLoadTask!;
        vm.ToggleApprovalDetailCommand.Execute(null);
        Assert.True(vm.ShowApprovalDetail);

        StubRun(AgentRunState.Completed);
        await vm.RefreshAsync();

        Assert.False(vm.HasApprovalDetail);
        Assert.False(vm.IsApprovalDetailShortened);
        Assert.False(vm.IsApprovalDetailExpanded);
        Assert.False(vm.ShowApprovalDetail);
        vm.Dispose();
    }

    /// <summary>The latch clears with the park, or a run that parks twice would show the first call's detail
    /// once and nothing ever again.</summary>
    [Fact]
    public async Task ASecondPark_ReadsAgain_BecauseTheLatchClearsWithThePark()
    {
        StubRow(Row("write_file", ArgumentsJson("hello")));

        var vm = CreateVm();
        await vm.ApprovalDetailLoadTask!;

        StubRun(AgentRunState.Running);
        await vm.RefreshAsync();
        Assert.False(vm.HasApprovalDetail);

        StubParkedRun();
        await vm.RefreshAsync();
        await vm.ApprovalDetailLoadTask!;

        Assert.True(vm.HasApprovalDetail);
        await _toolCalls.Received(2).GetParkedCallAsync(_runId, "write_file", Arg.Any<CancellationToken>());
        vm.Dispose();
    }

    [Fact]
    public async Task WithoutTheStore_ThePanelOffersNoApprovalDetail()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        var vm = new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance);
        await vm.RefreshAsync();

        Assert.True(vm.IsToolApprovalPause);
        Assert.False(vm.HasApprovalDetail);
        Assert.Null(vm.ApprovalDetailLoadTask);
        vm.Dispose();
    }

    [Fact]
    public async Task AFaultedStoreRead_LeavesThePanelWithoutADetail()
    {
        _toolCalls.GetParkedCallAsync(_runId, "write_file", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("store down"));

        var vm = CreateVm();
        await vm.ApprovalDetailLoadTask!;

        Assert.False(vm.HasApprovalDetail);
        vm.Dispose();
    }

    [Fact]
    public async Task ToggleApprovalDetail_FlipsTheExpansionAndTheLabel()
    {
        StubRow(Row("write_file", ArgumentsJson("hello")));

        var vm = CreateVm();
        await vm.ApprovalDetailLoadTask!;

        Assert.False(vm.ShowApprovalDetail);
        Assert.Equal("Run_ToolApproval_ShowFullCall", vm.ApprovalDetailToggleLabel);

        vm.ToggleApprovalDetailCommand.Execute(null);
        Assert.True(vm.ShowApprovalDetail);
        Assert.Equal("Run_ToolApproval_HideFullCall", vm.ApprovalDetailToggleLabel);

        // Expanded with nothing loaded is still nothing to show.
        vm.ApprovalDetailText = null;
        Assert.False(vm.ShowApprovalDetail);
        vm.Dispose();
    }

    private static string ArgumentsJson(string content) =>
        $$"""{"content":"{{content}}","path":"canary.md"}""";

    private static AgentToolExchangeRow Row(string toolName, string argumentsJson) => new(
        Id: Guid.NewGuid(),
        RunId: Guid.NewGuid(),
        StepId: null,
        MessageSeq: 0,
        Seq: 7,
        Round: 3,
        Role: "assistant",
        Kind: AgentToolExchangeKind.ParkedCall,
        CallId: "call-1",
        ToolName: toolName,
        PluginId: null,
        ArgumentsJson: argumentsJson,
        ArgsOmitted: false,
        DisplayArgs: CappedArgs,
        ResultKind: AgentToolExchangeResult.None,
        ResultText: null,
        Chars: argumentsJson.Length,
        AnchorMessageId: null,
        CreatedAt: DateTime.UtcNow,
        ReplayedAt: null,
        SupersededAt: null);

    private void StubRow(AgentToolExchangeRow? row) =>
        _toolCalls.GetParkedCallAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(row));

    private void StubParkedRun() => StubRun(
        AgentRunState.WaitingForInput,
        $$"""{"paused":true,"reason":"tool-approval","tool":"write_file","args":"{{CappedArgs}}"}""");

    private void StubRun(AgentRunState state, string? extraJson = null) =>
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            State = state,
            Plan = [],
            ExtraJson = extraJson,
        });

    private RunProgressViewModel CreateVm()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance, toolCalls: _toolCalls);
    }
}
