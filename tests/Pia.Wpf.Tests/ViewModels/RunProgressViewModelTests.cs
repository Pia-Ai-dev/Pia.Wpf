using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// R12/G3: the run-progress projection over a real <see cref="AgentRunService"/>. Covers state mapping
/// (incl. the distinct truncated-Completed), the moving Running highlight, live ledger accrual, run-id
/// filtering, and off-thread RunChanged marshaling with no cross-thread WPF exception.
/// Written to run on Windows/CI — the WPF test assembly cannot execute on macOS.
/// </summary>
public sealed class RunProgressViewModelTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SqliteContext _ctx;
    private readonly AssistantChatService _chats;
    private readonly AgentRunService _runs;

    public RunProgressViewModelTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
    }

    /// <summary>Runs Post callbacks inline so the projection is observable synchronously in tests.</summary>
    private sealed class InlineSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    private async Task<AgentRun> NewPlannedRunAsync()
    {
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId,
            SchemaVersion = 1,
            Title = "t",
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(),
            Messages = [],
        });
        return await _runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "g"));
    }

    private RunProgressViewModel CreateVm(Guid runId)
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, runId, NullLogger.Instance);
    }

    [Fact]
    public async Task Projects_Planning_Then_Running_And_MovesHighlight()
    {
        var run = await NewPlannedRunAsync();
        var stepA = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending };
        var stepB = new AgentStep { Id = Guid.NewGuid(), Ordinal = 1, Title = "B", Status = AgentStepStatus.Pending };
        await _runs.ReplaceStepsAsync(run.Id, new[] { stepA, stepB });

        var vm = CreateVm(run.Id);
        await vm.RefreshAsync();
        Assert.Equal(RunProgressState.Planning, vm.State);
        Assert.Equal(2, vm.Steps.Count);
        Assert.Equal("A", vm.Steps[0].Title);

        await _runs.SetStateAsync(run.Id, AgentRunState.Running);
        await _runs.SetStepStatusAsync(stepA.Id, AgentStepStatus.Running);
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.Running, vm.State);
        Assert.True(vm.Steps[0].IsRunning);
        Assert.False(vm.Steps[1].IsRunning);

        await _runs.SetStepStatusAsync(stepA.Id, AgentStepStatus.Done);
        await _runs.SetStepStatusAsync(stepB.Id, AgentStepStatus.Running);
        await vm.RefreshAsync();

        Assert.Equal(AgentStepStatus.Done, vm.Steps[0].Status);
        Assert.False(vm.Steps[0].IsRunning);
        Assert.True(vm.Steps[1].IsRunning);

        vm.Dispose();
    }

    [Fact]
    public async Task LedgerAccrues_Live()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.AddUsageAsync(run.Id, null, new UsageDetails { InputTokenCount = 10, OutputTokenCount = 4 });
        await vm.RefreshAsync();

        Assert.Equal(10, vm.TotalInputTokens);
        Assert.Equal(4, vm.TotalOutputTokens);
        Assert.Contains("14 Tokens", vm.LedgerSummary);

        vm.Dispose();
    }

    [Fact]
    public async Task TruncatedComplete_IsDistinctFromCleanComplete()
    {
        var truncatedRun = await NewPlannedRunAsync();
        var truncVm = CreateVm(truncatedRun.Id);
        await _runs.CompleteAsync(truncatedRun.Id, truncated: true, truncationReason: "budget");
        await truncVm.RefreshAsync();
        Assert.Equal(RunProgressState.TruncatedCompleted, truncVm.State);
        Assert.True(truncVm.IsTruncated);
        truncVm.Dispose();

        var cleanRun = await NewPlannedRunAsync();
        var cleanVm = CreateVm(cleanRun.Id);
        await _runs.CompleteAsync(cleanRun.Id);
        await cleanVm.RefreshAsync();
        Assert.Equal(RunProgressState.Completed, cleanVm.State);
        Assert.False(cleanVm.IsTruncated);
        cleanVm.Dispose();
    }

    [Fact]
    public async Task Failed_MapsToFailed()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);
        await _runs.FailAsync(run.Id, "generic");
        await vm.RefreshAsync();
        Assert.Equal(RunProgressState.Failed, vm.State);
        vm.Dispose();
    }

    [Fact]
    public async Task RunChanged_ForOtherRunId_IsIgnored()
    {
        var run = await NewPlannedRunAsync();
        var other = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);
        await vm.RefreshAsync();
        var before = vm.State;

        // A terminal event for a DIFFERENT run must not reproject this vm (no throw either).
        await _runs.CompleteAsync(other.Id, truncated: true, truncationReason: "budget");

        Assert.Equal(before, vm.State);
        Assert.False(vm.IsTruncated);
        vm.Dispose();
    }

    [Fact]
    public async Task OffThreadRunChanged_Marshals_NoCrossThreadException()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        // Raise a state change from a background thread — the vm must marshal via the captured
        // SynchronizationContext (G3). With the inline context installed on THIS thread, the assert is
        // that no exception escapes; production posts to the WPF dispatcher.
        await Task.Run(async () => await _runs.SetStateAsync(run.Id, AgentRunState.Running));

        vm.Dispose();
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
    }
}
