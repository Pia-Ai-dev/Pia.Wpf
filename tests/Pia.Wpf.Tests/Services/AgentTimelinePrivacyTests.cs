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
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The batch's central invariant, proved by inspecting the PERSISTED ROWS rather than the code: nothing a tool
/// call carried — its arguments, its result, its user-facing description, its target path — reaches any column
/// of <c>AgentTimelineEvents</c>.
/// <para>
/// This drives the real unattended gate against the real store, so the assertion covers the whole path from
/// <c>HandleToolCallAsync</c> through <c>AgentTimelineScope</c> to the INSERT.
/// </para>
/// </summary>
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

        // Everything a gated call carries is poisoned, EXCEPT the tool name — §3 explicitly permits the name,
        // whose precedent is both gates logging it at Information today.
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

        // Control: the row IS there, so the sweep below is not vacuously green.
        var cells = ReadEveryCell();
        Assert.NotEmpty(cells);
        Assert.Contains("write_file", cells);

        Assert.DoesNotContain(cells, cell => cell.Contains(Canary, StringComparison.OrdinalIgnoreCase));
        // And the low-entropy half specifically: a path fragment must not survive either.
        Assert.DoesNotContain(cells, cell => cell.Contains("C:/Users", StringComparison.OrdinalIgnoreCase));

        // Lengths ARE recorded — that is the point of the metadata design, and a length is not a fingerprint.
        var rows = await _timeline.GetForRunAsync(run.Id, ct);
        var row = Assert.Single(rows);
        Assert.Equal($"wrote {Canary} (2 KB)".Length, row.ResultChars);
    }

    /// <summary>Every value of every column of every row, stringified — the sweep T-PRIV-1 runs.</summary>
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
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => Drive(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3)));

        ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();
        return new BackgroundAssistantTurnRunner(
            ai, plugins, composer, personas, chats, titles, settings,
            TokenMapFactory, runs, new ExecutingRunStore(),
            NullLogger<BackgroundAssistantTurnRunner>.Instance);
    }

    private static async IAsyncEnumerable<ChatStreamItem> Drive(Func<FunctionCallContent, Task<object?>>? handler)
    {
        if (handler is not null)
        {
            // The ARGUMENTS carry the canary too — this is the half a hash column would leak.
            await handler(new FunctionCallContent("call-1", "write_file",
                new Dictionary<string, object?> { ["path"] = $"C:/Users/marco/{Canary}.md", ["content"] = Canary }));
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
        _ctx.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }
}
