using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

public class AssistantChatServiceTests : IDisposable
{
    private readonly SqliteContext _ctx;
    private readonly AssistantChatService _service;
    private readonly string _tmpDir;
    private readonly List<Guid> _createdIds = [];

    public AssistantChatServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _service = new AssistantChatService(_ctx, new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance));
    }

    [Fact]
    public async Task SaveAsync_PopulatesFtsRow()
    {
        var chat = MakeChat(title: "UniqueWordABC title", body: "UniqueWordXYZ body");
        await _service.SaveAsync(chat);
        _createdIds.Add(chat.Id);

        var conn = _ctx.GetConnection();
        using var countFts = conn.CreateCommand();
        countFts.CommandText = "SELECT COUNT(*) FROM AssistantChatsFts WHERE ChatId = @Id";
        countFts.Parameters.AddWithValue("@Id", chat.Id.ToString());
        Assert.Equal(1, Convert.ToInt32(await countFts.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task SearchAsync_FindsByTitleAndBody_FullToken()
    {
        var chat = MakeChat(title: "Lunch options today", body: "Should we get pizza?");
        await _service.SaveAsync(chat);
        _createdIds.Add(chat.Id);

        var byTitle = await _service.SearchAsync(searchText: "lunch");
        Assert.Contains(byTitle, c => c.Id == chat.Id);

        var byBody = await _service.SearchAsync(searchText: "pizza");
        Assert.Contains(byBody, c => c.Id == chat.Id);
    }

    [Fact]
    public async Task SearchAsync_ExcludesMessageLessStubChats()
    {
        // Real chat with messages — should appear.
        var real = MakeChat(title: "Real chat", body: "has content");
        await _service.SaveAsync(real, TestContext.Current.CancellationToken);
        _createdIds.Add(real.Id);

        // Message-less stub (as left by a failed/empty headless turn per §16 R1) — should be hidden.
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
        await _service.SaveAsync(chat);
        _createdIds.Add(chat.Id);

        var byTitlePrefix = await _service.SearchAsync(searchText: "micro");
        Assert.Contains(byTitlePrefix, c => c.Id == chat.Id);

        var byBodyPrefix = await _service.SearchAsync(searchText: "kuber");
        Assert.Contains(byBodyPrefix, c => c.Id == chat.Id);
    }

    [Fact]
    public async Task SearchAsync_FindsAcrossMultipleTokens()
    {
        var chat = MakeChat(title: "Project Phoenix kickoff", body: "Discussing the roadmap and milestones");
        await _service.SaveAsync(chat);

        var hits = await _service.SearchAsync(searchText: "phoenix roadmap");
        Assert.Contains(hits, c => c.Id == chat.Id);
    }

    [Fact]
    public async Task SearchAsync_OperatorChars_AreSafe()
    {
        var chat = MakeChat(title: "Test", body: "Hello world");
        await _service.SaveAsync(chat);

        // Should not throw on FTS5 operator chars.
        var exception = await Record.ExceptionAsync(
            () => _service.SearchAsync(searchText: "hello* OR \"NEAR(\""));
        Assert.Null(exception);
    }

    [Fact]
    public async Task SaveFromRemoteAsync_DoesNotRaiseChatsChanged()
    {
        var raised = new List<AssistantChatChangedEventArgs>();
        _service.ChatsChanged += (_, e) => raised.Add(e);

        var chat = MakeChat(title: "Remote chat", body: "remote body");
        await _service.SaveFromRemoteAsync(chat);
        _createdIds.Add(chat.Id);

        Assert.Empty(raised);

        // Verify the row is actually written so the suppression isn't masking a real bug.
        var loaded = await _service.GetAsync(chat.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Remote chat", loaded!.Title);
    }

    [Fact]
    public async Task DeleteFromRemoteAsync_DoesNotRaiseChatsChanged()
    {
        var chat = MakeChat(title: "to-delete", body: "x");
        await _service.SaveAsync(chat);

        var raised = new List<AssistantChatChangedEventArgs>();
        _service.ChatsChanged += (_, e) => raised.Add(e);

        await _service.DeleteFromRemoteAsync(chat.Id);

        Assert.Empty(raised);
        Assert.Null(await _service.GetAsync(chat.Id));
    }

    [Fact]
    public async Task DeleteFromRemoteAsync_RemovesFromFts()
    {
        var chat = MakeChat(title: "UniqueRemoteWordABC", body: "UniqueRemoteWordXYZ");
        await _service.SaveAsync(chat);

        await _service.DeleteFromRemoteAsync(chat.Id);

        var conn = _ctx.GetConnection();
        using var countFts = conn.CreateCommand();
        countFts.CommandText = "SELECT COUNT(*) FROM AssistantChatsFts WHERE ChatId = @Id";
        countFts.Parameters.AddWithValue("@Id", chat.Id.ToString());
        Assert.Equal(0, Convert.ToInt32(await countFts.ExecuteScalarAsync()));
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

        await _service.SaveAsync(chat);
        _createdIds.Add(chat.Id);

        var loaded = await _service.GetAsync(chat.Id);
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

        await _service.SaveAsync(chat);
        _createdIds.Add(chat.Id);

        var loaded = await _service.GetAsync(chat.Id);
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

        await _service.SaveAsync(chat);
        _createdIds.Add(chat.Id);

        var loaded = await _service.GetAsync(chat.Id);
        Assert.NotNull(loaded);
        Assert.Equal("projects/app", loaded!.WorkingDirectory);
    }

    [Fact]
    public async Task SaveAndGet_NullWorkingDirectory_RoundTripsAsNull()
    {
        var chat = MakeChat(title: "Root chat", body: "x");
        chat.WorkingDirectory = null;

        await _service.SaveAsync(chat);
        _createdIds.Add(chat.Id);

        var loaded = await _service.GetAsync(chat.Id);
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
        await _service.SaveAsync(chat);
        _createdIds.Add(chat.Id);

        chat.WorkingDirectory = "second/dir";
        await _service.SaveAsync(chat);

        var loaded = await _service.GetAsync(chat.Id);
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
        await _service.SaveAsync(chat);
        _createdIds.Add(chat.Id);

        var hits = await _service.SearchAsync(searchText: "SearchableWorkingDir");
        var found = Assert.Single(hits, c => c.Id == chat.Id);
        Assert.Equal("via/search", found.WorkingDirectory);
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
        // The whole database lives under _tmpDir, so disposing the connection and
        // deleting the directory discards everything this fixture created.
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }
}
