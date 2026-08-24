using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

public class AssistantChatServiceTests : IDisposable
{
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _service;
    private readonly string _tmpDir;
    private readonly List<Guid> _createdIds = [];

    public AssistantChatServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _service = new AssistantChatService(_ctx, _runs);
    }

    [Fact]
    public async Task SaveAsync_PopulatesFtsRow()
    {
        // The service writes on its own dedicated connection, so asserting through the shared handle also
        // checks cross-connection commit visibility.
        var chat = MakeChat(title: "UniqueWordABC title", body: "UniqueWordXYZ body");
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        var conn = _ctx.GetConnection();
        using var countFts = conn.CreateCommand();
        countFts.CommandText = "SELECT COUNT(*) FROM AssistantChatsFts WHERE ChatId = @Id";
        countFts.Parameters.AddWithValue("@Id", chat.Id.ToString());
        Assert.Equal(1, Convert.ToInt32(await countFts.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task SaveAsync_OnTheDedicatedConnection_IsVisibleToAReaderOnTheSharedConnection()
    {
        // Reds if the dedicated connection ever opened a different file, or if a write were left in an
        // uncommitted transaction.
        var chat = MakeChat(title: "Cross connection", body: "committed body");
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        var shared = _ctx.GetConnection();
        using var readRow = shared.CreateCommand();
        readRow.CommandText = "SELECT Title FROM AssistantChats WHERE Id = @Id";
        readRow.Parameters.AddWithValue("@Id", chat.Id.ToString());
        Assert.Equal("Cross connection", (string?)await readRow.ExecuteScalarAsync(TestContext.Current.CancellationToken));

        using var readMessages = shared.CreateCommand();
        readMessages.CommandText = "SELECT COUNT(*) FROM AssistantChatMessages WHERE ChatId = @Id";
        readMessages.Parameters.AddWithValue("@Id", chat.Id.ToString());
        Assert.Equal(1, Convert.ToInt32(await readMessages.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ChatsChangedSubscriber_MayCallBackIntoTheService_WithoutDeadlocking()
    {
        // The gate is not reentrant, so ChatsChanged must be raised strictly after it is released — under the
        // gate this test would hang.
        var chat = MakeChat(title: "Reentrant", body: "body");
        SyncAssistantChat? readBack = null;
        _service.ChatsChanged += (_, e) =>
        {
            if (e.Kind == AssistantChatChangeKind.Upserted)
                readBack = _service.GetAsync(e.Id, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        };

        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        Assert.NotNull(readBack);
        Assert.Equal(chat.Id, readBack!.Id);
    }

    [Fact]
    public async Task DeleteAllAsync_RaisesOneEventPerDeletedId_AfterTheGateIsReleased()
    {
        var a = MakeChat(title: "a", body: "a");
        var b = MakeChat(title: "b", body: "b");
        await _service.SaveAsync(a, TestContext.Current.CancellationToken);
        await _service.SaveAsync(b, TestContext.Current.CancellationToken);

        var deleted = new List<Guid>();
        _service.ChatsChanged += (_, e) =>
        {
            if (e.Kind != AssistantChatChangeKind.Deleted) return;
            deleted.Add(e.Id);
            // Re-entering under the gate would deadlock; the row is already gone, so this must return null.
            Assert.Null(_service.GetAsync(e.Id, TestContext.Current.CancellationToken).GetAwaiter().GetResult());
        };

        var ids = await _service.DeleteAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, ids.Count);
        Assert.Equal(ids.OrderBy(i => i), deleted.OrderBy(i => i));
    }

    [Fact]
    public async Task Dispose_ThenSaveAsync_NoOpsInsteadOfThrowing()
    {
        // FlowPersistenceStore.Dispose semantics: _disposed is set UNDER the gate and BEFORE the handle is
        // closed, so an in-flight headless step's persist can never reach a half-disposed connection.
        var chat = MakeChat(title: "after dispose", body: "x");

        _service.Dispose();

        await _service.SaveAsync(chat, TestContext.Current.CancellationToken); // must not throw
        Assert.Null(await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken));
        _service.Dispose(); // idempotent
    }

    [Fact]
    public async Task SetTitleAsync_ChangesOnlyTheTitle_AndLeavesTheMessageRowsUntouched()
    {
        // A title write must not carry a message payload.
        var chat = MakeChat(title: "old title", body: "the one message");
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);
        var before = await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken);

        Assert.True(await _service.SetTitleAsync(chat.Id, "new title", TestContext.Current.CancellationToken));

        var after = await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken);
        Assert.Equal("new title", after!.Title);
        Assert.Equal(before!.Messages.Select(m => m.Id), after.Messages.Select(m => m.Id));
        Assert.Equal(before.Messages.Select(m => m.Content), after.Messages.Select(m => m.Content));
        Assert.True(after.UpdatedAt >= before.UpdatedAt);
    }

    [Fact]
    public async Task SetTitleAsync_PreservesMessagesAppendedByAnotherWriter()
    {
        // The rename's snapshot is taken before the title LLM call, so by the time it writes a headless step
        // may have appended rows — a title-only write cannot delete them.
        var chat = MakeChat(title: "old title", body: "first");
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        // "Another writer" appends a second message via a full replace.
        var appended = new SyncAssistantChatMessage
        {
            Id = Guid.NewGuid(),
            Role = "assistant",
            Content = "written by the headless run",
            Timestamp = DateTime.UtcNow,
        };
        var grown = await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken);
        grown!.Messages.Add(appended);
        await _service.SaveAsync(grown, TestContext.Current.CancellationToken);

        Assert.True(await _service.SetTitleAsync(chat.Id, "llm title", TestContext.Current.CancellationToken));

        var after = await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken);
        Assert.Equal("llm title", after!.Title);
        Assert.Equal(2, after.Messages.Count);
        Assert.Contains(after.Messages, m => m.Id == appended.Id);
    }

    [Fact]
    public async Task SetTitleAsync_RefreshesTheFtsRow_SoSearchMatchesTheNewTitleAndStillTheBody()
    {
        var chat = MakeChat(title: "StaleTitleWordABC", body: "BodyWordXYZ stays searchable");
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        await _service.SetTitleAsync(chat.Id, "FreshTitleWordDEF", TestContext.Current.CancellationToken);

        var byNewTitle = await _service.SearchAsync(searchText: "freshtitleworddef", ct: TestContext.Current.CancellationToken);
        Assert.Contains(byNewTitle, c => c.Id == chat.Id);
        var byOldTitle = await _service.SearchAsync(searchText: "staletitlewordabc", ct: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(byOldTitle, c => c.Id == chat.Id);
        // The body half of the FTS row must survive the refresh — it is re-derived from the message rows.
        var byBody = await _service.SearchAsync(searchText: "bodywordxyz", ct: TestContext.Current.CancellationToken);
        Assert.Contains(byBody, c => c.Id == chat.Id);
    }

    [Fact]
    public async Task SetTitleAsync_MissingChat_ReturnsFalse_AndRaisesNoEvent()
    {
        var raised = new List<AssistantChatChangedEventArgs>();
        _service.ChatsChanged += (_, e) => raised.Add(e);

        Assert.False(await _service.SetTitleAsync(Guid.NewGuid(), "orphan", TestContext.Current.CancellationToken));

        Assert.Empty(raised);
    }

    [Fact]
    public async Task SetTitleAsync_RaisesUpsertedAfterTheGateIsReleased()
    {
        var chat = MakeChat(title: "before", body: "b");
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        string? titleSeenBySubscriber = null;
        _service.ChatsChanged += (_, e) =>
        {
            // Re-entering the service from the raising thread would deadlock if the gate were still held.
            if (e.Kind == AssistantChatChangeKind.Upserted && e.Id == chat.Id)
                titleSeenBySubscriber = _service.GetAsync(e.Id, TestContext.Current.CancellationToken)
                    .GetAwaiter().GetResult()?.Title;
        };

        await _service.SetTitleAsync(chat.Id, "after", TestContext.Current.CancellationToken);

        Assert.Equal("after", titleSeenBySubscriber);
    }

    [Fact]
    public async Task SearchAsync_FindsByTitleAndBody_FullToken()
    {
        var chat = MakeChat(title: "Lunch options today", body: "Should we get pizza?");
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        var byTitle = await _service.SearchAsync(searchText: "lunch", ct: TestContext.Current.CancellationToken);
        Assert.Contains(byTitle, c => c.Id == chat.Id);

        var byBody = await _service.SearchAsync(searchText: "pizza", ct: TestContext.Current.CancellationToken);
        Assert.Contains(byBody, c => c.Id == chat.Id);
    }

    [Fact]
    public async Task SearchAsync_ExcludesMessageLessStubChats()
    {
        // Real chat with messages — should appear.
        var real = MakeChat(title: "Real chat", body: "has content");
        await _service.SaveAsync(real, TestContext.Current.CancellationToken);
        _createdIds.Add(real.Id);

        // Message-less stub, as left by a failed/empty headless turn — should be hidden.
        var now = DateTime.UtcNow;
        var stub = new SyncAssistantChat
        {
            Id = Guid.NewGuid(),
            SchemaVersion = 1,
            Title = "Stub chat",
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = "Assistant",
            Messages = [],
        };
        await _service.SaveAsync(stub, TestContext.Current.CancellationToken);
        _createdIds.Add(stub.Id);

        var all = await _service.SearchAsync(ct: TestContext.Current.CancellationToken);
        Assert.Contains(all, c => c.Id == real.Id);
        Assert.DoesNotContain(all, c => c.Id == stub.Id);
    }

    [Fact]
    public async Task SearchAsync_FindsByPrefix_PartialToken()
    {
        var chat = MakeChat(title: "Microservices design", body: "Discussion of Kubernetes");
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        var byTitlePrefix = await _service.SearchAsync(searchText: "micro", ct: TestContext.Current.CancellationToken);
        Assert.Contains(byTitlePrefix, c => c.Id == chat.Id);

        var byBodyPrefix = await _service.SearchAsync(searchText: "kuber", ct: TestContext.Current.CancellationToken);
        Assert.Contains(byBodyPrefix, c => c.Id == chat.Id);
    }

    [Fact]
    public async Task SearchAsync_FindsAcrossMultipleTokens()
    {
        var chat = MakeChat(title: "Project Phoenix kickoff", body: "Discussing the roadmap and milestones");
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);

        var hits = await _service.SearchAsync(searchText: "phoenix roadmap", ct: TestContext.Current.CancellationToken);
        Assert.Contains(hits, c => c.Id == chat.Id);
    }

    [Fact]
    public async Task SearchAsync_OperatorChars_AreSafe()
    {
        var chat = MakeChat(title: "Test", body: "Hello world");
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);

        // Should not throw on FTS5 operator chars.
        var exception = await Record.ExceptionAsync(
            () => _service.SearchAsync(searchText: "hello* OR \"NEAR(\"", ct: TestContext.Current.CancellationToken));
        Assert.Null(exception);
    }

    [Fact]
    public async Task SaveFromRemoteAsync_DoesNotRaiseChatsChanged()
    {
        var raised = new List<AssistantChatChangedEventArgs>();
        _service.ChatsChanged += (_, e) => raised.Add(e);

        var chat = MakeChat(title: "Remote chat", body: "remote body");
        await _service.SaveFromRemoteAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        Assert.Empty(raised);

        // Verify the row is actually written so the suppression isn't masking a real bug.
        var loaded = await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal("Remote chat", loaded!.Title);
    }

    [Fact]
    public async Task DeleteFromRemoteAsync_DoesNotRaiseChatsChanged()
    {
        var chat = MakeChat(title: "to-delete", body: "x");
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);

        var raised = new List<AssistantChatChangedEventArgs>();
        _service.ChatsChanged += (_, e) => raised.Add(e);

        await _service.DeleteFromRemoteAsync(chat.Id, TestContext.Current.CancellationToken);

        Assert.Empty(raised);
        Assert.Null(await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteFromRemoteAsync_RemovesFromFts()
    {
        var chat = MakeChat(title: "UniqueRemoteWordABC", body: "UniqueRemoteWordXYZ");
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);

        await _service.DeleteFromRemoteAsync(chat.Id, TestContext.Current.CancellationToken);

        var conn = _ctx.GetConnection();
        using var countFts = conn.CreateCommand();
        countFts.CommandText = "SELECT COUNT(*) FROM AssistantChatsFts WHERE ChatId = @Id";
        countFts.Parameters.AddWithValue("@Id", chat.Id.ToString());
        Assert.Equal(0, Convert.ToInt32(await countFts.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ExtensionData_SurvivesRoundTrip()
    {
        var chat = MakeChat(title: "Forward-compat chat", body: "anything");
        var futureField = JsonSerializer.Deserialize<JsonElement>("\"server-only value\"");
        chat.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["serverOnlyFutureField"] = futureField,
        };

        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        var loaded = await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.ExtensionData);
        Assert.True(loaded.ExtensionData!.TryGetValue("serverOnlyFutureField", out var value));
        Assert.Equal("server-only value", value.GetString());
    }

    [Fact]
    public async Task SaveAndGet_RoundTripsPersonaSnapshot()
    {
        var chat = MakeChat(title: "Persona test", body: "user question");
        var personaId = Guid.NewGuid();
        chat.Messages.Add(new SyncAssistantChatMessage
        {
            Id = Guid.NewGuid(),
            Role = "assistant",
            Content = "assistant answer",
            Timestamp = DateTime.UtcNow,
            Tokens = 10,
            ModelName = "gpt-5",
            Persona = new SyncMessagePersona { Id = personaId, Name = "Marketing Writer", Emoji = "✍️" },
        });

        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        var loaded = await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        var assistant = loaded!.Messages.Single(m => m.Role == "assistant");
        Assert.NotNull(assistant.Persona);
        Assert.Equal(personaId, assistant.Persona!.Id);
        Assert.Equal("Marketing Writer", assistant.Persona.Name);
        Assert.Equal("✍️", assistant.Persona.Emoji);

        var user = loaded.Messages.Single(m => m.Role == "user");
        Assert.Null(user.Persona);
    }

    [Fact]
    public async Task SaveAndGet_RoundTripsWorkingDirectory()
    {
        var chat = MakeChat(title: "WorkingDir test", body: "x");
        chat.WorkingDirectory = "projects/app";

        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        var loaded = await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal("projects/app", loaded!.WorkingDirectory);
    }

    [Fact]
    public async Task SaveAndGet_NullWorkingDirectory_RoundTripsAsNull()
    {
        var chat = MakeChat(title: "Root chat", body: "x");
        chat.WorkingDirectory = null;

        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        var loaded = await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Null(loaded!.WorkingDirectory);
    }

    [Fact]
    public async Task ReSave_RepointsWorkingDirectory_ViaOnConflictUpdate()
    {
        // Guards the headline "re-point mid-chat" feature: re-saving the SAME Id with a changed
        // WorkingDirectory must persist through the ON CONFLICT(Id) DO UPDATE SET path.
        var chat = MakeChat(title: "Repoint test", body: "x");
        chat.WorkingDirectory = "first";
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        chat.WorkingDirectory = "second/dir";
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);

        var loaded = await _service.GetAsync(chat.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal("second/dir", loaded!.WorkingDirectory);
    }

    [Fact]
    public async Task SearchAsync_ReadsWorkingDirectory()
    {
        // The list/Search read path is a separate SELECT feeding MapChat; verify its ordinals
        // map WorkingDirectory correctly too.
        var chat = MakeChat(title: "SearchableWorkingDir", body: "body");
        chat.WorkingDirectory = "via/search";
        await _service.SaveAsync(chat, TestContext.Current.CancellationToken);
        _createdIds.Add(chat.Id);

        var hits = await _service.SearchAsync(searchText: "SearchableWorkingDir", ct: TestContext.Current.CancellationToken);
        var found = Assert.Single(hits, c => c.Id == chat.Id);
        Assert.Equal("via/search", found.WorkingDirectory);
    }

    [Fact]
    public async Task GetProviderIdAsync_RoundTripsTheRowsProvider_AndAnswersNullForAnAbsentOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var providerId = Guid.NewGuid();

        var withProvider = MakeChat(title: "pinned", body: "b");
        withProvider.ProviderId = providerId;
        await _service.SaveAsync(withProvider, ct);
        _createdIds.Add(withProvider.Id);

        var withoutProvider = MakeChat(title: "unpinned", body: "b");
        await _service.SaveAsync(withoutProvider, ct);
        _createdIds.Add(withoutProvider.Id);

        Assert.Equal(providerId, await _service.GetProviderIdAsync(withProvider.Id, ct));
        Assert.Null(await _service.GetProviderIdAsync(withoutProvider.Id, ct));
        // A deleted/evicted chat is the resume path's real case, and it must read as "no pin" rather than throw.
        Assert.Null(await _service.GetProviderIdAsync(Guid.NewGuid(), ct));
    }

    private static SyncAssistantChat MakeChat(string title, string body)
    {
        var now = DateTime.UtcNow;
        var chatId = Guid.NewGuid();
        return new SyncAssistantChat
        {
            Id = chatId,
            SchemaVersion = 1,
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = "Assistant",
            ProviderId = null,
            Messages =
            [
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "user",
                    Content = body,
                    Timestamp = now,
                },
            ],
        };
    }

    public void Dispose()
    {
        // The chat service and the run service each own a dedicated connection to the same file, so both must
        // be closed before the delete or Windows keeps the temp file locked.
        _service.Dispose();
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }
}
