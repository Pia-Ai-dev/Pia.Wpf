using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The catalogue is read by a pre-approval surface, so a tool it lists is a tool the page will offer to
/// grant. The enabled-plugin skip is therefore the boundary, not a display detail.
/// </summary>
public sealed class PluginServiceToolCatalogTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "pia-catalog-" + Guid.NewGuid().ToString("N") + ".db");

    private SqliteContext? _sqlite;

    public void Dispose()
    {
        _sqlite?.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private PluginService CreateService()
    {
        var todo = Substitute.For<ITodoToolHandler>();
        todo.GetTools().Returns(_ => new List<AITool>
        {
            AIFunctionFactory.Create(() => "ok", "create_todo", "Create a todo"),
            AIFunctionFactory.Create(() => "ok", "delete_todo", "Delete a todo"),
        });

        var reminder = Substitute.For<IReminderToolHandler>();
        reminder.GetTools().Returns(_ => new List<AITool>
        {
            AIFunctionFactory.Create(() => "ok", "create_reminder", "Create a reminder"),
        });

        _sqlite = new SqliteContext(_dbPath);
        return new PluginService(
            Substitute.For<IMemoryToolHandler>(),
            todo,
            reminder,
            Substitute.For<IScheduledJobToolHandler>(),
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IIngestToolHandler>(),
            Substitute.For<IGitToolHandler>(),
            Substitute.For<IChatHistoryToolHandler>(),
            Substitute.For<ISettingsService>(),
            NullLogger<PluginService>.Instance,
            _sqlite);
    }

    /// <summary>A copy, because the entries in <c>BuiltInPluginDefaults.Defaults</c> are shared static instances.</summary>
    private static SyncPlugin DisabledCopyOf(SyncPlugin source) => new()
    {
        Id = source.Id,
        Kind = source.Kind,
        Name = source.Name,
        Description = source.Description,
        ConfigJson = source.ConfigJson,
        Version = source.Version,
        IsPreloaded = true,
        IsActive = true,
        UserEnabled = false,
        UpdatedAt = source.UpdatedAt,
    };

    [Fact]
    public void Catalog_CarriesThePluginRouteAndTheAbsenceOfAServerHint()
    {
        var entry = Assert.Single(CreateService().GetToolCatalog(), e => e.ToolName == "create_todo");

        Assert.Equal(BuiltInPluginDefaults.TodoPluginId, entry.PluginId);
        // The plugin KEY, which is what ToolClassifier maps; a display name would classify as Unknown.
        Assert.Equal("todo", entry.PluginName);
        Assert.Equal("Create a todo", entry.Description);
        Assert.False(entry.IsExternalRoute);
        Assert.False(entry.ServerDeclaredDestructive);
    }

    [Fact]
    public async Task ADisabledPlugin_ContributesNoGrantableRows()
    {
        var service = CreateService();
        Assert.Contains(service.GetToolCatalog(), e => e.PluginName == "todo");

        // Disabled through the sync path on purpose: SetPluginEnabledAsync writes UserEnabled onto the
        // shared static default and would leak the change into every other test in the process.
        var disabled = DisabledCopyOf(BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.TodoPluginId]);
        await service.ApplyServerPluginsAsync([disabled], []);

        var catalog = service.GetToolCatalog();
        Assert.DoesNotContain(catalog, e => e.PluginName == "todo");
        // Non-vacuity: the skip is targeted, not a catalogue that stopped being built.
        Assert.Contains(catalog, e => e.ToolName == "create_reminder");
    }

    /// <summary>Without the event the catalogue keeps its pre-toggle shape for the rest of the app run.</summary>
    [Fact]
    public async Task EnablingAPlugin_RaisesPluginsChanged_AndItsToolsComeBack()
    {
        var service = CreateService();
        // A copy in _pluginConfigs, so the enable below writes to it and not to the shared static default.
        var disabled = DisabledCopyOf(BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.TodoPluginId]);
        await service.ApplyServerPluginsAsync([disabled], []);
        Assert.DoesNotContain(service.GetToolCatalog(), e => e.PluginName == "todo");

        // Subscribed after the apply, so only the toggle can raise it.
        var raised = 0;
        service.PluginsChanged += (_, _) => raised++;

        await service.SetPluginEnabledAsync(BuiltInPluginDefaults.TodoPluginId, true);

        Assert.Equal(1, raised);
        Assert.Contains(service.GetToolCatalog(), e => e.PluginName == "todo");
    }

    [Fact]
    public void ABuiltInHandler_DeclaresNoServerHint()
    {
        // The default interface method is the whole implementation for a built-in: "no hint available", so
        // the delete-like NAME remains the entire rule there.
        IPluginToolHandler adapter = BuiltInPluginHandler.FromTodoHandler(
            Substitute.For<ITodoToolHandler>(),
            BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.TodoPluginId]);

        Assert.False(adapter.DeclaresDestructive("delete_todo"));
    }

    [Fact]
    public void AnMcpHandler_ReadsNoHintForAToolItDoesNotHave()
    {
        var handler = new McpPluginToolHandler(
            Guid.NewGuid(), "some-mcp-server", "noop.exe", [], null, NullLogger.Instance);

        Assert.False(((IPluginToolHandler)handler).DeclaresDestructive("purge_index"));
    }
}
