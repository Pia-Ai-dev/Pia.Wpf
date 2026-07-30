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

/// <summary>
/// Batch 04 D13. Voice mode used to execute EVERY pending write with no eligibility check, no grant check, no
/// card and no destructive floor (<c>"Auto-approve write operations in voice mode (no dialog)"</c>), so
/// <c>write_file</c>, <c>delete_file</c>, <c>forget</c> and every destructive MCP tool ran silently. Nothing in
/// the suite pinned it either way — this file is both the fix's coverage and the first voice-mode harness.
/// <para>
/// The gate is driven through <c>HandleVoiceModeToolCall</c>, made <c>internal</c> for exactly this: the
/// alternative is standing up a whole voice turn through <c>StreamVoiceModeResponse</c>, which is a
/// disproportionate fixture for five facts.
/// </para>
/// </summary>
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

        Assert.Equal("read-result", await vm.HandleVoiceModeToolCall(Call("query_todos")));
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
        Assert.Equal("write-done", await vm.HandleVoiceModeToolCall(Call("create_todo")));
        Assert.True(executed);
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
        var result = Assert.IsType<string>(await vm.HandleVoiceModeToolCall(Call("write_file")));

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
        var result = Assert.IsType<string>(await vm.HandleVoiceModeToolCall(Call(toolName)));

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
        Assert.Equal("write-done", await vm.HandleVoiceModeToolCall(Call("create_issue")));
        Assert.True(executed);
    }

    [Fact]
    public async Task WithTheSettingOn_APresetClassWriteRuns()
    {
        var executed = false;
        var pluginId = Guid.NewGuid();
        ArrangeWrite("write_file", "files", pluginId, () => executed = true);

        var vm = Build(new AppSettings { AgentRunAutoApproveBuiltInWrites = true });
        Assert.Equal("write-done", await vm.HandleVoiceModeToolCall(Call("write_file")));
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
        var result = Assert.IsType<string>(await vm.HandleVoiceModeToolCall(Call("purge_records")));

        Assert.False(executed);
        Assert.Contains("destructive external", result);
    }

    [Fact]
    public async Task AnUnroutedToolIsStillReportedUnknown()
    {
        var vm = Build();
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?, PluginToolCall?)?)null);

        Assert.Equal("Unknown tool: nope", await vm.HandleVoiceModeToolCall(Call("nope")));
    }
}
