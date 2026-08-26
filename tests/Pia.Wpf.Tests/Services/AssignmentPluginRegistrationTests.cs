using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Services.Plugins;
using Pia.Shared.Models;
using Pia.Shared.Operators;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Locks the wiring that makes the assignment pack REACHABLE by the model: a preloaded, default-enabled
/// built-in whose adapter follows the server surface, and whose system prompt names every tool it registers
/// and states the two things the model cannot infer — the answer comes back as a chat, and the model never
/// picks the records.
/// </summary>
public sealed class AssignmentPluginRegistrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "pia-assignments-plugin-" + Guid.NewGuid().ToString("N") + ".db");

    private SqliteContext? _sqlite;

    public void Dispose()
    {
        _sqlite?.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private static SyncPlugin AssignmentConfig() =>
        BuiltInPluginDefaults.Defaults[BuiltInPluginDefaults.AssignmentsPluginId];

    private static IAssignmentToolHandler HandlerWith(bool available)
    {
        var handler = Substitute.For<IAssignmentToolHandler>();
        handler.IsAvailable.Returns(available);
        handler.GetTools().Returns(_ => available
            ? new List<AITool>
            {
                AIFunctionFactory.Create(() => "ok", "query_assignments", "List runs"),
                AIFunctionFactory.Create(() => "ok", "get_assignment", "One run's progress"),
            }
            : []);
        return handler;
    }

    /// <summary>The production handler, whose <c>GetTools</c> needs no I/O, so the prompt can be checked
    /// against the tools that actually register rather than a stub's idea of them.</summary>
    private static AssignmentToolHandler RealHandler() => new(
        SurfaceCache(),
        Substitute.For<IAssignmentApiClient>(),
        Substitute.For<IAssignmentPendingStore>(),
        Substitute.For<IAssignmentConsentPrompt>(),
        Substitute.For<IHeadlessAssignmentLauncher>(),
        Substitute.For<ILocalizationService>(),
        NullLogger<AssignmentToolHandler>.Instance);

    private static IAssignmentSurfaceCache SurfaceCache()
    {
        var cache = Substitute.For<IAssignmentSurfaceCache>();
        cache.Surface.Returns(new AssignmentSurface(
            true, [new AssignmentSkill("deep-research", "Deep research", "Assistant", [])]));
        return cache;
    }

    private PluginService CreateService(IAssignmentToolHandler handler, IAssignmentSurfaceCache cache)
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
            handler,
            cache,
            Substitute.For<ISettingsService>(),
            NullLogger<PluginService>.Instance,
            _sqlite);
    }

    [Fact]
    public void AssignmentsPlugin_IsPreloadedAndDefaultEnabled()
    {
        Assert.Contains(BuiltInPluginDefaults.AssignmentsPluginId, BuiltInPluginDefaults.PreloadedPluginIds);

        var config = AssignmentConfig();
        Assert.True(config.IsPreloaded);
        Assert.True(config.IsActive);
        Assert.Equal("assignments", config.Name);
        Assert.Contains("\"handlerId\":\"assignments\"", config.ConfigJson);
        Assert.Contains("\"defaultEnabled\":true", config.ConfigJson);
    }

    /// <summary>Derived from the PRODUCTION tool list rather than from literals or a stub's, so a tool added
    /// without extending the prompt fails here instead of shipping a name the model never learns.</summary>
    [Fact]
    public void AssignmentsPlugin_SystemPrompt_NamesEveryTool()
    {
        var adapter = BuiltInPluginHandler.FromAssignmentHandler(RealHandler(), AssignmentConfig());
        var prompt = adapter.GetSystemPromptAddition();

        Assert.NotNull(prompt);
        Assert.NotEmpty(adapter.GetTools());
        foreach (var tool in adapter.GetTools())
            Assert.Contains(tool.Name, prompt, StringComparison.Ordinal);

        // The answer does not come back through the tools, and the records are never the model's to choose.
        Assert.Contains("arrives as a new chat", prompt, StringComparison.Ordinal);
        Assert.Contains("never choose which records are sent", prompt, StringComparison.Ordinal);
    }

    /// <summary>The prompt reaches a granted background run too, where the call starts a real run with no
    /// dialog — promising a confirmation there is what makes a model call it speculatively.</summary>
    [Fact]
    public void AssignmentsPlugin_SystemPrompt_DoesNotPromiseAConfirmationOnEverySurface()
    {
        var prompt = BuiltInPluginHandler.FromAssignmentHandler(RealHandler(), AssignmentConfig())
            .GetSystemPromptAddition();

        Assert.NotNull(prompt);
        Assert.Contains("granted this tool it starts at once", prompt, StringComparison.Ordinal);
        Assert.Contains("never call it speculatively", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void FromAssignmentHandler_ExposesToolsAndPrompt_WhenTheSurfaceIsAvailable()
    {
        var adapter = BuiltInPluginHandler.FromAssignmentHandler(HandlerWith(available: true), AssignmentConfig());

        Assert.Contains(adapter.GetTools(), t => t.Name == "query_assignments");
        Assert.Contains(adapter.GetTools(), t => t.Name == "get_assignment");
        Assert.False(string.IsNullOrWhiteSpace(adapter.GetSystemPromptAddition()));
    }

    [Fact]
    public void FromAssignmentHandler_SuppressesToolsAndPrompt_WhenTheSurfaceIsHidden()
    {
        var adapter = BuiltInPluginHandler.FromAssignmentHandler(HandlerWith(available: false), AssignmentConfig());

        Assert.Empty(adapter.GetTools());
        Assert.Null(adapter.GetSystemPromptAddition());
    }

    /// <summary>The guard against the inline-only factory shape, which drops the pending half of the tuple and
    /// hardcodes a throw for executePending: confirming would then never run the side effect.</summary>
    [Fact]
    public async Task FromAssignmentHandler_ForwardsAPendingActionToExecute()
    {
        var executed = false;
        var handler = HandlerWith(available: true);
        handler.HandleToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, new AssignmentToolCall("start_assignment", "Start it", "skill: research",
                () => { executed = true; return Task.FromResult<object?>("started"); })));

        var adapter = BuiltInPluginHandler.FromAssignmentHandler(handler, AssignmentConfig());
        var (result, pending) = await adapter.HandleToolCallAsync(
            new FunctionCallContent("1", "start_assignment", new Dictionary<string, object?>()),
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.NotNull(pending);
        Assert.Equal("start_assignment", pending.ToolName);
        Assert.Equal(BuiltInPluginDefaults.AssignmentsPluginId, pending.PluginId);
        Assert.Equal("assignments", pending.PluginName);
        Assert.False(executed);

        Assert.Equal("started", await adapter.ExecutePendingActionAsync(pending));
        Assert.True(executed);
    }

    /// <summary>The surface is an async HTTP probe, so it flips outside every other route-rebuild trigger.
    /// <c>GetAllTools</c> reads <c>GetTools()</c> live and would pass with or without the subscription; only
    /// the ROUTE map can tell whether it exists.</summary>
    [Fact]
    public async Task ASurfaceThatTurnsOnAfterStartup_GetsItsToolsRouted()
    {
        var available = false;
        var handler = Substitute.For<IAssignmentToolHandler>();
        handler.IsAvailable.Returns(_ => available);
        handler.GetTools().Returns(_ => available
            ? new List<AITool> { AIFunctionFactory.Create(() => "ok", "query_assignments", "List runs") }
            : []);
        handler.HandleToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)"routed", (AssignmentToolCall?)null));

        var cache = Substitute.For<IAssignmentSurfaceCache>();
        var service = CreateService(handler, cache);
        var call = new FunctionCallContent("1", "query_assignments", new Dictionary<string, object?>());

        Assert.Null(await service.RouteToolCallAsync(call, TestContext.Current.CancellationToken));

        available = true;
        cache.Changed += Raise.Event<EventHandler>(cache, EventArgs.Empty);

        var routed = await service.RouteToolCallAsync(call, TestContext.Current.CancellationToken);
        Assert.NotNull(routed);
        Assert.Equal("routed", routed.Value.Result);
    }
}
