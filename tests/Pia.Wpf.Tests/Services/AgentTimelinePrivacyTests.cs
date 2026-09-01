using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
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

public sealed class AgentTimelinePrivacyTests : IDisposable
{
    private const string Canary = "CANARY-9f3a1c";

    private readonly string _tmpDir;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly AgentTimelineService _timeline;

    public AgentTimelinePrivacyTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        _timeline = new AgentTimelineService(_ctx, NullLogger<AgentTimelineService>.Instance);
    }

    [Fact]
    public async Task NoCanaryFromArgsResultsOrPathsReachesAnyPersistedColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatId = await MakeChatAsync();
        var run = await _runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), ct);

        // Everything a gated call carries is poisoned except the tool name, which is deliberately allowed through.
        var pending = new PluginToolCall(
            ToolName: "write_file",
            PluginId: Guid.NewGuid(),
            PluginName: "files",
            Description: $"Write {Canary} to disk",
            Details: $"{{\"path\":\"C:/Users/marco/{Canary}.md\"}}",
            Execute: () => Task.FromResult<object?>($"wrote {Canary} (2 KB)"),
            DiffPreview: null,
            TargetPath: $"C:/Users/marco/{Canary}.md");

        var runner = BuildRunner(pending);
        await runner.RunExchangeAsync(
            [new ChatMessage(ChatRole.User, "go")], Provider(),
            new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "write_file" },
            ct,
            timeline: new AgentTimelineScope(_timeline, run.Id, stepId: null));

        await _timeline.DrainAsync();

        // Control: without a row present the sweep below would be vacuously green.
        var cells = ReadEveryCell();
        Assert.NotEmpty(cells);
        Assert.Contains("write_file", cells);

        Assert.DoesNotContain(cells, cell => cell.Contains(Canary, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(cells, cell => cell.Contains("C:/Users", StringComparison.OrdinalIgnoreCase));

        // Lengths are recorded on purpose: a length is not a fingerprint.
        var rows = await _timeline.GetForRunAsync(run.Id, ct);
        var row = Assert.Single(rows);
        Assert.Equal($"wrote {Canary} (2 KB)".Length, row.ResultChars);
    }

    private List<string> ReadEveryCell()
    {
        var cells = new List<string>();
        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT * FROM AgentTimelineEvents";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
                cells.Add(reader.IsDBNull(i) ? string.Empty : reader.GetValue(i).ToString() ?? string.Empty);
        }

        return cells;
    }

    private static AiProvider Provider() => new()
    {
        Id = Guid.NewGuid(),
        Name = "P",
        Endpoint = "https://example",
        TimeoutSeconds = 60,
    };

    private static BackgroundAssistantTurnRunner BuildRunner(PluginToolCall pending)
    {
        var ai = Substitute.For<IAiClientService>();
        var plugins = Substitute.For<IPluginService>();
        var composer = Substitute.For<IAssistantPromptComposer>();
        var personas = Substitute.For<IPersonaService>();
        var chats = Substitute.For<IAssistantChatService>();
        var titles = Substitute.For<IChatTitleService>();
        var settings = Substitute.For<ISettingsService>();
        var runs = Substitute.For<IAgentRunService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)pending));

        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => Drive(ci.ArgAt<ToolCallHandler?>(3)));

        ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();
        return new BackgroundAssistantTurnRunner(
            ai, plugins, Substitute.For<IToolPermissionService>(), composer, personas, chats, titles, settings,
            TokenMapFactory, runs, new ExecutingRunStore(),
            NullLogger<BackgroundAssistantTurnRunner>.Instance);
    }

    private static async IAsyncEnumerable<ChatStreamItem> Drive(ToolCallHandler? handler)
    {
        if (handler is not null)
        {
            // The call id is poisoned too: it is copied out of provider JSON unvalidated, so the sweep has to
            // cover that column as well — a path-shaped id fails the charset check and stores NULL.
            await handler(new FunctionCallContent($"C:/Users/marco/{Canary}.md", "write_file",
                new Dictionary<string, object?> { ["path"] = $"C:/Users/marco/{Canary}.md", ["content"] = Canary }), new ToolDispatchContext(1));
        }

        yield return new TextDelta("done");
        yield return new Finished(null, "test-model");
    }

    private async Task<Guid> MakeChatAsync()
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = id,
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = "Assistant",
        }, TestContext.Current.CancellationToken);
        return id;
    }

    public void Dispose()
    {
        _timeline.Dispose();
        _runs.Dispose();
        _chats.Dispose();
        _ctx.Dispose();
        SqlitePool.ClearFor(_ctx.ConnectionString);
        TempPath.Remove(_tmpDir);
    }
}
