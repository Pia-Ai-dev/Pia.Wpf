using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The signal-band redesign's two new projections: the plan WINDOW (a long plan folds to the running step ±1,
/// because the card is pinned above a chat transcript and may neither grow without bound nor introduce an inner
/// scrollbar) and the band's SUB-LINE (state · position · elapsed, or the settled run's totals).
/// <para>
/// Store-less: <c>IAgentRunService.GetAsync</c> is stubbed with a hand-built <see cref="AgentRun"/>, which is
/// what makes a 12-step plan a one-line fixture. The window and the sub-line both read only the projected step
/// rows and the ledger, so nothing here needs SQLite.
/// </para>
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

    /// <summary>The band's own elapsed format, in the current culture — mirrored here rather than hardcoded so
    /// these facts pin the FORMAT and not the test host's locale. Above a minute the VM spends the localized
    /// min/sec key, which the loc stub echoes with its arguments.</summary>
    private static string Duration(long milliseconds) => milliseconds / 1000 < 60
        ? $"{milliseconds / 1000.0:0.#}s"
        : $"Run_Duration_MinSec|{milliseconds / 60000},{milliseconds / 1000 % 60}";

    /// <summary>A plan whose step at <paramref name="runningIndex"/> is Running, everything before it Done and
    /// everything after it Pending — the ordinary shape of a run in flight.</summary>
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

    /// <summary>
    /// GUARD, and the non-vacuity control for every fact below: a plan AT the limit is not windowed at all. The
    /// window is a concession to a bounded card, not a default, so the ordinary short plan must render exactly as
    /// it did before the redesign.
    /// </summary>
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

    /// <summary>
    /// <b>REGRESSION.</b> One step over the limit and the list folds to the running step ±1 — EXCEPT the plan's
    /// last row, which always stays visible (below its own fold) because a windowed run must keep showing the
    /// step it is working toward. The three counts are asserted together with the sum, because an off-by-one in
    /// either bound hides a step from the reader with no other symptom.
    /// </summary>
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

    /// <summary>
    /// The other half of the fold copy, and the one that can tell a lie: "all done" over a fold hiding a SKIPPED
    /// step would report a run that went better than it did. The VM claims the qualifier only when it holds.
    /// <para>Neutralize: collapse the two branches in <c>ApplyStepWindow</c> onto the qualified key → red.</para>
    /// </summary>
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

    /// <summary>
    /// <b>REGRESSION.</b> A fold that would hide exactly ONE step folds nothing. Two reasons, and the second is
    /// what makes this load-bearing rather than cosmetic: a 24px fold row in place of a 28px step row buys no
    /// height while costing the reader the step's title, AND one is the only count at which the fold copy's plural
    /// is wrong in every locale ("1 earlier steps", "1 frühere Schritte"). Absorbing the row is what let the copy
    /// stay plural in all three languages instead of going to a paren form.
    /// <para>Neutralize: delete either absorption line in <c>ApplyStepWindow</c> → the corresponding leg reds.</para>
    /// </summary>
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

    /// <summary>
    /// The fold rows' only action, and it is one-way: once a reader has asked for the whole plan, a later step
    /// transition may not re-fold it under them. The second projection is the half that bites — the command alone
    /// would pass without the latch.
    /// </summary>
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

    /// <summary>
    /// The whole-plan strip is bound to its OWN source so the list's folding cannot reach it — it is the only
    /// element that shows every step of a windowed plan. Asserted as a wrapper over the SAME rows (never a copy,
    /// which could disagree with the list about a status) and as a DIFFERENT object (the panel's two ItemsControls
    /// are told apart by their sources).
    /// </summary>
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

    /// <summary>
    /// <b>REGRESSION.</b> The band's sub-line for a run in flight: the state, where it is in its plan, and how
    /// long it has been going. The step POSITION is the leg that matters — it is the only place the card says
    /// "7 of 12" once the list is windowed, and it is computed from the projected rows, not from the plan length.
    /// </summary>
    [Fact]
    public async Task TheBandSubLineNamesTheStateThePositionAndTheElapsedTime()
    {
        StubPlan(count: 12, runningIndex: 6,
            ledgerJson: """{"inputTokens":10000,"outputTokens":230,"wallClockMs":96700,"perStep":[]}""");

        var vm = CreateVm();
        await vm.RefreshAsync();

        // Built from the same culture-aware formats the VM uses: the test host runs under a German culture, and
        // "96,7s" / "70.137" are the CORRECT renderings there. A literal "96.7s" here would pin the machine, not
        // the behaviour.
        Assert.Equal($"Run_State_Running · Run_Sub_Step|7,12 · Run_Sub_Elapsed|{Duration(96700)}", vm.SubLine);
        Assert.True(vm.HasSubLine);
        vm.Dispose();
    }

    /// <summary>
    /// The settled run's sub-line trades the state name for its totals — steps, seconds and the token figure that
    /// used to live in the header ledger strip. Asserted for the CLEAN finish, which is the only one that spends
    /// a clause on tokens.
    /// </summary>
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

    /// <summary>
    /// <b>REGRESSION.</b> A windowed run keeps its LAST step visible below the tail fold — the step the run is
    /// working toward must not be one of the ones the fold swallows (readers experienced exactly that as the
    /// last step "disappearing" mid-run and returning once the run settled). Unfolding returns the row to the
    /// list and clears the outside slot.
    /// </summary>
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
