using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

public class ChatSessionManagerProposePlanTests
{
    private readonly IAssistantChatService _chatService = Substitute.For<IAssistantChatService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IPersonaService _personas = Substitute.For<IPersonaService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IAssistantPromptComposer _composer = Substitute.For<IAssistantPromptComposer>();
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunService _runService = Substitute.For<IAgentRunService>();
    private readonly IProviderCapabilityService _capability = Substitute.For<IProviderCapabilityService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly AppSettings _appSettings = new() { UnifiedAssistantFlowEnabled = true };

    private readonly Persona _persona = new() { Name = "Tester", SystemPrompt = "be helpful", ToolScope = PersonaToolScope.Full };

    private IList<AITool>? _sentTools;
    private string? _sentSystemPrompt;
    private object? _toolResult;
    private bool _stopRequested;
    private bool _leadIn = true;
    private Func<IDictionary<string, object?>>? _proposeArgs;

    private static readonly AITool SuggestTool = AssistantPromptComposer.BuildSuggestAgentModeTool();
    private static readonly AITool OtherTool = AIFunctionFactory.Create(() => "x", "read_file");

    public ChatSessionManagerProposePlanTests()
    {
        _settings.GetSettingsAsync().Returns(_ => _appSettings);
        _tokenMap.Detokenize(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        _chatService.UpsertMessagesAsync(Arg.Any<SyncAssistantChat>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _capability.GetPlanningCapabilityAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>())
            .Returns(PlanningCapability.Weak);
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(_persona);
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>())
            .Returns(new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", SupportsToolCalling = true });
        _composer.PrepareTurn(default!, default!, default!, default)
            .ReturnsForAnyArgs(new AssistantTurnSetup("system", new List<AITool> { OtherTool, SuggestTool }, true, false));
        _runService.GetByChatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentRun>());
        _runService.CreateAsync(Arg.Any<AgentRunCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AgentRun
            {
                Id = Guid.NewGuid(),
                ChatId = ci.Arg<AgentRunCreateRequest>().ChatId,
                RunShape = RunShape.Planned,
                State = AgentRunState.Planning,
                PolicyJson = ci.Arg<AgentRunCreateRequest>().PolicyJson,
            });
        _ai.GetChatCompletionWithToolsAsync(default!, default!, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(ci => Stream(ci.ArgAt<IList<ChatMessage>>(0), ci.ArgAt<IList<AITool>?>(2), ci.ArgAt<ToolCallHandler?>(3)));
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    private async IAsyncEnumerable<ChatStreamItem> Stream(
        IList<ChatMessage> messages, IList<AITool>? tools, ToolCallHandler? handler,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _sentTools = tools;
        _sentSystemPrompt = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text;
        if (_leadIn)
            yield return new TextDelta("I'd split this into steps:");
        if (_proposeArgs is not null && handler is not null)
        {
            var stop = new ToolLoopStopSignal();
            _toolResult = await handler(new FunctionCallContent("c1", ProposePlanTool.Name, _proposeArgs()),
                new ToolDispatchContext(1, stop));
            _stopRequested = stop.IsStopRequested;
        }
        await Task.Yield();
    }

    private ChatSessionManager CreateSut()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        var orchestrator = new AgentRunOrchestrator(
            _runService, Substitute.For<IAgentPlanner>(), new Pia.Tests.Services.FakeVerifier(),
            NullLogger<AgentRunOrchestrator>.Instance);
        return new ChatSessionManager(
            NullLogger<ChatSessionManager>.Instance, NullLoggerFactory.Instance,
            _chatService, _settings, _personas, _providers, _composer,
            Substitute.For<IChatTitleService>(), Substitute.For<IActionCardBuilder>(), Substitute.For<IPluginService>(),
            _ai, Substitute.For<IToolPermissionService>(), _loc,
            () => _tokenMap, Substitute.For<IBackgroundChatNotifier>(),
            Substitute.For<Pia.Services.Flow.IFlowService>(), Substitute.For<IFilesToolHandler>(), orchestrator, _runService,
            _capability, Substitute.For<IGoalTriageService>(), Substitute.For<IHeadlessRunLauncher>(),
            Substitute.For<IWindowManagerService>(), new ExecutingRunStore());
    }

    private static Dictionary<string, object?> Args(params string[] titles) => new()
    {
        ["goal"] = "Compare three operators and summarise.",
        ["steps"] = JsonSerializer.SerializeToElement(titles.Select(t => new { title = t, intent = "do " + t }).ToArray()),
    };

    private async Task<ChatSession> SendAsync(ChatSessionManager sut, bool planned = false)
    {
        var session = sut.GetOrCreateActiveForNewChat();
        Assert.True(await sut.StartTurnAsync(session, "plan a comparison", null, planned: planned));
        await Eventually.TrueAsync(() => !session.IsStreaming, "turn finished", TestContext.Current.CancellationToken);
        return session;
    }

    [Fact]
    public async Task SettingOn_OffersProposePlan_InPlaceOfSuggestAgentMode_EvenOnAWeakProvider()
    {
        await SendAsync(CreateSut());

        Assert.Contains(_sentTools!, t => t.Name == ProposePlanTool.Name);
        Assert.DoesNotContain(_sentTools!, t => t.Name == "suggest_agent_mode");
        Assert.Contains("propose_plan", _sentSystemPrompt);
    }

    [Fact]
    public async Task SettingOff_ToolListIsUntouched()
    {
        _appSettings.UnifiedAssistantFlowEnabled = false;
        await SendAsync(CreateSut());

        Assert.Equal(new[] { "read_file", "suggest_agent_mode" }, _sentTools!.Select(t => t.Name));
        Assert.Equal("system", _sentSystemPrompt);
    }

    [Fact]
    public async Task NoToolTurn_IsNotOfferedProposePlan()
    {
        _composer.PrepareTurn(default!, default!, default!, default)
            .ReturnsForAnyArgs(new AssistantTurnSetup("system", null, false, false));
        await SendAsync(CreateSut());

        Assert.Null(_sentTools);
    }

    [Fact]
    public async Task SettingOn_IgnoresThePlannedFlag_AndCreatesNoLeverRun()
    {
        await SendAsync(CreateSut(), planned: true);

        await _runService.DidNotReceiveWithAnyArgs().CreateAsync(default!, TestContext.Current.CancellationToken);
        Assert.NotNull(_sentTools);
    }

    [Fact]
    public async Task ValidPlan_CreatesTheParkedJob_AnchoredOnTheProposingMessage_AndEndsTheTurn()
    {
        _proposeArgs = () => Args("Collect", "Compare", "Write");
        var session = await SendAsync(CreateSut());

        var proposing = session.Messages.Last();
        Assert.Equal(ChatRole.Assistant, proposing.Role);
        Assert.True(_stopRequested);
        Assert.Equal(ChatSessionManager.ProposePlanCreatedResult, _toolResult);

        await _runService.Received(1).CreateAsync(Arg.Is<AgentRunCreateRequest>(r =>
            r.Shape == RunShape.Planned && r.Trigger == AgentRunTrigger.User
            && r.Goal == "Compare three operators and summarise." && r.PersonaId == _persona.Id
            && HeadlessRunLauncher.TryRestoreChatJob(r.PolicyJson)), Arg.Any<CancellationToken>());
        await _runService.Received(1).ReplaceStepsAsync(Arg.Any<Guid>(),
            Arg.Is<IReadOnlyList<AgentStep>>(s => s.Select(x => x.Title).SequenceEqual(new[] { "Collect", "Compare", "Write" })),
            Arg.Any<CancellationToken>());
        await _runService.Received(1).SetRunMessageRangeAsync(Arg.Any<Guid>(), proposing.Id, proposing.Id, Arg.Any<CancellationToken>());
        await _runService.Received(1).PauseAsync(Arg.Any<Guid>(), AgentRunOrchestrator.PlanApprovalReason,
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>());
        await _chatService.ReceivedWithAnyArgs().UpsertMessagesAsync(default!, default, TestContext.Current.CancellationToken);
        Assert.NotNull(session.ActiveRunId);
        Assert.True(session.PlanApprovalParkActive);
    }

    [Fact]
    public async Task ProposalWithoutLeadIn_GetsTheLocalizedLeadIn()
    {
        _leadIn = false;
        _proposeArgs = () => Args("Collect", "Compare");
        var session = await SendAsync(CreateSut());

        Assert.Equal("ProposePlan_LeadIn", session.Messages.Last().Content);
    }

    [Theory]
    [InlineData("Only")]
    [InlineData("Same", "same")]
    public async Task InvalidPlan_ReturnsTheReason_AndKeepsTheLoopGoing(params string[] titles)
    {
        _proposeArgs = () => Args(titles);
        await SendAsync(CreateSut());

        Assert.False(_stopRequested);
        Assert.IsType<string>(_toolResult);
        Assert.NotEqual(ChatSessionManager.ProposePlanCreatedResult, _toolResult);
        await _runService.DidNotReceiveWithAnyArgs().CreateAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnfinishedJobInTheChat_RefusesASecondPlan()
    {
        _runService.GetByChatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentRun>
        {
            new()
            {
                Id = Guid.NewGuid(), RunShape = RunShape.Planned, State = AgentRunState.WaitingForInput,
                PolicyJson = HeadlessRunLauncher.ChatJobEmptyEnvelopeJson,
            },
        });
        _proposeArgs = () => Args("Collect", "Compare");
        await SendAsync(CreateSut());

        Assert.Equal(ChatSessionManager.ProposePlanJobExistsResult, _toolResult);
        Assert.False(_stopRequested);
        await _runService.DidNotReceiveWithAnyArgs().CreateAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FinishedJobInTheChat_DoesNotBlockANewPlan()
    {
        _runService.GetByChatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentRun>
        {
            new()
            {
                Id = Guid.NewGuid(), RunShape = RunShape.Planned, State = AgentRunState.Completed,
                PolicyJson = HeadlessRunLauncher.ChatJobEmptyEnvelopeJson,
            },
        });
        _proposeArgs = () => Args("Collect", "Compare");
        await SendAsync(CreateSut());

        Assert.True(_stopRequested);
    }

    [Fact]
    public async Task FailureAfterCreate_SettlesTheRun_AndAnswersInText()
    {
        _runService.ReplaceStepsAsync(default, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(Task.FromException(new InvalidOperationException()));
        _proposeArgs = () => Args("Collect", "Compare");
        await SendAsync(CreateSut());

        Assert.Equal(ChatSessionManager.ProposePlanSaveFailedResult, _toolResult);
        Assert.False(_stopRequested);
        await _runService.ReceivedWithAnyArgs(1).FailAsync(default, default, ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SettingOff_AnInventedProposePlanIsAnUnknownTool()
    {
        _appSettings.UnifiedAssistantFlowEnabled = false;
        _proposeArgs = () => Args("Collect", "Compare");
        await SendAsync(CreateSut());

        Assert.Equal("Unknown tool.", _toolResult);
        await _runService.DidNotReceiveWithAnyArgs().CreateAsync(default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task PlanApprovalPark_AcceptsASend_OnlyWithTheSettingOn(bool settingOn, bool accepted)
    {
        _appSettings.UnifiedAssistantFlowEnabled = settingOn;
        var runId = Guid.NewGuid();
        _runService.GetAsync(runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = runId, RunShape = RunShape.Planned, State = AgentRunState.WaitingForInput,
            ExtraJson = JsonSerializer.Serialize(new { paused = true, reason = AgentRunOrchestrator.PlanApprovalReason }),
        });
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        session.SetActiveRun(runId);

        Assert.Equal(accepted, await sut.StartTurnAsync(session, "drop step 3", null));
    }
}
