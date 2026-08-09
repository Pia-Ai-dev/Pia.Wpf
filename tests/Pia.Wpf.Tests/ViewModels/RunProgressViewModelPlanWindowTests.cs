using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// A long plan folds to the running step ±1 because the card is pinned above a chat transcript and may neither
/// grow without bound nor introduce an inner scrollbar.
/// </summary>
public sealed class RunProgressViewModelPlanWindowTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();

    public RunProgressViewModelPlanWindowTests()
    {
        // Echo the key, and the key plus its arguments for Format, so an assertion can read both the string the
        // VM chose AND the numbers it put in it.
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{(string)ci[0]}|{string.Join(",", (object[])ci[1])}");
    }

    private RunProgressViewModel CreateVm()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance);
    }

    // Mirrors the band's own culture-aware format rather than hardcoding it, so these facts pin the FORMAT and
    // not the test host's locale.
    private static string Duration(long milliseconds) => milliseconds / 1000 < 60
        ? $"{milliseconds / 1000.0:0.#}s"
        : $"Run_Duration_MinSec|{milliseconds / 60000},{milliseconds / 1000 % 60}";

    private void StubPlan(int count, int runningIndex, AgentRunState state = AgentRunState.Running,
        string? ledgerJson = null)
    {
        var plan = new List<AgentStep>();
        for (var i = 0; i < count; i++)
        {
            plan.Add(new AgentStep
            {
                Id = Guid.NewGuid(),
                Ordinal = i,
                Title = $"Step {i + 1}",
                Status = i < runningIndex ? AgentStepStatus.Done
                    : i == runningIndex ? AgentStepStatus.Running
                    : AgentStepStatus.Pending,
            });
        }

        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            State = state,
            Plan = plan,
            LedgerJson = ledgerJson,
        });
    }

    // The non-vacuity control for every fact below: the window is a concession to a bounded card, not a default.
    [Fact]
    public async Task APlanAtTheLimitIsNotWindowed()
    {
        StubPlan(count: 7, runningIndex: 3);

        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.Equal(7, vm.Steps.Count);
        Assert.All(vm.Steps, r => Assert.True(r.IsInWindow));
        Assert.Equal(0, vm.EarlierFoldCount);
        Assert.Equal(0, vm.LaterFoldCount);
        Assert.Null(vm.EarlierFoldLabel);
        Assert.Null(vm.LaterFoldLabel);
        vm.Dispose();
    }

    // The plan's last row always stays visible below its own fold, because a windowed run must keep showing the
    // step it is working toward.
    [Fact]
    public async Task ALongPlanFoldsToTheRunningStepPlusMinusOne_AndTheFoldCountsAccountForEveryHiddenStep()
    {
        StubPlan(count: 12, runningIndex: 6); // steps 1-6 done, 7 running, 8-12 pending

        var vm = CreateVm();
        await vm.RefreshAsync();

        var visible = vm.Steps.Where(r => r.IsInWindow).ToList();
        Assert.Equal(4, visible.Count);
        Assert.Equal(["Step 6", "Step 7", "Step 8", "Step 12"], visible.Select(r => r.Title));

        Assert.Equal(5, vm.EarlierFoldCount);
        Assert.Equal(3, vm.LaterFoldCount);
        Assert.Equal(vm.Steps.Count, vm.EarlierFoldCount + visible.Count + vm.LaterFoldCount);

        // The qualified copy, because every folded step at each end really is Done / really is Pending.
        Assert.Equal("Run_Plan_Fold_Earlier|5", vm.EarlierFoldLabel);
        Assert.Equal("Run_Plan_Fold_Later|3", vm.LaterFoldLabel);

        // The last row rides outside the list: hidden in it, rendered below the fold, still "in window".
        Assert.Same(vm.Steps[11], vm.LastStepRow);
        Assert.True(vm.HasLastStepRow);
        Assert.True(vm.Steps[11].RenderedOutside);
        Assert.False(vm.Steps[11].ShowInList);
        Assert.Single(vm.LastStepView);
        vm.Dispose();
    }

    // "All done" over a fold hiding a SKIPPED step would report a run that went better than it did, so the VM
    // claims that qualifier only when it holds.
    [Fact]
    public async Task AFoldHidingAnUnfinishedStepDropsTheAllDoneClaim()
    {
        StubPlan(count: 12, runningIndex: 6);
        var run = await _runs.GetAsync(_runId, TestContext.Current.CancellationToken);
        run!.Plan[2].Status = AgentStepStatus.Skipped; // inside the earlier fold, and not Done

        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.Equal("Run_Plan_Fold_EarlierMixed|5", vm.EarlierFoldLabel);
        Assert.Equal("Run_Plan_Fold_Later|3", vm.LaterFoldLabel);   // the later end is untouched
        vm.Dispose();
    }

    // A fold hiding exactly one step is absorbed: it buys no height, and one is the only count at which the fold
    // copy's plural is wrong in every locale ("1 earlier steps", "1 frühere Schritte").
    [Theory]
    // 8 steps, running at index 2: the earlier fold would hide step 1 alone, so it is absorbed; the tail fold
    // hides steps 5-7 while the always-visible last row rides outside the list (4 in it + 1 outside).
    [InlineData(8, 2, 0, 5, 3)]
    // …and mirrored at the far end: running at index 5 leaves the last step INSIDE the window, so no outside
    // row and no tail fold at all.
    [InlineData(8, 5, 4, 4, 0)]
    public async Task AFoldThatWouldHideASingleStepIsAbsorbedIntoTheWindow(
        int count, int runningIndex, int expectedEarlier, int expectedVisible, int expectedLater)
    {
        StubPlan(count, runningIndex);

        var vm = CreateVm();
        await vm.RefreshAsync();

        var visible = vm.Steps.Count(r => r.IsInWindow);
        Assert.Equal(expectedEarlier, vm.EarlierFoldCount);
        Assert.Equal(expectedVisible, visible);
        Assert.Equal(expectedLater, vm.LaterFoldCount);
        Assert.Equal(count, vm.EarlierFoldCount + visible + vm.LaterFoldCount);

        // A zero count means no row at all, so no label either — the label is what would carry the bad plural.
        if (expectedEarlier == 0) Assert.Null(vm.EarlierFoldLabel); else Assert.NotNull(vm.EarlierFoldLabel);
        if (expectedLater == 0) Assert.Null(vm.LaterFoldLabel); else Assert.NotNull(vm.LaterFoldLabel);

        // The invariant the absorption exists to guarantee: a fold row never reports a count of one.
        Assert.NotEqual(1, vm.EarlierFoldCount);
        Assert.NotEqual(1, vm.LaterFoldCount);
        vm.Dispose();
    }

    // One-way: once a reader has asked for the whole plan, a later step transition may not re-fold it under them.
    [Fact]
    public async Task ExpandingTheWindowShowsEveryStep_AndSurvivesTheNextProjection()
    {
        StubPlan(count: 12, runningIndex: 6);

        var vm = CreateVm();
        await vm.RefreshAsync();
        Assert.Equal(5, vm.EarlierFoldCount);

        vm.ExpandStepWindowCommand.Execute(null);

        Assert.All(vm.Steps, r => Assert.True(r.IsInWindow));
        Assert.Equal(0, vm.EarlierFoldCount);
        Assert.Equal(0, vm.LaterFoldCount);

        StubPlan(count: 12, runningIndex: 8); // the run moved on
        await vm.RefreshAsync();

        Assert.All(vm.Steps, r => Assert.True(r.IsInWindow));
        Assert.Equal(0, vm.EarlierFoldCount);
        vm.Dispose();
    }

    /// <summary>
    /// A PAUSED run is never windowed. That is the one state whose per-row buttons can rewrite the plan, so
    /// hiding the rows a user paused in order to edit would be the panel working against them.
    /// </summary>
    [Fact]
    public async Task APausedRunShowsEveryStep_BecauseItsPlanIsEditable()
    {
        StubPlan(count: 12, runningIndex: 6, state: AgentRunState.Paused);

        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.Paused, vm.State);
        Assert.True(vm.CanMutatePlan);
        Assert.All(vm.Steps, r => Assert.True(r.IsInWindow));
        Assert.Equal(0, vm.EarlierFoldCount);
        Assert.Equal(0, vm.LaterFoldCount);
        vm.Dispose();
    }

    // The strip is bound to its OWN source so the list's folding cannot reach it, and it wraps the SAME rows —
    // copies could disagree with the list about a status.
    [Fact]
    public async Task TheProgressStripSeesEveryStepEvenWhenTheListIsWindowed()
    {
        StubPlan(count: 12, runningIndex: 6);

        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.NotSame(vm.Steps, (object)vm.PlanSegments);
        Assert.Equal(vm.Steps.Count, vm.PlanSegments.Count);
        Assert.Same(vm.Steps[0], vm.PlanSegments[0]);
        Assert.True(vm.ShowProgressSegments);
        vm.Dispose();
    }

    /// <summary>The strip is a LIVE instrument: a settled run drops it, and the band's sub-line carries the
    /// position instead. Without this the strip would sit under a finished card claiming work in flight.</summary>
    [Fact]
    public async Task TheProgressStripIsGoneOnASettledRun()
    {
        StubPlan(count: 4, runningIndex: 3, state: AgentRunState.Completed);

        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.False(vm.ShowProgressSegments);
        Assert.False(vm.ShowPlanSkeleton);
        vm.Dispose();
    }

    // The step POSITION is the leg that matters: it is the only place the card says "7 of 12" once the list is
    // windowed, and it is computed from the projected rows, not from the plan length.
    [Fact]
    public async Task TheBandSubLineNamesTheStateThePositionAndTheElapsedTime()
    {
        StubPlan(count: 12, runningIndex: 6,
            ledgerJson: """{"inputTokens":10000,"outputTokens":230,"wallClockMs":96700,"perStep":[]}""");

        var vm = CreateVm();
        await vm.RefreshAsync();

        // Built from the same culture-aware formats the VM uses: a literal "96.7s" here would pin the test host's
        // locale rather than the behaviour.
        Assert.Equal($"Run_State_Running · Run_Sub_Step|7,12 · Run_Sub_Elapsed|{Duration(96700)}", vm.SubLine);
        Assert.True(vm.HasSubLine);
        vm.Dispose();
    }

    // Asserted for the CLEAN finish, the only settled sub-line that spends a clause on tokens.
    [Fact]
    public async Task ACompletedRunsSubLineCarriesItsTotals()
    {
        StubPlan(count: 4, runningIndex: 3, state: AgentRunState.Completed,
            ledgerJson: """{"inputTokens":69000,"outputTokens":1137,"wallClockMs":651700,"perStep":[]}""");

        var vm = CreateVm();
        await vm.RefreshAsync();

        // Three of four steps are Done and the fourth is still Running in the fixture, which is exactly why the
        // settled count is read off the ROWS: the sub-line reports what the plan actually shows.
        Assert.Equal(RunProgressState.Completed, vm.State);
        Assert.Equal($"Run_Sub_Steps|3,4 · {Duration(651700)} · Run_Sub_Tokens|{70137:N0}", vm.SubLine);
        vm.Dispose();
    }

    // Readers experienced the last step "disappearing" mid-run: the step a run is working toward must not be one
    // the tail fold swallows.
    [Fact]
    public async Task AWindowedRunKeepsItsLastStepBelowTheFold_AndExpandReturnsItToTheList()
    {
        StubPlan(count: 12, runningIndex: 2);

        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.Same(vm.Steps[11], vm.LastStepRow);
        Assert.False(vm.Steps[11].ShowInList);
        Assert.Equal(7, vm.LaterFoldCount); // steps 4-10 hidden between the window and the last row

        vm.ExpandStepWindowCommand.Execute(null);

        Assert.Null(vm.LastStepRow);
        Assert.False(vm.HasLastStepRow);
        Assert.Empty(vm.LastStepView);
        Assert.All(vm.Steps, r => Assert.True(r.ShowInList));
        vm.Dispose();
    }

    /// <summary>A failed run says WHERE it stopped, which is the failed step — not the next pending one, and not
    /// the plan's length.</summary>
    [Fact]
    public async Task AFailedRunsSubLineNamesTheStepItStoppedAt()
    {
        StubPlan(count: 4, runningIndex: 1, state: AgentRunState.Failed,
            ledgerJson: """{"inputTokens":1000,"outputTokens":204,"wallClockMs":96700,"perStep":[]}""");
        var run = await _runs.GetAsync(_runId, TestContext.Current.CancellationToken);
        run!.Plan[1].Status = AgentStepStatus.Failed;

        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.Equal($"Run_Sub_StoppedAtStep|2,4 · {Duration(96700)}", vm.SubLine);
        vm.Dispose();
    }
}
