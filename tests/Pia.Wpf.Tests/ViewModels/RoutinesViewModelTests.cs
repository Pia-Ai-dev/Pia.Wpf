using System.Collections;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Resources.Strings;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;
using Pia.Services.MeetingAttendee;

namespace Pia.Tests.ViewModels;

/// <summary>The facts a XAML parse test cannot reach: an unrecognised status, a refusal reaching the user, a
/// malformed time refused rather than coerced, and the recurrence day actually leaving the editor.</summary>
public class RoutinesViewModelTests
{
    private static ILocalizationService Localizer()
    {
        // Echoes the key back, and formats by appending the argument — enough to assert WHICH message was
        // chosen without pinning any English text, which LocalizationTests owns.
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci =>
        {
            var key = (string)ci[0];
            return IsBlueprintText(key) ? BlueprintLookup(key) : key;
        });
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{(string)ci[0]}:{string.Join(",", (object[])ci[1])}");
        return loc;
    }

    /// <summary>A blueprint's template, slot defaults and guard are resx values, so echoing their keys would
    /// leave the goal box with no {slot} to render and the slot facts below untestable. Every other key still
    /// echoes, which is what keeps the message assertions free of English.</summary>
    private static bool IsBlueprintText(string key) =>
        key.EndsWith("_Query", StringComparison.Ordinal)
        || (key.Contains("_Slot_", StringComparison.Ordinal) && key.EndsWith("_Default", StringComparison.Ordinal))
        || (key.StartsWith("Routines_Catalog_", StringComparison.Ordinal) && key.EndsWith("Guard", StringComparison.Ordinal));

    private static string BlueprintLookup(string key) =>
        ViewStrings.ResourceManager.GetString(key, CultureInfo.InvariantCulture) ?? key;

    private static RoutineBlueprintText TextOf(RoutineBlueprint blueprint) =>
        RoutineBlueprintText.Resolve(blueprint, BlueprintLookup);

    private static string SlotDefault(RoutineBlueprint blueprint, int index) =>
        TextOf(blueprint).SlotDefaults[blueprint.Slots[index].Name]!;

    private static readonly Guid FilesPlugin = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TodoPlugin = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid McpPlugin = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>Deliberately unsorted, so the ordering assertion is not vacuous, and with two plugins exposing
    /// one tool name — the grant list is name-only, so those two rows are a single grant.</summary>
    private static IReadOnlyList<ToolCatalogEntry> ToolCatalog() =>
    [
        new(TodoPlugin, "todo", "create_todo", "Create a todo", IsExternalRoute: false, ServerDeclaredDestructive: false),
        new(FilesPlugin, "files", "write_file", "Write a file", IsExternalRoute: false, ServerDeclaredDestructive: false),
        new(FilesPlugin, "files", "delete_file", "Delete a file", IsExternalRoute: false, ServerDeclaredDestructive: false),
        new(McpPlugin, "some-mcp-server", "create_todo", "Create a todo", IsExternalRoute: true, ServerDeclaredDestructive: false),
    ];

    private sealed record Sut(
        RoutinesViewModel Vm,
        IScheduledJobService Jobs,
        IScheduledJobRunner Runner,
        IProviderService Providers,
        IPersonaService Personas,
        IAgentRunService Runs,
        IDialogService Dialogs,
        IWindowManagerService Windows,
        IPluginService Plugins,
        ITextOptimizationService Drafting);

    private static Sut CreateSut(params ScheduledJob[] jobs) => CreateSut(runs: null, jobs);

    /// <param name="runs">The run-history source, or null for one that reports no firings.</param>
    private static Sut CreateSut(IAgentRunService? runs, params ScheduledJob[] jobs)
    {
        var service = Substitute.For<IScheduledJobService>();
        service.GetAllAsync().Returns(jobs);
        service.IsOwnedByThisDeviceAsync(Arg.Any<ScheduledJob>()).Returns(true);

        var providers = Substitute.For<IProviderService>();
        providers.GetProvidersAsync().Returns(Array.Empty<AiProvider>());

        // Must be stubbed: an unstubbed Task-returning member hands back null, and RefreshAsync awaits it.
        var personas = Substitute.For<IPersonaService>();
        personas.GetPersonasAsync().Returns(Array.Empty<Persona>());

        var runner = Substitute.For<IScheduledJobRunner>();

        // Only stubbed when this method owns the substitute: an Arg.Any setup applied afterwards would override
        // the per-job history a caller had already configured.
        if (runs is null)
        {
            runs = Substitute.For<IAgentRunService>();
            runs.GetFiringsForTriggerAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ScheduledFiringOutcome>());
        }

        var dialogs = Substitute.For<IDialogService>();
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var windows = Substitute.For<IWindowManagerService>();

        // Stubbed explicitly: an auto-valued catalogue would silently make every picker assertion vacuous.
        var plugins = Substitute.For<IPluginService>();
        plugins.GetToolCatalog().Returns(ToolCatalog());

        var drafting = Substitute.For<ITextOptimizationService>();

        var vm = new RoutinesViewModel(service, runner, providers, personas, runs, dialogs, windows, Localizer(),
            plugins, Substitute.For<IBrowserProvisioner>(), NullLogger<RoutinesViewModel>.Instance, drafting);

        return new Sut(vm, service, runner, providers, personas, runs, dialogs, windows, plugins, drafting);
    }

    private static RoutineDraft Draft(
        string? name = "Drafted",
        string? goal = "Do the thing.",
        RecurrenceType? recurrence = RecurrenceType.Weekly,
        DayOfWeek? dayOfWeek = DayOfWeek.Thursday,
        string? timeOfDay = "06:45",
        ReasoningEffort? effort = ReasoningEffort.High,
        bool needsWebSearch = false,
        IReadOnlyList<string>? tools = null) =>
        new(name, goal, recurrence, dayOfWeek,
            timeOfDay is null ? null : TimeOnly.ParseExact(timeOfDay, "HH\\:mm"), effort, needsWebSearch,
            tools);

    // ---- required fields ------------------------------------------------------------------------

    /// <summary>The set SaveAsync refuses on, surfaced before the click so the editor can grey out Save.</summary>
    [Fact]
    public void ABlankNameOrGoal_CannotSave()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);

        Assert.False(sut.Vm.CanSave);

        sut.Vm.EditName = "Nightly";
        Assert.False(sut.Vm.CanSave);

        sut.Vm.EditQuery = "Report what changed.";
        Assert.True(sut.Vm.CanSave);

        sut.Vm.EditTimeOfDay = "   ";
        Assert.False(sut.Vm.CanSave);
    }

    /// <summary>A meeting has no goal — the link is the instruction — so the two meeting fields take its place.</summary>
    [Fact]
    public void AMeetingNeedsItsLinkAndItsConsentInsteadOfAGoal()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "Standup";
        sut.Vm.EditKind = ScheduledJobKind.MeetingAttendance;

        Assert.False(sut.Vm.CanSave);

        sut.Vm.EditMeetingUrl = "https://teams.microsoft.com/l/meetup-join/x";
        Assert.False(sut.Vm.CanSave);

        sut.Vm.EditMeetingConsent = true;
        Assert.True(sut.Vm.CanSave);
    }

    /// <summary>CanSave is a required-field marker, so the FORMAT check has to stay on the save path — the
    /// alternative is a greyed-out button with nothing saying the time is malformed.</summary>
    [Fact]
    public async Task AMalformedTime_StillPassesCanSaveAndIsRefusedOnSave()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "Nightly";
        sut.Vm.EditQuery = "Report what changed.";
        sut.Vm.EditTimeOfDay = "25:99";

        Assert.True(sut.Vm.CanSave);
        await sut.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Settings_ScheduledJobs_Validation_Time", sut.Vm.StatusMessage);
    }

    // ---- drafting -------------------------------------------------------------------------------

    [Fact]
    public async Task ADraft_FillsTheNameGoalAndSchedule()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Drafting.GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>()).Returns(Draft());
        sut.Vm.EditDescription = "remind me what shipped every Thursday morning";

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        Assert.Equal("Drafted", sut.Vm.EditName);
        Assert.Equal("Do the thing.", sut.Vm.EditQuery);
        // Not AgentTask, which a blank start opens on: an AgentTask with no grants is remapped by the launcher
        // to write_file, so a drafted read-only routine would run able to write files.
        Assert.Equal(ScheduledJobKind.Research, sut.Vm.EditKind);
        Assert.Equal(RecurrenceType.Weekly, sut.Vm.EditRecurrence);
        Assert.Equal(DayOfWeek.Thursday, sut.Vm.EditDayOfWeek);
        Assert.Equal("06:45", sut.Vm.EditTimeOfDay);
        Assert.Equal(ReasoningEffort.High, sut.Vm.EditEffort!.Value);
        Assert.False(sut.Vm.IsDrafting);
    }

    /// <summary>Prefill, not overwrite: a draft after some typing must leave the typing alone.</summary>
    [Fact]
    public async Task ADraft_LeavesWhatTheUserAlreadyTypedAlone()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Drafting.GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>()).Returns(Draft());
        sut.Vm.EditName = "my own name";
        sut.Vm.EditQuery = "my own wording";
        sut.Vm.EditTimeOfDay = "23:15";
        sut.Vm.EditDescription = "anything";

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        Assert.Equal("my own name", sut.Vm.EditName);
        Assert.Equal("my own wording", sut.Vm.EditQuery);
        // A typed field has no blank state, so the latch is what protects a schedule the user has chosen.
        Assert.Equal("23:15", sut.Vm.EditTimeOfDay);
        Assert.Equal(RecurrenceType.Daily, sut.Vm.EditRecurrence);
    }

    /// <summary>A kind the user actually picked is a choice, so the draft leaves it alone — a routine set up
    /// to sit in a meeting must not come back as a research job. The editor's own AgentTask default is NOT
    /// protected, deliberately: it is a default nobody chose, and an AgentTask with no grants is the case the
    /// launcher remaps to write_file.</summary>
    [Fact]
    public async Task ADraft_LeavesAHandPickedKindAlone()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Drafting.GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>()).Returns(Draft());
        sut.Vm.EditKind = ScheduledJobKind.MeetingAttendance;
        sut.Vm.EditDescription = "anything";

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        Assert.Equal(ScheduledJobKind.MeetingAttendance, sut.Vm.EditKind);
    }

    /// <summary>A card carries its own kind, and the shortened templates are all Research, so the draft has
    /// nothing to correct there.</summary>
    [Fact]
    public async Task ADraftOnTopOfACard_LeavesTheCardsKind()
    {
        var sut = CreateSut();
        sut.Drafting.GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>()).Returns(Draft());
        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.MeetingFollowup);
        var kind = sut.Vm.EditKind;
        sut.Vm.EditDescription = "anything";

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        Assert.Equal(kind, sut.Vm.EditKind);
    }

    /// <summary>A card already chose a schedule, so a draft on top of one must not move it.</summary>
    [Fact]
    public async Task ADraftOnTopOfACard_LeavesTheCardsSchedule()
    {
        var sut = CreateSut();
        sut.Drafting.GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>()).Returns(Draft());
        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);
        sut.Vm.EditDescription = "anything";

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        Assert.Equal("09:00", sut.Vm.EditTimeOfDay);
        Assert.Equal(RecurrenceType.Daily, sut.Vm.EditRecurrence);
    }

    /// <summary>A drafted goal carries no guard, so a provider that cannot search would answer from memory.</summary>
    [Fact]
    public async Task ADraftThatNeedsTheWeb_CarriesTheWebSearchGuard()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Drafting.GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>())
            .Returns(Draft(goal: "Report the news.", needsWebSearch: true));
        sut.Vm.EditDescription = "the news every morning";

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        Assert.StartsWith("Report the news. ", sut.Vm.EditQuery);
        Assert.EndsWith(BlueprintLookup(RoutineBlueprintCatalog.WebSearchGuardKey), sut.Vm.EditQuery);
    }

    [Fact]
    public async Task ADraftThatDoesNotNeedTheWeb_CarriesNoGuard()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Drafting.GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>())
            .Returns(Draft(goal: "Report the news.", needsWebSearch: false));
        sut.Vm.EditDescription = "anything";

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        Assert.Equal("Report the news.", sut.Vm.EditQuery);
    }

    [Fact]
    public async Task ABlankDescription_DraftsNothing()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditDescription = "   ";

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        await sut.Drafting.DidNotReceive().GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>());
    }

    /// <summary>The gap the persona assist has: a provider failure escapes its command with nothing shown.</summary>
    [Fact]
    public async Task ADraftThatThrows_ReportsItAndClearsTheSpinner()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Drafting.GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>())
            .Throws(new InvalidOperationException("No AI provider configured"));
        sut.Vm.EditDescription = "anything";

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        Assert.Equal("Routines_Draft_Failed", sut.Vm.StatusMessage);
        Assert.False(sut.Vm.IsDrafting);
        Assert.Empty(sut.Vm.EditName);
    }

    // ---- drafted tool grants --------------------------------------------------------------------

    [Fact]
    public async Task ADraft_TicksTheToolsItAsksFor()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Drafting.GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>())
            .Returns(Draft(tools: ["create_todo"]));
        sut.Vm.EditDescription = "turn my meeting notes into todos every evening";

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        Assert.Equal(["create_todo"], TickedTools(sut.Vm));
    }

    /// <summary>The model is offered the catalogue so it need not guess, and what it picks is checked against
    /// it anyway — an invented name would otherwise reach a stored grant and sync to every other device.</summary>
    [Fact]
    public async Task ADraft_DropsAToolThisDeviceDoesNotOffer()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Drafting.GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>())
            .Returns(Draft(tools: ["create_todo", "send_email", "post_to_slack"]));
        sut.Vm.EditDescription = "anything";

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        Assert.Equal(["create_todo"], TickedTools(sut.Vm));
    }

    /// <summary>The same create-time rule the model already faces on the tool path: a destructive name we do
    /// not ship is never offered, so it can never be picked.</summary>
    [Fact]
    public async Task ADraft_IsNeverOfferedAPresumedExternalDeleteTool()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        IReadOnlyList<RoutineDraftTool>? offered = null;
        sut.Drafting.GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>())
            .Returns(ci => { offered = (IReadOnlyList<RoutineDraftTool>)ci[1]!; return Draft(tools: ["purge_records"]); });
        sut.Vm.EditDescription = "anything";

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        Assert.NotNull(offered);
        Assert.DoesNotContain("purge_records", offered!.Select(t => t.Name));
        // delete_file is one of ours, so the picker still offers it — the filter is about names we do not ship.
        Assert.Contains("delete_file", offered!.Select(t => t.Name));
        Assert.Empty(TickedTools(sut.Vm));
    }

    /// <summary>A grant has a blank state, so nothing ticked is the only case the draft may fill — a tick the
    /// user made, or a card that deliberately grants nothing, both stand.</summary>
    [Fact]
    public async Task ADraft_LeavesAToolTheUserAlreadyTickedAlone()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Drafting.GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>())
            .Returns(Draft(tools: ["create_todo"]));
        var row = sut.Vm.EditToolGroups.SelectMany(g => g.Tools).First(t => t.ToolName == "write_file");
        row.IsSelected = true;
        sut.Vm.EditDescription = "anything";

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        Assert.Equal(["write_file"], TickedTools(sut.Vm));
    }

    [Fact]
    public async Task ADraftThatNeedsNoTools_TicksNothing()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Drafting.GenerateRoutineDraftAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<RoutineDraftTool>>(), Arg.Any<Guid?>())
            .Returns(Draft(tools: []));
        sut.Vm.EditDescription = "the news every morning";

        await sut.Vm.GenerateDraftCommand.ExecuteAsync(null);

        Assert.Empty(TickedTools(sut.Vm));
    }

    // ---- the slot value inside the goal ---------------------------------------------------------

    /// <summary>The span the editor tints, which is the only thing connecting the slot field to the prose.</summary>
    [Fact]
    public void ACardWithSlots_MarksWhereItsValueLandedInTheGoal()
    {
        var sut = CreateSut();
        var blueprint = RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.TopicDigest)!;

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);

        var value = SlotDefault(blueprint, 0);
        Assert.Equal(value.Length, sut.Vm.GoalHighlightLength);
        Assert.Equal(value, sut.Vm.EditQuery.Substring(sut.Vm.GoalHighlightStart, sut.Vm.GoalHighlightLength));
    }

    [Fact]
    public void TypingASlotValue_MovesTheMarkToTheNewValue()
    {
        var sut = CreateSut();
        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);

        sut.Vm.EditSlots[0].Value = "quantum computing";

        Assert.Equal("quantum computing",
            sut.Vm.EditQuery.Substring(sut.Vm.GoalHighlightStart, sut.Vm.GoalHighlightLength));
    }

    /// <summary>Once the prose is the user's own, the span no longer describes it.</summary>
    [Fact]
    public void AHandEditedGoal_DropsTheMark()
    {
        var sut = CreateSut();
        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);
        Assert.True(sut.Vm.GoalHighlightLength > 0);

        sut.Vm.EditQuery = "my own wording";

        Assert.Equal(0, sut.Vm.GoalHighlightLength);
    }

    [Fact]
    public void ACardWithoutSlots_MarksNothing()
    {
        var sut = CreateSut();

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.MorningBrief);

        Assert.Equal(0, sut.Vm.GoalHighlightLength);
    }

    [Fact]
    public void ABlankStart_MarksNothing()
    {
        var sut = CreateSut();
        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);

        sut.Vm.StartCreateCommand.Execute(null);

        Assert.Equal(0, sut.Vm.GoalHighlightLength);
    }

    /// <summary>The three Runs the preview binds have to reassemble into exactly the stored goal, or the user
    /// is reading something other than what will run.</summary>
    [Fact]
    public void TheGoalPreview_SplitsTheGoalWithoutChangingIt()
    {
        var sut = CreateSut();

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);

        Assert.True(sut.Vm.ShowsGoalPreview);
        Assert.Equal(sut.Vm.EditQuery, sut.Vm.GoalPrefix + sut.Vm.GoalHighlightText + sut.Vm.GoalSuffix);
        Assert.Equal(SlotDefault(RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.TopicDigest)!, 0),
            sut.Vm.GoalHighlightText);
    }

    [Fact]
    public void EditingTheGoal_ReplacesThePreviewWithTheBoxForTheRestOfTheEdit()
    {
        var sut = CreateSut();
        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);

        sut.Vm.EditGoalCommand.Execute(null);

        Assert.False(sut.Vm.ShowsGoalPreview);
        // The goal itself is untouched by the hand-over — only who renders it changes.
        Assert.Contains(SlotDefault(RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.TopicDigest)!, 0),
            sut.Vm.EditQuery);

        // A slot keystroke after the hand-over must not drag the preview back over the box.
        sut.Vm.EditSlots[0].Value = "quantum computing";
        Assert.False(sut.Vm.ShowsGoalPreview);
    }

    [Fact]
    public void ACardWithoutSlots_ShowsTheGoalBoxRatherThanAPreview()
    {
        var sut = CreateSut();

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.MorningBrief);

        Assert.False(sut.Vm.ShowsGoalPreview);
    }

    /// <summary>A meeting routine has no goal at all, so the preview must not claim the row.</summary>
    [Fact]
    public void AMeetingRoutine_ShowsNoGoalPreview()
    {
        var sut = CreateSut();
        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);
        Assert.True(sut.Vm.ShowsGoalPreview);

        sut.Vm.EditKind = ScheduledJobKind.MeetingAttendance;

        Assert.False(sut.Vm.ShowsGoalPreview);
    }

    /// <summary>Reopening the editor on another card has to put the preview back, or the second card opens on
    /// a plain box and the tint is a one-shot.</summary>
    [Fact]
    public void OpeningAnotherCard_RestoresThePreview()
    {
        var sut = CreateSut();
        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);
        sut.Vm.EditGoalCommand.Execute(null);
        Assert.False(sut.Vm.ShowsGoalPreview);

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.NewsBriefing);

        Assert.True(sut.Vm.ShowsGoalPreview);
    }

    /// <summary>A description left behind would follow the user onto the next routine they open.</summary>
    [Fact]
    public void OpeningTheEditorAgain_ClearsTheDescription()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditDescription = "left behind";

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);

        Assert.Empty(sut.Vm.EditDescription);
    }

    /// <summary>Distinct because one tool name can appear under two plugins, and that is a single grant.</summary>
    private static IReadOnlyList<string> TickedTools(RoutinesViewModel vm) =>
    [
        .. vm.EditToolGroups.SelectMany(g => g.Tools)
             .Where(t => t.IsSelected)
             .Select(t => t.ToolName)
             .Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    private static RoutineToolRow Row(RoutinesViewModel vm, string toolName) =>
        vm.EditToolGroups.SelectMany(g => g.Tools).First(t => t.ToolName == toolName);

    private static Persona NewPersona(string name) => new()
    {
        Name = name,
        SystemPrompt = "be brief",
    };

    private static ScheduledJob NewJob(ScheduledJobStatus status = ScheduledJobStatus.Active) => new()
    {
        Name = "Nightly digest",
        Query = "summarise today",
        Recurrence = RecurrenceType.Daily,
        TimeOfDay = new TimeOnly(9, 0),
        NextFireAt = DateTime.Now.AddHours(4),
        Status = status,
    };

    /// <summary>Nothing else loads this view: the list and the provider ComboBox bind collections only
    /// <c>RefreshAsync</c> fills, so without the navigation hook the view renders "no routines yet" forever —
    /// a correct binding with no data behind it, which no parse test can see.</summary>
    [Fact]
    public async Task NavigatingToTheView_LoadsTheJobs()
    {
        var sut = CreateSut(NewJob());

        await sut.Vm.OnNavigatedToAsync(null);

        await sut.Jobs.Received(1).GetAllAsync();
        Assert.True(sut.Vm.HasJobs);
        Assert.NotEmpty(sut.Vm.ProviderChoices);
    }

    /// <summary>The summary counts a CANCELLED firing with the failed ones, since it did not deliver either,
    /// while the detail list keeps every state apart.</summary>
    [Fact]
    public async Task TheRunHistoryReachesTheRow_WithCancelledCountedAsNotOk()
    {
        var job = NewJob();
        var runs = Substitute.For<IAgentRunService>();
        var settled = new DateTime(2026, 8, 4, 7, 0, 0, DateTimeKind.Utc);
        runs.GetFiringsForTriggerAsync(job.Id, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new ScheduledFiringOutcome(job.Id, Guid.NewGuid(), Guid.NewGuid(), settled, AgentRunState.Completed),
            new ScheduledFiringOutcome(job.Id, Guid.NewGuid(), Guid.NewGuid(), settled.AddHours(-1), AgentRunState.Failed),
            new ScheduledFiringOutcome(job.Id, Guid.NewGuid(), Guid.NewGuid(), settled.AddHours(-2), AgentRunState.Cancelled),
        ]);
        var sut = CreateSut(runs, job);

        await sut.Vm.RefreshAsync();

        var row = Assert.Single(sut.Vm.Jobs);
        Assert.True(row.HasRecentRuns);
        Assert.Equal("Settings_ScheduledJobs_RecentRuns:3,1,2", row.RecentRunsSummary);
        Assert.Equal(3, row.RecentRuns.Count);
        Assert.Contains(row.RecentRuns, r => r.StateLabel == "Settings_ScheduledJobs_RunState_Cancelled");
        Assert.Single(row.RecentRuns, r => r.Succeeded);
    }

    /// <summary>A history read that throws must not cost the jobs list: the row renders without the line.</summary>
    [Fact]
    public async Task AFailingHistoryRead_LeavesTheRowIntact()
    {
        var job = NewJob();
        var runs = Substitute.For<IAgentRunService>();
        runs.GetFiringsForTriggerAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ScheduledFiringOutcome>>(_ => throw new InvalidOperationException("db"));
        var sut = CreateSut(runs, job);

        await sut.Vm.RefreshAsync();

        var row = Assert.Single(sut.Vm.Jobs);
        Assert.Equal("Nightly digest", row.Name);
        Assert.False(row.HasRecentRuns);
        Assert.Empty(row.RecentRunsSummary);
    }

    /// <summary>A firing that failed produced no chat, so the detail row must not offer to open one.</summary>
    [Fact]
    public async Task AFiringWithNoChat_OffersNoLink()
    {
        var job = NewJob();
        var runs = Substitute.For<IAgentRunService>();
        var settled = new DateTime(2026, 8, 4, 7, 0, 0, DateTimeKind.Utc);
        runs.GetFiringsForTriggerAsync(job.Id, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new ScheduledFiringOutcome(job.Id, Guid.NewGuid(), Guid.NewGuid(), settled, AgentRunState.Completed),
            new ScheduledFiringOutcome(job.Id, Guid.NewGuid(), Guid.Empty, settled.AddHours(-1), AgentRunState.Failed),
        ]);
        var sut = CreateSut(runs, job);

        await sut.Vm.RefreshAsync();

        var row = Assert.Single(sut.Vm.Jobs);
        Assert.True(row.RecentRuns[0].HasChat);
        Assert.False(row.RecentRuns[1].HasChat);
    }

    [Fact]
    public async Task AnUnrecognisedStatus_RendersAsUnknownAndIsInert()
    {
        // NOT defensive padding: ScheduledJobStatus crosses the sync wire as an int and SyncMapper casts it back
        // with no Enum.IsDefined check, so a newer peer's ordinal really does arrive here.
        var sut = CreateSut(NewJob((ScheduledJobStatus)7));

        await sut.Vm.RefreshAsync();

        var row = Assert.Single(sut.Vm.Jobs);
        Assert.False(row.StatusIsKnown);
        Assert.False(row.IsEnabled);
        Assert.False(row.CanRunNow);
        Assert.Equal("Settings_ScheduledJobs_Status_Unknown:7", row.StatusLabel);
    }

    [Fact]
    public async Task TogglingAnUnrecognisedStatus_ChangesNothing_AndSaysWhy()
    {
        var sut = CreateSut(NewJob((ScheduledJobStatus)7));
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        await sut.Vm.ToggleEnabledCommand.ExecuteAsync(null);

        await sut.Jobs.DidNotReceive().EnableAsync(Arg.Any<Guid>());
        await sut.Jobs.DidNotReceive().DisableAsync(Arg.Any<Guid>());
        Assert.Equal("Settings_ScheduledJobs_UnknownStatusInert", sut.Vm.StatusMessage);
    }

    [Fact]
    public async Task AJobOwnedElsewhere_CannotBeRunFromHere()
    {
        // The owner guardrail, seen from the UI: the row says so and the button is off. The service refuses
        // independently — this is the courtesy half, and it must agree with the enforcement half.
        var job = NewJob();
        var sut = CreateSut(job);
        sut.Jobs.IsOwnedByThisDeviceAsync(job).Returns(false);

        await sut.Vm.RefreshAsync();

        var row = Assert.Single(sut.Vm.Jobs);
        Assert.False(row.OwnedByThisDevice);
        Assert.False(row.CanRunNow);
    }

    [Fact]
    public async Task RunNow_SurfacesTheRefusalReason_NotJustAFailure()
    {
        var job = NewJob();
        var sut = CreateSut(job);
        sut.Runner.RunNowAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ScheduledJobRunNowResult.NotOwner);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        await sut.Vm.RunNowCommand.ExecuteAsync(null);

        // The distinction the result enum exists for: "another device owns this" is a correct refusal and must
        // not read as a failure the user should retry.
        Assert.Equal("Settings_ScheduledJobs_RunNotOwner", sut.Vm.StatusMessage);
    }

    /// <summary>The three keys must be DISTINCT: <c>AlreadyRunning</c> falling through to the default
    /// <c>NotFound</c> arm tells the user a job that exists and is running no longer exists.</summary>
    [Fact]
    public async Task RunNow_TellsTheTruthAboutADispatchAndAboutARunAlreadyGoing()
    {
        var job = NewJob();

        async Task<string?> MessageFor(ScheduledJobRunNowResult result)
        {
            var sut = CreateSut(job);
            sut.Runner.RunNowAsync(job.Id, Arg.Any<CancellationToken>()).Returns(result);
            await sut.Vm.RefreshAsync();
            sut.Vm.SelectedJob = sut.Vm.Jobs[0];
            await sut.Vm.RunNowCommand.ExecuteAsync(null);
            return sut.Vm.StatusMessage;
        }

        var dispatched = await MessageFor(ScheduledJobRunNowResult.Dispatched);
        var busy = await MessageFor(ScheduledJobRunNowResult.AlreadyRunning);
        // NotFound is the DEFAULT arm, so "already running" landing there is exactly how this regresses — and
        // it is a different sentence about a different situation.
        var gone = await MessageFor(ScheduledJobRunNowResult.NotFound);

        Assert.Equal("Settings_ScheduledJobs_RunStarted", dispatched);
        Assert.Equal("Settings_ScheduledJobs_RunAlreadyRunning", busy);
        Assert.Equal("Settings_ScheduledJobs_RunNotFound", gone);
        Assert.NotEqual(gone, busy);
        Assert.NotEqual(gone, dispatched);
    }

    [Fact]
    public async Task Save_RefusesAMalformedTime_RatherThanCoercingTheSchedule()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "n";
        sut.Vm.EditQuery = "q";
        sut.Vm.EditTimeOfDay = "half nine";

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Settings_ScheduledJobs_Validation_Time", sut.Vm.StatusMessage);
        await sut.Jobs.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(), Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(),
            Arg.Any<string?>());
        Assert.True(sut.Vm.IsEditorOpen, "a refused save must leave the editor open with the user's input intact.");
    }

    [Fact]
    public async Task Save_OnlyCarriesASpecificDateForAOneOff()
    {
        // A date on a recurring job would persist a field the recurrence calculator ignores, and it would then
        // reappear if the job were later switched to Once.
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "n";
        sut.Vm.EditQuery = "q";
        sut.Vm.EditRecurrence = RecurrenceType.Daily;
        sut.Vm.EditSpecificDate = DateTime.Now.AddDays(5);

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).CreateAsync("n", "q", RecurrenceType.Daily, Arg.Any<TimeOnly>(),
            Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(),
            specificDate: null,
            providerId: Arg.Any<Guid?>(), grantedTools: Arg.Any<IReadOnlyCollection<string>>(),
            kind: Arg.Any<ScheduledJobKind>(), quietOnSuccess: Arg.Any<bool>(),
            personaId: Arg.Any<Guid?>(), reasoningEffort: Arg.Any<ReasoningEffort?>());
    }

    /// <summary>
    /// The whole point of the day pickers. A Weekly job saved without a DayOfWeek leaves
    /// <c>RecurrenceCalculator</c> substituting today's weekday on every recompute, so one late run relocates
    /// the job permanently.
    /// </summary>
    [Fact]
    public async Task Save_CarriesTheChosenWeekday_ForAWeeklyJob()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "n";
        sut.Vm.EditQuery = "q";
        sut.Vm.EditRecurrence = RecurrenceType.Weekly;
        sut.Vm.EditDayOfWeek = DayOfWeek.Thursday;

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).CreateAsync("n", "q", RecurrenceType.Weekly, Arg.Any<TimeOnly>(),
            dayOfWeek: DayOfWeek.Thursday, dayOfMonth: null, month: null,
            specificDate: null, providerId: Arg.Any<Guid?>(),
            grantedTools: Arg.Any<IReadOnlyCollection<string>>(),
            kind: Arg.Any<ScheduledJobKind>(), quietOnSuccess: Arg.Any<bool>(),
            personaId: Arg.Any<Guid?>(), reasoningEffort: Arg.Any<ReasoningEffort?>());
    }

    /// <summary>Each recurrence carries only the fields it reads: a Yearly job needs both month and day, a
    /// Monthly one needs no month, and neither wants a weekday.</summary>
    [Theory]
    [InlineData(RecurrenceType.Monthly, null, 14, null)]
    [InlineData(RecurrenceType.Yearly, null, 14, 6)]
    public async Task Save_CarriesOnlyTheRecurrenceFieldsThatRecurrenceReads(
        RecurrenceType recurrence, DayOfWeek? expectedDayOfWeek, int? expectedDayOfMonth, int? expectedMonth)
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "n";
        sut.Vm.EditQuery = "q";
        sut.Vm.EditRecurrence = recurrence;
        sut.Vm.EditDayOfWeek = DayOfWeek.Thursday;
        sut.Vm.EditDayOfMonth = 14;
        sut.Vm.EditMonth = 6;

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).CreateAsync("n", "q", recurrence, Arg.Any<TimeOnly>(),
            dayOfWeek: expectedDayOfWeek, dayOfMonth: expectedDayOfMonth, month: expectedMonth,
            specificDate: null, providerId: Arg.Any<Guid?>(),
            grantedTools: Arg.Any<IReadOnlyCollection<string>>(),
            kind: Arg.Any<ScheduledJobKind>(), quietOnSuccess: Arg.Any<bool>(),
            personaId: Arg.Any<Guid?>(), reasoningEffort: Arg.Any<ReasoningEffort?>());
    }

    /// <summary>A job created before the pickers existed has no stored day, and NextFireAt is the only record of
    /// the day it actually fires on — so that, not today, is what the editor must offer.</summary>
    [Fact]
    public async Task EditingAJobThatPredatesTheDayPickers_OffersTheDayItCurrentlyFiresOn()
    {
        var job = NewJob();
        job.Recurrence = RecurrenceType.Weekly;
        job.DayOfWeek = null;
        // 31 days is not a multiple of 7, so this weekday cannot coincide with today's.
        job.NextFireAt = DateTime.Now.Date.AddDays(-31).AddHours(9);
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        sut.Vm.StartEditCommand.Execute(null);

        Assert.Equal(job.NextFireAt.DayOfWeek, sut.Vm.EditDayOfWeek);
        Assert.NotEqual(DateTime.Now.DayOfWeek, sut.Vm.EditDayOfWeek);
    }

    /// <summary>The editor is ONE panel for create and edit, so the quiet checkbox has to reach BOTH service
    /// calls or a ticked box silently creates notifying jobs.</summary>
    [Fact]
    public async Task CreatingAQuietJob_PassesTheFlagThrough()
    {
        var sut = CreateSut();
        await sut.Vm.RefreshAsync();

        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "Monitor";
        sut.Vm.EditQuery = "check the feed";
        sut.Vm.EditQuietOnSuccess = true;

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).CreateAsync("Monitor", "check the feed", Arg.Any<RecurrenceType>(),
            Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<ScheduledJobKind>(), quietOnSuccess: true,
            personaId: Arg.Any<Guid?>(), reasoningEffort: Arg.Any<ReasoningEffort?>());
    }

    [Fact]
    public async Task EditingAOneOff_PassesTheNewDateThrough_WhichIsWhatReArmsIt()
    {
        // The UI half of the re-arm: without specificDate reaching UpdateAsync, a settled one-off stays settled
        // no matter what the user types. The service half is pinned in ScheduledJobServiceTests.
        var job = NewJob(ScheduledJobStatus.Completed);
        job.Recurrence = RecurrenceType.Once;
        job.SpecificDate = DateTime.Now.Date.AddDays(-2);
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        sut.Vm.StartEditCommand.Execute(null);
        var target = DateTime.Now.Date.AddDays(4);
        sut.Vm.EditSpecificDate = target;

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).UpdateAsync(job.Id, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<RecurrenceType?>(), Arg.Any<TimeOnly?>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(),
            specificDate: target, kind: Arg.Any<ScheduledJobKind?>(),
            // The editor sends these on every save, so the matcher has to name them — NSubstitute matches on
            // the whole argument list.
            quietOnSuccess: Arg.Any<bool?>(), personaId: Arg.Any<Guid?>(),
            reasoningEffort: Arg.Any<ReasoningEffort?>(), clearReasoningEffort: Arg.Any<bool>());
    }

    /// <summary>
    /// A save that throws must leave the editor exactly as the user left it. Clearing <c>EditingJobId</c> turns
    /// the retry into a duplicate CREATE, and the refresh that used to follow re-resolved the selection to a
    /// fresh row instance, which cancelled the editor and discarded the input.
    /// </summary>
    [Fact]
    public async Task AFailedSave_KeepsTheEditorOpen_WithItsEditingIdAndTheTypedInput()
    {
        var job = NewJob();
        var sut = CreateSut(job);
        sut.Jobs.When(x => x.UpdateAsync(job.Id, Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<RecurrenceType?>(), Arg.Any<TimeOnly?>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
                Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateTime?>(),
                Arg.Any<ScheduledJobKind?>(), Arg.Any<bool?>(), Arg.Any<Guid?>(),
                Arg.Any<ReasoningEffort?>(), Arg.Any<bool>()))
            .Do(_ => throw new InvalidOperationException("db"));
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];
        sut.Vm.StartEditCommand.Execute(null);
        sut.Vm.EditName = "Renamed";

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Settings_ScheduledJobs_SaveFailed", sut.Vm.StatusMessage);
        Assert.True(sut.Vm.IsEditorOpen);
        Assert.Equal(job.Id, sut.Vm.EditingJobId);
        Assert.Equal("Renamed", sut.Vm.EditName);
    }

    /// <summary>Delete is irreversible and had no gate at all in the settings surface it replaces.</summary>
    [Fact]
    public async Task Delete_AsksFirst_AndKeepsTheJobWhenTheUserDeclines()
    {
        var job = NewJob();
        var sut = CreateSut(job);
        sut.Dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        await sut.Vm.DeleteCommand.ExecuteAsync(null);

        await sut.Dialogs.Received(1).ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>());
        await sut.Jobs.DidNotReceive().DeleteAsync(Arg.Any<Guid>());
        Assert.NotNull(sut.Vm.SelectedJob);
    }

    [Fact]
    public async Task Delete_RemovesTheJob_OnceConfirmed()
    {
        var job = NewJob();
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        await sut.Vm.DeleteCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).DeleteAsync(job.Id);
    }

    /// <summary>Selection drives the whole right pane, so the commands behind it must be off without one — this
    /// is the gate that replaces the settings surface's clickable-but-silently-dead buttons.</summary>
    [Fact]
    public async Task WithNothingSelected_TheJobCommandsAreOff()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = null;

        Assert.False(sut.Vm.StartEditCommand.CanExecute(null));
        Assert.False(sut.Vm.DeleteCommand.CanExecute(null));
        Assert.False(sut.Vm.RunNowCommand.CanExecute(null));
        Assert.False(sut.Vm.ToggleEnabledCommand.CanExecute(null));

        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        Assert.True(sut.Vm.StartEditCommand.CanExecute(null));
        Assert.True(sut.Vm.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public async Task SelectingADifferentJob_ClosesAnEditorOpenOnThePreviousOne()
    {
        // Otherwise the next save writes this job's fields onto the previously edited job's id.
        var first = NewJob();
        var second = NewJob();
        var sut = CreateSut(first, second);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];
        sut.Vm.StartEditCommand.Execute(null);
        Assert.True(sut.Vm.IsEditorOpen);

        sut.Vm.SelectedJob = sut.Vm.Jobs[1];

        Assert.False(sut.Vm.IsEditorOpen);
        Assert.Null(sut.Vm.EditingJobId);
    }

    [Fact]
    public async Task OpeningTheChatOfAFiring_RoutesToTheAssistantWindow()
    {
        var chatId = Guid.NewGuid();
        var sut = CreateSut();

        sut.Vm.OpenRunChatCommand.Execute(new RoutineRunRow
        {
            SettledAt = DateTime.Now,
            Succeeded = true,
            StateLabel = "Completed",
            ChatId = chatId,
        });

        sut.Windows.Received(1).ShowAssistantChat(chatId);
        await Task.CompletedTask;
    }

    /// <summary>Queues instead of running, which is what the VM's marshal does whenever the context captured at
    /// construction is not the one the caller is on — the ordering the running app actually has.</summary>
    private sealed class DeferringContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();

        public override void Post(SendOrPostCallback d, object? state) => _pending.Enqueue((d, state));

        public void Drain()
        {
            while (_pending.Count > 0)
            {
                var (callback, state) = _pending.Dequeue();
                callback(state);
            }
        }
    }

    /// <summary>Under the default inline marshal every save looks right. Deferred — which is what the app does —
    /// a save that picked its row out of <c>Jobs</c> after awaiting the refresh read the rows from before it, so
    /// a new routine went unselected and the pane kept showing whichever one was selected before.</summary>
    [Fact]
    public async Task SavingANewRoutine_SelectsIt_WhenTheRebuildIsDeferred()
    {
        var existing = NewJob();
        var created = NewJob();
        var stored = new List<ScheduledJob> { existing };

        var jobs = Substitute.For<IScheduledJobService>();
        jobs.GetAllAsync().Returns(_ => stored.ToArray());
        jobs.IsOwnedByThisDeviceAsync(Arg.Any<ScheduledJob>()).Returns(true);
        jobs.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>())
            .Returns(_ => { stored.Add(created); return created; });

        var providers = Substitute.For<IProviderService>();
        providers.GetProvidersAsync().Returns(Array.Empty<AiProvider>());
        var personas = Substitute.For<IPersonaService>();
        personas.GetPersonasAsync().Returns(Array.Empty<Persona>());
        var runs = Substitute.For<IAgentRunService>();
        runs.GetFiringsForTriggerAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ScheduledFiringOutcome>());

        // Installed only for the constructor: the VM captures this instance, and the comparison against
        // SynchronizationContext.Current then fails for every later call, exactly as it does under WPF.
        var context = new DeferringContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        RoutinesViewModel vm;
        try
        {
            vm = new RoutinesViewModel(jobs, Substitute.For<IScheduledJobRunner>(), providers, personas, runs,
                Substitute.For<IDialogService>(), Substitute.For<IWindowManagerService>(), Localizer(),
                Substitute.For<IPluginService>(), Substitute.For<IBrowserProvisioner>(),
                NullLogger<RoutinesViewModel>.Instance);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await vm.RefreshAsync();
        context.Drain();
        vm.SelectedJob = Assert.Single(vm.Jobs);

        vm.StartCreateCommand.Execute(null);
        vm.EditName = "Morning briefing";
        vm.EditQuery = "what happened overnight";
        await vm.SaveCommand.ExecuteAsync(null);
        context.Drain();

        Assert.Equal(created.Id, vm.SelectedJob?.Id);
    }

    /// <summary>The card text is resolved once where the localizer is, so the internal catalog never has to reach
    /// a binding — and the id is what a UI script addresses the card by.</summary>
    [Fact]
    public void TheBlueprintCards_CarryResolvedTextAndAnAddressableId()
    {
        var sut = CreateSut();

        Assert.Equal(RoutineBlueprintCatalog.All.Count, sut.Vm.Blueprints.Count);
        Assert.True(sut.Vm.HasBlueprints);

        var card = Assert.Single(sut.Vm.Blueprints, c => c.Key == RoutineBlueprintCatalog.TopicDigest);
        var blueprint = RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.TopicDigest)!;
        Assert.Equal(blueprint.TitleKey, card.Title);
        Assert.Equal(blueprint.DescriptionKey, card.Description);
        Assert.Equal($"Settings_ScheduledJobs_Kind_{blueprint.Kind}", card.KindLabel);
        Assert.Equal($"Settings_ScheduledJobs_Recurrence_{blueprint.Recurrence}", card.RecurrenceLabel);
        Assert.Equal("09:00", card.TimeLabel);
        Assert.Equal("Routines_Blueprint_topic-digest", card.AutomationId);
    }

    /// <summary>The whole point of the feature: the editor opens carrying the blueprint instead of a blank box.</summary>
    [Fact]
    public async Task PickingABlueprint_PrefillsTheEditorAndOpensIt()
    {
        var sut = CreateSut();
        await sut.Vm.RefreshAsync();
        var blueprint = RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.TopicDigest)!;

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);

        Assert.True(sut.Vm.IsEditorOpen);
        Assert.Null(sut.Vm.EditingJobId);
        Assert.Equal(blueprint.TitleKey, sut.Vm.EditName);
        // Rendered, not the template: the goal box is in front of the user, so a literal {topic} is a defect.
        Assert.Equal(RoutineBlueprintFill.ToCreateArgs(blueprint, TextOf(blueprint)).Query, sut.Vm.EditQuery);
        Assert.DoesNotContain("{", sut.Vm.EditQuery);
        Assert.Equal(blueprint.Kind, sut.Vm.EditKind);
        Assert.Equal(blueprint.Recurrence, sut.Vm.EditRecurrence);
        Assert.Equal("09:00", sut.Vm.EditTimeOfDay);
        Assert.Equal(
            blueprint.GrantedTools.OrderBy(t => t, StringComparer.Ordinal),
            TickedTools(sut.Vm).OrderBy(t => t, StringComparer.Ordinal));
        Assert.Equal(blueprint.QuietOnSuccess, sut.Vm.EditQuietOnSuccess);
        Assert.Null(sut.Vm.EditSpecificDate);
        Assert.Equal(sut.Vm.ProviderChoices.FirstOrDefault(), sut.Vm.EditProvider);
    }

    /// <summary>A card is an offer, not a decision — nothing is written until the user reads it and saves.</summary>
    [Fact]
    public async Task PickingABlueprint_CreatesNothing()
    {
        var sut = CreateSut();
        await sut.Vm.RefreshAsync();

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);

        await sut.Jobs.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(), Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(),
            Arg.Any<string?>());
    }

    /// <summary>The prefill has to survive the editor's own parse, or the card would open a form that refuses to
    /// save — and the save must still be the one existing create path.</summary>
    [Fact]
    public async Task ABlueprintSavesThroughTheExistingCreatePath()
    {
        var sut = CreateSut();
        sut.Jobs.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>())
            .Returns(NewJob());
        await sut.Vm.RefreshAsync();
        var blueprint = RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.TopicDigest)!;

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);
        await sut.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(sut.Vm.StatusMessage);
        await sut.Jobs.Received(1).CreateAsync(blueprint.TitleKey,
            RoutineBlueprintFill.ToCreateArgs(blueprint, TextOf(blueprint)).Query!,
            blueprint.Recurrence, new TimeOnly(9, 0), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
            Arg.Is<IReadOnlyCollection<string>>(g => g.Count == 0), blueprint.Kind, false,
            personaId: Arg.Any<Guid?>(), reasoningEffort: Arg.Any<ReasoningEffort?>(),
            blueprintKey: blueprint.Key);
    }

    /// <summary>Provenance, so "which cards do people actually use" is answerable. A blank start and an edit of
    /// an existing job must both record nothing.</summary>
    [Fact]
    public async Task ABlankStart_RecordsNoBlueprintKey()
    {
        var sut = CreateSut();
        sut.Jobs.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>())
            .Returns(NewJob());
        await sut.Vm.RefreshAsync();

        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "Hand-written";
        sut.Vm.EditQuery = "do the thing";
        await sut.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(sut.Vm.StatusMessage);
        await sut.Jobs.Received(1).CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(),
            Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(),
            Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(),
            Arg.Any<bool>(), Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), blueprintKey: null);
    }

    /// <summary>The key must not survive a card click that the user then abandons for a blank start.</summary>
    [Fact]
    public async Task ABlankStartAfterACardClick_RecordsNoBlueprintKey()
    {
        var sut = CreateSut();
        sut.Jobs.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>())
            .Returns(NewJob());
        await sut.Vm.RefreshAsync();

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "Hand-written";
        sut.Vm.EditQuery = "do the thing";
        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(),
            Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(),
            Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(),
            Arg.Any<bool>(), Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), blueprintKey: null);
    }

    /// <summary>Keys are persisted-adjacent, so a stale one has to be inert rather than open a half-filled form.</summary>
    [Fact]
    public void AnUnknownBlueprintKey_LeavesTheEditorClosed()
    {
        var sut = CreateSut();

        sut.Vm.StartFromBlueprintCommand.Execute("no-such-blueprint");
        sut.Vm.StartFromBlueprintCommand.Execute(null);

        Assert.False(sut.Vm.IsEditorOpen);
        Assert.Equal(string.Empty, sut.Vm.EditQuery);
    }

    [Fact]
    public async Task NewRoutine_OpensTheCatalogRatherThanTheEditor()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();

        sut.Vm.BrowseBlueprintsCommand.Execute(null);

        Assert.True(sut.Vm.ShowsCatalog);
        Assert.False(sut.Vm.IsEditorOpen);
        Assert.False(sut.Vm.ShowsPlaceholder);
    }

    /// <summary>The button used to reopen the catalog over an open editor, silently discarding it.</summary>
    [Fact]
    public async Task NewRoutine_IsRefusedWhileTheEditorIsOpen()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = Assert.Single(sut.Vm.Jobs);
        sut.Vm.StartEditCommand.Execute(null);
        sut.Vm.EditName = "Half typed";

        Assert.False(sut.Vm.BrowseBlueprintsCommand.CanExecute(null));
        // Execute ignores CanExecute, so the body has to refuse too — a UI script can reach it either way.
        sut.Vm.BrowseBlueprintsCommand.Execute(null);

        Assert.True(sut.Vm.IsEditorOpen);
        Assert.Equal("Half typed", sut.Vm.EditName);
        Assert.False(sut.Vm.ShowsCatalog);
    }

    [Fact]
    public async Task NewRoutine_IsOfferedAgainOnceTheEditorCloses()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = Assert.Single(sut.Vm.Jobs);
        sut.Vm.StartEditCommand.Execute(null);

        sut.Vm.CancelEditCommand.Execute(null);

        Assert.True(sut.Vm.BrowseBlueprintsCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartFromBlank_StillOpensAnEmptyEditor()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.BrowseBlueprintsCommand.Execute(null);

        sut.Vm.StartCreateCommand.Execute(null);

        Assert.True(sut.Vm.IsEditorOpen);
        Assert.False(sut.Vm.ShowsCatalog);
        Assert.Null(sut.Vm.EditingJobId);
        Assert.Equal(string.Empty, sut.Vm.EditName);
        Assert.Equal(string.Empty, sut.Vm.EditQuery);
    }

    /// <summary>The substituted localizer echoes the resx key, so the term here matches a key stem rather than
    /// the English title.</summary>
    [Fact]
    public void ASearchTerm_NarrowsTheCatalogAndDropsTheGroupItEmpties()
    {
        var sut = CreateSut();

        sut.Vm.SearchQuery = "market";

        var group = Assert.Single(sut.Vm.BlueprintGroups);
        Assert.Equal(RoutineBlueprintCategories.Ready, group.Key);
        Assert.Equal(RoutineBlueprintCatalog.MarketSnapshot, Assert.Single(group.Cards).Key);
        Assert.True(sut.Vm.HasBlueprintMatches);
    }

    [Fact]
    public void ATermNothingMatches_LeavesNoGroupsToRender()
    {
        var sut = CreateSut();

        sut.Vm.SearchQuery = "no-blueprint-says-this";

        Assert.Empty(sut.Vm.BlueprintGroups);
        Assert.False(sut.Vm.HasBlueprintMatches);
    }

    [Fact]
    public void ASearchSpanningBothGroups_ForcesBothExpanded()
    {
        var sut = CreateSut();
        Assert.False(sut.Vm.BlueprintGroups
            .Single(g => g.Key == RoutineBlueprintCategories.YourData).IsExpanded);

        sut.Vm.SearchQuery = "brief";

        Assert.Equal(
            new[] { RoutineBlueprintCategories.Ready, RoutineBlueprintCategories.YourData },
            sut.Vm.BlueprintGroups.Select(g => g.Key).ToArray());
        Assert.All(sut.Vm.BlueprintGroups, g => Assert.True(g.IsExpanded));
        Assert.Equal(RoutineBlueprintCatalog.NewsBriefing, Assert.Single(sut.Vm.BlueprintGroups[0].Cards).Key);
        Assert.Equal(RoutineBlueprintCatalog.MorningBrief, Assert.Single(sut.Vm.BlueprintGroups[1].Cards).Key);
    }

    /// <summary>The one keystroke that matters is the one INTO a search; after that the user's own collapse
    /// has to survive.</summary>
    [Fact]
    public void AGroupCollapsedMidSearch_StaysCollapsedOnTheNextKeystroke()
    {
        var sut = CreateSut();
        sut.Vm.SearchQuery = "brie";
        var group = sut.Vm.BlueprintGroups.Single(g => g.Key == RoutineBlueprintCategories.YourData);
        Assert.True(group.IsExpanded);
        group.IsExpanded = false;

        sut.Vm.SearchQuery = "brief";

        Assert.False(sut.Vm.BlueprintGroups
            .Single(g => g.Key == RoutineBlueprintCategories.YourData).IsExpanded);
    }

    /// <summary>A query left over from the last visit would open the menu on one card of twenty.</summary>
    [Fact]
    public async Task ReopeningTheCatalog_ClearsTheSearchItWasLeftWith()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.SearchQuery = "market";

        sut.Vm.BrowseBlueprintsCommand.Execute(null);

        Assert.Equal(string.Empty, sut.Vm.SearchQuery);
        Assert.Equal(
            new[] { RoutineBlueprintCategories.Ready, RoutineBlueprintCategories.YourData },
            sut.Vm.BlueprintGroups.Select(g => g.Key).ToArray());
        Assert.False(sut.Vm.BlueprintGroups
            .Single(g => g.Key == RoutineBlueprintCategories.YourData).IsExpanded);
    }

    /// <summary>The pane a save lands on is decided before the reload, not by whether it succeeds.</summary>
    [Fact]
    public async Task ASaveFromACard_ClosesTheCatalogEvenWhenTheReloadFails()
    {
        var sut = CreateSut();
        sut.Jobs.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>())
            .Returns(NewJob());
        await sut.Vm.RefreshAsync();
        Assert.True(sut.Vm.ShowsCatalog);
        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);
        sut.Jobs.GetAllAsync().ThrowsAsync(new InvalidOperationException("the table is gone"));

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        Assert.False(sut.Vm.IsCatalogOpen);
        Assert.False(sut.Vm.ShowsCatalog);
    }

    [Fact]
    public async Task WithNothingScheduled_TheCatalogOpensItself()
    {
        var sut = CreateSut();

        await sut.Vm.RefreshAsync();

        Assert.True(sut.Vm.ShowsCatalog);
        Assert.False(sut.Vm.ShowsPlaceholder);
    }

    [Fact]
    public async Task WithJobsAlreadyThere_TheCatalogStaysShut()
    {
        var sut = CreateSut(NewJob());

        await sut.Vm.RefreshAsync();

        Assert.False(sut.Vm.ShowsCatalog);
        Assert.True(sut.Vm.ShowsPlaceholder);
    }

    /// <summary>The panes are siblings in one Grid with no ZIndex, so a second true state still hit-tests.</summary>
    [Fact]
    public async Task ExactlyOnePaneShows_AcrossTheWholeCycle()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        AssertOnlyPane(sut.Vm, "placeholder");

        sut.Vm.BrowseBlueprintsCommand.Execute(null);
        AssertOnlyPane(sut.Vm, "catalog");

        sut.Vm.SelectedJob = Assert.Single(sut.Vm.Jobs);
        AssertOnlyPane(sut.Vm, "detail");

        // A delete or a deselect must not spring the menu back open over the empty pane.
        sut.Vm.SelectedJob = null;
        AssertOnlyPane(sut.Vm, "placeholder");

        sut.Vm.BrowseBlueprintsCommand.Execute(null);
        sut.Vm.StartCreateCommand.Execute(null);
        AssertOnlyPane(sut.Vm, "editor");

        sut.Vm.CancelEditCommand.Execute(null);
        AssertOnlyPane(sut.Vm, "catalog");
    }

    /// <summary>Home is a reset of the pane, not a navigation: every other state has to land back on the
    /// placeholder, including the catalog, which no selection change would have closed.</summary>
    [Fact]
    public async Task GoHome_ReturnsToThePlaceholder_FromEveryOtherPane()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();

        sut.Vm.SelectedJob = Assert.Single(sut.Vm.Jobs);
        sut.Vm.GoHomeCommand.Execute(null);
        AssertOnlyPane(sut.Vm, "placeholder");

        sut.Vm.BrowseBlueprintsCommand.Execute(null);
        sut.Vm.GoHomeCommand.Execute(null);
        AssertOnlyPane(sut.Vm, "placeholder");

        sut.Vm.BrowseBlueprintsCommand.Execute(null);
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.GoHomeCommand.Execute(null);
        AssertOnlyPane(sut.Vm, "placeholder");
        Assert.Null(sut.Vm.EditingJobId);
    }

    /// <summary>The exclusion is in the expression, not in the selection handler.</summary>
    [Fact]
    public async Task TheCatalogFlag_CannotBeatASelectedJob()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = Assert.Single(sut.Vm.Jobs);

        sut.Vm.IsCatalogOpen = true;

        Assert.True(sut.Vm.ShowsDetail);
        Assert.False(sut.Vm.ShowsCatalog);
        Assert.False(sut.Vm.ShowsPlaceholder);
    }

    /// <summary>The hint has to read the same rule the prompt composer applies.</summary>
    [Theory]
    [InlineData(AiProviderType.Ollama, false, true)]
    [InlineData(AiProviderType.OpenAI, true, false)]
    [InlineData(AiProviderType.PiaCloud, false, false)]
    public async Task TheWebSearchHint_FollowsTheProviderAnAssistantRunWouldUse(
        AiProviderType type, bool enableWebSearch, bool expectHint)
    {
        var sut = CreateSut(NewJob());
        sut.Providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).Returns(new AiProvider
        {
            Name = "default",
            Endpoint = "http://localhost:11434",
            ProviderType = type,
            EnableWebSearch = enableWebSearch,
        });

        await sut.Vm.RefreshAsync();

        Assert.Equal(expectHint, sut.Vm.DefaultProviderCannotSearchWeb);
    }

    [Fact]
    public async Task WithNoProviderToResolve_TheWebSearchHintStaysDown()
    {
        var sut = CreateSut(NewJob());

        await sut.Vm.RefreshAsync();

        Assert.False(sut.Vm.DefaultProviderCannotSearchWeb);
    }

    private static void AssertOnlyPane(RoutinesViewModel vm, string expected)
    {
        var showing = new List<string>();
        if (vm.IsEditorOpen) showing.Add("editor");
        if (vm.ShowsCatalog) showing.Add("catalog");
        if (vm.ShowsDetail) showing.Add("detail");
        if (vm.ShowsPlaceholder) showing.Add("placeholder");

        Assert.Equal(expected, Assert.Single(showing));
    }

    [Fact]
    public async Task AFailedLoad_SaysSo_RatherThanRenderingAnEmptyList()
    {
        // "You have no routines" and "this could not be read" are different claims.
        var sut = CreateSut();
        sut.Jobs.GetAllAsync().Returns<IReadOnlyList<ScheduledJob>>(_ => throw new InvalidOperationException("db"));

        await sut.Vm.RefreshAsync();

        Assert.Empty(sut.Vm.Jobs);
        Assert.False(sut.Vm.HasJobs);
        Assert.Equal("Settings_ScheduledJobs_LoadFailed", sut.Vm.StatusMessage);
    }

    /// <summary>The picker leads with the "inherit" row, the way the provider one does, so a routine with no
    /// pin has something selected to show.</summary>
    [Fact]
    public async Task ThePersonaPicker_LeadsWithTheDefaultRow_ThenTheServicesPersonas()
    {
        var persona = NewPersona("Analyst");
        var sut = CreateSut();
        sut.Personas.GetPersonasAsync().Returns(new[] { persona });

        await sut.Vm.RefreshAsync();

        Assert.Equal(2, sut.Vm.PersonaChoices.Count);
        Assert.Null(sut.Vm.PersonaChoices[0].Id);
        Assert.Equal("Routines_Field_Persona_Default", sut.Vm.PersonaChoices[0].Name);
        Assert.False(sut.Vm.PersonaChoices[0].IsUnavailable);
        Assert.Equal(persona.Id, sut.Vm.PersonaChoices[1].Id);
        Assert.Equal("Analyst", sut.Vm.PersonaChoices[1].Name);
    }

    /// <summary>A label per member plus the inherit row, every label from the localizer: a ComboBox bound
    /// straight to the enum values renders the C# identifier in every locale.</summary>
    [Fact]
    public void TheEffortPicker_OffersAnInheritRowAndEveryMember_EachLocalized()
    {
        var sut = CreateSut();

        Assert.Equal(Enum.GetValues<ReasoningEffort>().Length + 1, sut.Vm.EffortChoices.Count);
        Assert.Null(sut.Vm.EffortChoices[0].Value);
        Assert.Equal("Routines_Field_Effort_Default", sut.Vm.EffortChoices[0].Label);
        foreach (var effort in Enum.GetValues<ReasoningEffort>())
            Assert.Equal($"Routines_Effort_{effort}",
                Assert.Single(sut.Vm.EffortChoices, c => c.Value == effort).Label);
    }

    /// <summary>The inherit row must not read as <c>None</c> in any locale — a blur risks pinning no-reasoning
    /// on unattended runs by accident, so this checks the shipped strings, not the echoing localizer double.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("de")]
    [InlineData("fr")]
    public void TheInheritRowNeverReusesTheNoReasoningWording(string culture)
    {
        var target = culture.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(culture);
        var inherit = ViewStrings.ResourceManager.GetString("Routines_Field_Effort_Default", target);
        var none = ViewStrings.ResourceManager.GetString("Routines_Effort_None", target);

        Assert.False(string.IsNullOrWhiteSpace(inherit));
        Assert.False(string.IsNullOrWhiteSpace(none));
        Assert.False(inherit!.Contains(none!, StringComparison.OrdinalIgnoreCase),
            $"the inherit row must not read as the None row in '{culture}': \"{inherit}\" vs \"{none}\".");
    }

    /// <summary>Both pins have to be filled from the row AND forwarded on save; a field wired into three of the
    /// four editor entry points is the bug the quiet flag already had to have fixed once.</summary>
    [Fact]
    public async Task StartEdit_FillsBothPins_AndSaveForwardsThem()
    {
        var persona = NewPersona("Analyst");
        var job = NewJob();
        job.PersonaId = persona.Id;
        job.ReasoningEffort = ReasoningEffort.High;
        var sut = CreateSut(job);
        sut.Personas.GetPersonasAsync().Returns(new[] { persona });
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        sut.Vm.StartEditCommand.Execute(null);

        Assert.Equal(persona.Id, sut.Vm.EditPersona?.Id);
        Assert.Equal(ReasoningEffort.High, sut.Vm.EditEffort?.Value);

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).UpdateAsync(job.Id, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<RecurrenceType?>(), Arg.Any<TimeOnly?>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateTime?>(),
            Arg.Any<ScheduledJobKind?>(), Arg.Any<bool?>(),
            personaId: persona.Id, reasoningEffort: ReasoningEffort.High, clearReasoningEffort: false);
    }

    /// <summary>Guid.Empty, not null: null means "leave unchanged", so the default row used to save as a no-op.
    /// True of the PROVIDER row too, which is where that was a live bug.</summary>
    [Fact]
    public async Task ChoosingTheDefaultRows_SendsTheClearSentinel_ForPersonaAndProvider()
    {
        var persona = NewPersona("Analyst");
        var provider = new AiProvider { Name = "Cloud", Endpoint = "https://example.test" };
        var job = NewJob();
        job.PersonaId = persona.Id;
        job.ProviderId = provider.Id;
        job.ReasoningEffort = ReasoningEffort.Minimal;
        var sut = CreateSut(job);
        sut.Personas.GetPersonasAsync().Returns(new[] { persona });
        sut.Providers.GetProvidersAsync().Returns(new[] { provider });
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];
        sut.Vm.StartEditCommand.Execute(null);

        sut.Vm.EditPersona = sut.Vm.PersonaChoices[0];
        sut.Vm.EditProvider = sut.Vm.ProviderChoices[0];
        sut.Vm.EditEffort = sut.Vm.EffortChoices[0];
        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).UpdateAsync(job.Id, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<RecurrenceType?>(), Arg.Any<TimeOnly?>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), providerId: Guid.Empty, grantedTools: Arg.Any<IReadOnlyCollection<string>>(),
            specificDate: Arg.Any<DateTime?>(), kind: Arg.Any<ScheduledJobKind?>(),
            quietOnSuccess: Arg.Any<bool?>(), personaId: Guid.Empty,
            reasoningEffort: null, clearReasoningEffort: true);
    }

    /// <summary>A pin whose persona is gone must stay visible and survive an unrelated edit — falling back to
    /// the default row would let the next Save destroy it as a change the user never made.</summary>
    [Fact]
    public async Task AnUnresolvablePersonaPin_ShowsAsUnavailable_AndSurvivesTheNextSave()
    {
        var pinned = Guid.NewGuid();
        var job = NewJob();
        job.PersonaId = pinned;
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        Assert.True(sut.Vm.Jobs[0].HasPersonaPin);
        Assert.Equal("Routines_Field_Persona_Missing", sut.Vm.Jobs[0].PersonaLabel);

        sut.Vm.StartEditCommand.Execute(null);

        var chosen = sut.Vm.EditPersona;
        Assert.NotNull(chosen);
        Assert.Equal(pinned, chosen!.Id);
        Assert.True(chosen.IsUnavailable);
        Assert.Equal("Routines_Field_Persona_Missing", chosen.Name);

        sut.Vm.EditName = "Renamed";
        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).UpdateAsync(job.Id, name: Arg.Is("Renamed"), query: Arg.Any<string>(),
            recurrence: Arg.Any<RecurrenceType?>(), timeOfDay: Arg.Any<TimeOnly?>(),
            dayOfWeek: Arg.Any<DayOfWeek?>(), dayOfMonth: Arg.Any<int?>(), month: Arg.Any<int?>(),
            providerId: Arg.Any<Guid?>(), grantedTools: Arg.Any<IReadOnlyCollection<string>>(),
            specificDate: Arg.Any<DateTime?>(), kind: Arg.Any<ScheduledJobKind?>(),
            quietOnSuccess: Arg.Any<bool?>(), personaId: pinned,
            reasoningEffort: Arg.Any<ReasoningEffort?>(), clearReasoningEffort: Arg.Any<bool>());
    }

    /// <summary>The synthetic row belongs to one job. Left behind, the next routine's editor offers a stranger's
    /// dead pin as a choice.</summary>
    [Fact]
    public async Task TheUnavailableRow_DoesNotSurviveIntoTheNextEditorSession()
    {
        var job = NewJob();
        job.PersonaId = Guid.NewGuid();
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];
        sut.Vm.StartEditCommand.Execute(null);
        Assert.Equal(2, sut.Vm.PersonaChoices.Count);

        sut.Vm.StartCreateCommand.Execute(null);

        Assert.Single(sut.Vm.PersonaChoices);
        Assert.Same(sut.Vm.PersonaChoices[0], sut.Vm.EditPersona);
    }

    /// <summary>The trap in the naive binding: <c>StartCreate</c> leaves the foreign row selected, so a picker
    /// bound to the selection alone would lock a brand-new routine this device is about to own.</summary>
    [Fact]
    public async Task StartCreate_KeepsThePinsEnabled_WhileAForeignOwnedRowIsStillSelected()
    {
        var sut = CreateSut(NewJob());
        sut.Jobs.IsOwnedByThisDeviceAsync(Arg.Any<ScheduledJob>()).Returns(false);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        sut.Vm.StartEditCommand.Execute(null);
        Assert.False(sut.Vm.EditorPinsEnabled);

        sut.Vm.StartCreateCommand.Execute(null);

        Assert.False(sut.Vm.Jobs[0].OwnedByThisDevice);
        Assert.Same(sut.Vm.Jobs[0], sut.Vm.SelectedJob);
        Assert.True(sut.Vm.EditorPinsEnabled);
    }

    /// <summary>The editor is ONE panel for create and edit, so both pins have to reach the create call too.</summary>
    [Fact]
    public async Task CreatingARoutineWithBothPins_ForwardsThemToTheCreateCall()
    {
        var persona = NewPersona("Analyst");
        var sut = CreateSut();
        sut.Personas.GetPersonasAsync().Returns(new[] { persona });
        sut.Jobs.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>())
            .Returns(NewJob());
        await sut.Vm.RefreshAsync();

        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "Monitor";
        sut.Vm.EditQuery = "check the feed";
        sut.Vm.EditPersona = sut.Vm.PersonaChoices[1];
        sut.Vm.EditEffort = Assert.Single(sut.Vm.EffortChoices, c => c.Value == ReasoningEffort.Minimal);

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).CreateAsync("Monitor", "check the feed", Arg.Any<RecurrenceType>(),
            Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
            personaId: persona.Id, reasoningEffort: ReasoningEffort.Minimal);
    }

    /// <summary>The run substitutes a fallback persona with only a log line, so the detail pane is the one place
    /// a person can find out what a routine is actually pinned to.</summary>
    [Fact]
    public async Task TheDetailPane_NamesThePinnedPersonaAndEffort()
    {
        var persona = NewPersona("Analyst");
        var pinned = NewJob();
        pinned.PersonaId = persona.Id;
        pinned.ReasoningEffort = ReasoningEffort.XHigh;
        var unpinned = NewJob();
        var sut = CreateSut(pinned, unpinned);
        sut.Personas.GetPersonasAsync().Returns(new[] { persona });

        await sut.Vm.RefreshAsync();

        var pinnedRow = Assert.Single(sut.Vm.Jobs, j => j.Id == pinned.Id);
        Assert.True(pinnedRow.HasPersonaPin);
        Assert.Equal("Analyst", pinnedRow.PersonaLabel);
        Assert.True(pinnedRow.HasEffortPin);
        Assert.Equal("Routines_Effort_XHigh", pinnedRow.EffortLabel);

        var plainRow = Assert.Single(sut.Vm.Jobs, j => j.Id == unpinned.Id);
        Assert.False(plainRow.HasPersonaPin);
        Assert.Equal(string.Empty, plainRow.PersonaLabel);
        Assert.False(plainRow.HasEffortPin);
        Assert.Equal(string.Empty, plainRow.EffortLabel);
    }

    /// <summary>LocalizationTests' literal-key regexes cannot see a key built by interpolation, and a missing one
    /// renders as "[Key]" at runtime with nothing else catching it.</summary>
    [Fact]
    public void EveryInterpolatedEditorPinKeyResolvesInAllThreeLocales()
    {
        var keys = new List<string>
        {
            "Routines_Field_Persona_Default",
            "Routines_Field_Persona_Missing",
            "Routines_Field_Effort_Default",
        };
        keys.AddRange(Enum.GetValues<ReasoningEffort>().Select(e => $"Routines_Effort_{e}"));

        var missing = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            var available = ResourceKeysFor(culture);
            foreach (var key in keys.Where(k => !available.Contains(k)))
                missing.Add($"{culture.Name}: {key}");
        }

        Assert.True(missing.Count == 0,
            $"every routine pin key must exist in all three locales, but these are missing: {string.Join(", ", missing)}");
    }

    private static HashSet<string> ResourceKeysFor(CultureInfo culture)
    {
        var keys = new HashSet<string>();
        var set = ViewStrings.ResourceManager.GetResourceSet(culture, true, false);
        if (set is null) return keys;

        foreach (DictionaryEntry entry in set) keys.Add((string)entry.Key);
        return keys;
    }

    [Fact]
    public void ACardWithSlots_PrefillsOneFieldPerSlotFromItsDefault()
    {
        var sut = CreateSut();
        var blueprint = RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.TopicDigest)!;

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);

        Assert.True(sut.Vm.HasEditSlots);
        var row = Assert.Single(sut.Vm.EditSlots);
        Assert.Equal("topic", row.Name);
        Assert.Equal(SlotDefault(blueprint, 0), row.Value);
        Assert.Contains(SlotDefault(blueprint, 0), sut.Vm.EditQuery);
    }

    [Fact]
    public void ACardWithoutSlots_ShowsNoSlotBlock()
    {
        var sut = CreateSut();

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.MorningBrief);

        Assert.False(sut.Vm.HasEditSlots);
        Assert.Empty(sut.Vm.EditSlots);
    }

    [Fact]
    public void TypingASlotValue_ReRendersTheGoal()
    {
        var sut = CreateSut();
        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);

        sut.Vm.EditSlots[0].Value = "quantum computing";

        Assert.Contains("quantum computing", sut.Vm.EditQuery);
        Assert.DoesNotContain("artificial intelligence", sut.Vm.EditQuery);
    }

    /// <summary>The whole point of the latch: a slot keystroke must not overwrite prose the user wrote.</summary>
    [Fact]
    public void AHandEditedGoal_SurvivesALaterSlotKeystroke()
    {
        var sut = CreateSut();
        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);

        sut.Vm.EditQuery = "my own wording";
        sut.Vm.EditSlots[0].Value = "quantum computing";

        Assert.Equal("my own wording", sut.Vm.EditQuery);
    }

    [Fact]
    public void SwitchingToAnotherCard_ClearsTheHandEditLatchAndRendersAgain()
    {
        var sut = CreateSut();
        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);
        sut.Vm.EditQuery = "my own wording";

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.CompetitorWatch);

        Assert.NotEqual("my own wording", sut.Vm.EditQuery);
        sut.Vm.EditSlots[0].Value = "Contoso";
        Assert.Contains("Contoso", sut.Vm.EditQuery);
    }

    /// <summary>Blank counts as unsupplied in the fill engine, so an emptied field is the default again rather
    /// than a hole in the prompt.</summary>
    [Fact]
    public void ClearingASlotField_FallsBackToThatSlotsDefault()
    {
        var sut = CreateSut();
        var fallback = SlotDefault(RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.TopicDigest)!, 0);
        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);
        sut.Vm.EditSlots[0].Value = "quantum computing";

        sut.Vm.EditSlots[0].Value = string.Empty;

        Assert.Contains(fallback, sut.Vm.EditQuery);
        Assert.DoesNotContain("{", sut.Vm.EditQuery);
    }

    [Fact]
    public async Task AnEditOfAnExistingJob_ShowsNoSlotBlock()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        sut.Vm.StartEditCommand.Execute(null);

        Assert.False(sut.Vm.HasEditSlots);
        Assert.Equal("summarise today", sut.Vm.EditQuery);
    }

    [Fact]
    public async Task ASlotEdit_IsWhatGetsSaved()
    {
        var sut = CreateSut();
        sut.Jobs.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>())
            .Returns(NewJob());
        await sut.Vm.RefreshAsync();

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);
        sut.Vm.EditSlots[0].Value = "quantum computing";
        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).CreateAsync(Arg.Any<string>(),
            Arg.Is<string>(q => q.Contains("quantum computing")),
            Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
            personaId: Arg.Any<Guid?>(), reasoningEffort: Arg.Any<ReasoningEffort?>(),
            blueprintKey: RoutineBlueprintCatalog.TopicDigest);
    }

    /// <summary>The editor must open showing what the routine STORES, not what the launcher will substitute.
    /// Pre-ticking write_file would pin an implicit default on every routine the user merely touched.</summary>
    [Fact]
    public async Task AnAgentRoutineStoringNoGrants_OpensWithNothingTicked_AndSavesNothing()
    {
        var job = NewJob();
        job.Kind = ScheduledJobKind.AgentTask;
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        sut.Vm.StartEditCommand.Execute(null);

        Assert.Empty(TickedTools(sut.Vm));
        Assert.True(sut.Vm.EditorIsAgentTask);
        Assert.Equal("Routines_Field_Tools_Summary_None_Agent", sut.Vm.EditToolsSummary);

        sut.Vm.EditName = "Renamed";
        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).UpdateAsync(job.Id, name: Arg.Any<string>(), query: Arg.Any<string>(),
            recurrence: Arg.Any<RecurrenceType?>(), timeOfDay: Arg.Any<TimeOnly?>(),
            dayOfWeek: Arg.Any<DayOfWeek?>(), dayOfMonth: Arg.Any<int?>(), month: Arg.Any<int?>(),
            providerId: Arg.Any<Guid?>(), grantedTools: Arg.Is<IReadOnlyCollection<string>>(g => g.Count == 0),
            specificDate: Arg.Any<DateTime?>(), kind: Arg.Any<ScheduledJobKind?>(),
            quietOnSuccess: Arg.Any<bool?>(), personaId: Arg.Any<Guid?>(),
            reasoningEffort: Arg.Any<ReasoningEffort?>(), clearReasoningEffort: Arg.Any<bool>());
    }

    /// <summary>The collapsed header is the only line most users read, so it has to track a tick.</summary>
    [Fact]
    public async Task TickingATool_UpdatesTheSummary_AndReachesTheSavePayload()
    {
        var job = NewJob();
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];
        sut.Vm.StartEditCommand.Execute(null);

        var raised = new List<string?>();
        sut.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Row(sut.Vm, "write_file").IsSelected = true;

        Assert.Contains(nameof(RoutinesViewModel.EditToolsSummary), raised);
        Assert.Equal("write_file", sut.Vm.EditToolsSummary);

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).UpdateAsync(job.Id, name: Arg.Any<string>(), query: Arg.Any<string>(),
            recurrence: Arg.Any<RecurrenceType?>(), timeOfDay: Arg.Any<TimeOnly?>(),
            dayOfWeek: Arg.Any<DayOfWeek?>(), dayOfMonth: Arg.Any<int?>(), month: Arg.Any<int?>(),
            providerId: Arg.Any<Guid?>(),
            grantedTools: Arg.Is<IReadOnlyCollection<string>>(g => g.SequenceEqual(new[] { "write_file" })),
            specificDate: Arg.Any<DateTime?>(), kind: Arg.Any<ScheduledJobKind?>(),
            quietOnSuccess: Arg.Any<bool?>(), personaId: Arg.Any<Guid?>(),
            reasoningEffort: Arg.Any<ReasoningEffort?>(), clearReasoningEffort: Arg.Any<bool>());
    }

    /// <summary>A grant is stored by NAME, so two plugins exposing one name are one grant. Rows that disagreed
    /// would let a user untick the tool and leave it granted anyway.</summary>
    [Fact]
    public async Task TwoPluginsSharingAToolName_ToggleTogether_AndSaveOneGrant()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.StartCreateCommand.Execute(null);

        var rows = sut.Vm.EditToolGroups.SelectMany(g => g.Tools)
                        .Where(t => t.ToolName == "create_todo").ToList();
        Assert.Equal(2, rows.Count);

        rows[0].IsSelected = true;
        Assert.All(rows, r => Assert.True(r.IsSelected));
        Assert.Single(TickedTools(sut.Vm));

        rows[1].IsSelected = false;
        Assert.All(rows, r => Assert.False(r.IsSelected));
        Assert.Empty(TickedTools(sut.Vm));
    }

    /// <summary>The stored column is a JSON array, so re-ordering it on an untouched save manufactures a sync
    /// diff. The direct guard on reading the selection list rather than the display rows.</summary>
    [Fact]
    public async Task SavingAnUntouchedRoutine_PreservesTheStoredGrantOrder()
    {
        var job = NewJob();
        job.GrantedTools = ["write_file", "create_todo"];
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];
        sut.Vm.StartEditCommand.Execute(null);

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).UpdateAsync(job.Id, name: Arg.Any<string>(), query: Arg.Any<string>(),
            recurrence: Arg.Any<RecurrenceType?>(), timeOfDay: Arg.Any<TimeOnly?>(),
            dayOfWeek: Arg.Any<DayOfWeek?>(), dayOfMonth: Arg.Any<int?>(), month: Arg.Any<int?>(),
            providerId: Arg.Any<Guid?>(),
            grantedTools: Arg.Is<IReadOnlyCollection<string>>(
                g => g.SequenceEqual(new[] { "write_file", "create_todo" })),
            specificDate: Arg.Any<DateTime?>(), kind: Arg.Any<ScheduledJobKind?>(),
            quietOnSuccess: Arg.Any<bool?>(), personaId: Arg.Any<Guid?>(),
            reasoningEffort: Arg.Any<ReasoningEffort?>(), clearReasoningEffort: Arg.Any<bool>());
    }

    /// <summary>Same contract as an unresolvable persona pin: a grant nothing here provides is shown and kept,
    /// never silently revoked by the next save. It stays removable, or it could never be revoked at all.</summary>
    [Fact]
    public async Task AGrantWithNoCatalogRow_ShowsAsUnavailable_SurvivesTheSave_AndCanBeRemoved()
    {
        var job = NewJob();
        job.GrantedTools = ["jira_create_issue"];
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];
        sut.Vm.StartEditCommand.Execute(null);

        Assert.True(sut.Vm.HasEditMissingTools);
        var orphan = Row(sut.Vm, "jira_create_issue");
        Assert.True(orphan.IsUnavailable);
        Assert.True(orphan.IsSelected);
        Assert.Equal("Routines_Field_Tools_Missing_Hint", orphan.UnavailableReason);

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).UpdateAsync(job.Id, name: Arg.Any<string>(), query: Arg.Any<string>(),
            recurrence: Arg.Any<RecurrenceType?>(), timeOfDay: Arg.Any<TimeOnly?>(),
            dayOfWeek: Arg.Any<DayOfWeek?>(), dayOfMonth: Arg.Any<int?>(), month: Arg.Any<int?>(),
            providerId: Arg.Any<Guid?>(),
            grantedTools: Arg.Is<IReadOnlyCollection<string>>(g => g.Contains("jira_create_issue")),
            specificDate: Arg.Any<DateTime?>(), kind: Arg.Any<ScheduledJobKind?>(),
            quietOnSuccess: Arg.Any<bool?>(), personaId: Arg.Any<Guid?>(),
            reasoningEffort: Arg.Any<ReasoningEffort?>(), clearReasoningEffort: Arg.Any<bool>());

        sut.Vm.StartEditCommand.Execute(null);
        Row(sut.Vm, "jira_create_issue").IsSelected = false;
        Assert.Empty(TickedTools(sut.Vm));
    }

    /// <summary>The synthetic group belongs to one routine; left behind it offers a stranger's dead grant.</summary>
    [Fact]
    public async Task TheUnavailableToolGroup_DoesNotSurviveIntoTheNextEditorSession()
    {
        var job = NewJob();
        job.GrantedTools = ["jira_create_issue"];
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];
        sut.Vm.StartEditCommand.Execute(null);
        Assert.True(sut.Vm.HasEditMissingTools);

        sut.Vm.StartCreateCommand.Execute(null);

        Assert.False(sut.Vm.HasEditMissingTools);
        Assert.DoesNotContain(sut.Vm.EditToolGroups.SelectMany(g => g.Tools), t => t.IsUnavailable);
    }

    /// <summary>The kind decides what an empty list means, so the type dropdown must rewrite both lines.</summary>
    [Fact]
    public async Task SwitchingTheKind_RewritesTheEmptySelectionLines()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.StartCreateCommand.Execute(null);

        var raised = new List<string?>();
        sut.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sut.Vm.EditKind = ScheduledJobKind.Research;

        Assert.Contains(nameof(RoutinesViewModel.EditToolsSummary), raised);
        Assert.Contains(nameof(RoutinesViewModel.EditorIsAgentTask), raised);
        Assert.False(sut.Vm.EditorIsAgentTask);
        Assert.Equal("Routines_Field_Tools_Summary_None_Research", sut.Vm.EditToolsSummary);
    }

    /// <summary>The catalogue arrives in arbitrary handler order, so grouping and sorting are the picker's job.</summary>
    [Fact]
    public async Task TheToolGroupsSortByPluginThenByTool()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.StartCreateCommand.Execute(null);

        Assert.Equal(["files", "some-mcp-server", "todo"],
            sut.Vm.EditToolGroups.Where(g => !g.IsUnavailableGroup).Select(g => g.Header));
        Assert.Equal(["delete_file", "write_file"],
            sut.Vm.EditToolGroups.First(g => g.Header == "files").Tools.Select(t => t.ToolName));
    }

    /// <summary>A destructive tool carries the ROUTINE caution: unattended, an unnamed one is refused outright,
    /// so the Tool access page's "you will be asked each time" would be a false promise here.</summary>
    [Fact]
    public async Task ATickedDestructiveTool_CarriesTheRoutineCaution()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.StartCreateCommand.Execute(null);

        var row = Row(sut.Vm, "delete_file");
        Assert.False(row.HasCaution);

        row.IsSelected = true;

        Assert.True(row.HasCaution);
        Assert.Equal("ToolCatalog_Caution_Routine_Destructive", row.CautionText);
    }

    /// <summary>Hiding the line when nothing is stored let the pane imply an agent routine could not write.</summary>
    [Fact]
    public async Task TheDetailPane_NamesTheLauncherDefault_ForAnAgentRoutineWithNoGrants()
    {
        var agent = NewJob();
        agent.Kind = ScheduledJobKind.AgentTask;
        var sut = CreateSut(agent);
        await sut.Vm.RefreshAsync();

        Assert.Equal("Routines_Detail_Tools_AgentDefault", sut.Vm.Jobs[0].ToolsSummary);
    }

    /// <summary>A research routine with no grants genuinely is read-only, and must not borrow the agent line.</summary>
    [Fact]
    public async Task TheDetailPane_SaysReadOnly_ForAResearchRoutineWithNoGrants()
    {
        var research = NewJob();
        research.Kind = ScheduledJobKind.Research;
        var sut = CreateSut(research);
        await sut.Vm.RefreshAsync();

        Assert.Equal("Routines_Detail_Tools_None", sut.Vm.Jobs[0].ToolsSummary);
    }
}
