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

    /// <summary>A half payload is unreplayable, so the per-park bound refuses records whole.</summary>
    [Fact]
    public async Task ThePerParkCap_DropsWholeRecordsRatherThanTruncatingThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var content = new string('c', 200_000);

        var approvals = new ToolApprovalStore(canPark: true);
        for (var i = 0; i < 12; i++)
        {
            approvals.Record(new ToolApprovalStore.ParkedCall(
                "write_file", "call-" + i, 1, Guid.NewGuid(),
                AgentToolExchangeSerializer.SerializeArguments(
                    new Dictionary<string, object?> { ["path"] = "f" + i + ".md", ["content"] = content }),
                "path=f" + i + ".md", Withheld: i > 0));
        }

        Assert.Equal(ToolApprovalStore.MaxRecordedCalls, approvals.RecordedCalls.Count);
        Assert.Equal(4, approvals.DroppedRecords);

        await _store.AppendParkedAsync(
            approvals.RecordedCalls.Select(c => RowFor(run.Id, c)).ToList(), ct);

        var rows = await _store.GetReplayableAsync(run.Id, "write_file", ct);
        Assert.Equal(ToolApprovalStore.MaxRecordedCalls, rows.Count);
        Assert.All(rows, r =>
        {
            var arguments = AgentToolExchangeSerializer.DeserializeArguments(r.ArgumentsJson);
            Assert.NotNull(arguments);
            Assert.Equal(content, ((System.Text.Json.JsonElement)arguments!["content"]!).GetString());
        });
    }

    /// <summary>Per row instead of per pass, a four-file delete approval would leave only the last file
    /// replayable and silently drop three.</summary>
    [Fact]
    public async Task SupersedeRunsOncePerPass_SoSiblingCallsOfOneToolDoNotCancelEachOther()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        string[] tools = ["delete_file"];

        await _store.SupersedeUnreplayedAsync(run.Id, tools, ct);
        await _store.AppendParkedAsync(
            [.. Enumerable.Range(0, 4).Select(i => ParkedRow(run.Id, "delete_file", $"{{\"path\":\"f{i}.md\"}}"))],
            ct);

        var first = await _store.GetReplayableAsync(run.Id, "delete_file", ct);
        Assert.Equal(4, first.Count);
        Assert.All(first, r => Assert.Null(r.SupersededAt));
        Assert.Equal(first.Select(r => r.Seq).Order().ToArray(), first.Select(r => r.Seq).ToArray());

        // The second pass is what supersede exists for: the earlier pass's rows go stale, this pass's do not.
        await _store.SupersedeUnreplayedAsync(run.Id, tools, ct);
        await _store.AppendParkedAsync(
            [.. Enumerable.Range(4, 2).Select(i => ParkedRow(run.Id, "delete_file", $"{{\"path\":\"f{i}.md\"}}"))],
            ct);

        var second = await _store.GetReplayableAsync(run.Id, "delete_file", ct);
        Assert.Equal(2, second.Count);
        Assert.Empty(second.Select(r => r.Id).Intersect(first.Select(r => r.Id)));
    }

    /// <summary>Superseding every unreplayed row of the run would be cheaper, and would drop the withheld call
    /// of another tool — the document body the reported run had to compose twice.</summary>
    [Fact]
    public async Task SupersedeIsScopedToThePassesOwnTools_SoAnotherToolsWithheldCallSurvives()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        await _store.AppendParkedAsync(
            [ParkedRow(run.Id, "write_file", "{\"path\":\"report.md\"}"),
             ParkedRow(run.Id, "create_source", "{\"path\":\"sources/panel.md\"}",
                 AgentToolExchangeKind.WithheldCall)],
            ct);

        // Two under the broad rule, and the count is the only place the two rules differ observably.
        Assert.Equal(1, await _store.SupersedeUnreplayedAsync(run.Id, ["write_file"], ct));

        Assert.Empty(await _store.GetReplayableAsync(run.Id, "write_file", ct));
        var survivor = Assert.Single(await _store.GetReplayableAsync(run.Id, "create_source", ct));
        Assert.Null(survivor.SupersededAt);
        Assert.Null(survivor.ReplayedAt);
    }

    /// <summary>The per-run cap is Kind 1/2's alone: dropping the row a human's Continue press replays would
    /// disable an approval they just gave, with nothing failing.</summary>
    [Fact]
    public async Task AParkedRowIsNeverDroppedByThePerRunCap()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();

        // One batch: RecordAsync refuses a batch that would CROSS the cap, so the fill has to land at it exactly.
        var pairs = Enumerable.Range(0, AgentToolExchangeStore.MaxRowsPerRun / 2).SelectMany(i => Round(i)).ToList();
        await _store.RecordAsync(run.Id, null, 1, pairs, ct);
        Assert.Equal(AgentToolExchangeStore.MaxRowsPerRun, CountRows(run.Id));

        // Non-vacuity: the run really is at the cap, so a Kind 1/2 round would now be refused.
        await _store.RecordAsync(run.Id, null, 2, Round(999), ct);
        Assert.Equal(AgentToolExchangeStore.MaxRowsPerRun, CountRows(run.Id));

        await _store.AppendParkedAsync([ParkedRow(run.Id, "write_file", "{\"path\":\"late.md\"}")], ct);

        Assert.Equal(AgentToolExchangeStore.MaxRowsPerRun + 1, CountRows(run.Id));
        Assert.Single(await _store.GetReplayableAsync(run.Id, "write_file", ct));
    }

    /// <summary>The structural half of at-most-once. An unconditional UPDATE returns true to both callers, so two
    /// resume dispatches would each execute the same approved call.</summary>
    [Fact]
    public async Task MarkingReplayedIsConditional_SoOnlyOneCallerEverExecutes()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        await _store.AppendParkedAsync(
            [ParkedRow(run.Id, "write_file", "{\"path\":\"a.md\"}"),
             ParkedRow(run.Id, "write_file", "{\"path\":\"b.md\"}")],
            ct);

        var rows = await _store.GetReplayableAsync(run.Id, "write_file", ct);
        Assert.Equal(2, rows.Count);

        Assert.True(await _store.TryMarkReplayedAsync(rows[0].Id, DateTime.UtcNow, ct));
        Assert.False(await _store.TryMarkReplayedAsync(rows[0].Id, DateTime.UtcNow, ct));

        // Off the calling thread on both sides: the store does its work synchronously under its own lock, so
        // awaiting the two calls in sequence would exercise no contention at all.
        var now = DateTime.UtcNow;
        var contended = await Task.WhenAll(
            Task.Run(() => _store.TryMarkReplayedAsync(rows[1].Id, now, ct), ct),
            Task.Run(() => _store.TryMarkReplayedAsync(rows[1].Id, now, ct), ct));
        Assert.Equal(1, contended.Count(won => won));

        // A claimed row leaves the set the replay iterates, so it is never offered again.
        Assert.Empty(await _store.GetReplayableAsync(run.Id, "write_file", ct));
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

    /// <summary>Mirrors the executor's own mapping, which is private to it.</summary>
    private static AgentToolExchangeRow RowFor(Guid runId, ToolApprovalStore.ParkedCall call) => new(
        Id: Guid.NewGuid(),
        RunId: runId,
        StepId: null,
        MessageSeq: 0,
        Seq: 0,
        Round: call.Round,
        Role: "assistant",
        Kind: call.Withheld ? AgentToolExchangeKind.WithheldCall : AgentToolExchangeKind.ParkedCall,
        CallId: call.CallId ?? string.Empty,
        ToolName: call.ToolName,
        PluginId: call.PluginId,
        ArgumentsJson: call.ArgumentsJson,
        ArgsOmitted: false,
        DisplayArgs: call.DisplayArgs,
        ResultKind: AgentToolExchangeResult.None,
        ResultText: null,
        Chars: call.ArgumentsJson?.Length ?? 0,
        AnchorMessageId: null,
        CreatedAt: DateTime.UtcNow,
        ReplayedAt: null,
        SupersededAt: null);

    private static AgentToolExchangeRow ParkedRow(
        Guid runId, string toolName, string argumentsJson,
        AgentToolExchangeKind kind = AgentToolExchangeKind.ParkedCall) => new(
        Id: Guid.NewGuid(),
        RunId: runId,
        StepId: null,
        MessageSeq: 0,
        Seq: 0,
        Round: 3,
        Role: "assistant",
        Kind: kind,
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
