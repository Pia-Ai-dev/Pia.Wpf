using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Controls.Assistant;
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
    /// children. The live walk measures 28 tuples (23 distinct paths — the walker yields one tuple per bound
    /// DP, not per distinct path: <c>State</c> is bound three times, <c>CanPublish</c>/<c>PublishNote</c>/
    /// <c>ChildrenNote</c> twice each). This floor is set well under that, at roughly 64%, so an ordinary edit
    /// to the panel never has to touch this file.
    /// </summary>
    private const int MinimumBoundPaths = 18;

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
    /// Scope limits, stated so this is not read as "the whole panel": the four <c>ItemTemplate</c>s' 26
    /// item-scoped bindings are out of reach (Steps <c>:75</c>–<c>:107</c>, Timeline <c>:132</c>–<c>:165</c>,
    /// Children <c>:185</c>–<c>:253</c>, and a child-timeline <c>ItemsControl</c> at <c>:220</c> nested inside
    /// the children template — doubly unreachable, since a DataTemplate's content is never in the LOGICAL tree
    /// until a container realizes it). <c>loc:Str</c> is out of scope by design (explicit <c>Source</c>, never
    /// the DataContext). <c>DynamicResource</c> stores a <c>ResourceReferenceExpression</c>, not a
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

        // Batch 14 review D3: the four anchors above cover two of the six surfaces the batch spec named for
        // this fact (branch line, ledger strip) -- and the floor (18) is exactly the tuple count at-or-before
        // :74, so every tuple from the Steps ItemsControl onward was droppable (e.g. by extracting the
        // timeline block into its own templated control) without tripping anything. These four close the
        // remaining named surfaces; each is single-occurrence in the markup, per the impl spec's own anchor
        // discipline (`=State ` and `=CanPublish ` would also fit but are each bound twice or more, so an
        // anchor on either would survive removal of one occurrence).
        // RED DEMO (one of the four, same shape/helper/format string as the other three): renamed
        // RunProgressPanel.xaml:50's Binding CurrentActivity to CurrentActivityX (single-occurrence,
        // grep-confirmed), full -t:Rebuild (still 0 Warning(s)/0 Error(s) -- a binding path is not a compile
        // error), ran the class: with this anchor present, "Assert.Contains() Failure: Filter not matched in
        // collection" here (:96); with this line ALSO temporarily commented out (test file only, never
        // src/), the sweep at :108 failed independently naming
        // "TextBlock.Text=CurrentActivityX [RunProgressViewModel] UNRESOLVED". Both the XAML typo and the
        // comment-out reverted immediately; git diff --stat -- src/ came back empty.
        Assert.Contains(bindings, b => b.Contains("=TruncationNote "));   // the state chip's region (:24)
        Assert.Contains(bindings, b => b.Contains("=CurrentActivity "));  // the activity line (:50)
        Assert.Contains(bindings, b => b.Contains("=PublishCommand "));   // the publish offer (:40)
        Assert.Contains(bindings, b => b.Contains("=TimelineNote "));     // the first Expander's content (:120)

        // Proves the walk reached the SECOND Expander's content and did not stop at the first: both Expanders'
        // Content IS in the logical tree at parse time regardless of IsExpanded (Expander : HeaderedContentControl
        // : ContentControl adds it unconditionally). If that ever stopped being true this walk would silently
        // lose tuples 25-28 -- it does NOT, on its own, prove tuples 19-24 (the first Expander's content) were
        // walked; the =TimelineNote anchor above covers that half.
        Assert.Contains(bindings, b => b.Contains("=Children "));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in Controls/Assistant/RunProgressPanel.xaml do not resolve to a public " +
            "property on the ViewModel the markup roots them at, so they bind to nothing and fail silently " +
            $"at runtime: {string.Join(", ", unresolved)}");
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
    private static RunProgressViewModel CreateRunProgressViewModelWithInterpolatingLocalization()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{(string)ci[0]}|{string.Join(",", (object[])ci[1])}");

        return new RunProgressViewModel(
            Substitute.For<IAgentRunService>(), Guid.NewGuid(), loc,
            Substitute.For<IAgentRunResumeService>(), NullLogger.Instance);
    }
}
