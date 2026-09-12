using System.IO;
using System.Text.Json;
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

/// <summary>
/// The export → import round-trip is the spine both import paths land on: if a Pia archive
/// restores every persisted column, an Open WebUI file is just a second producer feeding it.
/// </summary>
public sealed class ChatArchiveServiceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly ChatArchiveService _sut;

    public ChatArchiveServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaChatArchive_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        _sut = new ChatArchiveService(_chats, NullLogger<ChatArchiveService>.Instance);
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    private static SyncAssistantChat FullyPopulatedChat()
    {
        var created = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        return new SyncAssistantChat
        {
            Id = Guid.NewGuid(),
            SchemaVersion = 1,
            Title = "Round-trip me",
            CreatedAt = created,
            UpdatedAt = created.AddMinutes(9),
            LastAccessedAt = created.AddMinutes(12),
            WindowMode = "Assistant",
            ProviderId = Guid.NewGuid(),
            WorkingDirectory = "notes/sub",
            Messages =
            [
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "user",
                    Content = "question",
                    Timestamp = created,
                },
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "assistant",
                    Content = "answer",
                    ThinkingContent = "reasoning",
                    Timestamp = created.AddMinutes(1),
                    Tokens = 42,
                    ModelName = "some-model",
                    Persona = new SyncMessagePersona { Id = Guid.NewGuid(), Name = "Pia", Emoji = "🙂" },
                },
            ],
        };
    }

    [Fact]
    public async Task ExportThenImport_RestoresEveryPersistedField()
    {
        var original = FullyPopulatedChat();
        await _chats.SaveAsync(original, Ct);

        var file = PathFor("archive.json");
        Assert.Equal(1, await _sut.ExportAsync([original.Id], file, Ct));

        await _chats.DeleteAsync(original.Id, Ct);
        Assert.Null(await _chats.GetAsync(original.Id, Ct));

        var importStarted = DateTime.UtcNow;
        var result = await _sut.ImportAsync(file, ct: Ct);

        Assert.Equal(ChatArchiveFormat.Pia, result.Format);
        Assert.Equal(1, result.Imported);

        var restored = await _chats.GetAsync(original.Id, Ct);
        Assert.NotNull(restored);
        Assert.Equal(original.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(original.Title, restored.Title);
        Assert.Equal(original.CreatedAt, restored.CreatedAt);
        Assert.Equal(original.UpdatedAt, restored.UpdatedAt);
        // The one field an import does not restore: it is stamped, so the file's own value cannot
        // hand a years-old archive straight to retention.
        Assert.True(restored.LastAccessedAt >= importStarted);
        Assert.Equal(original.WindowMode, restored.WindowMode);
        Assert.Equal(original.ProviderId, restored.ProviderId);
        Assert.Equal(original.WorkingDirectory, restored.WorkingDirectory);

        Assert.Equal(original.Messages.Count, restored.Messages.Count);
        for (var i = 0; i < original.Messages.Count; i++)
        {
            var expected = original.Messages[i];
            var actual = restored.Messages[i];
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Role, actual.Role);
            Assert.Equal(expected.Content, actual.Content);
            Assert.Equal(expected.ThinkingContent, actual.ThinkingContent);
            Assert.Equal(expected.Timestamp, actual.Timestamp);
            Assert.Equal(expected.Tokens, actual.Tokens);
            Assert.Equal(expected.ModelName, actual.ModelName);
            Assert.Equal(expected.Persona?.Id, actual.Persona?.Id);
            Assert.Equal(expected.Persona?.Name, actual.Persona?.Name);
            Assert.Equal(expected.Persona?.Emoji, actual.Persona?.Emoji);
        }
    }

    [Fact]
    public async Task ImportingTheSameArchiveTwice_DoesNotDuplicate()
    {
        var chat = FullyPopulatedChat();
        await _chats.SaveAsync(chat, Ct);
        var file = PathFor("twice.json");
        await _sut.ExportAsync([chat.Id], file, Ct);

        var second = await _sut.ImportAsync(file, ct: Ct);

        Assert.Equal(0, second.Imported);
        Assert.Equal(1, second.SkippedUpToDate);
        Assert.Single(await _chats.GetAllIdsAsync(Ct));
    }

    [Fact]
    public async Task ReImporting_RepairsAnAccessDateRetentionWouldEvictOn()
    {
        var chat = FullyPopulatedChat();
        chat.LastAccessedAt = DateTime.UtcNow.AddYears(-3);
        await _chats.SaveAsync(chat, Ct);
        var file = PathFor("repair.json");
        await _sut.ExportAsync([chat.Id], file, Ct);
        var importStarted = DateTime.UtcNow;

        var result = await _sut.ImportAsync(file, ct: Ct);

        Assert.Equal(0, result.Imported);
        Assert.Equal(1, result.SkippedUpToDate);
        var stored = await _chats.GetAsync(chat.Id, Ct);
        Assert.NotNull(stored);
        Assert.True(stored.LastAccessedAt >= importStarted,
            $"a skipped chat keeps the date retention evicts on, got {stored.LastAccessedAt:O}");
    }

    [Fact]
    public async Task Import_DoesNotOverwriteALocallyNewerChat()
    {
        var chat = FullyPopulatedChat();
        await _chats.SaveAsync(chat, Ct);
        var file = PathFor("stale.json");
        await _sut.ExportAsync([chat.Id], file, Ct);

        chat.Title = "edited after the export";
        chat.UpdatedAt = chat.UpdatedAt.AddHours(1);
        await _chats.SaveAsync(chat, Ct);

        var result = await _sut.ImportAsync(file, ct: Ct);

        Assert.Equal(0, result.Imported);
        Assert.Equal(1, result.SkippedUpToDate);
        var stored = await _chats.GetAsync(chat.Id, Ct);
        Assert.Equal("edited after the export", stored?.Title);
    }

    [Fact]
    public async Task ExportAll_SkipsMessagelessStubs()
    {
        var real = FullyPopulatedChat();
        await _chats.SaveAsync(real, Ct);
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = Guid.NewGuid(),
            Title = "stub",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
        }, Ct);

        var file = PathFor("all.json");
        Assert.Equal(1, await _sut.ExportAllAsync(file, Ct));
    }

    [Fact]
    public async Task Import_ReportsOldestUpdatedAt_SoTheCallerCanWidenItsDateFilter()
    {
        var old = FullyPopulatedChat();
        old.UpdatedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        old.CreatedAt = old.UpdatedAt;
        old.LastAccessedAt = old.UpdatedAt;
        await _chats.SaveAsync(old, Ct);

        var file = PathFor("old.json");
        await _sut.ExportAsync([old.Id], file, Ct);
        await _chats.DeleteAsync(old.Id, Ct);

        var result = await _sut.ImportAsync(file, ct: Ct);

        Assert.Equal(old.UpdatedAt, result.OldestUpdatedAt);
    }

    [Fact]
    public async Task Import_KeepsTheArchivesOwnDates_ButStampsLastAccessed()
    {
        var file = PathFor("ancient.json");
        await File.WriteAllTextAsync(file, OpenWebUiExport(1, AncientEpoch), Ct);
        var importStarted = DateTime.UtcNow;

        Assert.Equal(1, (await _sut.ImportAsync(file, ct: Ct)).Imported);

        var stored = await _chats.GetAsync(Assert.Single(await _chats.GetAllIdsAsync(Ct)), Ct);
        Assert.NotNull(stored);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(AncientEpoch).UtcDateTime, stored.CreatedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(AncientEpoch + 100).UtcDateTime, stored.UpdatedAt);
        Assert.True(stored.LastAccessedAt >= importStarted,
            $"import must count as an access, got {stored.LastAccessedAt:O}");
    }

    [Fact]
    public async Task AnImportedArchive_SurvivesTheNextRetentionPass()
    {
        var stale = ChatWithMessageId(Guid.NewGuid(), "not opened in years");
        stale.LastAccessedAt = DateTime.UtcNow.AddYears(-3);
        await _chats.SaveAsync(stale, Ct);

        var file = PathFor("retention.json");
        await File.WriteAllTextAsync(file, OpenWebUiExport(1, AncientEpoch), Ct);
        Assert.Equal(1, (await _sut.ImportAsync(file, ct: Ct)).Imported);

        await RetentionSut().RunCleanupAsync(Ct);

        var surviving = await _chats.GetAllIdsAsync(Ct);
        // The stale chat proves the pass really evicted: without it the assertion below holds
        // whether retention ran or silently faulted.
        Assert.DoesNotContain(stale.Id, surviving);
        Assert.Single(surviving);
    }

    [Fact]
    public async Task Import_RejectsAForeignFile()
    {
        var file = PathFor("foreign.json");
        await File.WriteAllTextAsync(file, """{"hello":"world"}""", Ct);

        var result = await _sut.ImportAsync(file, ct: Ct);

        Assert.Equal(ChatArchiveFormat.Unknown, result.Format);
        Assert.Equal(0, result.Imported);
    }

    /// <summary>Message ids are a global primary key, so a file that repeats one must not abort the batch.</summary>
    [Fact]
    public async Task Import_RekeysDuplicateMessageIds()
    {
        var sharedId = Guid.NewGuid();
        var archive = new PiaChatArchive
        {
            ExportedAt = DateTime.UtcNow,
            Chats =
            [
                ChatWithMessageId(sharedId, "first"),
                ChatWithMessageId(sharedId, "second"),
            ],
        };

        var file = PathFor("dupes.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(archive, CamelCase), Ct);

        var result = await _sut.ImportAsync(file, ct: Ct);

        Assert.Equal(2, result.Imported);
        Assert.Equal(0, result.Failed);
    }

    /// <summary>Sync's E2EE fields must not survive into the plaintext local store.</summary>
    [Fact]
    public async Task Import_DropsTransportEncryptionFields()
    {
        var chat = FullyPopulatedChat();
        var archive = new PiaChatArchive
        {
            ExportedAt = DateTime.UtcNow,
            Chats = [chat],
        };
        chat.EncryptedPayload = "not-a-real-payload";
        chat.WrappedDek = "not-a-real-dek";

        var file = PathFor("encrypted.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(archive, CamelCase), Ct);

        var result = await _sut.ImportAsync(file, ct: Ct);

        Assert.Equal(1, result.Imported);
        var stored = await _chats.GetAsync(chat.Id, Ct);
        Assert.Null(stored?.EncryptedPayload);
        Assert.Null(stored?.WrappedDek);
        Assert.Equal("question", stored?.Messages[0].Content);
    }

    /// <summary>
    /// Stands in for WPF's context. Unlike the base class it makes itself <c>Current</c> while running a
    /// posted continuation, which is what lets the test below see a captured context at all.
    /// </summary>
    private sealed class DetectableSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                d(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    /// <summary>
    /// The import ran on the UI thread and froze the app for minutes on a 37 MB export. Parsing,
    /// conversion and every store write must happen with no ambient context to marshal back to.
    /// </summary>
    [Fact]
    public async Task Import_DoesNotRunOnTheCallersSynchronizationContext()
    {
        var file = PathFor("offthread.json");
        await File.WriteAllTextAsync(file, OpenWebUiExport(3), Ct);

        var contexts = new List<SynchronizationContext?>();
        _chats.ChatsChanged += (_, _) => contexts.Add(SynchronizationContext.Current);

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DetectableSynchronizationContext());
        try
        {
            var result = await _sut.ImportAsync(file, ct: Ct);
            Assert.Equal(3, result.Imported);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.NotEmpty(contexts);
        Assert.All(contexts, c => Assert.Null(c));
    }

    [Fact]
    public async Task Import_ReportsProgress_FromReadingToTheLastChat()
    {
        var file = PathFor("progress.json");
        await File.WriteAllTextAsync(file, OpenWebUiExport(4), Ct);

        var ticks = new List<ChatImportProgress>();
        await _sut.ImportAsync(file, new SynchronousProgress(ticks.Add), Ct);

        Assert.Equal(ChatImportPhase.Reading, ticks[0].Phase);
        Assert.Contains(ticks, t => t.Phase == ChatImportPhase.Converting);

        var storing = ticks.Where(t => t.Phase == ChatImportPhase.Storing).ToList();
        Assert.All(storing, t => Assert.Equal(4, t.Total));
        Assert.Equal(0, storing[0].Processed);
        Assert.Equal(4, storing[^1].Processed);
    }

    /// <summary>
    /// <see cref="Progress{T}"/> would post to the test's context and report after the assertion;
    /// the production caller wants exactly that marshalling, a test wants the ticks inline.
    /// </summary>
    private sealed class SynchronousProgress(Action<ChatImportProgress> onReport) : IProgress<ChatImportProgress>
    {
        public void Report(ChatImportProgress value) => onReport(value);
    }

    /// <summary>2019-03-04T05:06:07Z — far enough back that any retention window evicts it.</summary>
    private const long AncientEpoch = 1551675967;

    private const long DefaultExportEpoch = 1779274800;

    private AssistantChatRetentionService RetentionSut()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        return new AssistantChatRetentionService(
            _chats,
            settings,
            new AssistantChatSyncService(
                _chats,
                Substitute.For<ICloudCapabilityService>(),
                Substitute.For<IAuthService>(),
                settings,
                Substitute.For<System.Net.Http.IHttpClientFactory>(),
                new SyncMapper(Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance)),
                Substitute.For<ISyncClientService>(),
                NullLogger<AssistantChatSyncService>.Instance),
            Substitute.For<IAgentTimelineService>(),
            Substitute.For<IAgentToolExchangeStore>(),
            NullLogger<AssistantChatRetentionService>.Instance);
    }

    private static string OpenWebUiExport(int chatCount, long createdAtEpoch = DefaultExportEpoch)
    {
        var records = Enumerable.Range(0, chatCount).Select(i => $$"""
            {
              "id": "{{Guid.NewGuid()}}",
              "title": "chat {{i}}",
              "created_at": {{createdAtEpoch}},
              "updated_at": {{createdAtEpoch + 100 + i}},
              "chat": {
                "history": {
                  "currentId": "{{AnswerId(i)}}",
                  "messages": {
                    "{{PromptId(i)}}": { "id": "{{PromptId(i)}}", "parentId": null,
                                         "role": "user", "content": "prompt {{i}}", "timestamp": {{createdAtEpoch + 1}} },
                    "{{AnswerId(i)}}": { "id": "{{AnswerId(i)}}", "parentId": "{{PromptId(i)}}",
                                         "role": "assistant", "content": "answer {{i}}", "timestamp": {{createdAtEpoch + 2}} }
                  }
                },
                "messages": [ { "role": "user", "content": "prompt {{i}}" } ]
              }
            }
            """);

        return $"[{string.Join(",", records)}]";
    }

    private static Guid PromptId(int index) => new($"aaaaaaaa-0000-0000-0000-{index:D12}");

    private static Guid AnswerId(int index) => new($"bbbbbbbb-0000-0000-0000-{index:D12}");

    private static JsonSerializerOptions CamelCase => new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static SyncAssistantChat ChatWithMessageId(Guid messageId, string content) => new()
    {
        Id = Guid.NewGuid(),
        Title = content,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        LastAccessedAt = DateTime.UtcNow,
        Messages =
        [
            new SyncAssistantChatMessage
            {
                Id = messageId,
                Role = "user",
                Content = content,
                Timestamp = DateTime.UtcNow,
            },
        ],
    };

    public void Dispose()
    {
        _chats.Dispose();
        _runs.Dispose();
        _ctx.Dispose();
        TempPath.Remove(_dir);
    }
}
