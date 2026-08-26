using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The read-only assignment tools. The load-bearing assertions are the ones about what the result does NOT say:
/// an unanswered server must not be reported as "you have no runs" or "that run does not exist", and the list
/// arm must not carry an artifact even when the server sends one. Assertions are made against the SERIALIZED
/// result, because that is what the provider receives.
/// </summary>
public class AssignmentToolHandlerTests
{
    private const string ArtifactBody = "SECRET-ARTIFACT-BODY";
    private const string EventMessage = "SECRET-EVENT-MESSAGE";

    private static readonly DateTime Created = new(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc);

    private static readonly AssignmentSkill DeepResearch = new("deep-research", "Deep research", "Assistant", []);

    private readonly IAssignmentSurfaceCache _surface = Substitute.For<IAssignmentSurfaceCache>();
    private readonly IAssignmentApiClient _api = Substitute.For<IAssignmentApiClient>();
    private readonly IAssignmentPendingStore _pending = Substitute.For<IAssignmentPendingStore>();
    private readonly IAssignmentConsentPrompt _prompt = Substitute.For<IAssignmentConsentPrompt>();
    private readonly IHeadlessAssignmentLauncher _unattended = Substitute.For<IHeadlessAssignmentLauncher>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();

    public AssignmentToolHandlerTests()
    {
        _surface.Surface.Returns(new AssignmentSurface(true, [DeepResearch]));
        _surface.FindSkill("deep-research").Returns(DeepResearch);
        _localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        _localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => ci.Arg<string>());
        StubJournal();
    }

    private AssignmentToolHandler Handler() => new(
        _surface, _api, _pending, _prompt, _unattended, _localization,
        NullLogger<AssignmentToolHandler>.Instance);

    private async Task<object?> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var (result, pending) = await Handler().HandleToolCallAsync(
            new FunctionCallContent("call-1", tool, args), TestContext.Current.CancellationToken);

        Assert.Null(pending);
        return result;
    }

    private Task<object?> CallAsync(string tool) => CallAsync(tool, new Dictionary<string, object?>());

    private Task<object?> GetAsync(Guid id) =>
        CallAsync("get_assignment", new Dictionary<string, object?> { ["assignment_id"] = id.ToString() });

    private void StubJournal(params PendingAssignment[] entries)
    {
        IReadOnlyList<PendingAssignment> journal = entries;
        _pending.GetJournalAsync().Returns(journal);
    }

    private void StubList(params AssignmentDto[] rows)
    {
        IReadOnlyList<AssignmentDto> listed = rows;
        _api.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(listed);
    }

    private void StubListUnanswered() =>
        _api.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AssignmentDto>?)null);

    /// <summary>A string result travels as-is; a record travels as its JSON. Assert on whichever the tool chose.</summary>
    private static string Text(object? result) => result as string ?? JsonSerializer.Serialize(result);

    private static AssignmentDto Dto(
        Guid id,
        string status = "Completed",
        string? artifactText = null,
        DateTime? droppedAt = null,
        IReadOnlyList<AssignmentEventDto>? events = null) =>
        new(id, "deep-research", "Assistant", status, 3, 1200, 0,
            Created, Created, Created, Created, null, null, null, artifactText, droppedAt, events);

    private static AssignmentEventDto Event(string kind = "StepCompleted") =>
        new(Guid.NewGuid(), kind, EventMessage, "{}", Created);

    private static PendingAssignment Journal(Guid id, Guid chatId, bool collected) =>
        new(id, chatId, "deep-research", "what the user asked", Created,
            collected ? Created.AddMinutes(5) : null);

    // ---- availability -------------------------------------------------------------------------

    [Fact]
    public void ToolsAreEmptyWhenTheSurfaceIsHidden()
    {
        _surface.Surface.Returns(AssignmentSurface.Hidden);

        Assert.Empty(Handler().GetTools());
        Assert.False(Handler().IsAvailable);
    }

    [Fact]
    public void ToolsAreTheThreeAssignmentToolsWhenTheSurfaceIsAvailable()
    {
        var names = Handler().GetTools()
            .OfType<AIFunction>()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "get_assignment", "query_assignments", "start_assignment" }, names);
        Assert.True(Handler().IsAvailable);
    }

    /// <summary>Availability is a cached read: PluginService asks for it inside its own constructor, so an
    /// awaited probe here would block launch.</summary>
    [Fact]
    public void GetToolsTouchesNoHttp()
    {
        _ = Handler().GetTools();

        Assert.Empty(_api.ReceivedCalls());
    }

    // ---- query_assignments --------------------------------------------------------------------

    [Fact]
    public async Task QueryAssignmentsTransportFailureDoesNotClaimTheUserHasNoRuns()
    {
        StubListUnanswered();

        var text = Text(await CallAsync("query_assignments"));

        Assert.DoesNotContain("no background assignments", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be reached", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryAssignmentsAThrowingReadIsAlsoATransportFailure()
    {
        _api.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("down"));

        var text = Text(await CallAsync("query_assignments"));

        Assert.DoesNotContain("no background assignments", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be reached", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryAssignmentsEmptyListSaysThereAreNone()
    {
        StubList();

        var text = Text(await CallAsync("query_assignments"));

        Assert.Contains("no background assignments", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not be reached", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The rows are stubbed WITH an artifact and events — the shape a changed server would send —
    /// because rows without them would let a straight pass-through pass this test.</summary>
    [Fact]
    public async Task QueryAssignmentsListsRunsWithoutArtifactText()
    {
        var id = Guid.NewGuid();
        StubList(Dto(id, artifactText: ArtifactBody, events: [Event()]));

        var text = Text(await CallAsync("query_assignments"));

        Assert.Contains(id.ToString(), text);
        Assert.Contains("deep-research", text);
        Assert.DoesNotContain(ArtifactBody, text);
        Assert.DoesNotContain(EventMessage, text);
        await _api.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The wire tags a timestamp UTC only when the JSON carried a Z, so an untagged one must be
    /// rendered as it stands — converting it would move every stamp by the reader's own offset.</summary>
    [Fact]
    public async Task QueryAssignmentsRendersAnUntaggedTimestampAsTheUtcItAlreadyIs()
    {
        var untagged = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Unspecified);
        StubList(new AssignmentDto(
            Guid.NewGuid(), "deep-research", "Assistant", "Completed", 1, 10, 0,
            untagged, untagged, untagged, untagged, null, null, null));

        var text = Text(await CallAsync("query_assignments"));

        Assert.Contains("2026-08-26 09:00 UTC", text);
    }

    [Fact]
    public async Task QueryAssignmentsClampsTheModelsLimit()
    {
        StubList();

        await CallAsync("query_assignments", new Dictionary<string, object?> { ["limit"] = 5000 });

        await _api.Received(1).ListAsync(0, 50, Arg.Any<CancellationToken>());
    }

    // ---- get_assignment -----------------------------------------------------------------------

    [Fact]
    public async Task GetAssignmentServerCannotAnswerDoesNotClaimTheRunIsGone()
    {
        var id = Guid.NewGuid();
        _api.GetAsync(id, Arg.Any<CancellationToken>()).Returns((AssignmentDto?)null);

        var text = Text(await GetAsync(id));

        Assert.Contains("could not answer", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("does not exist", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no such", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAssignmentDroppedAndCollectedPointsAtTheChat()
    {
        var id = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        _api.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(Dto(id, droppedAt: Created.AddMinutes(10), events: [Event()]));
        StubJournal(Journal(id, chatId, collected: true));

        var text = Text(await GetAsync(id));

        Assert.Contains("chat history", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(chatId.ToString(), text);
    }

    /// <summary>The journal, not the outstanding list: a collected run is absent from GetAllAsync, so a handler
    /// keyed off it would report "no local record" for every run that already finished.</summary>
    [Fact]
    public async Task GetAssignmentReadsTheJournalNotTheOutstandingList()
    {
        var id = Guid.NewGuid();
        _api.GetAsync(id, Arg.Any<CancellationToken>()).Returns(Dto(id, droppedAt: Created.AddMinutes(10)));
        StubJournal(Journal(id, Guid.NewGuid(), collected: true));

        await GetAsync(id);

        await _pending.Received(1).GetJournalAsync();
        await _pending.DidNotReceive().GetAllAsync();
    }

    /// <summary>The local journal is not synced, so a run collected on another device is absent here while its
    /// answer sits in the user's chat history — the note must send the model there, not declare it lost.</summary>
    [Fact]
    public async Task GetAssignmentDroppedAndNotInJournalPointsAtChatHistory()
    {
        var id = Guid.NewGuid();
        _api.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(Dto(id, droppedAt: Created.AddMinutes(10), artifactText: ArtifactBody));

        var text = Text(await GetAsync(id));

        Assert.Contains("chat history", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("search_chats", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be retrieved", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ArtifactBody, text);
    }

    [Fact]
    public async Task GetAssignmentWithNoLocalChatAndNothingDroppedReturnsTheAnswer()
    {
        var id = Guid.NewGuid();
        _api.GetAsync(id, Arg.Any<CancellationToken>()).Returns(Dto(id, artifactText: ArtifactBody));

        Assert.Contains(ArtifactBody, Text(await GetAsync(id)));
    }

    [Fact]
    public async Task GetAssignmentRendersTheEventLogForARunningJob()
    {
        var id = Guid.NewGuid();
        _api.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(Dto(id, status: "Running", events: [Event("Created"), Event("StepCompleted")]));

        var text = Text(await GetAsync(id));

        Assert.Contains("Running", text);
        Assert.Contains("StepCompleted", text);
        Assert.Contains(EventMessage, text);
    }

    [Fact]
    public async Task GetAssignmentRefusesAnIdItCannotParse()
    {
        var text = Text(await CallAsync(
            "get_assignment", new Dictionary<string, object?> { ["assignment_id"] = "the-second-one" }));

        Assert.Contains("assignment_id", text);
        await _api.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnknownToolNameIsReportedRatherThanThrown()
    {
        Assert.Contains("Unknown tool", Text(await CallAsync("cancel_assignment")));
    }

    /// <summary>A failing pending action must surface as a tool error, not a broken card.</summary>
    [Fact]
    public async Task ExecutePendingActionReportsAFailureInsteadOfThrowing()
    {
        var pending = new AssignmentToolCall(
            "start_assignment", "description", null, () => throw new InvalidOperationException("boom"));

        Assert.Contains("boom", Text(await Handler().ExecutePendingActionAsync(pending)));
    }

    // ---- start_assignment ---------------------------------------------------------------------

    [Fact]
    public async Task StartAssignment_ReturnsAPendingAction_AndDoesNotPromptUntilExecuted()
    {
        var (result, pending) = await StartAsync("deep-research", "Summarise last week");

        Assert.Null(result);
        Assert.NotNull(pending);
        Assert.Equal("start_assignment", pending!.ToolName);
        await _prompt.DidNotReceiveWithAnyArgs().PromptAsync(default, default!, TestContext.Current.CancellationToken);

        await pending.Execute();

        await _prompt.ReceivedWithAnyArgs(1).PromptAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAssignment_Executed_PassesTheModelsSkillAndPromptThrough()
    {
        PromptReturns(AssignmentStartStatus.Started);

        var (_, pending) = await StartAsync("deep-research", "  Summarise last week  ");
        var text = Text(await pending!.Execute());

        await _prompt.Received(1).PromptAsync(
            "deep-research", "Summarise last week", Arg.Any<CancellationToken>());
        Assert.Contains("new chat", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An omitted skill is the model declining to choose, not an error: the dialog picks one.</summary>
    [Fact]
    public async Task StartAssignment_WithoutASkill_StillProposesTheRun()
    {
        var (result, pending) = await Handler().HandleToolCallAsync(
            new FunctionCallContent("call-1", "start_assignment",
                new Dictionary<string, object?> { ["prompt"] = "Summarise last week" }),
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.NotNull(pending);

        await pending!.Execute();

        await _prompt.Received(1).PromptAsync(null, "Summarise last week", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAssignment_Dismissed_ReportsNothingWasSent()
    {
        PromptReturns(null);

        var (_, pending) = await StartAsync("deep-research", "Summarise last week");
        var text = Text(await pending!.Execute());

        Assert.Contains("closed the confirmation", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("was started", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AssignmentStartStatus.ConsentMissing)]
    [InlineData(AssignmentStartStatus.TooLarge)]
    [InlineData(AssignmentStartStatus.Refused)]
    public async Task StartAssignment_AnUnsentOutcomeIsNeverReportedAsRunning(AssignmentStartStatus status)
    {
        PromptReturns(status);

        var (_, pending) = await StartAsync("deep-research", "Summarise last week");
        var text = Text(await pending!.Execute());

        Assert.DoesNotContain("The assignment was started", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAssignment_SurfaceHidden_ReturnsAnErrorAndNoCard()
    {
        _surface.Surface.Returns(AssignmentSurface.Hidden);

        var (result, pending) = await StartAsync("deep-research", "Summarise last week");

        Assert.Null(pending);
        Assert.Contains("not available", Text(result), StringComparison.OrdinalIgnoreCase);
        await _prompt.DidNotReceiveWithAnyArgs().PromptAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAssignment_PromptOverTheCap_ReturnsAnErrorAndNoCard()
    {
        var (result, pending) = await StartAsync(
            "deep-research", new string('x', AssignmentInput.MaxPromptChars + 1));

        Assert.Null(pending);
        Assert.Contains(AssignmentInput.MaxPromptChars.ToString(), Text(result), StringComparison.Ordinal);
        await _prompt.DidNotReceiveWithAnyArgs().PromptAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAssignment_WithoutAPrompt_ReturnsAnErrorAndNoCard()
    {
        var (result, pending) = await StartAsync("deep-research", "   ");

        Assert.Null(pending);
        Assert.Contains("prompt", Text(result), StringComparison.OrdinalIgnoreCase);
        await _prompt.DidNotReceiveWithAnyArgs().PromptAsync(default, default!, TestContext.Current.CancellationToken);
    }

    /// <summary>Every plugin adapter and both confirm sites call <c>Execute</c> directly, so the handler's own
    /// try/catch wrapper never runs: a throw from inside the closure would reach the turn raw.</summary>
    [Fact]
    public async Task StartAssignment_APromptThatThrows_IsReportedRatherThanPropagated()
    {
        _prompt.PromptAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("no dialog host"));

        var (_, pending) = await StartAsync("deep-research", "Summarise last week");

        Assert.Contains("could not be shown", Text(await pending!.Execute()), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The model may propose the work, never the records, and the absence of the parameter is the
    /// whole mechanism — so pin it on the schema rather than on a comment. Asserted on the property KEYS: the
    /// serialized schema carries the descriptions, whose prose names records on purpose.</summary>
    [Fact]
    public void StartAssignment_ExposesNoItemOrRecordParameter()
    {
        var tool = Handler().GetTools().OfType<AIFunction>().Single(t => t.Name == "start_assignment");

        var keys = tool.JsonSchema.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).ToList();

        Assert.Equal(["skill", "prompt"], keys);
        Assert.DoesNotContain(keys, k =>
            k.Contains("item", StringComparison.OrdinalIgnoreCase)
            || k.Contains("record", StringComparison.OrdinalIgnoreCase)
            || k.Contains("entity", StringComparison.OrdinalIgnoreCase)
            || k.Contains("id", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The dialog silently preselects its first skill, so a card naming one the catalog does not have
    /// would have the user approve a skill that never runs.</summary>
    [Fact]
    public async Task StartAssignment_WithAnUnknownSkill_DropsItRatherThanCarryingItToTheDialog()
    {
        PromptReturns(AssignmentStartStatus.Started);

        var (result, pending) = await StartAsync("no-such-skill", "Summarise last week");

        Assert.Null(result);
        Assert.NotNull(pending);
        Assert.DoesNotContain("no-such-skill", pending!.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("no-such-skill", pending.Details ?? string.Empty, StringComparison.Ordinal);

        await pending.Execute();

        await _prompt.Received(1).PromptAsync(null, "Summarise last week", Arg.Any<CancellationToken>());
    }

    /// <summary>A compile-time constant shipped on every surface, a granted background run included — where
    /// the call starts a real run with no dialog at all, so it must not promise one.</summary>
    [Fact]
    public void StartAssignment_DescriptionDoesNotPromiseAConfirmationOnEverySurface()
    {
        var description = Handler().GetTools().OfType<AIFunction>()
            .Single(t => t.Name == "start_assignment").Description;

        Assert.DoesNotContain("does NOT start it", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("background run", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no confirmation", description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Granting it hands a later background run standing authority to send with nobody watching, so
    /// the grant tiers must carry the caution the routine-authoring tools already do.</summary>
    [Fact]
    public void StartAssignment_CountsAsAuthorityAuthoring()
    {
        Assert.True(ToolPermissionService.IsAuthorityAuthoring("start_assignment"));
        Assert.Equal(
            ToolGrantCaution.AuthorityAuthoring,
            ToolCatalogRow.CautionFor("start_assignment", serverDeclaredDestructive: false));
    }

    private void PromptReturns(AssignmentStartStatus? status) =>
        _prompt.PromptAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(status);

    private Task<(object? Result, AssignmentToolCall? PendingAction)> StartAsync(string? skill, string prompt) =>
        Handler().HandleToolCallAsync(
            new FunctionCallContent("call-1", "start_assignment",
                new Dictionary<string, object?> { ["skill"] = skill, ["prompt"] = prompt }),
            TestContext.Current.CancellationToken);
}
