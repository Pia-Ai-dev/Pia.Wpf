using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Controls.Assistant;
using Pia.Models;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Pia.Tests.Services;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Pia.Views;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// The suite's first test that PARSES a View. Markup compilation catches malformed XAML and unknown
/// types/properties, but resource-key resolution, <c>loc:Str</c> keys and Binding PATHS are runtime
/// concerns — and a wrong binding path fails SILENTLY. That was confirmed, not assumed: a deliberately
/// misspelled path on this very composer hint produced an always-visible hint with the build still at
/// 0 errors and the suite still green. This test is the only thing in the repo that catches that class
/// of regression.
/// <para>
/// One assertion covers three failure modes at once: the view parses (every <c>StaticResource</c> in
/// the non-templated regions resolves), the <c>loc:Str</c> key resolves, and the Binding path
/// <c>ForeignRunActive</c> is spelled right.
/// </para>
/// <para>
/// Requires a real <see cref="Application"/>, which is process-wide and cannot be torn down. That is
/// what Batch 12 paid for: with <c>IUiDispatcher</c> injected, a live <c>Application</c> no longer
/// changes any ViewModel's threading behaviour. Before that migration this test cost 42
/// <c>MeetingAttendeeViewModelTests</c> failures and was withdrawn.
/// </para>
/// <para>
/// <b>Honesty, because this file could not be executed where it was written:</b> it was authored on
/// macOS, where the test host cannot run at all (no <c>Microsoft.WindowsDesktop.App</c> for osx-arm64,
/// 0 tests executed). It has NEVER been executed — not the STA thread, not <c>new App()</c>, not the
/// XAML parse, not <c>Pump()</c>. The first Windows run is what validates BOTH this test and the
/// process-wide-<c>Application</c> risk it introduces for the rest of the suite: the ~13 converters
/// that read <c>Application.Current?.TryFindResource</c> (category (c)) start returning real brushes,
/// and the service-layer dispatcher reads in <c>OutputService</c> / <c>WindowManagerService</c> /
/// the notification surfaces (category (d)) stop taking their null branch. If that run hangs rather
/// than fails, <c>Dispatcher.Run()</c> in <see cref="WpfStaHost"/> is the first suspect; every wait
/// this test owns is bounded so it reports a cause instead of blocking the suite.
/// </para>
/// <para>
/// Scope limits, stated so nobody reads this as "the whole view parses": the walk is LOGICAL, so it
/// descends into <c>AutocompletePopup</c>'s <c>Popup.Child</c> (content a visual walk would skip), and
/// it does NOT cover the message <c>ItemsControl.ItemTemplate</c> or the persona item template —
/// <c>Messages</c> and <c>AvailablePersonas</c> are empty, so that deferred content is never realized
/// and its styles, converters and loc keys stay out of reach.
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class AssistantViewParseTests
{
    // ViewStrings.resx (neutral = EN). LocalizationSource's culture is InvariantCulture and no test
    // ever calls SetCulture (the only writer is LocalizationService, and every test substitutes
    // ILocalizationService), so the neutral resx is deterministically what the view renders.
    private const string HintText =
        "A background run is writing to this chat. Sending resumes when it finishes.";

    [Fact]
    public void ComposerHint_Parses_AndTracksForeignRunActive()
    {
        // Run(mutate) → Pump() → Run(observe), because Pump() drains from the TEST thread now (see
        // WpfStaHost.Pump). The WPF objects live in these locals but are only ever DEREFERENCED inside a
        // Run body, i.e. always on the host thread; only primitives and enums cross back.
        AssistantViewModel? vm = null;
        AssistantView? view = null;
        TextBlock? hint = null;
        bool found;
        Visibility? before, after;
        try
        {
            WpfStaHost.Run(() =>
            {
                vm = CreateAssistantViewModel();
                view = new AssistantView { DataContext = vm };
                return 0;
            });
            WpfStaHost.Pump();

            (found, before) = WpfStaHost.Run(() =>
            {
                hint = FindTextBlocks(view!).FirstOrDefault(tb => tb.Text == HintText);
                return (hint is not null, hint?.Visibility);
            });

            WpfStaHost.Run(() =>
            {
                // Set the [ObservableProperty] directly. Going through ChatSession.ForeignRunActiveChanged
                // would route via the injected dispatcher and add a second thing under test.
                vm!.ForeignRunActive = true;
                return 0;
            });
            WpfStaHost.Pump();

            after = WpfStaHost.Run(() => hint?.Visibility);
        }
        finally
        {
            // On the host thread, and in a finally: the VM subscribes to events on construction, so a failed
            // assertion must not leak a live subscriber onto a dispatcher that outlives every test.
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }

        Assert.True(found,
            $"No TextBlock in the parsed AssistantView renders '{HintText}'. Either the view failed to " +
            "parse, or the loc:Str key Assistant_BackgroundRunActive_Hint no longer resolves.");
        Assert.Equal(Visibility.Collapsed, before);
        Assert.Equal(Visibility.Visible, after);
    }

    [Fact]
    public void ParsedView_HasNoUnresolvedLocalizationKeys()
    {
        // LocalizationSource returns the literal "[Key]" for an unknown key, and StrExtension.ProvideValue
        // binds [{Key}] against the static LocalizationSource.Instance with an explicit Source (no
        // DataContext, no DI). So an unresolved key is visible as rendered text, for five lines.
        //
        // SCOPE, stated so this is not read as "the whole view": the walk yields TextBlocks, so only
        // loc:Str bound to TextBlock.Text is visible — 4 of the 22 loc:Str usages in AssistantView.xaml
        // (:65, :247, :493, :587). The other 18 are ToolTip (11), Content (5, on ui:Button, where the
        // string becomes a TextBlock only after template application), PlaceholderText and Value: all
        // structurally invisible to a logical walk without layout. Widening it means realizing templates,
        // which is exactly what this file must not do.
        AssistantViewModel? vm = null;
        AssistantView? view = null;
        List<string> rendered, unresolved;
        try
        {
            WpfStaHost.Run(() =>
            {
                vm = CreateAssistantViewModel();
                view = new AssistantView { DataContext = vm };
                return 0;
            });
            WpfStaHost.Pump();

            (rendered, unresolved) = WpfStaHost.Run(() =>
            {
                var texts = FindTextBlocks(view!).Select(tb => tb.Text).ToList();
                var hits = texts
                    .Where(t => t is not null && Regex.IsMatch(t, @"^\[\w+\]$"))
                    .Distinct()
                    .ToList();
                return (texts, hits);
            });
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }

        // NON-VACUITY FLOOR, and it carries the whole assertion below: "no unresolved keys" is trivially
        // true over an EMPTY walk, which is reachable — if LogicalTreeHelper.GetChildren stops descending
        // (a container swapped for a templated one, a refactor of FindTextBlocks), this fact would report
        // a clean sweep over nothing and stay green forever. Anchoring on the one string the composer is
        // known to render costs nothing (the walk already materialised it), adds no new failure mode the
        // fact above does not already carry, and proves the walk reached a deep logical descendant.
        Assert.Contains(HintText, rendered);

        Assert.True(unresolved.Count == 0,
            $"unresolved loc:Str keys among the {rendered.Count} TextBlocks walked in the parsed " +
            $"AssistantView: {string.Join(", ", unresolved)}");
    }

    /// <summary>
    /// LOGICAL tree, not visual: AssistantView is a UserControl, i.e. a templated ContentControl, so its
    /// Content is not a VISUAL child until the template is applied — a VisualTreeHelper walk from a
    /// freshly constructed view finds ZERO children. Making a visual walk work would require layout, and
    /// layout is exactly what drags in the Wpf.Ui measure paths, a PresentationSource and the Loaded
    /// handlers this test must not arm. The logical tree is populated by InitializeComponent() with no
    /// layout at all, and binding evaluation needs no layout either — Visibility is a DP set by the
    /// BindingExpression, which Pump() drains.
    /// </summary>
    private static IEnumerable<TextBlock> FindTextBlocks(DependencyObject root)
    {
        if (root is TextBlock tb)
            yield return tb;

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in FindTextBlocks(child))
                yield return descendant;
    }

    /// <summary>
    /// Batch 03's trace row, RENDERED. The panel's own binding paths resolve at runtime and fail silently, and
    /// two of them were bound by nothing at all: <c>OutcomeSuffix</c> and <c>StepLabel</c> were computed,
    /// localized into three resx files and unit-tested over the VM property, while the row template bound three
    /// columns — so an <c>Error</c> row rendered byte-identically to a successful one on the one surface whose
    /// job is to say what happened.
    /// <para>
    /// Drives <see cref="RunProgressPanel"/> DIRECTLY rather than through <see cref="AssistantView"/>: this file
    /// documents Measure/Arrange on that view as a measured hazard (it arms three <c>Loaded</c> handlers), and a
    /// layout pass is exactly what is needed here to realize the deferred <c>ItemTemplate</c>. The panel's own
    /// code-behind is nothing but <c>InitializeComponent</c>.
    /// </para>
    /// <para>
    /// Note for the record: the builder's open item claimed "NOTHING parses RunProgressPanel.xaml". That is
    /// false — <c>AssistantView.xaml</c> places the panel as a plain element, so
    /// <c>AssistantView.InitializeComponent()</c> already constructs it and runs its
    /// <c>InitializeComponent()</c>, meaning the Expander's non-deferred markup has been parsed by the fact
    /// above since it landed. What was genuinely uncovered is the deferred row template and the binding paths,
    /// which is what this fact adds.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunProgressPanel_RendersATimelineRow_WithItsStepOutcomeAndDecision()
    {
        RunProgressViewModel? vm = null;
        RunProgressPanel? panel = null;
        FrameworkElement? row = null;

        WpfStaHost.Run(() =>
        {
            vm = CreateRunProgressViewModel();
            panel = new RunProgressPanel { DataContext = vm };
            // Expanding RE-READS the trace, and with no store behind this VM that read clears the collection —
            // so this happens before any row is added.
            vm.IsTimelineExpanded = true;
            return 0;
        });

        // The expand's load is a fire-and-forget the VM exposes precisely so a fact can await it instead of
        // racing it; the read hops off-thread, so draining the dispatcher alone would NOT wait for it. That is
        // the seam eb0fb369 added and this fact never used — and its absence is why the projection could land
        // during a LATER test and overwrite state nobody expected to move.
        await WpfStaHost.Run(() => vm!.TimelineLoadTask)!;
        WpfStaHost.Pump();

        string[] texts;
        Visibility? emptyLineVisibility;
        try
        {
            WpfStaHost.Run(() =>
            {
                // AFTER the load, deliberately: the expand's own (store-less) projection would otherwise
                // overwrite this. This is what the real load does once it has rows.
                vm!.HasNoTimeline = false;

                // The trace's ItemsControl is declared markup, so it IS in the logical tree; its generated
                // containers are not (the walk here is logical — the same documented limit that keeps the
                // message template out of reach). So instantiate the row template the way WPF does and bind
                // one row to it: that is the TEMPLATE INSTANTIATION the builder's open item said no test
                // could reach, and it pins every path in the template without a layout pass on a view whose
                // Loaded handlers are a hazard.
                var items = FindLogical<ItemsControl>(panel!)
                    .Single(ic => ReferenceEquals(ic.ItemsSource, vm.Timeline));
                row = (FrameworkElement)items.ItemTemplate.LoadContent();
                row.DataContext = new TimelineRowViewModel
                {
                    TimeLabel = "14:03",
                    StepLabel = "Step 2",
                    ToolName = "write_file",
                    OutcomeSuffix = "failed",
                    DecisionLabel = "Auto-approved",
                };
                return 0;
            });
            WpfStaHost.Pump();

            (texts, emptyLineVisibility) = WpfStaHost.Run(() =>
            {
                var empty = FindTextBlocks(panel!).FirstOrDefault(tb => tb.Text == TimelineEmptyText);
                return (FindTextBlocks(row!).Select(tb => tb.Text).ToArray(), empty?.Visibility);
            });
        }
        finally
        {
            // The withdrawn version leaked this: the VM subscribes to IAgentRunService.RunChanged in its
            // ctor, and a leaked subscriber on a host that outlives every test is exactly how one fact
            // reaches into another.
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }

        // The five row binding paths, one assertion each. A typo in any of them used to ship silently.
        Assert.Contains("14:03", texts);
        Assert.Contains("Step 2", texts);
        Assert.Contains("write_file", texts);
        Assert.Contains("failed", texts);
        Assert.Contains("Auto-approved", texts);

        // …and the HasNoTimeline path: the "nothing recorded" line is not shown over a row.
        Assert.Equal(Visibility.Collapsed, emptyLineVisibility);
    }

    /// <summary>ViewStrings.resx (neutral = EN), same reasoning as <see cref="HintText"/>.</summary>
    private const string TimelineEmptyText = "No tool decisions were recorded for this run.";

    /// <summary>The <see cref="FindTextBlocks"/> walk, generalized — logical, for the same reason.</summary>
    private static IEnumerable<T> FindLogical<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T hit)
            yield return hit;

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in FindLogical<T>(child))
                yield return descendant;
    }

    /// <summary>
    /// A store-less <see cref="RunProgressViewModel"/>: the trailing-optional <c>IAgentTimelineService</c> is
    /// omitted on purpose, so nothing here reads a database and the rows under test are the ones this fact adds.
    /// Must be called ON the STA thread — the VM captures <c>SynchronizationContext.Current</c> and marshals
    /// every collection mutation through it.
    /// </summary>
    private static RunProgressViewModel CreateRunProgressViewModel()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        return new RunProgressViewModel(
            Substitute.For<IAgentRunService>(), Guid.NewGuid(), loc,
            Substitute.For<IAgentRunResumeService>(), NullLogger.Instance);
    }

    /// <summary>
    /// The REAL AssistantViewModel — a lightweight INPC stub would sidestep exactly the claim this test
    /// exists to prove. Must be called ON the STA thread: the ctor builds ChatTitleChipViewModel, which
    /// derives from UiThreadViewModel with <c>base(requireUiThread: true)</c> and throws when
    /// <c>SynchronizationContext.Current</c> is null.
    /// <para>
    /// Built inline rather than by reusing <c>AssistantViewModelLeverTests.CreateSut</c>: that is an
    /// instance method over six instance substitute fields shared by all 21 of its facts, and lifting
    /// them into a shared builder is a refactor whose only verification is the Windows run. This copy is
    /// purely additive — if it is wrong, only this file fails. It also must NOT install the bare
    /// SynchronizationContext that CreateSut installs, because the real DispatcherSynchronizationContext
    /// on the STA thread is the behaviour under test.
    /// </para>
    /// <para>
    /// Hard prohibitions, each a measured hazard: never touch <c>vm.InputText</c> (the composer's
    /// AtCommandAutocompleteBehavior hooks an <c>async</c> DispatcherTimer.Tick, an unhandled-exception
    /// source on a pumping dispatcher); never raise <c>IPersonaService.PersonasChanged</c>
    /// (LoadPersonasAsync NREs on <c>active.Id</c> before it reaches the dispatcher); never open a
    /// Window, force Loaded, or call Measure/Arrange/UpdateLayout (a PresentationSource arms
    /// AssistantView.OnLoaded, TodoPanelControl.OnLoaded's DI + LoadTodosAsync, and
    /// ViewModelLocator.OnElementLoaded); never call <c>Application.Shutdown()</c>.
    /// </para>
    /// </summary>
    private static AssistantViewModel CreateAssistantViewModel()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        // IWorkingDirectoryService is left UNSTUBBED on purpose: EnsureSubfolder's default (null/empty)
        // makes ApplyDefaultWorkingDirectoryAsync — fired from the ctor — return before it reaches the
        // dispatcher. Stub it to a real path and the queued callback mutates the session's working
        // directory at an unpredictable later Pump().
        var meeting = new MeetingAttendeeViewModel(
            Substitute.For<IMeetingAttendeeService>(),
            settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            NullLogger<MeetingAttendeeViewModel>.Instance,
            new InlineUiDispatcher());

        return new AssistantViewModel(
            NullLogger<AssistantViewModel>.Instance,
            Substitute.For<IAiClientService>(),
            Substitute.For<IProviderService>(),
            Substitute.For<IPersonaService>(),
            settings,
            Substitute.For<IOutputService>(),
            Substitute.For<IPluginService>(),
            Substitute.For<IVoiceInputService>(),
            Substitute.For<ITtsService>(),
            Substitute.For<IAudioRecordingService>(),
            Substitute.For<ITranscriptionService>(),
            NullLoggerFactory.Instance,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            Substitute.For<ILocalizationService>(),
            Substitute.For<ITokenMapService>(),
            Substitute.For<IAutocompleteService>(),
            Substitute.For<INavigationService>(),
            Substitute.For<ISuggestionService>(),
            Substitute.For<IAssistantChatService>(),
            meeting,
            Substitute.For<IAssistantPromptComposer>(),
            Substitute.For<IProviderCapabilityService>(),
            Substitute.For<IAgentRunService>(),
            Substitute.For<IAgentRunResumeService>(),
            Substitute.For<IChatSessionManager>(),
            Substitute.For<IWorkingDirectoryService>(),
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IMarkdownExportService>(),
            Substitute.For<IDialogService>(),
            new InlineUiDispatcher(),
            Substitute.For<IToolPermissionService>());
    }
}
