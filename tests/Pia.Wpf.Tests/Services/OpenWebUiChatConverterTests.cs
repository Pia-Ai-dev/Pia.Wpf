using System.Text.Json;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Fixtures are synthetic but shaped exactly like a real "Export All Chats" file: epoch seconds,
/// a prompt-as-title, a message tree alongside the active linear path, citations, and attachments.
/// </summary>
public sealed class OpenWebUiChatConverterTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private const string ChatId = "f97457e3-0cbe-4b05-902a-9f3d3a78d780";
    private const string UserMessageId = "fdb4a0ed-aa8f-43c4-b963-36939f9975b3";

    private static string SingleChat(string title = "Short title", string extraMessageFields = "") => $$"""
        [
          {
            "id": "{{ChatId}}",
            "title": {{JsonSerializer.Serialize(title)}},
            "created_at": 1779274800,
            "updated_at": 1779274900,
            "chat": {
              "id": "{{ChatId}}",
              "title": {{JsonSerializer.Serialize(title)}},
              "models": ["openai_responses_api_pipeline"],
              "history": { "currentId": "{{UserMessageId}}", "messages": {} },
              "messages": [
                {
                  "id": "{{UserMessageId}}",
                  "parentId": null,
                  "role": "user",
                  "content": "Wie geht es dir?",
                  "timestamp": 1779274802
                },
                {
                  "id": "43c01071-1cf3-4ad4-b072-49dad278b3d7",
                  "parentId": "{{UserMessageId}}",
                  "role": "assistant",
                  "content": "Gut, danke.",
                  "model": "openai_responses_api_pipeline",
                  "timestamp": 1779274850,
                  "usage": { "prompt_tokens": 2440, "completion_tokens": 2531, "total_tokens": 4971 }
                  {{extraMessageFields}}
                }
              ]
            }
          }
        ]
        """;

    [Fact]
    public void Detects_AnOpenWebUiExport()
    {
        Assert.True(OpenWebUiChatConverter.LooksLikeOpenWebUiExport(Parse(SingleChat())));
        Assert.True(OpenWebUiChatConverter.LooksLikeOpenWebUiExport(Parse("[]")));
        Assert.False(OpenWebUiChatConverter.LooksLikeOpenWebUiExport(Parse("""{"format":"pia.chat-archive"}""")));
        Assert.False(OpenWebUiChatConverter.LooksLikeOpenWebUiExport(Parse("[1,2,3]")));
        Assert.False(OpenWebUiChatConverter.LooksLikeOpenWebUiExport(Parse("""[{"nope":true}]""")));
    }

    [Fact]
    public void Maps_Chat_Messages_And_EpochSeconds()
    {
        var conversion = OpenWebUiChatConverter.Convert(Parse(SingleChat()));

        var chat = Assert.Single(conversion.Chats);
        Assert.Equal(Guid.Parse(ChatId), chat.Id);
        Assert.Equal("Short title", chat.Title);
        Assert.Equal("Assistant", chat.WindowMode);
        Assert.Equal(1, chat.SchemaVersion);
        // Open WebUI model ids resolve to no Pia provider, so resume must fall back to the active one.
        Assert.Null(chat.ProviderId);
        Assert.Equal(new DateTime(2026, 5, 20, 11, 0, 0, DateTimeKind.Utc), chat.CreatedAt);
        Assert.Equal(new DateTime(2026, 5, 20, 11, 1, 40, DateTimeKind.Utc), chat.UpdatedAt);
        Assert.Equal(chat.UpdatedAt, chat.LastAccessedAt);

        Assert.Equal(2, chat.Messages.Count);
        Assert.Equal("user", chat.Messages[0].Role);
        Assert.Equal("Wie geht es dir?", chat.Messages[0].Content);
        Assert.Equal(Guid.Parse(UserMessageId), chat.Messages[0].Id);
        Assert.Equal(new DateTime(2026, 5, 20, 11, 0, 2, DateTimeKind.Utc), chat.Messages[0].Timestamp);

        Assert.Equal("assistant", chat.Messages[1].Role);
        Assert.Equal("openai_responses_api_pipeline", chat.Messages[1].ModelName);
        Assert.Equal(2531, chat.Messages[1].Tokens);
    }

    [Fact]
    public void Collapses_APromptAsTitle_SoTheHistoryRowStaysReadable()
    {
        var sprawling = "Liebe Kundinnen und Kunden,\nmit Windows Server 2025 hat Microsoft die "
            + new string('x', 400);

        var chat = Assert.Single(OpenWebUiChatConverter.Convert(Parse(SingleChat(sprawling))).Chats);

        Assert.NotNull(chat.Title);
        Assert.DoesNotContain('\n', chat.Title);
        Assert.True(chat.Title!.Length <= 121, $"title was {chat.Title.Length} chars");
        Assert.EndsWith("…", chat.Title, StringComparison.Ordinal);
        Assert.StartsWith("Liebe Kundinnen und Kunden, mit Windows", chat.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_MillisecondEpochs_Too()
    {
        var json = SingleChat().Replace("\"updated_at\": 1779274900", "\"updated_at\": 1779274900000");

        var chat = Assert.Single(OpenWebUiChatConverter.Convert(Parse(json)).Chats);

        Assert.Equal(new DateTime(2026, 5, 20, 11, 1, 40, DateTimeKind.Utc), chat.UpdatedAt);
    }

    [Fact]
    public void Falls_Back_To_TheChatTimestamp_WhenAMessageHasNone()
    {
        var json = SingleChat().Replace("\"timestamp\": 1779274850,", "");

        var chat = Assert.Single(OpenWebUiChatConverter.Convert(Parse(json)).Chats);

        Assert.Equal(chat.UpdatedAt, chat.Messages[1].Timestamp);
    }

    [Fact]
    public void Counts_DroppedAttachments_And_FootnotesCitations()
    {
        var extras = """
            ,
            "files": [
              { "type": "file", "name": "bug-report.md" },
              { "type": "file", "name": "notes.md" }
            ],
            "sources": [
              { "source": { "name": "bug-report.md", "url": "b2721eb8-17a8-40e5-b66c-117698f2d208" },
                "document": ["a 50 KB body that must not be carried over"] },
              { "source": { "name": "Windows Server 2025 news", "url": "https://learn.microsoft.com/x" } },
              { "source": { "name": "Windows Server 2025 news", "url": "https://learn.microsoft.com/x" } }
            ]
            """;

        var conversion = OpenWebUiChatConverter.Convert(Parse(SingleChat(extraMessageFields: extras)));

        Assert.Equal(2, conversion.DroppedAttachments);
        var content = Assert.Single(conversion.Chats).Messages[1].Content;
        Assert.StartsWith("Gut, danke.", content, StringComparison.Ordinal);
        Assert.Contains("Sources:", content, StringComparison.Ordinal);
        // A file citation's `url` is an opaque upload id, so it stays plain text.
        Assert.Contains("- bug-report.md\n", content, StringComparison.Ordinal);
        Assert.Contains("[Windows Server 2025 news](https://learn.microsoft.com/x)", content, StringComparison.Ordinal);
        Assert.DoesNotContain("50 KB body", content, StringComparison.Ordinal);
        // The repeated citation is listed once.
        Assert.Equal(1, CountOccurrences(content, "learn.microsoft.com"));
    }

    [Fact]
    public void Skips_ChatsWithNoMessages_BecauseHistoryWouldHideThem()
    {
        var json = """
            [
              { "id": "f97457e3-0cbe-4b05-902a-9f3d3a78d780", "title": "empty",
                "created_at": 1779274800, "updated_at": 1779274900,
                "chat": { "messages": [] } }
            ]
            """;

        var conversion = OpenWebUiChatConverter.Convert(Parse(json));

        Assert.Empty(conversion.Chats);
        Assert.Equal(1, conversion.SkippedEmpty);
    }

    [Fact]
    public void Derives_AStableId_ForANonGuidChatId()
    {
        var json = SingleChat().Replace(ChatId, "legacy-chat-7");

        var first = Assert.Single(OpenWebUiChatConverter.Convert(Parse(json)).Chats);
        var second = Assert.Single(OpenWebUiChatConverter.Convert(Parse(json)).Chats);

        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void Normalizes_AnUnexpectedRole_ToAssistant()
    {
        var json = SingleChat().Replace("\"role\": \"assistant\"", "\"role\": \"system\"");

        var chat = Assert.Single(OpenWebUiChatConverter.Convert(Parse(json)).Chats);

        Assert.Equal("assistant", chat.Messages[1].Role);
    }

    /// <summary>
    /// Wraps a <c>history</c> object around a <c>chat.messages</c> array of the caller's choosing —
    /// the two disagree in a real export, and the tree is the side that is right.
    /// </summary>
    private static string ChatWithTree(string currentId, string treeNodes, string cachedMessages) => $$"""
        [
          {
            "id": "{{ChatId}}",
            "title": "Branching chat",
            "created_at": 1779274800,
            "updated_at": 1779274900,
            "chat": {
              "id": "{{ChatId}}",
              "history": { "currentId": {{JsonSerializer.Serialize(currentId)}}, "messages": { {{treeNodes}} } },
              "messages": [ {{cachedMessages}} ]
            }
          }
        ]
        """;

    private const string M1 = "11111111-1111-1111-1111-111111111111";
    private const string M2 = "22222222-2222-2222-2222-222222222222";
    private const string M3 = "33333333-3333-3333-3333-333333333333";
    private const string M4 = "44444444-4444-4444-4444-444444444444";

    private static string LinearTree() => $$"""
        "{{M1}}": { "id": "{{M1}}", "parentId": null, "role": "user", "content": "one", "timestamp": 1779274801 },
        "{{M2}}": { "id": "{{M2}}", "parentId": "{{M1}}", "role": "assistant", "content": "two", "timestamp": 1779274802 },
        "{{M3}}": { "id": "{{M3}}", "parentId": "{{M2}}", "role": "user", "content": "three", "timestamp": 1779274803 },
        "{{M4}}": { "id": "{{M4}}", "parentId": "{{M3}}", "role": "assistant", "content": "four", "timestamp": 1779274804 }
        """;

    /// <summary>
    /// The regression behind "an imported chat shows only my first prompt": newer Open WebUI builds
    /// leave <c>chat.messages</c> as a single stub while the real conversation sits in the tree.
    /// </summary>
    [Fact]
    public void Prefers_TheMessageTree_OverAStaleFlatCache()
    {
        var json = ChatWithTree(M4, LinearTree(), """{ "role": "user", "content": "one" }""");

        var conversion = OpenWebUiChatConverter.Convert(Parse(json));

        var chat = Assert.Single(conversion.Chats);
        Assert.Equal(["one", "two", "three", "four"], chat.Messages.Select(m => m.Content));
        Assert.Equal(["user", "assistant", "user", "assistant"], chat.Messages.Select(m => m.Role));
        Assert.Equal(Guid.Parse(M1), chat.Messages[0].Id);
        Assert.Equal(3, conversion.RecoveredFromTree);
    }

    /// <summary>
    /// An assistant turn can be present-but-empty in the flat cache while the tree holds the whole
    /// answer, which read as "no answer" on a chat that otherwise imported both turns.
    /// </summary>
    [Fact]
    public void Recovers_AnAnswer_TheFlatCacheLeftEmpty()
    {
        var cached = $$"""
            { "id": "{{M1}}", "parentId": null, "role": "user", "content": "one", "timestamp": 1779274801 },
            { "id": "{{M2}}", "parentId": "{{M1}}", "role": "assistant", "content": "", "timestamp": 1779274802 }
            """;
        var json = ChatWithTree(M2, LinearTree(), cached);

        var chat = Assert.Single(OpenWebUiChatConverter.Convert(Parse(json)).Chats);

        Assert.Equal(2, chat.Messages.Count);
        Assert.Equal("two", chat.Messages[1].Content);
    }

    /// <summary>Regenerating an answer leaves both in the tree; only the one <c>currentId</c> points at was shown.</summary>
    [Fact]
    public void Follows_CurrentId_ThroughARegeneratedBranch()
    {
        var branching = $$"""
            "{{M1}}": { "id": "{{M1}}", "parentId": null, "role": "user", "content": "one",
                        "childrenIds": ["{{M2}}", "{{M3}}"], "timestamp": 1779274801 },
            "{{M2}}": { "id": "{{M2}}", "parentId": "{{M1}}", "role": "assistant", "content": "discarded", "timestamp": 1779274802 },
            "{{M3}}": { "id": "{{M3}}", "parentId": "{{M1}}", "role": "assistant", "content": "regenerated", "timestamp": 1779274803 }
            """;
        var json = ChatWithTree(M3, branching, """{ "role": "user", "content": "one" }""");

        var chat = Assert.Single(OpenWebUiChatConverter.Convert(Parse(json)).Chats);

        Assert.Equal(["one", "regenerated"], chat.Messages.Select(m => m.Content));
    }

    /// <summary>Older exports carry an empty tree, so the flat array has to stay the fallback.</summary>
    [Fact]
    public void Falls_Back_ToTheFlatArray_WhenTheTreeIsEmpty()
    {
        var conversion = OpenWebUiChatConverter.Convert(Parse(SingleChat()));

        Assert.Equal(2, Assert.Single(conversion.Chats).Messages.Count);
        Assert.Equal(0, conversion.RecoveredFromTree);
    }

    [Theory]
    [InlineData("\"currentId\": null")]
    [InlineData("\"currentId\": \"00000000-0000-0000-0000-000000000009\"")]
    public void Falls_Back_ToTheFlatArray_WhenCurrentIdLeadsNowhere(string currentId)
    {
        var json = ChatWithTree(M4, LinearTree(), """{ "role": "user", "content": "cached" }""")
            .Replace($"\"currentId\": \"{M4}\"", currentId);

        var chat = Assert.Single(OpenWebUiChatConverter.Convert(Parse(json)).Chats);

        Assert.Equal("cached", Assert.Single(chat.Messages).Content);
    }

    /// <summary>A hand-edited export can point a parentId back down its own path; the walk must still end.</summary>
    [Fact]
    public void Terminates_OnACycleInTheTree()
    {
        var cyclic = $$"""
            "{{M1}}": { "id": "{{M1}}", "parentId": "{{M2}}", "role": "user", "content": "one", "timestamp": 1779274801 },
            "{{M2}}": { "id": "{{M2}}", "parentId": "{{M1}}", "role": "assistant", "content": "two", "timestamp": 1779274802 }
            """;
        var json = ChatWithTree(M2, cyclic, "");

        var chat = Assert.Single(OpenWebUiChatConverter.Convert(Parse(json)).Chats);

        Assert.Equal(["one", "two"], chat.Messages.Select(m => m.Content));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
