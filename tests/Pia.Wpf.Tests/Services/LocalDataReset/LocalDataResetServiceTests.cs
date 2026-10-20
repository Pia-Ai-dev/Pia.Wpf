using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Models.Vault;
using Pia.Paths;
using Pia.Services.Interfaces;
using Pia.Services.LocalDataReset;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.LocalDataReset;

[Collection("PiaPathsStatic")]
public sealed class LocalDataResetServiceTests : IDisposable
{
    private static readonly string Now = DateTime.UtcNow.ToString("O");

    private readonly string _roaming = Path.Combine(Path.GetTempPath(), $"pia-reset-r-{Guid.NewGuid():N}");
    private readonly string _local = Path.Combine(Path.GetTempPath(), $"pia-reset-l-{Guid.NewGuid():N}");
    private readonly IDisposable _override;
    private readonly SqliteContext _db;
    private readonly ITemplateService _templates = Substitute.For<ITemplateService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IMemoryService _memory = Substitute.For<IMemoryService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly ISyncClientService _sync = Substitute.For<ISyncClientService>();
    private readonly IChatSyncFlusher _chats = Substitute.For<IChatSyncFlusher>();
    private readonly IAppRestartService _restart = Substitute.For<IAppRestartService>();

    public LocalDataResetServiceTests()
    {
        _override = PiaPaths.OverrideForTests(_roaming, _local);
        Directory.CreateDirectory(_roaming);
        _db = new SqliteContext(Path.Combine(_local, "history.db"));

        _templates.GetTemplatesAsync().Returns(new List<OptimizationTemplate>
        {
            new() { Name = "Built-in", Prompt = "p", IsBuiltIn = true },
        });
        _providers.GetProvidersAsync().Returns(new List<AiProvider>
        {
            new() { Name = "Pia Cloud", Endpoint = "e", ProviderType = AiProviderType.PiaCloud },
        });
        _memory.ListMemoriesAsync().Returns(new VaultMemorySnapshot([], 0));
        _sync.StopBackgroundSyncAndWaitAsync().Returns(Task.CompletedTask);
        _chats.FlushAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    public void Dispose()
    {
        _db.Dispose();
        _override.Dispose();
        TempPath.Remove(_roaming);
        TempPath.Remove(_local);
    }

    [Fact]
    public async Task HasSyncedLocalData_FreshInstall_IsFalse()
    {
        Assert.False(await NewSut().HasSyncedLocalDataAsync());
    }

    [Fact]
    public async Task HasSyncedLocalData_CustomTemplate_IsTrue()
    {
        _templates.GetTemplatesAsync().Returns(new List<OptimizationTemplate> { new() { Name = "Mine", Prompt = "p", IsBuiltIn = false } });

        Assert.True(await NewSut().HasSyncedLocalDataAsync());
    }

    [Fact]
    public async Task HasSyncedLocalData_NonPiaCloudProvider_IsTrue()
    {
        _providers.GetProvidersAsync().Returns(new List<AiProvider> { new() { Name = "OpenAI", Endpoint = "e", ProviderType = AiProviderType.OpenAI } });

        Assert.True(await NewSut().HasSyncedLocalDataAsync());
    }

    [Fact]
    public async Task HasSyncedLocalData_RenamedSeedColumn_IsTrue()
    {
        Execute($"UPDATE KanbanColumns SET Name = 'Inbox' WHERE Id = '{SqliteContext.DefaultKanbanColumnId}'");

        Assert.True(await NewSut().HasSyncedLocalDataAsync());
    }

    [Fact]
    public async Task HasSyncedLocalData_ExtraColumn_IsTrue()
    {
        Execute($"""
            INSERT INTO KanbanColumns (Id, Name, SortOrder, CreatedAt, UpdatedAt)
                VALUES ('k3', 'Doing', 1, '{Now}', '{Now}')
            """);

        Assert.True(await NewSut().HasSyncedLocalDataAsync());
    }

    [Fact]
    public async Task HasSyncedLocalData_OnlyMeetingJob_IsFalse()
    {
        InsertJob("40000000-0000-0000-0000-000000000002", "MeetingAttendance");

        Assert.False(await NewSut().HasSyncedLocalDataAsync());
    }

    [Fact]
    public async Task HasSyncedLocalData_ResearchJob_IsTrue()
    {
        InsertJob("40000000-0000-0000-0000-000000000001", "Research");

        Assert.True(await NewSut().HasSyncedLocalDataAsync());
    }

    [Theory]
    [InlineData("Sessions")]
    [InlineData("AssistantChats")]
    [InlineData("Memories")]
    [InlineData("Todos")]
    [InlineData("Personas")]
    public async Task HasSyncedLocalData_AnyRowInSyncedTable_IsTrue(string table)
    {
        Execute(table switch
        {
            "Sessions" => $"INSERT INTO Sessions (Id, OriginalText, OptimizedText, TemplateId, ProviderId, CreatedAt) VALUES ('s1', 'a', 'b', 't', 'p', '{Now}')",
            "AssistantChats" => $"INSERT INTO AssistantChats (Id, CreatedAt, UpdatedAt, LastAccessedAt, WindowMode) VALUES ('c1', '{Now}', '{Now}', '{Now}', 'Assistant')",
            "Memories" => $"INSERT INTO Memories (Id, Type, Label, CreatedAt, UpdatedAt, LastAccessedAt) VALUES ('m1', 'note', 'l', '{Now}', '{Now}', '{Now}')",
            "Todos" => $"INSERT INTO Todos (Id, Title, CreatedAt, UpdatedAt) VALUES ('t1', 't', '{Now}', '{Now}')",
            _ => $"INSERT INTO Personas (Id, Name, SystemPrompt, CreatedAt, UpdatedAt) VALUES ('p1', 'n', 's', '{Now}', '{Now}')",
        });

        Assert.True(await NewSut().HasSyncedLocalDataAsync());
    }

    [Fact]
    public async Task Notices_VaultWithItems_ReportsMemories()
    {
        var item = new VaultMemoryItem("memory/notes/a.md", "memory/notes/a.md", "note", "A", "body", null);
        _memory.ListMemoriesAsync().Returns(new VaultMemorySnapshot([item], 4));

        var notices = await NewSut().GetNoticesAsync();

        Assert.True(notices.VaultHasMemories);
    }

    [Fact]
    public async Task Notices_EmptyVault_ReportsNoMemories()
    {
        var notices = await NewSut().GetNoticesAsync();

        Assert.False(notices.VaultHasMemories);
        Assert.Equal(ApiKeyNotice.None, notices.ApiKeys);
    }

    [Fact]
    public async Task Notices_ProviderWithoutKey_ReportsNone()
    {
        _providers.GetProvidersAsync().Returns(new List<AiProvider> { new() { Name = "OpenAI", Endpoint = "e", ProviderType = AiProviderType.OpenAI } });

        var notices = await NewSut().GetNoticesAsync();

        Assert.Equal(ApiKeyNotice.None, notices.ApiKeys);
    }

    [Theory]
    [InlineData(true, true, ApiKeyNotice.None)]
    [InlineData(true, false, ApiKeyNotice.WillBeLost)]
    [InlineData(false, false, ApiKeyNotice.MayBeLost)]
    public async Task Notices_ApiKeys(bool loggedIn, bool e2ee, ApiKeyNotice expected)
    {
        _providers.GetProvidersAsync().Returns(new List<AiProvider>
        {
            new() { Name = "OpenAI", Endpoint = "e", ProviderType = AiProviderType.OpenAI, EncryptedApiKey = "secret" },
        });
        _auth.IsLoggedIn.Returns(loggedIn);
        _settings.GetSettingsAsync().Returns(new AppSettings { IsE2EEEnabled = e2ee });

        var notices = await NewSut().GetNoticesAsync();

        Assert.Equal(expected, notices.ApiKeys);
    }

    [Fact]
    public async Task Upload_PushFailed_ReturnsFalse_AndRestartsBackgroundSync()
    {
        _sync.IsSyncActive.Returns(true);
        _sync.SyncNowAsync().Returns(new SyncResult(0, 0, 0, PushSucceeded: false));

        var ok = await NewSut().UploadPendingChangesAsync();

        Assert.False(ok);
        _sync.Received(1).StartBackgroundSync();
    }

    [Fact]
    public async Task Upload_SyncSkipped_ReturnsFalse()
    {
        _sync.SyncNowAsync().Returns((SyncResult?)null);

        Assert.False(await NewSut().UploadPendingChangesAsync());
    }

    [Fact]
    public async Task Upload_ChatsNotFlushed_ReturnsFalse()
    {
        _sync.SyncNowAsync().Returns(new SyncResult(0, 0, 0, PushSucceeded: true));
        _chats.FlushAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.False(await NewSut().UploadPendingChangesAsync());
    }

    [Fact]
    public async Task Upload_PushAndChatsOk_ReturnsTrue()
    {
        _sync.SyncNowAsync().Returns(new SyncResult(2, 0, 0, PushSucceeded: true));

        Assert.True(await NewSut().UploadPendingChangesAsync());
    }

    [Fact]
    public async Task Upload_SyncWasInactive_DoesNotStartBackgroundSync()
    {
        _sync.IsSyncActive.Returns(false);
        _sync.SyncNowAsync().Returns(new SyncResult(0, 0, 0, PushSucceeded: true));

        await NewSut().UploadPendingChangesAsync();

        _sync.DidNotReceive().StartBackgroundSync();
    }

    [Fact]
    public async Task ScheduleReset_WritesMarker_AndRestartDelegates()
    {
        var sut = NewSut();
        Assert.False(sut.IsResetPending);

        sut.ScheduleReset();
        await sut.RestartAsync();

        Assert.True(sut.IsResetPending);
        await _restart.Received(1).RestartAsync();
    }

    private LocalDataResetService NewSut() =>
        new(_db, _templates, _providers, _settings, _memory, _auth, _sync, _chats, _restart);

    private void InsertJob(string id, string kind) =>
        Execute($"""
            INSERT INTO ScheduledJobs (Id, Name, Query, Kind, Recurrence, TimeOfDay, NextFireAt, CreatedAt)
                VALUES ('{id}', 'n', 'q', '{kind}', 'Daily', '08:00', '{Now}', '{Now}')
            """);

    private void Execute(string sql)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
