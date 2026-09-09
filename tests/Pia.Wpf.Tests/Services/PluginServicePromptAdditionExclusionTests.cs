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
/// A turn that withholds a tool family must withhold the prose that names it too — the model cannot see the
/// absence, so an addition still describing <c>create_scheduled_research</c> just sends it hunting.
/// </summary>
public sealed class PluginServicePromptAdditionExclusionTests : IDisposable
{
    // Substrings of the real BuiltInPluginDefaults additions — the wrapper sources those from ConfigJson,
    // so a substituted handler cannot supply its own.
    private const string RoutineAddition = "Use create_scheduled_research to set one up";
    private const string TodoAddition = "You have access to a todo list";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "pia-additions-" + Guid.NewGuid().ToString("N") + ".db");

    private SqliteContext? _sqlite;

    public void Dispose()
    {
        _sqlite?.Dispose();
        TempPath.RemoveFile(_dbPath);
    }

    private PluginService CreateService()
    {
        var routines = Substitute.For<IScheduledJobToolHandler>();
        routines.GetTools().Returns(_ => new List<AITool>
        {
            AIFunctionFactory.Create(() => "ok", "create_scheduled_research", "Create a routine"),
            AIFunctionFactory.Create(() => "ok", "query_scheduled_research", "List routines"),
        });

        var todo = Substitute.For<ITodoToolHandler>();
        todo.GetTools().Returns(_ => new List<AITool>
        {
            AIFunctionFactory.Create(() => "ok", "create_todo", "Create a todo"),
        });

        _sqlite = new SqliteContext(_dbPath);
        return new PluginService(
            Substitute.For<IMemoryToolHandler>(),
            todo,
            Substitute.For<IReminderToolHandler>(),
            routines,
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IIngestToolHandler>(),
            Substitute.For<IGitToolHandler>(),
            Substitute.For<IChatHistoryToolHandler>(),
            Substitute.For<IAssignmentToolHandler>(),
            Substitute.For<IScreenCaptureToolHandler>(),
            Substitute.For<IAssignmentSurfaceCache>(),
            Substitute.For<ISettingsService>(),
            NullLogger<PluginService>.Instance,
            _sqlite);
    }

    [Fact]
    public void NoExclusions_KeepsEveryAddition()
    {
        var combined = CreateService().GetCombinedSystemPromptAdditions();

        Assert.Contains(RoutineAddition, combined, StringComparison.Ordinal);
        Assert.Contains(TodoAddition, combined, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExcludedToolName_DropsItsWholeHandlerAddition_AndNoOther()
    {
        var combined = CreateService().GetCombinedSystemPromptAdditions(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "create_scheduled_research" });

        // One named tool is enough: the addition describes the whole family, so keeping it would still
        // advertise query_scheduled_research alongside a tool that is gone.
        Assert.DoesNotContain(RoutineAddition, combined, StringComparison.Ordinal);
        Assert.Contains(TodoAddition, combined, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyExclusionSet_IsTheSameAsNone()
    {
        var service = CreateService();

        Assert.Equal(
            service.GetCombinedSystemPromptAdditions(),
            service.GetCombinedSystemPromptAdditions(new HashSet<string>()));
    }
}
