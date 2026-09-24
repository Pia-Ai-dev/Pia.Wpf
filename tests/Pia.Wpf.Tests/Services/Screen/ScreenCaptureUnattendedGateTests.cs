using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Services.Plugins;
using Pia.Services.Screen;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services.Screen;

/// <summary>
/// The assembled unattended behaviour: the real handler behind the real built-in adapter, routed through the
/// real headless gate. The pure resolver matrix lives in ToolAutonomyTests; this class proves what a run
/// nobody is watching can actually do with a screen capture.
/// </summary>
public class ScreenCaptureUnattendedGateTests
{
    [Fact]
    public async Task NoStandingGrant_NoPark_IsDenied_NothingCaptured()
    {
        var harness = new Harness();
        harness.AllowlistOutlook();

        var result = await harness.RunAsync(approvals: null);

        Assert.StartsWith("Denied: 'screen_capture'", (string)result!);
        Assert.Equal(0, harness.Fixture.Capture.CaptureCalls);
        Assert.Empty(harness.Fixture.Audited);
    }

    [Fact]
    public async Task NoStandingGrant_CanPark_Parks_NothingCaptured()
    {
        var harness = new Harness();
        harness.AllowlistOutlook();
        var approvals = new ToolApprovalStore(canPark: true);

        var result = await harness.RunAsync(approvals);

        Assert.StartsWith("Paused:", (string)result!);
        Assert.Equal("screen_capture", approvals.PendingToolName);
        Assert.Equal(0, harness.Fixture.Capture.CaptureCalls);
    }

    [Fact]
    public async Task StandingGrant_AllowlistedWindow_Captures_AndDelivers()
    {
        var harness = new Harness { StandingGrant = true };
        harness.AllowlistOutlook();

        var result = await harness.RunAsync(approvals: null);

        Assert.StartsWith("Captured window outlook", (string)result!);
        Assert.Equal(1, harness.Fixture.Capture.CaptureCalls);

        var image = Assert.Single(harness.Channel.Drain());
        Assert.Equal("call-1", image.CallId);

        var evt = Assert.Single(harness.Fixture.Audited);
        Assert.Equal(ScreenCaptureSurfaces.Unattended, evt.Surface);
        Assert.Equal(harness.Granter, evt.Granter);
        Assert.Equal(harness.RunId, evt.TaskId);
        harness.Fixture.Indicator.Received(1).NotifyCapture(evt);
    }

    [Fact]
    public async Task StandingGrant_OffListWindow_IsRefusedBeforeTheGate()
    {
        var harness = new Harness { StandingGrant = true };

        var result = await harness.RunAsync(approvals: null);

        Assert.Equal(string.Format(ScreenCaptureToolHandler.UnattendedNotListed, "outlook"), result);
        Assert.Equal(0, harness.Fixture.Capture.CaptureCalls);
        // No pending action was minted, so the gate was never even consulted.
        harness.Permissions.DidNotReceive().IsGranted(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Fact]
    public async Task StandingGrant_AmbiguousAllowlistEntry_IsRefused()
    {
        var harness = new Harness { StandingGrant = true };
        harness.AllowlistOutlook();
        harness.Fixture.Capture.Targets.Add(
            ScreenCaptureToolHandlerTests.Fixture.Window("outlook", "Inbox - Outlook"));
        harness.Fixture.Capture.Targets.Add(
            ScreenCaptureToolHandlerTests.Fixture.Window("outlook", "Calendar - Outlook"));

        var result = await harness.RunAsync(approvals: null, match: "Inbox");

        Assert.Equal(string.Format(ScreenCaptureToolHandler.UnattendedAmbiguous, "outlook", 2), result);
        Assert.Equal(0, harness.Fixture.Capture.CaptureCalls);
    }

    [Fact]
    public async Task StandingGrant_MonitorTarget_IsRefusedBeforeTheAllowlist()
    {
        var harness = new Harness { StandingGrant = true };
        harness.Fixture.Capture.Targets.Add(
            ScreenCaptureToolHandlerTests.Fixture.Monitor(@"\\.\DISPLAY1", primary: true));

        var result = await harness.RunAsync(approvals: null, target: "monitor", match: null);

        Assert.Equal(ScreenCaptureToolHandler.UnattendedMonitor, result);
        await harness.Fixture.Allowlist.DidNotReceive().ListAsync();
        Assert.Equal(0, harness.Fixture.Capture.CaptureCalls);
    }

    /// <summary>The session tier is a human's answer inside THIS process, and the store keys it by tool alone —
    /// so a grant minted an hour ago is indistinguishable from one minted inside the run. It buys nothing here.</summary>
    [Fact]
    public async Task SessionGrantMintedInTheSameRun_DoesNotSatisfy_Parks()
    {
        var harness = new Harness();
        harness.AllowlistOutlook();
        var session = Substitute.For<ISessionToolGrantStore>();
        session.IsGranted(BuiltInPluginDefaults.ScreenPluginId, "screen_capture").Returns(true);
        var approvals = new ToolApprovalStore(canPark: true, sessionGrants: session);

        var result = await harness.RunAsync(approvals);

        Assert.StartsWith("Paused:", (string)result!);
        Assert.Equal(0, harness.Fixture.Capture.CaptureCalls);
    }

    /// <summary>A control, not a proof: HasSessionGrant is gated on CanPark, so this row is denied with or
    /// without the Screen pin. The parking row above is the one that proves it.</summary>
    [Fact]
    public async Task SessionGrantMintedInTheSameRun_DoesNotSatisfy_NoPark_IsDenied()
    {
        var harness = new Harness();
        harness.AllowlistOutlook();
        var session = Substitute.For<ISessionToolGrantStore>();
        session.IsGranted(BuiltInPluginDefaults.ScreenPluginId, "screen_capture").Returns(true);

        var result = await harness.RunAsync(new ToolApprovalStore(canPark: false, sessionGrants: session));

        Assert.StartsWith("Denied:", (string)result!);
        Assert.Equal(0, harness.Fixture.Capture.CaptureCalls);
    }

    [Fact]
    public async Task NamedGrant_AllowlistedWindow_Captures()
    {
        var harness = new Harness();
        harness.AllowlistOutlook();

        var result = await harness.RunAsync(approvals: null, grantedWrites: ["screen_capture"]);

        Assert.StartsWith("Captured window outlook", (string)result!);
        Assert.Equal(1, harness.Fixture.Capture.CaptureCalls);
    }

    [Fact]
    public async Task NamedDenial_OutranksTheStandingGrant()
    {
        var harness = new Harness { StandingGrant = true };
        harness.AllowlistOutlook();

        var result = await harness.RunAsync(approvals: null, deniedWrites: ["screen_capture"]);

        Assert.StartsWith("Denied: the person declined", (string)result!);
        Assert.Equal(0, harness.Fixture.Capture.CaptureCalls);
    }

    /// <summary>The allowlist binds the unattended surface only; an interactive turn asks the user instead.</summary>
    [Fact]
    public async Task Interactive_OffListWindow_IsNotBoundByTheAllowlist()
    {
        var harness = new Harness();

        var result = await harness.RunAsync(approvals: null, granter: null);

        // The handler minted a card for the gate to decide on rather than refusing the window itself.
        Assert.NotNull(harness.Routed);
        Assert.NotEqual(string.Format(ScreenCaptureToolHandler.UnattendedNotListed, "outlook"), result);
        Assert.Equal(0, harness.Fixture.Capture.CaptureCalls);
    }

    [Fact]
    public async Task PolicyNamingScreen_NeverAutoRuns()
    {
        var harness = new Harness();
        harness.AllowlistOutlook();

        var result = await harness.RunAsync(
            new ToolApprovalStore(canPark: false), policy: new RunAutonomyPolicy([ToolClass.Screen]));

        Assert.StartsWith("Denied:", (string)result!);
        Assert.Equal(0, harness.Fixture.Capture.CaptureCalls);
    }

    /// <summary>Both screen safeguards key off the ambient granter, not the gate surface; a trigger kind that
    /// produced none would disarm them silently and audit as interactive.</summary>
    [Theory]
    [MemberData(nameof(EveryTrigger))]
    public void EveryUnattendedTrigger_NamesAGranter(AgentRunTrigger trigger)
    {
        Assert.False(string.IsNullOrEmpty(
            AssignmentGranter.ForUnattendedRun(trigger, Guid.NewGuid(), Guid.NewGuid())));
        Assert.False(string.IsNullOrEmpty(
            AssignmentGranter.ForUnattendedRun(trigger, null, Guid.NewGuid())));
    }

    public static TheoryData<AgentRunTrigger> EveryTrigger()
    {
        var data = new TheoryData<AgentRunTrigger>();
        foreach (var trigger in Enum.GetValues<AgentRunTrigger>()) data.Add(trigger);
        return data;
    }

    private sealed class Harness
    {
        public ScreenCaptureToolHandlerTests.Fixture Fixture { get; } = new();

        public IToolPermissionService Permissions { get; } = Substitute.For<IToolPermissionService>();

        public IPluginService Plugins { get; } = Substitute.For<IPluginService>();

        public ToolLoopImageChannel Channel { get; } = new(AiProviderType.PiaCloud);

        public Guid RunId { get; } = Guid.NewGuid();

        public bool StandingGrant { get; init; }

        public string Granter => $"background:{RunId}";

        /// <summary>The pending action the adapter minted, or null when the handler refused outright.</summary>
        public PluginToolCall? Routed { get; private set; }

        public void AllowlistOutlook() =>
            Fixture.Entries.Add(new ScreenCaptureAllowlistEntry(
                Guid.NewGuid(), "outlook", string.Empty, DateTimeOffset.UtcNow));

        public async Task<object?> RunAsync(
            ToolApprovalStore? approvals,
            string target = "window",
            string? match = "outlook",
            HashSet<string>? grantedWrites = null,
            HashSet<string>? deniedWrites = null,
            RunAutonomyPolicy? policy = null,
            string? granter = "")
        {
            if (Fixture.Capture.Targets.Count == 0)
                Fixture.Capture.Targets.Add(ScreenCaptureToolHandlerTests.Fixture.Window("outlook", "Inbox - Outlook"));

            var adapter = BuiltInPluginHandler.FromScreenCaptureHandler(
                Fixture.Handler, BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.ScreenPluginId]);

            Plugins.IsMcpTool(Arg.Any<string>()).Returns(false);
            Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    var (result, pending) = await adapter.HandleToolCallAsync(
                        ci.Arg<FunctionCallContent>(), ci.Arg<CancellationToken>());
                    Routed = pending;
                    return ((object? Result, PluginToolCall? PendingAction)?)(result, pending);
                });

            Permissions.IsGranted(Arg.Any<Guid>(), Arg.Any<string>()).Returns(StandingGrant);

            var args = new Dictionary<string, object?> { ["target"] = target };
            if (match is not null) args["match"] = match;
            var call = new FunctionCallContent("call-1", "screen_capture", args);

            object? answer = null;
            var ai = Substitute.For<IAiClientService>();
            ai.GetChatCompletionWithToolsAsync(
                    Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                    Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                    cancellationToken: Arg.Any<CancellationToken>())
                .Returns(ci => Drive(ci.ArgAt<ToolCallHandler?>(3), call, r => answer = r));

            var settings = Substitute.For<ISettingsService>();
            settings.GetSettingsAsync().Returns(new AppSettings());

            var runner = new BackgroundAssistantTurnRunner(
                ai, Plugins, Permissions,
                Substitute.For<IAssistantPromptComposer>(), Substitute.For<IPersonaService>(),
                Substitute.For<IAssistantChatService>(), Substitute.For<IChatTitleService>(), settings,
                () => Substitute.For<ITokenMapService>(), Substitute.For<IAgentRunService>(),
                new ExecutingRunStore(), NullLogger<BackgroundAssistantTurnRunner>.Instance, null);

            // Both ambients on the test flow: RunExchangeAsync sets none of its own, and without the channel
            // every case would refuse "no channel" long before the allowlist and pass vacuously.
            var previousTask = TaskAmbient.Current;
            var previousChannel = ToolLoopImageChannel.Current;
            TaskAmbient.Current = new TaskContext(
                RunId, null, UnattendedGranter: granter == "" ? Granter : granter);
            ToolLoopImageChannel.Current = Channel;
            try
            {
                await runner.RunExchangeAsync(
                    [new ChatMessage(ChatRole.User, "look")],
                    new AiProvider { Name = "P", Endpoint = "https://example", ProviderType = AiProviderType.PiaCloud },
                    new AssistantTurnSetup("system", [], SupportsTools: true, WebSearchActive: false),
                    grantedWrites ?? [],
                    TestContext.Current.CancellationToken,
                    policy: policy,
                    approvals: approvals,
                    deniedWrites: deniedWrites);
            }
            finally
            {
                TaskAmbient.Current = previousTask;
                ToolLoopImageChannel.Current = previousChannel;
            }

            return answer;
        }

        private static async IAsyncEnumerable<ChatStreamItem> Drive(
            ToolCallHandler? handler, FunctionCallContent call, Action<object?> record)
        {
            if (handler is not null)
                record(await handler(call, new ToolDispatchContext(1)));

            yield return new TextDelta("done");
            yield return new Finished(null, "test-model");
        }
    }
}
