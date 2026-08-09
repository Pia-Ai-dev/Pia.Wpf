using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Controls.Assistant;
using Pia.Controls.Chat;
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
/// Resource keys, <c>loc:Str</c> keys and Binding PATHS are runtime concerns that fail SILENTLY, and only a real
/// parse against a process-wide <see cref="Application"/> makes them observable.
/// </summary>
[Collection("WpfApplicationStatic")]
public class AssistantViewParseTests
{
    // ViewStrings.resx (neutral = EN): LocalizationSource stays on InvariantCulture because no test ever calls
    // SetCulture, so the neutral resx is deterministically what the view renders.
    private const string HintText =
        "A background run is writing to this chat. Sending resumes when it finishes.";

    /// <summary>Composer hint for a too-short goal (Assistant_GoalTooShort_Hint), same resx source as <see cref="HintText"/> above.</summary>
    private const string GoalTooShortHintText =
        "This looks too short to run as a goal. Add a few more words so it can be planned.";

    [Fact]
    public void ComposerHint_Parses_AndTracksForeignRunActive()
    {
        // Run(mutate) → Pump() → Run(observe): the WPF objects live in these locals but are only ever
        // DEREFERENCED inside a Run body, so only primitives and enums cross back to the test thread.
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

    // Sets the ObservableProperty the XAML binds to, because this harness must never touch InputText.
    [Fact]
    public void ComposerHint_Parses_AndTracksGoalTooShortHintVisible()
    {
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
                hint = FindTextBlocks(view!).FirstOrDefault(tb => tb.Text == GoalTooShortHintText);
                return (hint is not null, hint?.Visibility);
            });

            WpfStaHost.Run(() =>
            {
                vm!.GoalTooShortHintVisible = true;
                return 0;
            });
            WpfStaHost.Pump();

            after = WpfStaHost.Run(() => hint?.Visibility);
        }
        finally
        {
            WpfStaHost.Run(() =>
            {
                vm?.Dispose();
                return 0;
            });
        }

        Assert.True(found,
            $"No TextBlock in the parsed AssistantView renders '{GoalTooShortHintText}'. Either the view " +
            "failed to parse, or the loc:Str key Assistant_GoalTooShort_Hint no longer resolves.");
        Assert.Equal(Visibility.Collapsed, before);
        Assert.Equal(Visibility.Visible, after);
    }

    [Fact]
    public void ParsedView_HasNoUnresolvedLocalizationKeys()
    {
        // An unknown key renders as the literal "[Key]", so it is visible as rendered text. The walk yields
        // TextBlocks, so only loc:Str bound to TextBlock.Text is observable without realizing templates.
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

        // Non-vacuity floor: "no unresolved keys" is trivially true over an EMPTY walk, which is reachable if the
        // logical descent stops reaching deep children.
        Assert.Contains(HintText, rendered);

        Assert.True(unresolved.Count == 0,
            $"unresolved loc:Str keys among the {rendered.Count} TextBlocks walked in the parsed " +
            $"AssistantView: {string.Join(", ", unresolved)}");
    }

    // LOGICAL, not visual: a freshly constructed UserControl has no visual children until its template is applied,
    // and forcing layout would arm the PresentationSource and Loaded handlers this test must not.
    private static IEnumerable<TextBlock> FindTextBlocks(DependencyObject root)
    {
        if (root is TextBlock tb)
            yield return tb;

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in FindTextBlocks(child))
                yield return descendant;
    }

    // Drives RunProgressPanel directly rather than through AssistantView, whose Measure/Arrange would arm three
    // Loaded handlers; the row template's paths resolve at runtime and used to fail silently.
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

        // The expand's load is a fire-and-forget the VM exposes so a fact can await it instead of racing it: the
        // read hops off-thread, so draining the dispatcher alone would NOT wait for it.
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

                // The ItemsControl is declared markup, so it IS in the logical tree, but its generated containers
                // are not — so instantiate the row template the way WPF does and bind one row to it by hand.
                var items = BindingPathWalker.FindLogical<ItemsControl>(panel!)
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
            // The VM subscribes to IAgentRunService.RunChanged in its ctor, and a leaked subscriber on a host that
            // outlives every test is how one fact reaches into another.
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

    // DP-level facts no ViewModel test can see: a Guid? bound to the Guid PersonaId DP, and an unbound Emoji, both
    // shipped as an always-empty avatar box with the build and the suite green.
    [Fact]
    public void RunProgressPanel_RendersAStepRow_WithItsPersonaAvatar()
    {
        var personaId = Guid.NewGuid();
        RunProgressViewModel? vm = null;
        RunProgressPanel? panel = null;
        FrameworkElement? row = null;

        Guid boundPersonaId;
        string? boundEmoji, boundAccent;
        Visibility boundVisibility;
        string titleText;
        try
        {
            WpfStaHost.Run(() =>
            {
                vm = CreateRunProgressViewModel();
                panel = new RunProgressPanel { DataContext = vm };
                return 0;
            });

            // Before this drain the panel's ItemsSource bindings have not transferred, so the identity match below
            // is also what proves the ItemsControl found is the steps one and not the trace's.
            WpfStaHost.Pump();

            WpfStaHost.Run(() =>
            {
                var items = BindingPathWalker.FindLogical<ItemsControl>(panel!)
                    .Single(ic => ReferenceEquals(ic.ItemsSource, vm!.Steps));
                row = (FrameworkElement)items.ItemTemplate.LoadContent();
                row.DataContext = new StepRowViewModel
                {
                    Title = "Draft the release summary",
                    Status = AgentStepStatus.Running,
                    PersonaId = personaId,
                    PersonaEmoji = "🧭",
                    PersonaAccent = "#2563EB",
                };
                return 0;
            });
            WpfStaHost.Pump();

            (boundPersonaId, boundEmoji, boundAccent, boundVisibility, titleText) = WpfStaHost.Run(() =>
            {
                var avatar = BindingPathWalker.FindLogical<PiaPersonaAvatar>(row!).Single();

                // TextBlock.Text reads "" for a TextBlock whose content came from Inlines, so reading Text would
                // silently stop observing the title; located by its FontWeight path and read through its Runs.
                var title = FindTextBlocks(row!)
                    .Single(tb => BindingPathWalker.PathOf(tb, TextBlock.FontWeightProperty) == "Status");
                return (avatar.PersonaId, avatar.Emoji, avatar.AccentColor, avatar.Visibility,
                    string.Concat(title.Inlines.OfType<Run>().Select(r => r.Text)));
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

        // The Guid?/Guid mismatch: a re-introduced one leaves the DP at its Guid.Empty default rather than
        // failing, which is precisely why it shipped.
        Assert.Equal(personaId, boundPersonaId);

        // The binding that did not exist at all. An unbound DP reads as its default, so equality against the
        // row's value is the only form of this assertion that can fail.
        Assert.Equal("🧭", boundEmoji);
        Assert.Equal("#2563EB", boundAccent);

        // HasPersona → Visibility, i.e. the avatar is actually shown for an attributed step.
        Assert.Equal(Visibility.Visible, boundVisibility);

        // EXACT equality over the inline content: a whitespace-separated pair of Runs would render a trailing
        // space, and a misspelt Title path would render "".
        Assert.Equal("Draft the release summary", titleText);
    }

    /// <summary>ViewStrings.resx (neutral = EN), same reasoning as <see cref="HintText"/>.</summary>
    private const string TimelineEmptyText = "No tool decisions were recorded for this run.";

    // Store-less: the trailing-optional IAgentTimelineService is omitted so nothing reads a database. Must be
    // called ON the STA thread — the VM captures SynchronizationContext.Current for its collection mutations.
    private static RunProgressViewModel CreateRunProgressViewModel()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        return new RunProgressViewModel(
            Substitute.For<IAgentRunService>(), Guid.NewGuid(), loc,
            Substitute.For<IAgentRunResumeService>(), NullLogger.Instance);
    }

    // Must be called ON the STA thread: the ctor builds ChatTitleChipViewModel, which throws when
    // SynchronizationContext.Current is null. Never touch InputText or force layout — both arm timers or handlers.
    private static AssistantViewModel CreateAssistantViewModel()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        // IWorkingDirectoryService is left UNSTUBBED on purpose: stub it to a real path and the ctor's queued
        // callback mutates the session's working directory at an unpredictable later Pump().
        var meeting = new MeetingAttendeeViewModel(
            Substitute.For<IMeetingAttendeeService>(),
            settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            NullLogger<MeetingAttendeeViewModel>.Instance,
            new InlineUiDispatcher());

        var directTranscription = new DirectTranscriptionViewModel(
            Substitute.For<IDirectTranscriptionService>(),
            settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            NullLogger<DirectTranscriptionViewModel>.Instance,
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
            directTranscription,
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
