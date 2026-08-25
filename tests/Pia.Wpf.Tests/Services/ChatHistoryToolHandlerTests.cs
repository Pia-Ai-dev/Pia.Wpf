using System.Text.Json;
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
/// The chat-history pack: the current conversation is excluded from BOTH tools, a null ambient chat id
/// excludes nothing, the model's caps are clamped in both directions, paging reports itself, and thinking
/// content never reaches the wire. Assertions are made against the SERIALIZED result, because that JSON —
/// not the private result record — is what the provider receives.
/// </summary>
public sealed class ChatHistoryToolHandlerTests : IDisposable
{
    private readonly IAssistantChatService _chats = Substitute.For<IAssistantChatService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    public ChatHistoryToolHandlerTests()
    {
        TaskAmbient.Current = null;
        _settings.GetSettingsAsync().Returns(new AppSettings { AssistantChatHistoryToolsEnabled = true });
    }

    public void Dispose() => TaskAmbient.Current = null;

    private ChatHistoryToolHandler Handler() =>
        new(_chats, _settings, NullLogger<ChatHistoryToolHandler>.Instance);

    private Task<object?> CallAsync(string tool, Dictionary<string, object?> args) =>
        Handler().HandleToolCallAsync(
            new FunctionCallContent("call-1", tool, args), TestContext.Current.CancellationToken);

    private static JsonElement Json(object? result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result)).RootElement.Clone();

    private static SyncAssistantChat Chat(Guid id, string title, DateTime? updatedAt = null)
    {
        var when = updatedAt ?? new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc);
        return new SyncAssistantChat { Id = id, Title = title, CreatedAt = when, UpdatedAt = when };
    }

    private static SyncAssistantChat ChatWithMessages(Guid id, int count, string? thinking = null)
    {
        var chat = Chat(id, "Transcript");
        for (var i = 0; i < count; i++)
        {
            chat.Messages.Add(new SyncAssistantChatMessage
            {
                Id = Guid.NewGuid(),
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"message-{i}",
                ThinkingContent = thinking,
                Timestamp = new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc).AddMinutes(i),
            });
        }
        return chat;
    }

    private void StubRanked(params AssistantChatSearchHit[] hits) =>
        _chats.SearchRankedAsync(
                Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(hits);

    private void StubRecent(params SyncAssistantChat[] chats) =>
        _chats.SearchAsync(
                Arg.Any<string?>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(chats);

    private static AssistantChatSearchHit Hit(Guid id, string title = "Hit", string snippet = "excerpt") =>
        new(id, title, new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc), null, snippet, 7);

    // ---- availability -------------------------------------------------------------------------

    [Fact]
    public void GetTools_exposes_both_tools_when_enabled()
    {
        var names = Handler().GetTools().Select(t => t.Name).ToList();
        Assert.Equal(new[] { "search_chats", "read_chat" }, names);
    }

    [Fact]
    public async Task An_unrecognised_tool_name_is_reported_rather_than_thrown()
    {
        var json = Json(await CallAsync("delete_chat", new Dictionary<string, object?>()));
        Assert.Equal("Unknown tool: delete_chat", json.GetString());
    }

    [Fact]
    public void GetTools_carries_the_schema_descriptions()
    {
        // The 2-argument AIFunctionFactory.Create form leaves the description to the [Description]
        // attribute; a 3-argument call would silently win over it and ship a different string.
        var tools = Handler().GetTools().ToDictionary(t => t.Name, t => t.Description);
        Assert.Equal(
            "Search past conversations with this assistant (not the current one) by keyword and date",
            tools["search_chats"]);
        Assert.Equal("Read a past conversation's messages by id, from a search_chats hit", tools["read_chat"]);
    }

    [Fact]
    public void GetTools_is_empty_when_the_setting_is_off()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings { AssistantChatHistoryToolsEnabled = false });
        var handler = Handler();
        Assert.False(handler.IsAvailable);
        Assert.Empty(handler.GetTools());
    }

    [Fact]
    public void Turning_the_setting_off_withdraws_the_tools_without_a_restart()
    {
        var handler = Handler();
        Assert.True(handler.IsAvailable);

        _settings.SettingsChanged += Raise.Event<EventHandler<AppSettings>>(
            _settings, new AppSettings { AssistantChatHistoryToolsEnabled = false });

        Assert.False(handler.IsAvailable);
        Assert.Empty(handler.GetTools());
    }

    // ---- current-chat exclusion ---------------------------------------------------------------

    [Fact]
    public async Task SearchChats_passes_the_current_chat_as_the_exclusion()
    {
        // The substitute returns whatever it was told to, so asserting on the OUTPUT would pass even if
        // excludeChatId were never plumbed through. The argument is the assertion.
        var current = Guid.NewGuid();
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), null, ChatId: current);
        StubRanked(Hit(Guid.NewGuid()));

        await CallAsync("search_chats", new Dictionary<string, object?> { ["query"] = "hetzner" });

        await _chats.Received(1).SearchRankedAsync(
            "hetzner", Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
            Arg.Is<Guid?>(g => g == current), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchChats_recency_path_drops_the_current_chat_from_the_hits()
    {
        // SearchAsync has no exclusion parameter, so here the handler filters and the OUTPUT is the proof.
        var current = Guid.NewGuid();
        var other = Guid.NewGuid();
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), null, ChatId: current);
        StubRecent(Chat(current, "The one we are in"), Chat(other, "An older one"));

        var json = Json(await CallAsync("search_chats", new Dictionary<string, object?>()));

        var ids = json.GetProperty("chats").EnumerateArray()
            .Select(h => h.GetProperty("chat_id").GetString()).ToList();
        Assert.Equal(new[] { other.ToString() }, ids);
    }

    [Fact]
    public async Task SearchChats_recency_path_overfetches_so_the_exclusion_cannot_shorten_the_page()
    {
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), null, ChatId: Guid.NewGuid());
        StubRecent();

        await CallAsync("search_chats", new Dictionary<string, object?> { ["limit"] = 10 });

        await _chats.Received(1).SearchAsync(
            Arg.Any<string?>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
            Arg.Any<int>(), 11, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadChat_refuses_the_current_chat_without_reading_it()
    {
        var current = Guid.NewGuid();
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), null, ChatId: current);

        var json = Json(await CallAsync(
            "read_chat", new Dictionary<string, object?> { ["chat_id"] = current.ToString() }));

        Assert.Equal("That is the current conversation; it is already in front of you.", json.GetString());
        await _chats.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadChat_reads_any_chat_that_is_not_the_current_one()
    {
        var other = Guid.NewGuid();
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), null, ChatId: Guid.NewGuid());
        _chats.GetAsync(other, Arg.Any<CancellationToken>()).Returns(ChatWithMessages(other, 2));

        var json = Json(await CallAsync(
            "read_chat", new Dictionary<string, object?> { ["chat_id"] = other.ToString() }));

        Assert.Equal(2, json.GetProperty("messages").GetArrayLength());
    }

    // ---- fail open ----------------------------------------------------------------------------

    [Fact]
    public async Task SearchChats_excludes_nothing_when_there_is_no_ambient_turn()
    {
        TaskAmbient.Current = null;
        StubRanked();

        await CallAsync("search_chats", new Dictionary<string, object?> { ["query"] = "hetzner" });

        await _chats.Received(1).SearchRankedAsync(
            Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
            Arg.Is<Guid?>(g => g == null), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchChats_excludes_nothing_when_the_ambient_carries_no_chat_id()
    {
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), null);
        StubRanked();
        StubRecent(Chat(Guid.NewGuid(), "Anything"));

        await CallAsync("search_chats", new Dictionary<string, object?> { ["query"] = "hetzner" });
        await _chats.Received(1).SearchRankedAsync(
            Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
            Arg.Is<Guid?>(g => g == null), Arg.Any<int>(), Arg.Any<CancellationToken>());

        var json = Json(await CallAsync("search_chats", new Dictionary<string, object?>()));
        Assert.Equal(1, json.GetProperty("chats").GetArrayLength());
    }

    [Fact]
    public async Task ReadChat_reads_by_id_when_the_ambient_carries_no_chat_id()
    {
        var id = Guid.NewGuid();
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), null);
        _chats.GetAsync(id, Arg.Any<CancellationToken>()).Returns(ChatWithMessages(id, 1));

        var json = Json(await CallAsync(
            "read_chat", new Dictionary<string, object?> { ["chat_id"] = id.ToString() }));

        Assert.Equal(1, json.GetProperty("messages").GetArrayLength());
    }

    // ---- caps ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, 10)]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(7, 7)]
    [InlineData(25, 25)]
    [InlineData(500, 25)]
    public async Task SearchChats_clamps_the_limit_in_both_directions(int? requested, int expected)
    {
        StubRanked();
        var args = new Dictionary<string, object?> { ["query"] = "hetzner" };
        if (requested is not null) args["limit"] = requested;

        await CallAsync("search_chats", args);

        await _chats.Received(1).SearchRankedAsync(
            Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
            Arg.Any<Guid?>(), expected, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, 40)]
    [InlineData(0, 40)]
    [InlineData(-5, 40)]
    [InlineData(3, 3)]
    [InlineData(100, 100)]
    [InlineData(9999, 100)]
    public async Task ReadChat_clamps_the_limit_in_both_directions(int? requested, int expected)
    {
        var id = Guid.NewGuid();
        _chats.GetAsync(id, Arg.Any<CancellationToken>()).Returns(ChatWithMessages(id, 250));

        var args = new Dictionary<string, object?> { ["chat_id"] = id.ToString() };
        if (requested is not null) args["limit"] = requested;

        var json = Json(await CallAsync("read_chat", args));

        Assert.Equal(expected, json.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task ReadChat_truncates_a_long_message_body()
    {
        var id = Guid.NewGuid();
        var chat = Chat(id, "Long");
        chat.Messages.Add(new SyncAssistantChatMessage { Role = "user", Content = new string('x', 4000) });
        _chats.GetAsync(id, Arg.Any<CancellationToken>()).Returns(chat);

        var json = Json(await CallAsync(
            "read_chat", new Dictionary<string, object?> { ["chat_id"] = id.ToString() }));

        var content = json.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.NotNull(content);
        Assert.EndsWith("…[truncated]", content);
        Assert.Equal(1500 + "…[truncated]".Length, content.Length);
    }

    [Fact]
    public async Task ReadChat_does_not_cut_a_surrogate_pair_in_half()
    {
        var id = Guid.NewGuid();
        var chat = Chat(id, "Emoji");
        chat.Messages.Add(new SyncAssistantChatMessage
        {
            Role = "user",
            Content = new string('x', 1499) + "\U0001F600" + new string('y', 1000),
        });
        _chats.GetAsync(id, Arg.Any<CancellationToken>()).Returns(chat);

        var json = Json(await CallAsync(
            "read_chat", new Dictionary<string, object?> { ["chat_id"] = id.ToString() }));

        var content = json.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.NotNull(content);
        Assert.Equal(new string('x', 1499) + "…[truncated]", content);
        Assert.DoesNotContain(content.ToCharArray(), char.IsSurrogate);
    }

    // ---- paging -------------------------------------------------------------------------------

    [Fact]
    public async Task ReadChat_pages_a_transcript_to_its_last_message()
    {
        var id = Guid.NewGuid();
        _chats.GetAsync(id, Arg.Any<CancellationToken>()).Returns(_ => ChatWithMessages(id, 5));

        var first = Json(await CallAsync("read_chat", new Dictionary<string, object?>
        {
            ["chat_id"] = id.ToString(),
            ["limit"] = 2,
        }));
        Assert.Equal(5, first.GetProperty("message_count").GetInt32());
        Assert.True(first.GetProperty("has_more").GetBoolean());
        Assert.Equal(2, first.GetProperty("next_offset").GetInt32());
        Assert.Equal(0, first.GetProperty("messages")[0].GetProperty("index").GetInt32());
        Assert.Equal("message-1", first.GetProperty("messages")[1].GetProperty("content").GetString());

        var middle = Json(await CallAsync("read_chat", new Dictionary<string, object?>
        {
            ["chat_id"] = id.ToString(),
            ["offset"] = 2,
            ["limit"] = 2,
        }));
        Assert.True(middle.GetProperty("has_more").GetBoolean());
        Assert.Equal(4, middle.GetProperty("next_offset").GetInt32());
        Assert.Equal(2, middle.GetProperty("messages")[0].GetProperty("index").GetInt32());

        var last = Json(await CallAsync("read_chat", new Dictionary<string, object?>
        {
            ["chat_id"] = id.ToString(),
            ["offset"] = 4,
            ["limit"] = 2,
        }));
        Assert.Equal(1, last.GetProperty("messages").GetArrayLength());
        Assert.False(last.GetProperty("has_more").GetBoolean());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("next_offset").ValueKind);
        Assert.Equal(4, last.GetProperty("messages")[0].GetProperty("index").GetInt32());
    }

    [Fact]
    public async Task ReadChat_treats_an_offset_past_the_end_as_an_empty_final_page()
    {
        var id = Guid.NewGuid();
        _chats.GetAsync(id, Arg.Any<CancellationToken>()).Returns(ChatWithMessages(id, 3));

        var json = Json(await CallAsync("read_chat", new Dictionary<string, object?>
        {
            ["chat_id"] = id.ToString(),
            ["offset"] = 99,
        }));

        Assert.Empty(json.GetProperty("messages").EnumerateArray());
        Assert.False(json.GetProperty("has_more").GetBoolean());
    }

    [Fact]
    public async Task ReadChat_clamps_a_negative_offset_to_the_start()
    {
        var id = Guid.NewGuid();
        _chats.GetAsync(id, Arg.Any<CancellationToken>()).Returns(ChatWithMessages(id, 3));

        var json = Json(await CallAsync("read_chat", new Dictionary<string, object?>
        {
            ["chat_id"] = id.ToString(),
            ["offset"] = -20,
        }));

        Assert.Equal(3, json.GetProperty("messages").GetArrayLength());
        Assert.Equal(0, json.GetProperty("messages")[0].GetProperty("index").GetInt32());
    }

    // ---- thinking content ---------------------------------------------------------------------

    [Fact]
    public async Task ReadChat_never_emits_thinking_content()
    {
        // Asserting on the result record's shape would prove nothing — it structurally cannot carry the
        // field. The serialized payload is what the provider sees, so search THAT for the sentinel.
        var id = Guid.NewGuid();
        _chats.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(ChatWithMessages(id, 3, thinking: "SENTINEL-INTERNAL-SCRATCH"));

        var payload = JsonSerializer.Serialize(await CallAsync(
            "read_chat", new Dictionary<string, object?> { ["chat_id"] = id.ToString() }));

        Assert.DoesNotContain("SENTINEL-INTERNAL-SCRATCH", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("hinking", payload, StringComparison.Ordinal);
        Assert.Contains("message-0", payload, StringComparison.Ordinal);
    }

    // ---- readable failures --------------------------------------------------------------------

    [Fact]
    public async Task ReadChat_returns_a_readable_sentence_for_an_unknown_id()
    {
        var missing = Guid.NewGuid();
        _chats.GetAsync(missing, Arg.Any<CancellationToken>()).Returns((SyncAssistantChat?)null);

        var json = Json(await CallAsync(
            "read_chat", new Dictionary<string, object?> { ["chat_id"] = missing.ToString() }));

        var text = json.GetString();
        Assert.NotNull(text);
        Assert.Contains("No stored chat has that id", text, StringComparison.Ordinal);
        Assert.Contains("search_chats", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadChat_returns_a_readable_sentence_for_a_missing_or_malformed_id()
    {
        foreach (var args in new[]
                 {
                     new Dictionary<string, object?>(),
                     new Dictionary<string, object?> { ["chat_id"] = "the pricing one" },
                 })
        {
            var text = Json(await CallAsync("read_chat", args)).GetString();
            Assert.NotNull(text);
            Assert.Contains("chat_id", text, StringComparison.Ordinal);
        }

        await _chats.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchChats_says_so_when_the_query_has_no_searchable_words()
    {
        var text = Json(await CallAsync(
            "search_chats", new Dictionary<string, object?> { ["query"] = "!!!" })).GetString();

        Assert.NotNull(text);
        Assert.Contains("no searchable words", text, StringComparison.Ordinal);
        await _chats.DidNotReceive().SearchRankedAsync(
            Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
            Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchChats_rejects_a_date_it_cannot_parse()
    {
        var text = Json(await CallAsync("search_chats", new Dictionary<string, object?>
        {
            ["query"] = "hetzner",
            ["to_date"] = "last tuesday",
        })).GetString();

        Assert.NotNull(text);
        Assert.Contains("YYYY-MM-DD", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchChats_forwards_the_parsed_date_window()
    {
        StubRanked();

        await CallAsync("search_chats", new Dictionary<string, object?>
        {
            ["query"] = "hetzner",
            ["from_date"] = "2026-08-01",
            ["to_date"] = "2026-08-19",
        });

        await _chats.Received(1).SearchRankedAsync(
            Arg.Any<string>(),
            Arg.Is<DateTime?>(d => d == new DateTime(2026, 8, 1)),
            Arg.Is<DateTime?>(d => d == new DateTime(2026, 8, 19)),
            Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---- envelope and read-only-ness ----------------------------------------------------------

    [Fact]
    public async Task SearchChats_carries_the_standing_note_and_the_hit_fields()
    {
        var id = Guid.NewGuid();
        StubRanked(Hit(id, "Pricing for the Hetzner box", "…we settled on the CPX41 because …"));

        var json = Json(await CallAsync(
            "search_chats", new Dictionary<string, object?> { ["query"] = "hetzner" }));

        Assert.Equal(
            "Snippets are excerpts. Call read_chat(chat_id) for the actual conversation before relying on it.",
            json.GetProperty("note").GetString());

        var hit = json.GetProperty("chats")[0];
        Assert.Equal(id.ToString(), hit.GetProperty("chat_id").GetString());
        Assert.Equal("Pricing for the Hetzner box", hit.GetProperty("title").GetString());
        Assert.Equal("2026-08-19", hit.GetProperty("updated_at").GetString());
        Assert.Equal(7, hit.GetProperty("message_count").GetInt32());
        Assert.Contains("CPX41", hit.GetProperty("snippet").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Neither_tool_writes_to_the_store()
    {
        var id = Guid.NewGuid();
        _chats.GetAsync(id, Arg.Any<CancellationToken>()).Returns(ChatWithMessages(id, 2));
        StubRanked(Hit(id));
        StubRecent(Chat(id, "Recent"));

        await CallAsync("search_chats", new Dictionary<string, object?> { ["query"] = "hetzner" });
        await CallAsync("search_chats", new Dictionary<string, object?>());
        await CallAsync("read_chat", new Dictionary<string, object?> { ["chat_id"] = id.ToString() });

        var writes = _chats.ReceivedCalls()
            .Select(c => c.GetMethodInfo().Name)
            .Where(n => n is "TouchLastAccessedAsync" or "SaveAsync" or "SaveFromRemoteAsync"
                or "SaveMergedAsync" or "SetTitleAsync" or "DeleteAsync" or "DeleteFromRemoteAsync"
                or "EvictOlderThanAsync" or "DeleteAllAsync")
            .ToList();

        Assert.Empty(writes);
    }
}
