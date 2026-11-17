using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>A headless step whose provider call is interrupted parks the run resumably; real run/chat stores.</summary>
public sealed class InterruptionParkTests : IDisposable
{
    private readonly string _dir;
    private readonly string _runsBase;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly ExecutingRunStore _executing = new();

    public InterruptionParkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaInterruptionPark_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        _runsBase = Path.Combine(_dir, "runs");
        Directory.CreateDirectory(_runsBase);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _chats.Dispose();
        _ctx.Dispose();
        TempPath.Remove(_dir);
    }

    [Theory]
    [InlineData("credit", "interrupted-credit")]
    [InlineData("llm-timeout", "interrupted-timeout")]
    [InlineData("refused", "interrupted-offline")]
    [InlineData("stream-reset", "interrupted-offline")]
    [InlineData("cloud-stream", "interrupted-provider")]
    [InlineData("http-503", "interrupted-provider")]
    public async Task AStepWhoseProviderCallIsInterrupted_ParksWithTheToken(string shape, string token)
    {
        var probe = new FaultProbe { Fault = StepInterruptionClassifierTests.Build(shape) };
        var launcher = Build(probe);

        var run = await LaunchAndSettleAsync(launcher);

        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal(token, PauseMember(run, "reason"));
        Assert.Null(run.CompletedAt);
        Assert.Null(PauseMember(run, "error"));
        var chat = await _chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(chat!.Messages, m => m.Role == "assistant");
        await launcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AnOrdinaryFault_StillFailsTheRun()
    {
        var probe = new FaultProbe { Fault = new InvalidOperationException("boom") };
        var launcher = Build(probe);

        var run = await LaunchAndSettleAsync(launcher);

        Assert.Equal(AgentRunState.Failed, run.State);
        await launcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ADeterministic400_StillFails_AndNeverParks()
    {
        var probe = new FaultProbe { Fault = new HttpRequestException("m", null, HttpStatusCode.BadRequest) };
        var launcher = Build(probe);

        var run = await LaunchAndSettleAsync(launcher);

        Assert.Equal(AgentRunState.Failed, run.State);
        await launcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task APendingQuestion_OutranksTheInterruption()
    {
        var probe = new FaultProbe
        {
            Ask = true,
            Fault = new HttpRequestException(HttpRequestError.ConnectionError),
        };
        var launcher = Build(probe);

        var run = await LaunchAndSettleAsync(launcher);

        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal("needs-input", PauseMember(run, "reason"));
        await launcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AnInterruptedRun_ParksAgainWhileTheOutageLasts_ThenFinishesWhereItStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new FaultProbe { Fault = StepInterruptionClassifierTests.Build("credit") };
        var launcher = Build(probe);

        var run = await LaunchAndSettleAsync(launcher);
        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        var stepId = (await _runs.NextPendingStepAsync(run.Id, ct))!.Id;
        var turnsBefore = probe.Turns;

        Assert.True(await launcher.ResumeAsync(run.Id, "continue", ct));
        await AwaitTurnThenRestAsync(run.Id, probe, turnsBefore);
        run = await GetRunAsync(run.Id);
        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal("interrupted-credit", PauseMember(run, "reason"));
        Assert.Equal(stepId, (await _runs.NextPendingStepAsync(run.Id, ct))!.Id);

        probe.Fault = null;
        Assert.True(await launcher.ResumeAsync(run.Id, "continue", ct));
        await AwaitSettledAsync(run.Id);
        run = await GetRunAsync(run.Id);
        Assert.Equal(AgentRunState.Completed, run.State);
        Assert.NotEmpty(run.Plan);
        Assert.All(run.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
        await launcher.StopAsync(CancellationToken.None);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<AgentRun> LaunchAndSettleAsync(HeadlessRunLauncher launcher)
    {
        var ct = TestContext.Current.CancellationToken;
        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);
        return await GetRunAsync(handle.RunId);
    }

    private async Task<AgentRun> GetRunAsync(Guid runId)
        => (await _runs.GetAsync(runId, TestContext.Current.CancellationToken))!;

    private async Task AwaitSettledAsync(Guid runId)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var s = (await _runs.GetAsync(runId, ct))!.State;
            if (s is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled)
                return;
            await Task.Delay(20, ct);
        }

        Assert.Fail($"Run {runId} never settled.");
    }

    /// <summary>A resume returns once the dispatch is attached, so wait for its turn and then for the run to leave Running.</summary>
    private async Task AwaitTurnThenRestAsync(Guid runId, FaultProbe probe, int turnsBefore)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var state = (await _runs.GetAsync(runId, ct))!.State;
            if (probe.Turns > turnsBefore && state != AgentRunState.Running && _executing.GetChatId(runId) is null)
                return;
            await Task.Delay(20, ct);
        }

        Assert.Fail($"Run {runId} never came to rest after the resume.");
    }

    private static string? PauseMember(AgentRun run, string member)
    {
        if (string.IsNullOrEmpty(run.ExtraJson)) return null;
        using var doc = JsonDocument.Parse(run.ExtraJson);
        return doc.RootElement.TryGetProperty(member, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    // ---------------------------------------------------------------- doubles

    private sealed class FaultProbe
    {
        /// <summary>Thrown out of the stream enumeration; null ⇒ the turn completes.</summary>
        public Exception? Fault { get; set; }

        public bool Ask { get; set; }

        public int Turns { get; set; }
    }

    private sealed class OneStepPlanner : IAgentPlanner
    {
        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(new PlanResult(
                [new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "S0", Intent = "do it", Status = AgentStepStatus.Pending }],
                FallBackToSingleTurn: false));

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    private HeadlessRunLauncher Build(FaultProbe probe)
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys" };

        var ai = Substitute.For<IAiClientService>();
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                probe.Turns++;
                return Drive(ci.ArgAt<ToolCallHandler?>(3), probe.Ask, probe.Fault);
            });

        var plugins = Substitute.For<IPluginService>();
        plugins.IsMcpTool(Arg.Any<string>()).Returns(false);

        var composer = Substitute.For<IAssistantPromptComposer>();
        composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>(), unattended: Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false));
        var personas = Substitute.For<IPersonaService>();
        personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(provider);
        var titles = Substitute.For<IChatTitleService>();
        titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAiClientService>(ai);
        services.AddSingleton<IPluginService>(plugins);
        services.AddSingleton(Substitute.For<IToolPermissionService>());
        services.AddSingleton<IAssistantPromptComposer>(composer);
        services.AddSingleton<IPersonaService>(personas);
        services.AddSingleton<IProviderService>(providers);
        services.AddSingleton<IChatTitleService>(titles);
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton<IAgentRunService>(_runs);
        services.AddSingleton<IAssistantChatService>(_chats);
        services.AddSingleton<IAgentPlanner>(new OneStepPlanner());
        services.AddSingleton<IAgentVerifier>(new FakeVerifier());
        services.AddSingleton<Func<ITokenMapService>>(_ => () => Substitute.For<ITokenMapService>());
        services.AddSingleton<IExecutingRunStore>(_executing);
        services.AddSingleton<IAgentTimelineService>(new RecordingTimelineService());
        services.AddTransient<BackgroundAssistantTurnRunner>();
        services.AddTransient<HeadlessTurnExecutor>();
        services.AddTransient<AgentRunOrchestrator>();
        var sp = services.BuildServiceProvider();

        return new HeadlessRunLauncher(
            sp.GetRequiredService<IServiceScopeFactory>(), _chats, _runs, settings, providers, personas,
            _executing, NullLogger<HeadlessRunLauncher>.Instance, runsBaseDirOverride: _runsBase);
    }

    private static async IAsyncEnumerable<ChatStreamItem> Drive(ToolCallHandler? handler, bool ask, Exception? fault)
    {
        await Task.Yield();
        if (ask && handler is not null)
        {
            await handler(
                new FunctionCallContent("call-1", AgentStepTools.RequestUserInputToolName,
                    new Dictionary<string, object?> { ["question"] = "which cluster?" }),
                new ToolDispatchContext(1, new ToolLoopStopSignal()));
        }

        if (fault is not null)
            throw fault;

        yield return new TextDelta("reply");
        yield return new Finished(null, "test-model");
    }
}
