using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Services.Plugins;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// A built-in plugin's definition lives in code, but its on/off switch is the user's. Both halves are
/// tested here because the load path deliberately drops the persisted row for a built-in, and dropping
/// all of it is what lost the switch.
/// </summary>
public sealed class PluginServiceEnabledPersistenceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "pia-plugin-prefs-" + Guid.NewGuid().ToString("N") + ".db");

    private readonly List<SqliteContext> _contexts = [];

    public void Dispose()
    {
        foreach (var context in _contexts)
            context.Dispose();
        TempPath.RemoveFile(_dbPath);
    }

    /// <summary>A restart is a second service over the same database file.</summary>
    private PluginService CreateService()
    {
        var sqlite = new SqliteContext(_dbPath);
        _contexts.Add(sqlite);

        var todo = Substitute.For<ITodoToolHandler>();
        todo.GetTools().Returns(_ => new List<AITool>
        {
            AIFunctionFactory.Create(() => "ok", "create_todo", "Create a todo"),
        });

        return new PluginService(
            Substitute.For<IMemoryToolHandler>(),
            todo,
            Substitute.For<IReminderToolHandler>(),
            Substitute.For<IScheduledJobToolHandler>(),
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IIngestToolHandler>(),
            Substitute.For<IGitToolHandler>(),
            Substitute.For<IChatHistoryToolHandler>(),
            Substitute.For<IAssignmentToolHandler>(),
            Substitute.For<IAssignmentSurfaceCache>(),
            Substitute.For<ISettingsService>(),
            NullLogger<PluginService>.Instance,
            sqlite);
    }

    private static bool IsEnabled(PluginService service, Guid pluginId) =>
        service.GetAllPluginConfigs().Single(p => p.Id == pluginId).UserEnabled ?? true;

    [Fact]
    public async Task DisablingABuiltInPlugin_SurvivesARestart()
    {
        await CreateService().SetPluginEnabledAsync(BuiltInPluginDefaults.TodoPluginId, false);

        Assert.False(IsEnabled(CreateService(), BuiltInPluginDefaults.TodoPluginId));
    }

    /// <summary>What the user actually reports: the tools come back, not that a field flipped.</summary>
    [Fact]
    public async Task ADisabledBuiltInPlugin_StillOffersNoToolsAfterARestart()
    {
        var service = CreateService();
        Assert.Contains(service.GetToolCatalog(), e => e.ToolName == "create_todo");

        await service.SetPluginEnabledAsync(BuiltInPluginDefaults.TodoPluginId, false);

        Assert.DoesNotContain(CreateService().GetToolCatalog(), e => e.ToolName == "create_todo");
    }

    [Fact]
    public async Task ReEnablingABuiltInPlugin_SurvivesARestart()
    {
        await CreateService().SetPluginEnabledAsync(BuiltInPluginDefaults.TodoPluginId, false);
        await CreateService().SetPluginEnabledAsync(BuiltInPluginDefaults.TodoPluginId, true);

        Assert.True(IsEnabled(CreateService(), BuiltInPluginDefaults.TodoPluginId));
    }

    [Fact]
    public async Task ATogglePersistsOnlyThePreference_NotTheBuiltInDefinition()
    {
        var shipped = BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.TodoPluginId];
        await CreateService().SetPluginEnabledAsync(BuiltInPluginDefaults.TodoPluginId, false);

        var restarted = CreateService().GetAllPluginConfigs()
            .Single(p => p.Id == BuiltInPluginDefaults.TodoPluginId);

        // The row must never become the source of the prompt: a release that edits the built-in has to win.
        Assert.Equal(shipped.ConfigJson, restarted.ConfigJson);
        Assert.Equal(shipped.Description, restarted.Description);
    }

    /// <summary>The defaults are a static dictionary of shared instances, so a toggle that wrote through
    /// to one would follow the process into every later service and test.</summary>
    [Fact]
    public async Task ATogglePersists_WithoutMutatingTheSharedStaticDefault()
    {
        await CreateService().SetPluginEnabledAsync(BuiltInPluginDefaults.TodoPluginId, false);

        Assert.Null(BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.TodoPluginId].UserEnabled);
    }
}
