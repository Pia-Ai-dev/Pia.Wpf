using System.IO;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The payload-bearing exchange store. Every run needs a REAL chat + run row, because the enforced
/// <c>RunId</c> FK — the same one the cascade fact below depends on — would otherwise reject the INSERT.
/// </summary>
public sealed class AgentToolExchangeStoreTests : IDisposable
{
    /// <summary>Mirrors <c>FilesToolHandler.MaxWriteChars</c>, which is private to that handler.</summary>
    private const int WriteFileCap = 512 * 1024;

    private readonly string _tmpDir;
    private readonly string _dbPath;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly CapturingLogger<AgentToolExchangeStore> _logger = new();
    private readonly AgentToolExchangeStore _store;

    public AgentToolExchangeStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _dbPath = Path.Combine(_tmpDir, "history.db");
        _ctx = new SqliteContext(_dbPath);
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        _store = new AgentToolExchangeStore(_ctx, _logger);
    }

    [Fact]
    public async Task RecordAsync_ThenReadCarriedAsync_ReturnsTheRoundsMessagesInOrder_WithFullResultBodies()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var stepId = Guid.NewGuid();

        await _store.RecordAsync(run.Id, stepId, 1, Round(0, bodyChars: 6000), ct);
        await _store.RecordAsync(run.Id, stepId, 2, Round(1, bodyChars: 6000), ct);

        var rows = await _store.ReadCarriedAsync(run.Id, ct);

        Assert.Equal(4, rows.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, rows.Select(r => r.Seq).ToArray());
        Assert.Equal(new[] { 1, 1, 2, 2 }, rows.Select(r => r.Round!.Value).ToArray());
        Assert.All(rows, r => Assert.Equal(stepId, r.StepId));

        var messages = AgentToolExchangeSerializer.ToMessages(rows);
        Assert.Equal(4, messages.Count);

        // Full body, not the 4000-char carried cap: the store is the twin of _messages, and Capture is what
        // caps — it simply was not applied here.
        var bodies = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(c => c.Result as string).ToList();
        Assert.Equal(2, bodies.Count);
        Assert.All(bodies, b => Assert.Equal(6000, b!.Length));
    }

    [Fact]
    public async Task NoPersistedResultBody_IsEverAClearedPlaceholder()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();

        var rounds = AgentToolCarryover.KeptResults + 4;
        for (var i = 0; i < rounds; i++)
            await _store.RecordAsync(run.Id, null, i + 1, Round(i), ct);

        var rows = await _store.ReadCarriedAsync(run.Id, ct);
        var results = rows.Where(r => r.Kind == AgentToolExchangeKind.Result).ToList();

        // Non-vacuity: the assertion below is only worth anything if bodies actually came back.
        Assert.Equal(rounds, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.ResultText);
            Assert.DoesNotContain("[result cleared", r.ResultText!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AWriteFileArgumentOf512KChars_IsPersistedVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var content = new string('c', WriteFileCap);

        await _store.RecordAsync(run.Id, null, 1,
        [
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("w0", "write_file",
                new Dictionary<string, object?> { ["path"] = "out.md", ["content"] = content })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("w0", "written")]),
        ], ct);

        var rows = await _store.ReadCarriedAsync(run.Id, ct);
        var call = Assert.Single(rows, r => r.Kind == AgentToolExchangeKind.Call);

        Assert.False(call.ArgsOmitted);
        Assert.NotNull(call.ArgumentsJson);
        var arguments = AgentToolExchangeSerializer.DeserializeArguments(call.ArgumentsJson);
        Assert.NotNull(arguments);
        var rebuilt = new FunctionCallContent(call.CallId, call.ToolName!, arguments);
        Assert.Equal(content, ((System.Text.Json.JsonElement)rebuilt.Arguments!["content"]!).GetString());
    }

    [Fact]
    public async Task PastTheRunCharCap_TheWholeBatchIsRefused_SoNoCallIsStoredWithoutItsResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();

        // Four rounds just under a quarter of the cap each fit; the fifth and sixth cannot.
        var bodyChars = (AgentToolExchangeStore.MaxCharsPerRun / 4) - 100;
        for (var i = 0; i < 4; i++)
            await _store.RecordAsync(run.Id, null, i + 1, Round(i, bodyChars), ct);

        var atCap = await _store.ReadCarriedAsync(run.Id, ct);
        Assert.Equal(8, atCap.Count);

        await _store.RecordAsync(run.Id, null, 5, Round(5, bodyChars), ct);
        await _store.RecordAsync(run.Id, null, 6, Round(6, bodyChars), ct);

        var rows = await _store.ReadCarriedAsync(run.Id, ct);
        Assert.Equal(atCap.Count, rows.Count);

        var messages = AgentToolExchangeSerializer.ToMessages(rows);
        var callIds = messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToList();
        var resultIds = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(c => c.CallId).ToHashSet();
        Assert.NotEmpty(callIds);
        Assert.All(callIds, id => Assert.Contains(id, resultIds));

        var noted = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("cap reached", StringComparison.Ordinal));
        Assert.Contains(run.Id.ToString(), noted.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArgumentOverMaxRowChars_KeepsTheCallButOmitsTheArgs()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();

        await _store.RecordAsync(run.Id, null, 1,
        [
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("x0", "write_file",
                new Dictionary<string, object?> { ["content"] = new string('c', AgentToolExchangeSerializer.MaxRowChars + 1000) })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("x0", "written")]),
        ], ct);

        var rows = await _store.ReadCarriedAsync(run.Id, ct);

        var call = Assert.Single(rows, r => r.Kind == AgentToolExchangeKind.Call);
        Assert.True(call.ArgsOmitted);
        Assert.Null(call.ArgumentsJson);
        Assert.Equal(0, call.Chars);

        // The pair is never orphaned: the model still sees that the call was made and what it returned.
        var result = Assert.Single(rows, r => r.Kind == AgentToolExchangeKind.Result);
        Assert.Equal("written", result.ResultText);
        Assert.Equal(call.CallId, result.CallId);
    }

    [Fact]
    public async Task SeqContinues_AcrossAFreshStoreInstance_ForTheSameRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();

        await _store.RecordAsync(run.Id, null, 1, Round(0), ct);

        // The cross-process resume: a second instance allocates from MAX(Seq) in the table, not from zero.
        using var reopened = new AgentToolExchangeStore(_ctx, NullLogger<AgentToolExchangeStore>.Instance);
        await reopened.RecordAsync(run.Id, null, 2, Round(1), ct);

        var rows = await reopened.ReadCarriedAsync(run.Id, ct);

        Assert.Equal(new long[] { 1, 2, 3, 4 }, rows.Select(r => r.Seq).ToArray());
        Assert.Equal(new long[] { 1, 2, 3, 4 }, rows.Select(r => r.MessageSeq).ToArray());
    }

    [Fact]
    public async Task DeletingTheChat_PurgesTheExchangeRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatId = await MakeChatAsync();
        var run = await _runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), ct);

        await _store.AppendParkedAsync([ParkedRow(run.Id, "write_file", "{\"path\":\"canary.md\"}")], ct);
        Assert.Equal(1, CountRows());

        await _chats.DeleteAsync(chatId, ct);

        // AssistantChats → AgentRuns → AgentToolExchanges in one statement, which is what makes "purged with
        // the run's chat" a property of the schema rather than of a call site.
        Assert.Equal(0, CountRows());
    }

    [Fact]
    public async Task PruneAsync_DropsRowsOfTerminalRuns_AndKeepsAParkedRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        var states = new[]
        {
            AgentRunState.Completed, AgentRunState.Failed, AgentRunState.Cancelled, AgentRunState.WaitingForInput,
        };

        var runIds = new List<Guid>();
        foreach (var state in states)
        {
            var run = await MakeRunAsync();
            await _runs.SetStateAsync(run.Id, state, ct);
            await _store.RecordAsync(run.Id, null, 1, Round(0), ct);
            runIds.Add(run.Id);
        }

        // A cutoff in the past, so only the terminal clause can delete anything.
        var deleted = await _store.PruneAsync(DateTime.UtcNow - TimeSpan.FromDays(365), ct);

        Assert.Equal(6, deleted);
        Assert.Equal(0, CountRows(runIds[0]));
        Assert.Equal(0, CountRows(runIds[1]));
        Assert.Equal(0, CountRows(runIds[2]));
        Assert.Equal(2, CountRows(runIds[3]));
    }

    [Fact]
    public async Task ReadCarriedAsync_ExcludesAParkedCallRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();

        await _store.RecordAsync(run.Id, null, 1, Round(0), ct);
        await _store.AppendParkedAsync([ParkedRow(run.Id, "write_file", "{\"path\":\"real.md\"}")], ct);

        Assert.Equal(3, CountRows(run.Id));

        var carried = await _store.ReadCarriedAsync(run.Id, ct);

        // A re-seeded ParkedCall row would send a second FunctionCallContent under a CallId the Call row used.
        Assert.Equal(2, carried.Count);
        Assert.DoesNotContain(carried, r => r.Kind == AgentToolExchangeKind.ParkedCall);

        var replayable = await _store.GetReplayableAsync(run.Id, "WRITE_FILE", ct);
        Assert.Single(replayable);
    }

    [Fact]
    public async Task SealStepAsync_AnchorsOnlyItsOwnStepsUnanchoredRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var stepId = Guid.NewGuid();
        var anchor = Guid.NewGuid();

        await _store.RecordAsync(run.Id, stepId, 1, Round(0), ct);
        await _store.RecordAsync(run.Id, null, 1, Round(1), ct);

        Assert.Equal(2, await _store.SealStepAsync(run.Id, stepId, anchor, ct));

        // A second pass finds nothing: the predicate is scoped to rows still unanchored.
        Assert.Equal(0, await _store.SealStepAsync(run.Id, stepId, Guid.NewGuid(), ct));

        // The null-stepId arm is a separate group, not a wildcard over every row of the run.
        var runLevelAnchor = Guid.NewGuid();
        Assert.Equal(2, await _store.SealStepAsync(run.Id, null, runLevelAnchor, ct));

        var rows = await _store.ReadCarriedAsync(run.Id, ct);
        Assert.All(rows.Where(r => r.StepId == stepId), r => Assert.Equal(anchor, r.AnchorMessageId));
        Assert.All(rows.Where(r => r.StepId is null), r => Assert.Equal(runLevelAnchor, r.AnchorMessageId));
    }

    [Fact]
    public void TheStoresPublicSurface_NamesNoSyncAssistantChatType()
    {
        var offenders = new List<string>();

        foreach (var member in Surface(typeof(IAgentToolExchangeStore)).Concat(Surface(typeof(AgentToolExchangeStore))))
        {
            foreach (var type in member.Types)
            {
                if (Mentions(type))
                    offenders.Add($"{member.Name}: {type.Name}");
            }
        }

        Assert.True(offenders.Count == 0,
            "the exchange store must never name a cloud-synced chat type: " + string.Join(", ", offenders));
    }

    private static IEnumerable<(string Name, IEnumerable<Type> Types)> Surface(Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            yield return (method.Name, method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType));

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            yield return (property.Name, [property.PropertyType]);

        foreach (var ctor in type.GetConstructors())
            yield return (".ctor", ctor.GetParameters().Select(p => p.ParameterType));
    }

    private static bool Mentions(Type type)
    {
        if (type.Name.Contains("SyncAssistantChat", StringComparison.Ordinal))
            return true;

        return type.IsGenericType && type.GetGenericArguments().Any(Mentions);
    }

    private static List<ChatMessage> Round(int index, int bodyChars = 40) =>
    [
        new(ChatRole.Assistant, [new FunctionCallContent("c" + index, "read_file",
            new Dictionary<string, object?> { ["path"] = "f" + index + ".csv" })]),
        new(ChatRole.Tool, [new FunctionResultContent("c" + index, new string('b', bodyChars))]),
    ];

    private static AgentToolExchangeRow ParkedRow(Guid runId, string toolName, string argumentsJson) => new(
        Id: Guid.NewGuid(),
        RunId: runId,
        StepId: null,
        MessageSeq: 0,
        Seq: 0,
        Round: 3,
        Role: "assistant",
        Kind: AgentToolExchangeKind.ParkedCall,
        CallId: "park-" + Guid.NewGuid().ToString("N")[..8],
        ToolName: toolName,
        PluginId: null,
        ArgumentsJson: argumentsJson,
        ArgsOmitted: false,
        DisplayArgs: "path=canary.md",
        ResultKind: AgentToolExchangeResult.None,
        ResultText: null,
        Chars: argumentsJson.Length,
        AnchorMessageId: null,
        CreatedAt: DateTime.UtcNow,
        ReplayedAt: null,
        SupersededAt: null);

    private int CountRows(Guid? runId = null)
    {
        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = runId is null
            ? "SELECT COUNT(*) FROM AgentToolExchanges"
            : "SELECT COUNT(*) FROM AgentToolExchanges WHERE RunId = @RunId";
        if (runId is { } id)
            cmd.Parameters.AddWithValue("@RunId", id.ToString());

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private async Task<AgentRun> MakeRunAsync()
    {
        var chatId = await MakeChatAsync();
        return await _runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User),
            TestContext.Current.CancellationToken);
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
        _store.Dispose();
        _runs.Dispose();
        _ctx.Dispose();
        SqlitePool.ClearFor(_ctx.ConnectionString);
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }
}
