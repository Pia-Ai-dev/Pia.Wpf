using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Controls.Assistant;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The run-progress panel (<c>Controls/Assistant/RunProgressPanel.xaml</c>) — the surface Batches 06, 07 and
/// the consolidation pass all added lines to.
/// <para>
/// <b>Corrected claim (Batch 14 W7):</b> the panel is already PARSED today, twice, by shipped Batch-13 facts
/// (<see cref="AssistantViewParseTests.RunProgressPanel_RendersATimelineRow_WithItsStepOutcomeAndDecision"/>,
/// <see cref="AssistantViewParseTests.RunProgressPanel_RendersAStepRow_WithItsPersonaAvatar"/> — both build
/// <c>new RunProgressPanel { DataContext = vm }</c>). What has never happened is a BINDING-PATH WALK over it:
/// nobody has checked that a non-templated <c>{Binding}</c> path here actually resolves against the ViewModel
/// type the markup roots it at, as opposed to merely not throwing when the panel parses.
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class RunProgressPanelParseTests
{
    /// <summary>
    /// A floor, not a count (D2 — lives here, never in <see cref="BindingPathWalker"/>): "no unresolved paths"
    /// is vacuously true over an empty walk, which is reachable if a container ever stops reporting logical
    /// children. The floor stays exactly the tuple count AT-OR-BEFORE the Steps <c>ItemsControl</c>'s own
    /// <c>ItemsSource</c> binding, because everything from there on is droppable (e.g. by extracting the trace
    /// into its own templated control) without tripping this fact: 18 before Batch 08, 21 after 8a, 26 after 8b,
    /// 40 after the signal-band redesign — which rewrote the markup and roughly doubled the walk (72 tuples in
    /// total). MEASURED each time by a temporary <c>Assert.Fail</c> dumping the array, never taken from a
    /// document.
    /// </summary>
    private const int MinimumBoundPaths = 40;

    /// <summary>
    /// Reflects the ViewModel type off <see cref="AssistantViewModel.ActiveRunProgress"/> rather than
    /// hardcoding <see cref="RunProgressViewModel"/> — which pins the TYPE and nothing else. <c>nameof</c>
    /// makes a RENAME of that property a compile error and the <c>Assert.Equal</c> catches a RETYPE, so the
    /// walk below is only sound while the property still has this type. <b>It does not observe the host
    /// SITE:</b> nothing here opens <c>AssistantView.xaml</c>, where <c>:51</c> hosts the panel with
    /// <c>DataContext="{Binding ActiveRunProgress}"</c>, so a RE-HOST onto another property is caught by
    /// <see cref="ViewHostDataContextTests"/> and by nothing in this file (Batch 14 review, D1 — repointing
    /// <c>:51</c> at <c>VoiceMode</c> kills all 28 paths below and left 16/16 Views facts green). This walk
    /// needs both. <c>ActiveRunProgress</c> is source-generated from
    /// <c>[ObservableProperty] private RunProgressViewModel? _activeRunProgress;</c>
    /// (<c>AssistantViewModel.cs:136</c>–<c>:137</c>) — the <c>?</c> is compile-time NRT metadata only, so
    /// <c>.PropertyType</c> is still <c>typeof(RunProgressViewModel)</c>.
    /// <para>
    /// This fact needs no ViewModel at all: no substitute, no <c>finally</c>, no <c>Dispose</c>, no
    /// <see cref="IAgentRunService"/>. Paths are read declaratively off the parsed panel's
    /// <c>BindingExpression</c>s and resolved by reflection, exactly as
    /// <see cref="SettingsAssistantViewParseTests.EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt"/>
    /// does with no DataContext ever set. <c>new RunProgressPanel()</c> is already proven to parse (the two
    /// Batch-13 facts named above), so every non-templated <c>StaticResource</c> in the file resolves.
    /// </para>
    /// <para>
    /// Scope limits, stated so this is not read as "the whole panel": every <c>ItemTemplate</c>'s item-scoped
    /// bindings are out of reach — the Steps row, the trace row, the child row and the child-trace row nested
    /// inside it (doubly unreachable, since a DataTemplate's content is never in the LOGICAL tree until a
    /// container realizes it). Those are covered by <see cref="RunProgressStepRowTemplateTests"/> and by
    /// <see cref="AssistantViewParseTests"/>'s two <c>LoadContent()</c> facts. <c>loc:Str</c> is out of scope by
    /// design (explicit <c>Source</c>, never the DataContext). <c>DynamicResource</c> stores a
    /// <c>ResourceReferenceExpression</c>, not a
    /// <c>BindingExpression</c>, and is invisible to this walk.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryNonTemplatedBindingPath_ResolvesOnTheViewModelThatHostsThePanel()
    {
        var root = typeof(AssistantViewModel)
            .GetProperty(nameof(AssistantViewModel.ActiveRunProgress), BindingFlags.Public | BindingFlags.Instance)!
            .PropertyType;
        Assert.Equal(typeof(RunProgressViewModel), root);

        var bindings = WpfStaHost.Run(() => BindingPathWalker.Describe(new RunProgressPanel(), root));

        Assert.True(bindings.Length >= MinimumBoundPaths,
            $"only {bindings.Length} bound paths were found in the parsed RunProgressPanel, which is below " +
            $"the non-vacuity floor of {MinimumBoundPaths}. The walk is logical, so suspect a container that " +
            "no longer reports logical children rather than a genuine removal.");

        Assert.Contains(bindings, b => b.Contains("=OutputBranchNote "));   // the surface R11 left open
        Assert.Contains(bindings, b => b.Contains("=HasOutputBranch "));    // its visibility half
        Assert.Contains(bindings, b => b.Contains("=LedgerSummary "));      // Batch 02's strip

        // Each anchor below is SINGLE-OCCURRENCE in the markup, which is what makes it survive-proof: an anchor
        // on a path bound twice (`=State ` is bound a dozen times across the band) would still match after one
        // occurrence was removed. The set covers the surfaces the walk exists for — the branch line, the ledger
        // (now a tooltip on the band's sub-line, which is where the redesign demoted it), the truncation chip, the
        // band's lead line, the publish offer, the trace's truncation note, the Pause button, the pause-first
        // note, the steering box and the sub-agents list.
        // RED DEMO (one of them, same shape/helper/format string as the rest): renamed the Binding
        // CurrentActivity to CurrentActivityX in the XAML (single-occurrence, grep-confirmed), full -t:Rebuild
        // (still 0 Warning(s)/0 Error(s) -- a binding path is not a compile error), ran the class: with this
        // anchor present, "Assert.Contains() Failure: Filter not matched in collection"; with this line ALSO
        // temporarily commented out (test file only, never src/), the sweep at the end failed independently
        // naming "TextBlock.Text=CurrentActivityX [RunProgressViewModel] UNRESOLVED". Both reverted immediately;
        // git diff --stat -- src/ came back empty.
        Assert.Contains(bindings, b => b.Contains("=TruncationNote "));   // the band's truncation chip
        Assert.Contains(bindings, b => b.Contains("=CurrentActivity "));  // the band's lead line
        Assert.Contains(bindings, b => b.Contains("=PublishCommand "));   // the publish offer
        Assert.Contains(bindings, b => b.Contains("=TimelineNote "));     // inside the tool-activity section

        // Batch 08 8a: the header Pause button, single-occurrence in the markup (the same anchor discipline
        // as the four above). RED DEMO: renamed PauseCommand's binding in the XAML to PauseCommandX,
        // full -t:Rebuild (still 0 Warning(s)/0 Error(s) — a binding path is not a compile error), ran the
        // class: this anchor failed with "Filter not matched in collection"; reverted, git diff --stat -- src/
        // came back empty.
        Assert.Contains(bindings, b => b.Contains("=PauseCommand "));

        // Batch 08 8b: the pause-first note and the nudge box, both single-occurrence in the markup (the
        // same discipline). PlanMutationNote is NOT used as an anchor here — it is bound twice
        // (Visibility AND Text), so removing one occurrence would still leave it matching.
        // RED DEMO (CanMutatePlan): renamed its XAML binding to CanMutatePlanX, full -t:Rebuild (still
        // 0 Warning(s)/0 Error(s)), ran the class: this anchor failed with "Filter not matched in
        // collection"; reverted, git diff --stat -- src/ came back empty.
        // Batch 08 F12 MOVED this anchor, and it is the same surface: the pause-first note's Visibility was
        // rebound from `CanMutatePlan` (inverted) to `ShowPauseFirstNote`, because the inverse form rendered
        // the note in every state except Paused — including WaitingForInput and Completed, where there is no
        // Pause button to press. `=CanMutatePlan ` was single-occurrence in the WALKED markup (the row-group's
        // copy is `DataContext.CanMutatePlan` inside an ItemTemplate, which the logical walk never realizes
        // and which would describe as `=DataContext.CanMutatePlan ` anyway), so the old anchor could not
        // survive the rebind — MEASURED, not assumed: with `=CanMutatePlan ` still asserted after the XAML
        // change this class failed with "Assert.Contains() Failure: Filter not matched in collection" here.
        // Nothing was weakened: one single-occurrence anchor on this surface, before and after, and the
        // assertion count is unchanged.
        Assert.Contains(bindings, b => b.Contains("=ShowPauseFirstNote "));
        Assert.Contains(bindings, b => b.Contains("=NudgeText "));

        // Proves the walk reached the LAST section (sub-agents) and did not stop at the tool-activity one. Both
        // sections' bodies are plain collapsible Borders now rather than Expander content, which keeps them in the
        // logical tree unconditionally -- exactly the property this walk needs, and the reason the redesign did
        // not restyle the framework Expander. This does NOT, on its own, prove the tool-activity body was walked;
        // the =TimelineNote anchor above covers that half.
        Assert.Contains(bindings, b => b.Contains("=Children "));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Controls/Assistant/RunProgressPanel.xaml do not resolve to a public " +
            "property on the ViewModel the markup roots them at, so they bind to nothing and fail silently " +
            $"at runtime: {string.Join(", ", unresolved)}");
    }

    /// <summary>
    /// Batch 08 8a. The header Pause button's <c>Command</c> binding resolves to the SAME
    /// <see cref="CommunityToolkit.Mvvm.Input.IRelayCommand"/> instance <see cref="RunProgressViewModel.PauseCommand"/>
    /// exposes — command IDENTITY, not merely "not null" (the <see cref="ScheduledJobsRowTemplateTests"/>
    /// discipline). <see cref="System.Windows.UIElement.Visibility"/> is asserted by its declared binding PATH
    /// (hazard 12), never by its resolved value: <c>Visibility</c> defaults to <c>Visible</c>, so a value-only
    /// check would pass even against a deleted binding.
    /// </summary>
    [Fact]
    public void RunProgressPanel_PauseButton_IsBoundToThePauseCommand()
    {
        RunProgressViewModel? vm = null;
        RunProgressPanel? panel = null;
        string? commandPath;
        bool sameCommand;
        string? visibilityPath;
        try
        {
            WpfStaHost.Run(() =>
            {
                vm = CreateRunProgressViewModelWithInterpolatingLocalization();
                panel = new RunProgressPanel { DataContext = vm };
                return 0;
            });
            WpfStaHost.Pump();

            (commandPath, sameCommand, visibilityPath) = WpfStaHost.Run(() =>
            {
                var button = BindingPathWalker.FindLogical<ButtonBase>(panel!)
                    .Single(b => BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) == "PauseCommand");
                return (
                    BindingPathWalker.PathOf(button, ButtonBase.CommandProperty),
                    ReferenceEquals(button.Command, vm!.PauseCommand),
                    BindingPathWalker.PathOf(button, UIElement.VisibilityProperty));
            });
        }
        finally
        {
            // Hazard 4: the VM subscribes to IAgentRunService.RunChanged in its ctor and the host outlives
            // every test.
            WpfStaHost.Run(() => { vm?.Dispose(); return 0; });
        }

        Assert.Equal("PauseCommand", commandPath);
        Assert.True(sameCommand, "the Pause button's Command did not resolve to vm.PauseCommand itself");
        // ShowPauseButton, deliberately NOT CanPause. The two differ by exactly the in-flight term, and binding
        // Visibility to CanPause made the button VANISH the instant it was pressed: the band re-laid out around
        // the gap, the user lost the only acknowledgement that the click registered, and PauseLabel's "Pausing…"
        // was pushed to a collapsed element, i.e. was unrenderable. Enabledness is the command's job (CanExecute
        // still reads CanPause); visibility is the state's.
        Assert.Equal("ShowPauseButton", visibilityPath);
    }

    /// <summary>
    /// <b>The template-instantiation gap, closed.</b> Every other fact in this file and in
    /// <see cref="AssistantViewParseTests"/> reads declared bindings or <c>LoadContent()</c> output, so none of
    /// them ever applies a <c>ControlTemplate</c> — and an unresolved <c>StaticResource</c> or a malformed
    /// <c>Setter TargetName</c> inside one throws at TEMPLATE APPLICATION, i.e. the first time a user looks at the
    /// card. The signal-band redesign put three new templates behind that blind spot: the section header's
    /// (chevron plus its <c>IsChecked</c> symbol swap), the plan-mutation verb button's (its disabled-glyph
    /// trigger), and the pulse style's named <c>BeginStoryboard</c>/<c>StopStoryboard</c> pair.
    /// <para>
    /// A real measure/arrange pass is the only thing that reaches them, and it is safe HERE specifically: the
    /// panel's own code-behind is nothing but <c>InitializeComponent</c>. The layout hazard
    /// <see cref="AssistantViewParseTests"/> documents is scoped to <c>AssistantView</c>, which arms three
    /// <c>Loaded</c> handlers this control has none of.
    /// </para>
    /// <para>
    /// The state is chosen to realize the MOST templates at once: Paused (the only state whose rows render the
    /// five verb buttons), a plan long enough to fold (so a fold row's own style applies), both audit sections
    /// expanded, and one child run. The assertion is deliberately structural rather than cosmetic — this proves
    /// the templates apply and the visual tree builds, never that anything is legible.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EveryControlTemplateApplies_UnderARealLayoutPass()
    {
        var runId = Guid.NewGuid();
        var runs = Substitute.For<IAgentRunService>();
        var plan = Enumerable.Range(0, 12).Select(i => new AgentStep
        {
            Id = Guid.NewGuid(),
            Ordinal = i,
            Title = $"Step {i + 1}",
            Status = i < 6 ? AgentStepStatus.Done : i == 6 ? AgentStepStatus.Running : AgentStepStatus.Pending,
        }).ToList();
        runs.GetAsync(runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = runId,
            State = AgentRunState.Paused,
            Plan = plan,
            LedgerJson = """{"inputTokens":10000,"outputTokens":230,"wallClockMs":96700,"perStep":[]}""",
        });
        runs.GetChildRunsAsync(runId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun>
        {
            new() { Id = Guid.NewGuid(), ParentRunId = runId, State = AgentRunState.Completed, Goal = "summarize" },
        });

        RunProgressViewModel? vm = null;
        RunProgressPanel? panel = null;
        double height;
        int verbButtons, sectionHeaders;
        try
        {
            WpfStaHost.Run(() =>
            {
                vm = CreateRunProgressViewModelWithInterpolatingLocalization(
                    Substitute.For<IAgentRunSteeringService>(), runs, runId);
                panel = new RunProgressPanel { DataContext = vm };
                return 0;
            });

            await WpfStaHost.Run(() => vm!.RefreshAsync());
            WpfStaHost.Pump();

            WpfStaHost.Run(() =>
            {
                vm!.IsTimelineExpanded = true;
                vm.IsChildrenExpanded = true;
                vm.Children[0].IsExpanded = true;
                return 0;
            });
            WpfStaHost.Pump();

            // THE check: this throws if any template's resource lookup or trigger target is wrong.
            WpfStaHost.Run(() =>
            {
                panel!.Measure(new Size(640, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, 640, panel.DesiredSize.Height));
                panel.UpdateLayout();
                return 0;
            });
            WpfStaHost.Pump();

            (height, verbButtons, sectionHeaders) = WpfStaHost.Run(() => (
                panel!.ActualHeight,
                FindVisual<ButtonBase>(panel)
                    .Count(b => BindingPathWalker.PathOf(b, UIElement.IsEnabledProperty) == "IsMutable"),
                FindVisual<ToggleButton>(panel).Count()));
        }
        finally
        {
            WpfStaHost.Run(() => { vm?.Dispose(); return 0; });
        }

        Assert.True(height > 0, "the panel arranged to zero height, so nothing below was actually laid out");

        // Paused ⇒ every row renders all five verbs, AND a paused plan is never windowed (that is the one state
        // whose rows can be rewritten, so hiding them would work against the user) — so all 12 rows realize and
        // 60 buttons exist. The exact number is what proves the ROW template and the verb button's own template
        // both applied, rather than merely that some button exists somewhere.
        Assert.Equal(60, verbButtons);

        // Two section headers (tool activity, sub-agents) plus one per child row.
        Assert.Equal(3, sectionHeaders);
    }

    /// <summary>Visual, not logical: a <c>ControlTemplate</c>'s content only exists in the visual tree, which is
    /// the whole point of the fact above.</summary>
    private static IEnumerable<T> FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T hit) yield return hit;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            foreach (var found in FindVisual<T>(VisualTreeHelper.GetChild(root, i)))
                yield return found;
        }
    }

    /// <summary>
    /// <b>REGRESSION.</b> A pause in flight leaves the button ON SCREEN and DISABLED, and swaps its label to
    /// "Pausing…". Binding <c>Visibility</c> to <c>CanPause</c> (which carries the in-flight term) made it vanish
    /// the instant it was pressed: the band re-laid out around the gap, the user lost the only acknowledgement
    /// that their click registered, and <c>PauseLabel</c>'s in-flight copy was pushed to a collapsed element, i.e.
    /// could never be read. Visibility is the STATE's question (<c>ShowPauseButton</c>), enabledness the
    /// command's.
    /// <para>
    /// The window is not a flicker: <c>IsPausing</c> stays true until a projection observes the run actually
    /// leaving the pausable state, so this is what the user looks at while the request is out.
    /// </para>
    /// <para>Neutralize: rebind the button's <c>Visibility</c> to <c>CanPause</c> → the Visible leg reds.</para>
    /// </summary>
    [Fact]
    public void APauseInFlight_DisablesTheButtonAndRelabelsIt_WithoutHidingIt()
    {
        RunProgressViewModel? vm = null;
        RunProgressPanel? panel = null;
        (Visibility Visibility, bool Enabled, object? Content) idle, inFlight;
        try
        {
            WpfStaHost.Run(() =>
            {
                // WITH a steering service, unlike this file's other facts: without one the button is never
                // offered at all (the trailing-optional guard), and both legs below would read Collapsed for a
                // reason that has nothing to do with the claim.
                vm = CreateRunProgressViewModelWithInterpolatingLocalization(
                    Substitute.For<IAgentRunSteeringService>());
                panel = new RunProgressPanel { DataContext = vm };
                vm.State = RunProgressState.Running; // the state the Pause button is offered from
                return 0;
            });
            WpfStaHost.Pump();

            idle = WpfStaHost.Run(() => ProbePauseButton(panel!));

            WpfStaHost.Run(() => { vm!.IsPausing = true; return 0; });
            WpfStaHost.Pump();

            inFlight = WpfStaHost.Run(() => ProbePauseButton(panel!));
        }
        finally
        {
            WpfStaHost.Run(() => { vm?.Dispose(); return 0; });
        }

        // Visible in BOTH readings — the in-flight one is the leg that used to be Collapsed.
        Assert.Equal(Visibility.Visible, idle.Visibility);
        Assert.Equal(Visibility.Visible, inFlight.Visibility);

        // …and the label actually changes, i.e. the in-flight copy reaches a rendered element.
        Assert.Equal("Run_Action_Pause", idle.Content);
        Assert.Equal("Run_Action_Pausing", inFlight.Content);

        // …and the state it is actually in: pressable before, disabled while the request is out. Both legs are
        // non-vacuous — IsEnabled defaults to True, so the False reading is the one that bites, and the True
        // reading proves the disablement came from the in-flight term rather than from a dead command.
        Assert.True(idle.Enabled);
        Assert.False(inFlight.Enabled);
    }

    private static (Visibility, bool, object?) ProbePauseButton(RunProgressPanel panel)
    {
        var button = BindingPathWalker.FindLogical<ButtonBase>(panel)
            .Single(b => BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) == "PauseCommand");
        return (button.Visibility, button.IsEnabled, button.Content);
    }

    /// <summary>
    /// The half a path check cannot see. The naming is deliberate: it observes BOTH states.
    /// <c>TextBlock.Visibility</c> defaults to <c>Visible</c> (hazard 8), so asserting <c>Visible</c> after
    /// setting the branch is vacuous on its own — a deleted <c>HasOutputBranch</c> binding would pass it too.
    /// The <c>Collapsed</c> observation BEFORE the mutation is the one that bites.
    /// </summary>
    [Fact]
    public void RunProgressPanel_RendersTheOutputBranchLine_OnlyWhenTheRunHasABranch()
    {
        const string branchName = "pia/run/2026-08-01-abcdef";

        RunProgressViewModel? vm = null;
        RunProgressPanel? panel = null;
        Visibility before;
        Visibility after;
        string? text;
        try
        {
            WpfStaHost.Run(() =>
            {
                vm = CreateRunProgressViewModelWithInterpolatingLocalization();
                panel = new RunProgressPanel { DataContext = vm };
                return 0;
            });
            WpfStaHost.Pump();

            before = WpfStaHost.Run(() =>
                BindingPathWalker.FindLogical<TextBlock>(panel!)
                    .Single(tb => BindingPathWalker.PathOf(tb, TextBlock.TextProperty) == "OutputBranchNote")
                    .Visibility);

            WpfStaHost.Run(() =>
            {
                // OutputBranchName is [ObservableProperty] with [NotifyPropertyChangedFor] on BOTH
                // HasOutputBranch and OutputBranchNote (RunProgressViewModel.cs:187-190), so the generated
                // setter is the entire trigger -- no IRunWorkspaceService substitute is needed.
                vm!.OutputBranchName = branchName;
                return 0;
            });
            WpfStaHost.Pump();

            (after, text) = WpfStaHost.Run(() =>
            {
                var tb = BindingPathWalker.FindLogical<TextBlock>(panel!)
                    .Single(t => BindingPathWalker.PathOf(t, TextBlock.TextProperty) == "OutputBranchNote");
                return (tb.Visibility, tb.Text);
            });
        }
        finally
        {
            // Hazard 4, non-negotiable: the VM subscribes to IAgentRunService.RunChanged in its ctor and the
            // host outlives every test.
            // Batch 14 review D5, declined: this bounded Run sits in a finally, so a wedged dispatcher
            // throws a SECOND TimeoutException here that C# `finally` semantics let REPLACE whatever was
            // already propagating from the try body (the real failing stage and its message are lost).
            // Not fixed: the honest fix needs a `bodyFaulted` flag set from a catch that rethrows and
            // gates a swallow here, bigger than this nit's budget across the 3 sites this batch raised it
            // at; a bare `catch` was rejected because it would silently drop a genuine disposal failure on
            // an otherwise-passing test.
            WpfStaHost.Run(() => { vm?.Dispose(); return 0; });
        }

        Assert.Equal(Visibility.Collapsed, before);
        Assert.Equal(Visibility.Visible, after);
        Assert.Contains(branchName, text);
    }

    /// <summary>
    /// This file's OWN factory, deliberately not
    /// <see cref="AssistantViewParseTests.CreateRunProgressViewModel"/> (hazard 13). That helper stubs
    /// <c>loc.Format(...)</c> as <c>ci =&gt; (string)ci[0]</c>, i.e. it returns the KEY — so
    /// <c>OutputBranchNote</c> (<c>_localization.Format("Run_Output_Branch", OutputBranchName!)</c>,
    /// <c>RunProgressViewModel.cs:198</c>) would render literally <c>"Run_Output_Branch"</c>, and asserting
    /// that proves only that the note PROPERTY was read, never that the branch name reaches the rendered
    /// string. This stub instead interpolates the key with every arg, so the branch name must be a substring
    /// of the rendered text to pass. Not merged with the other helper: a future tidy-up that merges them would
    /// silently re-vacuate this fact's assertion.
    /// <para>
    /// Constructed ON the STA thread: the ctor captures <c>SynchronizationContext.Current</c>
    /// (<c>RunProgressViewModel.cs:274</c>), subscribes <c>RunChanged</c> (<c>:275</c>) and fires
    /// <c>RefreshAsync().SafeFireAndForget(...)</c> (<c>:276</c>). The three trailing-optional services
    /// (<c>IAgentTimelineService</c>, <c>IRunWorkspaceService</c>, <c>IPersonaService</c>) are omitted on
    /// purpose, which is what keeps this fact store-less.
    /// </para>
    /// </summary>
    private static RunProgressViewModel CreateRunProgressViewModelWithInterpolatingLocalization(
        IAgentRunSteeringService? steering = null, IAgentRunService? runs = null, Guid? runId = null)
    {
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{(string)ci[0]}|{string.Join(",", (object[])ci[1])}");

        return new RunProgressViewModel(
            runs ?? Substitute.For<IAgentRunService>(), runId ?? Guid.NewGuid(), loc,
            Substitute.For<IAgentRunResumeService>(), NullLogger.Instance, steering: steering);
    }
}
