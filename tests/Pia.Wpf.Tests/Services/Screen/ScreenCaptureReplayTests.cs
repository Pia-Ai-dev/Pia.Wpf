using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Services.Screen;
using Xunit;

namespace Pia.Tests.Services.Screen;

/// <summary>
/// The headless replay of a call a human approved on a park runs outside any tool loop, so there is nothing to
/// hand a picture to. The handler refuses at prepare time and the resumed step re-issues the call inside the
/// loop, where its approval has become a named grant.
/// </summary>
public class ScreenCaptureReplayTests
{
    [Fact]
    public async Task Replay_WithoutALoop_RefusesBeforeCapturing()
    {
        var harness = new Harness();
        harness.AllowlistOutlook();

        var previous = ToolLoopImageChannel.Current;
        ToolLoopImageChannel.Current = null;
        var previousTask = TaskAmbient.Current;
        TaskAmbient.Current = new TaskContext(harness.RunId, null, UnattendedGranter: harness.Granter);
        object? result;
        try
        {
            result = await harness.Runner.ReplayToolCallAsync(
                Harness.Call(), ["screen_capture"], round: 1);
        }
        finally
        {
            ToolLoopImageChannel.Current = previous;
            TaskAmbient.Current = previousTask;
        }

        Assert.Equal(ScreenCaptureToolHandler.NoDeliveryChannel, result);
        Assert.Equal(0, harness.Fixture.Capture.CaptureCalls);
        Assert.Empty(harness.Fixture.Audited);
    }

    [Fact]
    public void RefusalText_TellsTheModelToCallAgain()
    {
        Assert.Contains("Call screen_capture again", ScreenCaptureToolHandler.NoDeliveryChannel);
    }

    [Fact]
    public async Task ResumedStep_ReissuedCall_IsGrantedByName_AndDelivered()
    {
        var harness = new Harness();
        harness.AllowlistOutlook();

        var result = await harness.RunInLoopAsync(grantedWrites: ["screen_capture"]);

        Assert.StartsWith("Captured window outlook", (string)result!);
        Assert.Equal(1, harness.Fixture.Capture.CaptureCalls);
        Assert.Single(harness.Channel.Drain());
    }

    private sealed class Harness
    {
        public ScreenCaptureToolHandlerTests.Fixture Fixture { get; } = new();

        public ToolLoopImageChannel Channel { get; } = new(AiProviderType.PiaCloud);

        public Guid RunId { get; } = Guid.NewGuid();

        public string Granter => $"background:{RunId}";

        private readonly IPluginService _plugins = Substitute.For<IPluginService>();

        private IAiClientService _ai = Substitute.For<IAiClientService>();

        private object? _answer;

        public Harness()
        {
            Fixture.Capture.Targets.Add(ScreenCaptureToolHandlerTests.Fixture.Window("outlook", "Inbox - Outlook"));

            var adapter = BuiltInPluginHandler.FromScreenCaptureHandler(
                Fixture.Handler, BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.ScreenPluginId]);

            _plugins.IsMcpTool(Arg.Any<string>()).Returns(false);
            _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    var routed = await adapter.HandleToolCallAsync(
                        ci.Arg<FunctionCallContent>(), ci.Arg<CancellationToken>());
                    return ((object? Result, PluginToolCall? PendingAction)?)routed;
                });
        }

        public BackgroundAssistantTurnRunner Runner => Build();

        public void AllowlistOutlook() =>
            Fixture.Entries.Add(new ScreenCaptureAllowlistEntry(
                Guid.NewGuid(), "outlook", string.Empty, DateTimeOffset.UtcNow));

        public static FunctionCallContent Call() =>
            new("call-1", "screen_capture", new Dictionary<string, object?>
            {
                ["target"] = "window",
                ["match"] = "outlook",
            });

        public async Task<object?> RunInLoopAsync(HashSet<string> grantedWrites)
        {
            _ai = Substitute.For<IAiClientService>();
            _ai.GetChatCompletionWithToolsAsync(
                    Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                    Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                    cancellationToken: Arg.Any<CancellationToken>())
                .Returns(ci => Drive(ci.ArgAt<ToolCallHandler?>(3)));

            var runner = Build();

            var previousTask = TaskAmbient.Current;
            var previousChannel = ToolLoopImageChannel.Current;
            TaskAmbient.Current = new TaskContext(RunId, null, UnattendedGranter: Granter);
            ToolLoopImageChannel.Current = Channel;
            try
            {
                await runner.RunExchangeAsync(
                    [new ChatMessage(ChatRole.User, "look")],
                    new AiProvider { Name = "P", Endpoint = "https://example", ProviderType = AiProviderType.PiaCloud },
                    new AssistantTurnSetup("system", [], SupportsTools: true, WebSearchActive: false),
                    grantedWrites,
                    TestContext.Current.CancellationToken);
            }
            finally
            {
                TaskAmbient.Current = previousTask;
                ToolLoopImageChannel.Current = previousChannel;
            }

            return _answer;
        }

        private BackgroundAssistantTurnRunner Build()
        {
            var settings = Substitute.For<ISettingsService>();
            settings.GetSettingsAsync().Returns(new AppSettings());

            return new BackgroundAssistantTurnRunner(
                _ai, _plugins, Substitute.For<IToolPermissionService>(),
                Substitute.For<IAssistantPromptComposer>(), Substitute.For<IPersonaService>(),
                Substitute.For<IAssistantChatService>(), Substitute.For<IChatTitleService>(), settings,
                () => Substitute.For<ITokenMapService>(), Substitute.For<IAgentRunService>(),
                new ExecutingRunStore(), NullLogger<BackgroundAssistantTurnRunner>.Instance, null);
        }

        private async IAsyncEnumerable<ChatStreamItem> Drive(ToolCallHandler? handler)
        {
            if (handler is not null)
                _answer = await handler(Call(), new ToolDispatchContext(1));

            yield return new TextDelta("done");
            yield return new Finished(null, "test-model");
        }
    }
}
