using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The unattended half of <c>start_assignment</c>: a granted background run mints its own receipt in-process
/// and starts the run with an EMPTY item list, so the model's proposal can never carry a record it chose. The
/// routing assertion is the load-bearing one — a turn with a human on it must still reach the dialog.
/// </summary>
public class HeadlessAssignmentStartTests : IDisposable
{
    private const string Prompt = "SENTINEL-PROMPT-BODY summarise the week";

    private static readonly AssignmentSkill Deep = new("deep-research", "Deep research", "Assistant", []);
    private static readonly AssignmentSkill Brief = new("brief", "Brief", "Assistant", []);

    private readonly string _auditDirectory = Path.Combine(
        Path.GetTempPath(), $"pia-headless-consent-{Guid.NewGuid():N}");

    private readonly IAssignmentSurfaceCache _surface = Substitute.For<IAssignmentSurfaceCache>();
    private readonly IAssignmentApiClient _api = Substitute.For<IAssignmentApiClient>();
    private readonly IAssignmentPendingStore _pending = Substitute.For<IAssignmentPendingStore>();
    private readonly IAssignmentConsentPrompt _prompt = Substitute.For<IAssignmentConsentPrompt>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();
    private readonly IAssignmentRunOrchestrator _orchestrator = Substitute.For<IAssignmentRunOrchestrator>();
    private readonly JsonlAssignmentConsentStore _consent;

    public HeadlessAssignmentStartTests()
    {
        Directory.CreateDirectory(_auditDirectory);
        _consent = new JsonlAssignmentConsentStore(AuditPath, NullLogger<JsonlAssignmentConsentStore>.Instance);

        _surface.Surface.Returns(new AssignmentSurface(true, [Deep, Brief]));
        _surface.FindSkill(Arg.Any<string>()).Returns(ci => ci.Arg<string>() switch
        {
            "deep-research" => Deep,
            "brief" => Brief,
            _ => null,
        });
        _localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        _localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => ci.Arg<string>());
        _orchestrator.StartAsync(
                Arg.Any<AssignmentRequest>(), Arg.Any<AssignmentConsentReceipt>(), Arg.Any<CancellationToken>())
            .Returns(new AssignmentStartOutcome(AssignmentStartStatus.Started, Guid.NewGuid()));
    }

    public void Dispose()
    {
        TaskAmbient.Current = null;
        if (Directory.Exists(_auditDirectory)) Directory.Delete(_auditDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string AuditPath => Path.Combine(_auditDirectory, "assignments.jsonl");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HeadlessAssignmentLauncher Launcher() => new(
        _surface, _consent, _orchestrator, NullLogger<HeadlessAssignmentLauncher>.Instance);

    private AssignmentToolHandler Handler() => new(
        _surface, _api, _pending, _prompt, Launcher(), _localization,
        NullLogger<AssignmentToolHandler>.Instance);

    /// <summary>Sets the ambient a headless turn sets, which is what tells the tool nobody can be asked.</summary>
    private static string Unattended(Guid? jobId = null)
    {
        var granter = AssignmentGranter.ForUnattendedRun(
            jobId is null ? AgentRunTrigger.User : AgentRunTrigger.Schedule, jobId, Guid.NewGuid());
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), null, UnattendedGranter: granter);
        return granter;
    }

    private Task<(object? Result, AssignmentToolCall? PendingAction)> StartAsync(string? skill) =>
        Handler().HandleToolCallAsync(
            new FunctionCallContent("call-1", "start_assignment",
                new Dictionary<string, object?> { ["skill"] = skill, ["prompt"] = Prompt }),
            Ct);

    private AssignmentRequest? Sent =>
        _orchestrator.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAssignmentRunOrchestrator.StartAsync))
            .Select(c => (AssignmentRequest)c.GetArguments()[0]!)
            .FirstOrDefault();

    [Fact]
    public async Task AGrantedHeadlessStart_MintsAReceiptAndStartsTheRun()
    {
        var granter = Unattended(Guid.NewGuid());

        var (_, pending) = await StartAsync("deep-research");
        var text = (string)(await pending!.Execute())!;

        Assert.StartsWith("routine:", granter, StringComparison.Ordinal);
        Assert.NotNull(Sent);
        Assert.Equal("deep-research", Sent!.SkillName);
        Assert.Equal(Prompt, Sent.Prompt);
        Assert.True(File.Exists(AuditPath));
        Assert.Contains("was started", text, StringComparison.OrdinalIgnoreCase);
        await _prompt.DidNotReceiveWithAnyArgs().PromptAsync(default, default!, Ct);
    }

    [Fact]
    public async Task AHeadlessStart_SendsAnEmptyItemList()
    {
        Unattended(Guid.NewGuid());

        var (_, pending) = await StartAsync("deep-research");
        await pending!.Execute();

        Assert.NotNull(Sent);
        Assert.Empty(Sent!.Items);
    }

    /// <summary>The routing must not fail open. A turn a human is watching still gets the dialog, and the
    /// unattended launcher never runs — otherwise a card confirm would send with nobody asked.</summary>
    [Fact]
    public async Task AnAttendedTurn_StillGoesToTheDialog()
    {
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), null);
        _prompt.PromptAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AssignmentStartStatus.Started);

        var (_, pending) = await StartAsync("deep-research");
        await pending!.Execute();

        await _prompt.Received(1).PromptAsync("deep-research", Prompt, Arg.Any<CancellationToken>());
        Assert.Null(Sent);
        Assert.False(File.Exists(AuditPath));
    }

    [Fact]
    public async Task AHeadlessStart_WithAHiddenSurface_RefusesAndMintsNothing()
    {
        Unattended(Guid.NewGuid());
        _surface.Surface.Returns(AssignmentSurface.Hidden);

        var (result, pending) = await StartAsync("deep-research");

        Assert.Null(pending);
        Assert.Contains("not available", (string)result!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Sent);
        Assert.False(File.Exists(AuditPath));
    }

    [Fact]
    public async Task AHeadlessStart_WithAnUnknownSkill_RefusesAndMintsNothing()
    {
        Unattended(Guid.NewGuid());

        var (result, pending) = await StartAsync("no-such-skill");

        Assert.Null(pending);
        Assert.Contains("no user on this run", (string)result!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Sent);
        Assert.False(File.Exists(AuditPath));
    }

    /// <summary>The deliberate divergence from the attended path, where an omitted skill lets the dialog pick
    /// the first one: with nobody to pick, a choice between two is refused rather than guessed.</summary>
    [Fact]
    public async Task AHeadlessStart_WithNoSkillNamedAndSeveralOffered_RefusesBeforeMintingACard()
    {
        Unattended(Guid.NewGuid());

        var (result, pending) = await StartAsync(null);

        Assert.Null(pending);
        Assert.Contains("no user on this run", (string)result!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Sent);
    }

    /// <summary>One skill is not a choice, so the run proceeds on it.</summary>
    [Fact]
    public async Task AHeadlessStart_WithNoSkillNamedAndOnlyOneOffered_UsesIt()
    {
        Unattended(Guid.NewGuid());
        _surface.Surface.Returns(new AssignmentSurface(true, [Deep]));

        var (_, pending) = await StartAsync(null);
        await pending!.Execute();

        Assert.NotNull(Sent);
        Assert.Equal("deep-research", Sent!.SkillName);
    }

    [Fact]
    public async Task TheAuditEntryCarriesGrantedByAndPromptCharsButNotThePrompt()
    {
        var jobId = Guid.NewGuid();
        var granter = Unattended(jobId);

        var (_, pending) = await StartAsync("deep-research");
        await pending!.Execute();

        var line = Assert.Single(await File.ReadAllLinesAsync(AuditPath, Ct));
        Assert.DoesNotContain("SENTINEL-PROMPT-BODY", line, StringComparison.Ordinal);

        var entry = JsonDocument.Parse(line).RootElement;
        Assert.Equal($"routine:{jobId}", entry.GetProperty("grantedBy").GetString());
        Assert.Equal(granter, entry.GetProperty("grantedBy").GetString());
        Assert.Equal(Prompt.Length, entry.GetProperty("promptChars").GetInt32());
        Assert.Equal(0, entry.GetProperty("itemCount").GetInt32());
        Assert.Empty(entry.GetProperty("items").EnumerateArray());
    }

    /// <summary>A consent record that never reached disk, or a server that threw: the model must not be told a
    /// run exists. The card's own executor is bypassed on the headless path, so the closure carries this.</summary>
    [Fact]
    public async Task AHeadlessStart_ThatThrows_IsReportedRatherThanPropagated()
    {
        Unattended(Guid.NewGuid());
        _orchestrator.StartAsync(
                Arg.Any<AssignmentRequest>(), Arg.Any<AssignmentConsentReceipt>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("no server"));

        var (_, pending) = await StartAsync("deep-research");
        var text = (string)(await pending!.Execute())!;

        Assert.Contains("could not be started", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("was started", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A run with no scheduled job behind it names itself rather than inventing a routine id.</summary>
    [Fact]
    public void AnUnscheduledRunNamesItselfAsTheGranter()
    {
        var runId = Guid.NewGuid();

        Assert.Equal($"background:{runId}",
            AssignmentGranter.ForUnattendedRun(AgentRunTrigger.User, null, runId));
        Assert.Equal($"background:{runId}",
            AssignmentGranter.ForUnattendedRun(AgentRunTrigger.Event, Guid.NewGuid(), runId));
        Assert.Equal($"background:{runId}",
            AssignmentGranter.ForUnattendedRun(AgentRunTrigger.Schedule, null, runId));
    }
}
