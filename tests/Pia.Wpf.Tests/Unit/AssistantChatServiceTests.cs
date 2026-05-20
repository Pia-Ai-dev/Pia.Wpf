using System.IO;
using System.Text.Json;
using Pia.Infrastructure;
using Pia.Services;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

public class AssistantChatServiceTests : IDisposable
{
    private readonly SqliteContext _ctx;
    private readonly AssistantChatService _service;
    private readonly List<Guid> _createdIds = [];

    public AssistantChatServiceTests()
    {
        _ctx = new SqliteContext();
        _service = new AssistantChatService(_ctx);
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
        foreach (var id in _createdIds)
        {
            try { _service.DeleteAsync(id).GetAwaiter().GetResult(); }
            catch { /* best-effort cleanup */ }
        }
        _ctx.Dispose();
    }
}
