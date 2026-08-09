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

[Collection("WpfApplicationStatic")]
public class RunProgressPanelParseTests
{
    // A floor, not a count: "no unresolved paths" is vacuously true over an empty walk, which is reachable if a
    // container ever stops reporting logical children. Measured, never taken from a document.
    private const int MinimumBoundPaths = 40;

    // The root type is reflected off ActiveRunProgress, so a rename is a compile error and a retype fails the
    // Assert.Equal; a RE-HOST onto another property is invisible here and covered by ViewHostDataContextTests.
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

        Assert.Contains(bindings, b => b.Contains("=OutputBranchNote "));
        Assert.Contains(bindings, b => b.Contains("=HasOutputBranch "));
        Assert.Contains(bindings, b => b.Contains("=LedgerSummary "));

        // Each anchor below is SINGLE-OCCURRENCE in the markup: an anchor on a path bound twice (`=State ` is
        // bound a dozen times) would still match after one occurrence was removed.
        Assert.Contains(bindings, b => b.Contains("=TruncationNote "));   // the band's truncation chip
        Assert.Contains(bindings, b => b.Contains("=CurrentActivity "));  // the band's lead line
        Assert.Contains(bindings, b => b.Contains("=PublishCommand "));   // the publish offer
        Assert.Contains(bindings, b => b.Contains("=TimelineNote "));     // inside the tool-activity section

        Assert.Contains(bindings, b => b.Contains("=PauseCommand "));      // the header Pause button

        // PlanMutationNote is deliberately NOT an anchor: it is bound twice (Visibility AND Text), so removing
        // one occurrence would still leave it matching.
        Assert.Contains(bindings, b => b.Contains("=ShowPauseFirstNote "));
        Assert.Contains(bindings, b => b.Contains("=NudgeText "));

        // Proves the walk reached the LAST section: both section bodies are plain collapsible Borders rather than
        // Expander content, which is what keeps them in the logical tree unconditionally.
        Assert.Contains(bindings, b => b.Contains("=Children "));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Controls/Assistant/RunProgressPanel.xaml do not resolve to a public " +
            "property on the ViewModel the markup roots them at, so they bind to nothing and fail silently " +
            $"at runtime: {string.Join(", ", unresolved)}");
    }

    // Visibility is asserted by its declared binding PATH, never by its resolved value: it defaults to Visible,
    // so a value-only check would pass even against a deleted binding.
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
            // The VM subscribes to IAgentRunService.RunChanged in its ctor and the host outlives every test.
            WpfStaHost.Run(() => { vm?.Dispose(); return 0; });
        }

        Assert.Equal("PauseCommand", commandPath);
        Assert.True(sameCommand, "the Pause button's Command did not resolve to vm.PauseCommand itself");
        // ShowPauseButton, deliberately NOT CanPause: the in-flight term made the button vanish the instant it was
        // pressed. Enabledness is the command's job, visibility is the state's.
        Assert.Equal("ShowPauseButton", visibilityPath);
    }

    // A bad StaticResource or Setter TargetName inside a ControlTemplate throws only at template application, and
    // a real layout pass is safe here because this panel's code-behind is nothing but InitializeComponent.
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

        // Paused ⇒ five verbs per row and no windowing, so all 12 rows realize: the exact count is what proves
        // the row template and the verb button's own template both applied.
        Assert.Equal(60, verbButtons);

        // Two section headers (tool activity, sub-agents), one per child row, and the band's collapse chevron.
        Assert.Equal(4, sectionHeaders);
    }

    // Visual, not logical: a ControlTemplate's content only exists in the visual tree.
    private static IEnumerable<T> FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T hit) yield return hit;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            foreach (var found in FindVisual<T>(VisualTreeHelper.GetChild(root, i)))
                yield return found;
        }
    }

    // IsPausing stays true until a projection observes the run leaving the pausable state, so this is not a
    // flicker — it is what the user looks at while the request is out.
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
                // WITH a steering service: without one the button is never offered at all, and both legs below
                // would read Collapsed for a reason unrelated to the claim.
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

        // IsEnabled defaults to True, so the False reading is the one that bites; the True reading proves the
        // disablement came from the in-flight term and not from a dead command.
        Assert.True(idle.Enabled);
        Assert.False(inFlight.Enabled);
    }

    private static (Visibility, bool, object?) ProbePauseButton(RunProgressPanel panel)
    {
        var button = BindingPathWalker.FindLogical<ButtonBase>(panel)
            .Single(b => BindingPathWalker.PathOf(b, ButtonBase.CommandProperty) == "PauseCommand");
        return (button.Visibility, button.IsEnabled, button.Content);
    }

    // TextBlock.Visibility defaults to Visible, so the Visible leg is vacuous on its own — the Collapsed
    // observation BEFORE the mutation is the one that bites.
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
                // [NotifyPropertyChangedFor] on both HasOutputBranch and OutputBranchNote makes the generated
                // setter the entire trigger, so no IRunWorkspaceService substitute is needed.
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
            // The VM subscribes to IAgentRunService.RunChanged in its ctor and the host outlives every test.
            WpfStaHost.Run(() => { vm?.Dispose(); return 0; });
        }

        Assert.Equal(Visibility.Collapsed, before);
        Assert.Equal(Visibility.Visible, after);
        Assert.Contains(branchName, text);
    }

    // Deliberately not merged with AssistantViewParseTests' factory: that one stubs Format to return the KEY, so a
    // rendered note could never prove the branch name reached it. Merging them would re-vacuate that assertion.
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
