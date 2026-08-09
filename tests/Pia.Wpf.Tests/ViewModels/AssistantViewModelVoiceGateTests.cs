using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Pia.Tests.Services;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

// Driven through HandleVoiceModeToolCall, which is internal for exactly this: standing up a whole voice turn through
// StreamVoiceModeResponse is a disproportionate fixture for these facts.
public sealed class AssistantViewModelVoiceGateTests
{
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    private AssistantViewModel Build(AppSettings? appSettings = null)
    {
        // ChatTitleChipViewModel (built in the ctor) requires a captured SynchronizationContext.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        _settings.GetSettingsAsync().Returns(appSettings ?? new AppSettings());

        var meeting = new MeetingAttendeeViewModel(
            Substitute.For<IMeetingAttendeeService>(),
            _settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            NullLogger<MeetingAttendeeViewModel>.Instance,
            new InlineUiDispatcher());

        var directTranscription = new DirectTranscriptionViewModel(
            Substitute.For<IDirectTranscriptionService>(),
            _settings,
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
            _settings,
            Substitute.For<IOutputService>(),
            _plugins,
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
            _permissions);
    }

    private static FunctionCallContent Call(string name) =>
        new(Guid.NewGuid().ToString(), name, new Dictionary<string, object?>());

    private void ArrangeWrite(string toolName, string pluginName, Guid pluginId, Action onExecute)
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, new PluginToolCall(
                toolName, pluginId, pluginName, "desc", null,
                () => { onExecute(); return Task.FromResult<object?>("write-done"); })));
    }

    [Fact]
    public async Task ReadsAreUnaffected()
    {
        var vm = Build();
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)"read-result", (PluginToolCall?)null));

        Assert.Equal("read-result", await vm.HandleVoiceModeToolCall(Call("query_todos"), new ToolDispatchContext(1)));
    }

    [Fact]
    public async Task AllowlistedWriteStillRuns()
    {
        // No behaviour change for the common case: the four curated create/append tools still work by voice.
        var executed = false;
        var pluginId = Guid.NewGuid();
        ArrangeWrite("create_todo", "todo", pluginId, () => executed = true);
        _permissions.IsAutoApproveEligible("create_todo").Returns(true);

        var vm = Build();
        Assert.Equal("write-done", await vm.HandleVoiceModeToolCall(Call("create_todo"), new ToolDispatchContext(1)));
        Assert.True(executed);
    }

    /// <summary><c>IsAutoApproveEligible</c> is name-only and tool-name routes are last-wins, so an MCP server
    /// exposing <c>create_todo</c> owns that route and the curated allowlist must not authorize it.</summary>
    [Fact]
    public async Task AnMcpToolShadowingAnAllowlistedName_IsRefused_NotExecuted()
    {
        var executed = false;
        var pluginId = Guid.NewGuid();
        ArrangeWrite("create_todo", "some-mcp-server", pluginId, () => executed = true);
        // The allowlist says yes on the NAME — the route says the tool is external.
        _permissions.IsAutoApproveEligible("create_todo").Returns(true);
        _plugins.IsMcpTool("create_todo").Returns(true);

        var vm = Build();
        var result = Assert.IsType<string>(await vm.HandleVoiceModeToolCall(Call("create_todo"), new ToolDispatchContext(1)));

        Assert.False(executed);
        Assert.StartsWith("Denied:", result);
    }

    [Fact]
    public async Task UngrantedWriteFileIsRefused_NotExecuted()
    {
        var executed = false;
        var pluginId = Guid.NewGuid();
        ArrangeWrite("write_file", "files", pluginId, () => executed = true);
        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        _permissions.IsGranted(pluginId, "write_file").Returns(false);

        var vm = Build();
        var result = Assert.IsType<string>(await vm.HandleVoiceModeToolCall(Call("write_file"), new ToolDispatchContext(1)));

        Assert.False(executed);
        Assert.Contains("voice mode cannot show an approval card", result);
        Assert.Contains("chat window", result);
    }

    [Theory]
    [InlineData("delete_file", "files", false)]
    [InlineData("forget", "memory", false)]
    [InlineData("delete_issue", "linear", true)]
    public async Task DeleteLikeToolIsRefused_EvenWithTheSettingOn(string toolName, string pluginName, bool isMcp)
    {
        var executed = false;
        var pluginId = Guid.NewGuid();
        ArrangeWrite(toolName, pluginName, pluginId, () => executed = true);
        _plugins.IsMcpTool(toolName).Returns(isMcp);
        // Even a (forged) standing grant and the preset both on: a delete-like tool never runs by voice.
        _permissions.IsGranted(pluginId, toolName).Returns(true);

        var vm = Build(new AppSettings { AgentRunAutoApproveBuiltInWrites = true });
        var result = Assert.IsType<string>(await vm.HandleVoiceModeToolCall(Call(toolName), new ToolDispatchContext(1)));

        Assert.False(executed);
        Assert.StartsWith("Denied:", result);
    }

    [Fact]
    public async Task StandingGrantIsHonoured()
    {
        // An external tool the user has already "always allowed" keeps working by voice.
        var executed = false;
        var pluginId = Guid.NewGuid();
        ArrangeWrite("create_issue", "linear", pluginId, () => executed = true);
        _plugins.IsMcpTool("create_issue").Returns(true);
        _permissions.IsGranted(pluginId, "create_issue").Returns(true);

        var vm = Build();
        Assert.Equal("write-done", await vm.HandleVoiceModeToolCall(Call("create_issue"), new ToolDispatchContext(1)));
        Assert.True(executed);
    }

    [Fact]
    public async Task WithTheSettingOn_APresetClassWriteRuns()
    {
        var executed = false;
        var pluginId = Guid.NewGuid();
        ArrangeWrite("write_file", "files", pluginId, () => executed = true);

        var vm = Build(new AppSettings { AgentRunAutoApproveBuiltInWrites = true });
        Assert.Equal("write-done", await vm.HandleVoiceModeToolCall(Call("write_file"), new ToolDispatchContext(1)));
        Assert.True(executed);
    }

    [Fact]
    public async Task WhenMcpDerivationThrows_TheToolIsTreatedAsExternal_AndTheCallSurvives()
    {
        var executed = false;
        var pluginId = Guid.NewGuid();
        ArrangeWrite("purge_records", "linear", pluginId, () => executed = true);
        _plugins.IsMcpTool("purge_records").Returns(_ => throw new InvalidOperationException("boom"));
        _permissions.IsGranted(pluginId, "purge_records").Returns(true);

        var vm = Build();
        var result = Assert.IsType<string>(await vm.HandleVoiceModeToolCall(Call("purge_records"), new ToolDispatchContext(1)));

        Assert.False(executed);
        Assert.Contains("destructive external", result);
    }

    [Fact]
    public async Task AnUnroutedToolIsStillReportedUnknown()
    {
        // RouteToolCallAsync is deliberately NOT arranged: its return type is a nullable tuple, so NSubstitute's
        // own default already IS the unrouted answer.
        var vm = Build();

        Assert.Equal("Unknown tool: nope", await vm.HandleVoiceModeToolCall(Call("nope"), new ToolDispatchContext(1)));
    }
}
