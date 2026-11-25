using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.KnowledgeManager;
using Pia.Services.Operators;
using Pia.Services.Plugins;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.KnowledgeManager;

public sealed class KnowledgeManagerPluginRegistrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "pia-kbm-plugin-" + Guid.NewGuid().ToString("N") + ".db");

    private SqliteContext? _sqlite;

    public void Dispose()
    {
        _sqlite?.Dispose();
        TempPath.RemoveFile(_dbPath);
    }

    private static SyncPlugin Config() => BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.KbManagerPluginId];

    private static KnowledgeManagerToolHandler RealHandler(bool available)
    {
        var surface = Substitute.For<IKnowledgeManagerSurfaceCache>();
        surface.IsAvailable.Returns(available);
        return new KnowledgeManagerToolHandler(
            Substitute.For<IKnowledgeManagerApiClient>(), surface, Substitute.For<IFilesToolHandler>(),
            Substitute.For<ILocalizationService>(), NullLogger<KnowledgeManagerToolHandler>.Instance);
    }

    private PluginService CreateService(IKnowledgeManagerSurfaceCache surface)
    {
        _sqlite = new SqliteContext(_dbPath);
        return new PluginService(
            Substitute.For<IMemoryToolHandler>(),
            Substitute.For<ITodoToolHandler>(),
            Substitute.For<IReminderToolHandler>(),
            Substitute.For<IScheduledJobToolHandler>(),
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IIngestToolHandler>(),
            Substitute.For<IGitToolHandler>(),
            Substitute.For<IChatHistoryToolHandler>(),
            Substitute.For<IAssignmentToolHandler>(),
            Substitute.For<IScreenCaptureToolHandler>(),
            Substitute.For<IHelpToolHandler>(),
            Substitute.For<IKnowledgeManagerToolHandler>(),
            Substitute.For<IAssignmentSurfaceCache>(),
            surface,
            SettingsStubs.Returning(),
            NullLogger<PluginService>.Instance,
            _sqlite);
    }

    [Fact]
    public void KbManager_IsAPreloadedClientOnlyPack_OffByDefault()
    {
        Assert.Contains(BuiltInPluginDefaults.KbManagerPluginId, BuiltInPluginDefaults.PreloadedPluginIds);
        Assert.Equal(Guid.Parse("10000000-0000-0000-0000-00000000000D"), BuiltInPluginDefaults.KbManagerPluginId);

        var config = Config();
        Assert.Equal("kb-manager", config.Name);
        Assert.Equal("builtin_tool_pack", config.Kind);
        Assert.Contains("\"handlerId\":\"kb-manager\"", config.ConfigJson, StringComparison.Ordinal);
        Assert.Contains("\"defaultEnabled\":false", config.ConfigJson, StringComparison.Ordinal);
        Assert.False(PluginEnablement.IsEnabled(config));
    }

    [Fact]
    public void TheSystemPrompt_NamesEveryTool_AndTheTwoRules()
    {
        var adapter = BuiltInPluginHandler.FromKnowledgeManagerHandler(RealHandler(available: true), Config());
        var prompt = adapter.GetSystemPromptAddition();

        Assert.NotNull(prompt);
        Assert.NotEmpty(adapter.GetTools());
        foreach (var tool in adapter.GetTools())
            Assert.Contains(tool.Name, prompt, StringComparison.Ordinal);
        Assert.Contains("only when the user asks", prompt, StringComparison.Ordinal);
        Assert.Contains("name the knowledge base and the document", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenHidden_TheAdapterOffersNeitherToolsNorPrompt()
    {
        var adapter = BuiltInPluginHandler.FromKnowledgeManagerHandler(RealHandler(available: false), Config());

        Assert.Empty(adapter.GetTools());
        Assert.Null(adapter.GetSystemPromptAddition());
    }

    [Fact]
    public async Task APendingAction_CarriesTheHandlersWarningOntoThePluginToolCall()
    {
        var handler = Substitute.For<IKnowledgeManagerToolHandler>();
        handler.HandleToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (KbManagerToolCall?)new KbManagerToolCall(
                "upload_kb_document", "d", "x: y", "careful", () => Task.FromResult<object?>(null))));

        var (_, pending) = await BuiltInPluginHandler.FromKnowledgeManagerHandler(handler, Config())
            .HandleToolCallAsync(new FunctionCallContent("c", "upload_kb_document"), TestContext.Current.CancellationToken);

        Assert.Equal("careful", pending!.Warning);
        Assert.Equal("kb-manager", pending.PluginName);
        Assert.Equal(BuiltInPluginDefaults.KbManagerPluginId, pending.PluginId);
    }

    [Fact]
    public void TheSettingsRow_IsHiddenUntilTheSurfaceIsAvailable_AndFlipsWithIt()
    {
        var surface = Substitute.For<IKnowledgeManagerSurfaceCache>();
        surface.IsAvailable.Returns(false);
        var service = CreateService(surface);
        var changed = 0;
        service.PluginsChanged += (_, _) => changed++;

        Assert.DoesNotContain(service.GetVisiblePluginConfigs(), p => p.Id == BuiltInPluginDefaults.KbManagerPluginId);
        Assert.Contains(service.GetAllPluginConfigs(), p => p.Id == BuiltInPluginDefaults.KbManagerPluginId);

        surface.IsAvailable.Returns(true);
        surface.Changed += Raise.Event();

        Assert.Contains(service.GetVisiblePluginConfigs(), p => p.Id == BuiltInPluginDefaults.KbManagerPluginId);
        Assert.Equal(1, changed);
    }
}
