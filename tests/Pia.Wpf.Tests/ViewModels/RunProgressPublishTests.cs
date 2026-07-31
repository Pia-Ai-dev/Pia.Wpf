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
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Batch 06 G4 / plan D3: the run panel's publish affordance, and plan D5b's "the panel must say the output is
/// on a branch". ViewModel level only — <c>RunProgressPanel.xaml</c> is parsed by nothing in this suite and a
/// frame-pushing View test is not available (the WPF host holds a fixed number of such facts), so the XAML is
/// booked as manual-smoke debt and the projection is asserted here.
/// </summary>
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

    /// <summary>Runs Post callbacks inline so the projection is observable synchronously.</summary>
    private sealed class InlineSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
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

    /// <summary>
    /// T-G4-15, <b>REGRESSION</b>. Plan D3's second half: a failed run does not promote automatically, so its
    /// work is still in its workspace and the panel OFFERS to publish it rather than deciding for the user.
    /// </summary>
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

    /// <summary>
    /// T-G4-16, <b>GUARD</b>. A cleanly completed run promoted before it was marked complete and its workspace
    /// was torn down in the same breath, so there is nothing to describe and nothing to offer.
    /// </summary>
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

    /// <summary>
    /// <b>GUARD</b>. The outcome read is TERMINAL-ONLY. <c>RunChanged</c> fires on every step, state flip and
    /// ledger write, and the read behind it is a file read plus a directory enumeration — folding it into the
    /// projection path would pay for that dozens of times per run to answer a question a live run cannot even
    /// be asked (its files are still being written).
    /// </summary>
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

    /// <summary>
    /// T-G4-17, <b>REGRESSION</b>. Publishing promotes and THEN tears the workspace down — in that order, so a
    /// teardown never destroys files a promotion has not carried out yet — and the offer is cleared afterwards
    /// so the user cannot publish the same workspace twice.
    /// </summary>
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

    /// <summary>
    /// <b>REGRESSION</b>. Conflicts ARE surfaced, because "3 published" alone would read as "everything landed"
    /// when a file the user changed during the run was deliberately left alone.
    /// </summary>
    [Fact]
    public async Task Publish_WithConflicts_SaysSo()
    {
        var run = await NewRunAsync(AgentRunState.Failed);
        var workspaces = Workspaces();
        workspaces.Outcome = new RunWorkspaceOutcome(RunWorkspaceMode.Copy, null, HasUnpublishedFiles: true);
        workspaces.PromoteResult = new RunPromotionResult(RunWorkspaceMode.Copy, Promoted: 1, Skipped: 0, Conflicts: 2, BranchName: null);

        var vm = CreateVm(run.Id, workspaces);
        await vm.RefreshAsync();
        await vm.PublishCommand.ExecuteAsync(null);

        Assert.Contains("Run_Publish_Done:1", vm.PublishNote);
        Assert.Contains("Run_Publish_Conflicts:2", vm.PublishNote);
    }

    /// <summary>
    /// T-G4-18, <b>GUARD</b>. A publish that faults does not escape into the UI, and it leaves the offer
    /// standing: nothing was promoted, so the files are still there to try again with.
    /// </summary>
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

    /// <summary>
    /// <b>GUARD</b>. A promotion that returns "nothing promoted, workspace intact" — a relocated assistant
    /// folder is the realistic cause — is reported as a failure and keeps the offer, rather than clearing it and
    /// claiming zero files were published.
    /// </summary>
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

    /// <summary>
    /// T-G4-19, <b>REGRESSION</b>. Plan D5b at the only level a test can reach: in worktree mode nothing was
    /// copied anywhere, so the panel must name the branch the output is on. Without this line the honest user
    /// question after a successful run is "where is my file?".
    /// </summary>
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

    /// <summary>
    /// T-G4-20, <b>GUARD</b>. With no workspace service the panel is the pre-Batch-06 one: no offer, no branch
    /// line, and nothing asked of anybody. The pin that the trailing-defaulted ctor parameter changed nothing —
    /// the whole existing <c>RunProgressViewModel</c> suite passes unmodified for the same reason.
    /// </summary>
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
