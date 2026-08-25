using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The vault and chat-history read tools report what they put in front of the model through the ambient
/// sink, the twin of the file-touch sink that drives the open-file chips. A turn with no sink wired (a
/// direct test caller, a headless run) must still answer normally.
/// </summary>
public sealed class SourceCitationSinkTests : IDisposable
{
    private readonly List<SourceCitation> _cited = [];
    private readonly CultureInfo _originalCulture = Thread.CurrentThread.CurrentCulture;

    public SourceCitationSinkTests()
    {
        // A chip date follows the app culture; pin one so the expected format is not the runner locale.
        Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        Wire(collect: true);
    }

    public void Dispose()
    {
        Thread.CurrentThread.CurrentCulture = _originalCulture;
        TaskAmbient.Current = null;
    }

    private void Wire(bool collect, Guid? chatId = null) =>
        TaskAmbient.Current = new TaskContext(
            TaskId: Guid.NewGuid(),
            WorkingSubpath: null,
            ChatId: chatId,
            OnSourceCited: collect ? _cited.Add : null);

    // ---- vault ----------------------------------------------------------------------------------

    private readonly IMemoryService _memory = Substitute.For<IMemoryService>();

    private MemoryToolHandler MemoryHandler() =>
        new(_memory,
            Substitute.For<IEmbeddingService>(),
            Substitute.For<ILocalizationService>(),
            Substitute.For<IIngestScheduler>(),
            NullLogger<MemoryToolHandler>.Instance);

    private Task<(object? Result, MemoryToolCall? PendingAction)> MemoryCallAsync(
        string tool, Dictionary<string, object?> args) =>
        MemoryHandler().HandleToolCallAsync(
            new FunctionCallContent("call-1", tool, args), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Recall_CitesEachHit_WithoutTheMemoryPrefix()
    {
        _memory.RecallAsync("coffee").Returns([
            new RecallHit("memory/topics/coffee.md", "Espresso", "snippet", 0.9f),
            new RecallHit("memory/contacts.md#John Smith", "John Smith", "snippet", 0.8f),
        ]);

        await MemoryCallAsync("recall", new Dictionary<string, object?> { ["query"] = "coffee" });

        Assert.Equal(
            ["topics/coffee", "contacts#John Smith"],
            _cited.Select(c => c.Target).ToArray());
        Assert.All(_cited, c => Assert.Equal(SourceCitationKind.VaultPage, c.Kind));
        Assert.Equal(["coffee", "contacts"], _cited.Select(c => c.Label).ToArray());
        Assert.Equal(["Espresso", "John Smith"], _cited.Select(c => c.Meta).ToArray());
    }

    [Fact]
    public async Task Recall_StopsAtFiveHits()
    {
        _memory.RecallAsync("everything").Returns(
            Enumerable.Range(0, 9)
                .Select(i => new RecallHit($"memory/topics/p{i}.md", $"h{i}", "snippet", 1f - (i / 10f)))
                .ToList());

        await MemoryCallAsync("recall", new Dictionary<string, object?> { ["query"] = "everything" });

        Assert.Equal(5, _cited.Count);
        Assert.Equal("topics/p0", _cited[0].Target);
        Assert.Equal("topics/p4", _cited[4].Target);
    }

    [Fact]
    public async Task ReadTopic_CitesTheTopicTitle()
    {
        _memory.ReadTopicAsync("memory/topics/coffee.md").Returns(
            new TopicRead(true, "memory/topics/coffee.md", "Coffee preferences", "body", [], [], null));

        await MemoryCallAsync(
            "read_topic", new Dictionary<string, object?> { ["reference"] = "memory/topics/coffee.md" });

        var cited = Assert.Single(_cited);
        Assert.Equal("topics/coffee", cited.Target);
        Assert.Equal("Coffee preferences", cited.Label);
    }

    [Fact]
    public async Task ReadTopic_CitesNothingWhenTheRefWasRejected()
    {
        _memory.ReadTopicAsync("../escape").Returns(
            new TopicRead(false, "../escape", string.Empty, string.Empty, [], [], "outside the vault"));

        await MemoryCallAsync("read_topic", new Dictionary<string, object?> { ["reference"] = "../escape" });

        Assert.Empty(_cited);
    }

    [Fact]
    public async Task Recall_WithNoSinkWired_StillReturnsItsHits()
    {
        Wire(collect: false);
        _memory.RecallAsync("coffee").Returns([new RecallHit("memory/topics/coffee.md", "h", "s", 0.9f)]);

        var (result, _) = await MemoryCallAsync(
            "recall", new Dictionary<string, object?> { ["query"] = "coffee" });

        Assert.Single(Assert.IsType<RecallResult>(result).Hits);
        Assert.Empty(_cited);
    }

    // ---- chat history ---------------------------------------------------------------------------

    private readonly IAssistantChatService _chats = Substitute.For<IAssistantChatService>();

    private ChatHistoryToolHandler ChatHandler()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantChatHistoryToolsEnabled = true });
        return new ChatHistoryToolHandler(_chats, settings, NullLogger<ChatHistoryToolHandler>.Instance);
    }

    private Task<object?> ChatCallAsync(string tool, Dictionary<string, object?> args) =>
        ChatHandler().HandleToolCallAsync(
            new FunctionCallContent("call-1", tool, args), TestContext.Current.CancellationToken);

    private SyncAssistantChat StoredChat(Guid id, string? title)
    {
        var when = new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc);
        var chat = new SyncAssistantChat { Id = id, Title = title, CreatedAt = when, UpdatedAt = when };
        chat.Messages.Add(new SyncAssistantChatMessage
        {
            Id = Guid.NewGuid(), Role = "user", Content = "hi", Timestamp = when,
        });
        _chats.GetAsync(id, Arg.Any<CancellationToken>()).Returns(chat);
        return chat;
    }

    [Fact]
    public async Task ReadChat_CitesTheChatItRead()
    {
        var id = Guid.NewGuid();
        StoredChat(id, "Hetzner migration");

        await ChatCallAsync("read_chat", new Dictionary<string, object?> { ["chat_id"] = id.ToString() });

        var cited = Assert.Single(_cited);
        Assert.Equal(SourceCitationKind.Chat, cited.Kind);
        Assert.Equal(id.ToString(), cited.Target);
        Assert.Equal("Hetzner migration", cited.Label);
        Assert.Equal(
            new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc).ToLocalTime().ToString("d", new CultureInfo("de-DE")),
            cited.Meta);
    }

    [Fact]
    public async Task ReadChat_CitesNothingForTheCurrentConversation()
    {
        var id = Guid.NewGuid();
        Wire(collect: true, chatId: id);
        StoredChat(id, "This one");

        await ChatCallAsync("read_chat", new Dictionary<string, object?> { ["chat_id"] = id.ToString() });

        Assert.Empty(_cited);
    }

    [Fact]
    public async Task SearchChats_CitesNothing_BecauseASnippetIsNotAGrounding()
    {
        var when = new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc);
        _chats.SearchRankedAsync(
                Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new AssistantChatSearchHit(Guid.NewGuid(), "Hetzner migration", when, null, "snippet", 7)]);

        await ChatCallAsync("search_chats", new Dictionary<string, object?> { ["query"] = "hetzner" });

        Assert.Empty(_cited);
    }
}
