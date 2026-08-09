using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.Services;
using Pia.ViewModels;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.ViewModels;

public sealed class RunProgressPublishTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SqliteContext _ctx;
    private readonly AssistantChatService _chats;
    private readonly AgentRunService _runs;
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();

    public RunProgressPublishTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaRunPub_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        // Echo the key (and the key + its argument for Format) so a note line is assertable without pinning
        // English copy in a test.
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => (string)ci[0] + ":" + string.Join(",", (object[])ci[1]));
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<AgentRun> NewRunAsync(AgentRunState state)
    {
        var ct = TestContext.Current.CancellationToken;
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
        }, ct);

        var run = await _runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.Schedule, Goal: "g"), ct);

        switch (state)
        {
            case AgentRunState.Failed: await _runs.FailAsync(run.Id, "boom", cancelled: false, ct); break;
            case AgentRunState.Completed: await _runs.CompleteAsync(run.Id, ct: ct); break;
            default: await _runs.SetStateAsync(run.Id, state, ct); break;
        }

        return run;
    }

    private RunProgressViewModel CreateVm(Guid runId, IRunWorkspaceService? workspaces)
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, runId, _loc, _resume, NullLogger.Instance, null, workspaces);
    }

    private FakeRunWorkspaceService Workspaces() => new(_tmpDir);

    // A failed run does not promote automatically, so its work is still in its workspace and the panel offers to
    // publish it rather than deciding for the user.
    [Fact]
    public async Task AFailedRunWithUnpublishedFiles_OffersPublish()
    {
        var run = await NewRunAsync(AgentRunState.Failed);
        var workspaces = Workspaces();
        workspaces.Outcome = new RunWorkspaceOutcome(RunWorkspaceMode.Copy, BranchName: null, HasUnpublishedFiles: true);

        var vm = CreateVm(run.Id, workspaces);
        await vm.RefreshAsync();

        Assert.True(vm.HasUnpublishedFiles);
        Assert.True(vm.CanPublish);
        Assert.True(vm.PublishCommand.CanExecute(null));
    }

    // A cleanly completed run promoted and tore its workspace down before it was marked complete, so there is
    // nothing left to describe and nothing to offer.
    [Fact]
    public async Task ACompletedRun_OffersNothing()
    {
        var run = await NewRunAsync(AgentRunState.Completed);
        var workspaces = Workspaces();
        workspaces.DescribeReturnsNothing = true;

        var vm = CreateVm(run.Id, workspaces);
        await vm.RefreshAsync();

        // Non-vacuity: it really was asked (at least once — the ctor's initial projection also refreshes).
        Assert.True(workspaces.Describes >= 1);
        Assert.False(vm.HasUnpublishedFiles);
        Assert.False(vm.CanPublish);
        Assert.Null(vm.OutputBranchName);
    }

    // The outcome read is TERMINAL-ONLY: RunChanged fires on every step and the read behind it is file I/O, for a
    // question a live run cannot be asked while its files are still being written.
    [Fact]
    public async Task ALiveRun_IsNeverAskedAboutItsWorkspace()
    {
        var run = await NewRunAsync(AgentRunState.Running);
        var workspaces = Workspaces();

        var vm = CreateVm(run.Id, workspaces);
        await vm.RefreshAsync();

        Assert.Equal(0, workspaces.Describes);
        Assert.False(vm.CanPublish);
    }

    // Promote THEN tear down, in that order, so a teardown never destroys files a promotion has not carried out
    // yet; the offer is cleared afterwards so the same workspace cannot be published twice.
    [Fact]
    public async Task Publish_PromotesThenTearsDown_AndClearsTheOffer()
    {
        var run = await NewRunAsync(AgentRunState.Failed);
        var order = new List<string>();
        var workspaces = Workspaces();
        workspaces.Order = order;
        workspaces.Outcome = new RunWorkspaceOutcome(RunWorkspaceMode.Copy, null, HasUnpublishedFiles: true);
        workspaces.PromoteResult = new RunPromotionResult(RunWorkspaceMode.Copy, Promoted: 3, Skipped: 1, Conflicts: 0, BranchName: null);

        var vm = CreateVm(run.Id, workspaces);
        await vm.RefreshAsync();
        await vm.PublishCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "promote", "teardown" }, order);
        Assert.False(vm.CanPublish);
        // The count line, and only the count line: Skipped is the byte-identical no-op case and is
        // deliberately never surfaced — there is nothing to tell the user about a file already correct.
        Assert.Equal("Run_Publish_Done:3", vm.PublishNote);
    }

    // The run's version of a conflicted file exists ONLY in that workspace, so a publish must obey
    // RetainWorkspace and leave the offer standing instead of deleting it.
    [Fact]
    public async Task Publish_WithConflicts_SaysSo_AndKeepsTheWorkspaceItCouldNotEmpty()
    {
        var run = await NewRunAsync(AgentRunState.Failed);
        var workspaces = Workspaces();
        workspaces.Outcome = new RunWorkspaceOutcome(RunWorkspaceMode.Copy, null, HasUnpublishedFiles: true);
        workspaces.PromoteResult = new RunPromotionResult(
            RunWorkspaceMode.Copy, Promoted: 1, Skipped: 0, Conflicts: 2, BranchName: null, RetainWorkspace: true);

        var vm = CreateVm(run.Id, workspaces);
        await vm.RefreshAsync();
        await vm.PublishCommand.ExecuteAsync(null);

        Assert.Contains("Run_Publish_Done:1", vm.PublishNote);
        Assert.Contains("Run_Publish_Conflicts:2", vm.PublishNote);
        Assert.Empty(workspaces.TornDown);
        Assert.True(vm.CanPublish);
    }

    // A faulted publish leaves the offer standing: nothing was promoted, so the files are still there to retry.
    [Fact]
    public async Task Publish_Fault_DoesNotThrow_AndLeavesTheOfferStanding()
    {
        var run = await NewRunAsync(AgentRunState.Failed);
        var workspaces = Workspaces();
        workspaces.Outcome = new RunWorkspaceOutcome(RunWorkspaceMode.Copy, null, HasUnpublishedFiles: true);
        workspaces.ThrowOnPromote = true;

        var vm = CreateVm(run.Id, workspaces);
        await vm.RefreshAsync();
        await vm.PublishCommand.ExecuteAsync(null); // must not throw

        Assert.True(vm.CanPublish);
        Assert.Empty(workspaces.TornDown);
        Assert.Equal("Run_Publish_Failed", vm.PublishNote);
    }

    // "Nothing promoted, workspace intact" — a relocated assistant folder is the realistic cause — is a failure,
    // not a clean publish of zero files.
    [Fact]
    public async Task Publish_ThatPromotesNothing_KeepsTheOffer()
    {
        var run = await NewRunAsync(AgentRunState.Failed);
        var workspaces = Workspaces();
        workspaces.Outcome = new RunWorkspaceOutcome(RunWorkspaceMode.Copy, null, HasUnpublishedFiles: true);
        workspaces.PromoteResult = null; // the service's restrictive degrade

        var vm = CreateVm(run.Id, workspaces);
        await vm.RefreshAsync();
        await vm.PublishCommand.ExecuteAsync(null);

        Assert.True(vm.CanPublish);
        Assert.Empty(workspaces.TornDown);
        Assert.Equal("Run_Publish_Failed", vm.PublishNote);
    }

    // In worktree mode nothing was copied anywhere, so the panel must name the branch the output is on.
    [Fact]
    public async Task WorktreeOutcome_SurfacesTheBranchName()
    {
        var run = await NewRunAsync(AgentRunState.Completed);
        var branch = "pia/run/" + run.Id;
        var workspaces = Workspaces();
        workspaces.Outcome = new RunWorkspaceOutcome(RunWorkspaceMode.Worktree, branch, HasUnpublishedFiles: false);

        var vm = CreateVm(run.Id, workspaces);
        await vm.RefreshAsync();

        Assert.Equal(branch, vm.OutputBranchName);
        Assert.True(vm.HasOutputBranch);
        Assert.Equal("Run_Output_Branch:" + branch, vm.OutputBranchNote);
        Assert.False(vm.CanPublish); // the branch is the deliverable — there is nothing to publish as files
    }

    // An automatic promotion's conflict count rides on the workspace outcome the panel already reads, so the
    // number that says the run's work on a file was discarded no longer waits for a button press.
    [Fact]
    public async Task AnAutomaticPromotionsConflicts_AreAnnouncedWithoutAPublishClick()
    {
        var run = await NewRunAsync(AgentRunState.Completed);
        var workspaces = Workspaces();
        // What the real service answers for a clean COPY-mode run whose promotion hit a conflict: the workspace
        // was retained (the run's version of that file exists nowhere else) and the count came back with it.
        workspaces.Outcome = new RunWorkspaceOutcome(
            RunWorkspaceMode.Copy, null, HasUnpublishedFiles: true, Conflicts: 2);

        var vm = CreateVm(run.Id, workspaces);
        await vm.RefreshAsync();

        Assert.Equal("Run_Publish_Conflicts:2", vm.PublishNote);
        Assert.True(vm.CanPublish);          // and the offer beside it is the actionable half
        Assert.Empty(workspaces.Promoted);   // nothing was promoted BY THE PANEL — this is a read, not an act
    }

    // A worktree run whose run-branch commit failed has no branch name but does have files, so the panel must
    // claim no branch and offer a retry rather than name an empty one.
    [Fact]
    public async Task AWorktreeRunWithNoCommittedBranch_OffersTheRetry_AndClaimsNoBranch()
    {
        var run = await NewRunAsync(AgentRunState.Completed);
        var workspaces = Workspaces();
        workspaces.Outcome = new RunWorkspaceOutcome(
            RunWorkspaceMode.Worktree, BranchName: null, HasUnpublishedFiles: true);
        // The retry's own promotion still cannot commit, so it reports "kept, nothing moved" — the one result
        // where "Published 0 file(s)" would read as success and Run_Publish_Failed is the honest line.
        workspaces.PromoteResult = new RunPromotionResult(
            RunWorkspaceMode.Worktree, Promoted: 0, Skipped: 1, Conflicts: 0,
            BranchName: "pia/run/" + run.Id, RetainWorkspace: true);

        var vm = CreateVm(run.Id, workspaces);
        await vm.RefreshAsync();

        Assert.False(vm.HasOutputBranch);
        Assert.Null(vm.OutputBranchNote);
        Assert.True(vm.CanPublish);

        await vm.PublishCommand.ExecuteAsync(null);

        Assert.Equal("Run_Publish_Failed", vm.PublishNote);
        Assert.Empty(workspaces.TornDown);   // the workspace is still the only copy
        Assert.True(vm.CanPublish);          // so the retry stays available
    }

    // With no workspace service: no offer, no branch line, nothing asked of anybody — the pin that the
    // trailing-defaulted ctor parameter changed nothing.
    [Fact]
    public async Task WithNoWorkspaceService_ThePanelIsUnchanged()
    {
        var run = await NewRunAsync(AgentRunState.Failed);

        var vm = CreateVm(run.Id, workspaces: null);
        await vm.RefreshAsync();

        Assert.False(vm.HasUnpublishedFiles);
        Assert.False(vm.CanPublish);
        Assert.False(vm.PublishCommand.CanExecute(null));
        Assert.Null(vm.OutputBranchName);
        Assert.Null(vm.OutputBranchNote);
        Assert.Null(vm.PublishNote);
    }
}
