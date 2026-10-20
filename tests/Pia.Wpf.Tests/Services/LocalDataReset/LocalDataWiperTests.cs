using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Paths;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.LocalDataReset;
using Pia.Services.Plugins;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.LocalDataReset;

[Collection("PiaPathsStatic")]
public sealed class LocalDataWiperTests : IDisposable
{
    private const string ChatId = "c0000000-0000-0000-0000-000000000001";
    private const string ServerPluginId = "20000000-0000-0000-0000-000000000001";
    private const string LocalPluginId = "30000000-0000-0000-0000-000000000001";
    private const string ResearchJobId = "40000000-0000-0000-0000-000000000001";
    private const string MeetingJobId = "40000000-0000-0000-0000-000000000002";
    private const string ServerPluginConfig = """{"source":"catalog"}""";

    private static readonly string PreloadedPluginId = BuiltInPluginDefaults.MemoryPluginId.ToString();
    private static readonly string Now = DateTime.UtcNow.ToString("O");

    private readonly string _roaming = Path.Combine(Path.GetTempPath(), $"pia-wipe-r-{Guid.NewGuid():N}");
    private readonly string _local = Path.Combine(Path.GetTempPath(), $"pia-wipe-l-{Guid.NewGuid():N}");
    private readonly IDisposable _override;
    private SqliteContext _db;

    public LocalDataWiperTests()
    {
        _override = PiaPaths.OverrideForTests(_roaming, _local);
        Directory.CreateDirectory(_roaming);
        _db = NewContext();
    }

    public void Dispose()
    {
        _db.Dispose();
        _override.Dispose();
        TempPath.Remove(_roaming);
        TempPath.Remove(_local);
    }

    [Fact]
    public async Task Wipe_EmptiesEverySyncedTable()
    {
        await SeedAsync();

        await NewWiper().WipeAsync();

        AssertSyncedTablesEmpty();
    }

    [Fact]
    public async Task Wipe_ReseedsKanbanColumnsWithEpochStamps()
    {
        await SeedAsync();

        await NewWiper().WipeAsync();

        AssertKanbanReseeded();
    }

    [Fact]
    public async Task Wipe_KeepsDeviceLocalRows()
    {
        await SeedAsync();

        await NewWiper().WipeAsync();

        AssertDeviceLocalRowsKept();
    }

    [Fact]
    public async Task Wipe_ResetsServerPluginsButKeepsBuiltIns()
    {
        await SeedAsync();

        await NewWiper().WipeAsync();

        AssertPluginsReset();
    }

    [Fact]
    public async Task Wipe_ClearsSyncedFilesAndKeepsOnlyPiaCloudProvider()
    {
        await SeedAsync();

        await NewWiper().WipeAsync();

        AssertFilesCleared();
    }

    [Fact]
    public async Task Wipe_ClearsLeftoverSignInState()
    {
        await SeedAsync();

        await NewWiper().WipeAsync();

        await AssertSettingsResetAsync();
    }

    [Fact]
    public async Task Wipe_RunTwice_Converges()
    {
        await SeedAsync();
        await NewWiper().WipeAsync();

        _db.Dispose();
        _db = NewContext();
        await NewWiper().WipeAsync();

        AssertSyncedTablesEmpty();
        AssertKanbanReseeded();
        AssertDeviceLocalRowsKept();
        AssertPluginsReset();
        AssertFilesCleared();
        await AssertSettingsResetAsync();
    }

    private SqliteContext NewContext() => new(Path.Combine(_local, "history.db"));

    private static SettingsService NewSettingsService() =>
        new(NullLogger<SettingsService>.Instance, Substitute.For<IPolicyService>());

    private LocalDataWiper NewWiper() =>
        new(_db, NewSettingsService(), NullLogger<LocalDataWiper>.Instance);

    private async Task SeedAsync()
    {
        var localMcp = LocalMcpConfig.ToConfigJson(
            LocalMcpDefinition.Empty with { Name = "local", Command = "node", ToolPrefix = "local" }, s => s);

        Execute($"""
            INSERT INTO Personas (Id, Name, SystemPrompt, CreatedAt, UpdatedAt) VALUES ('p1', 'n', 's', '{Now}', '{Now}');
            INSERT INTO ManagedPersonas (Id, Name, SystemPrompt, CreatedAt, UpdatedAt) VALUES ('m1', 'n', 's', '{Now}', '{Now}');
            INSERT INTO Sessions (Id, OriginalText, OptimizedText, TemplateId, ProviderId, CreatedAt)
                VALUES ('s1', 'a', 'b', 't', 'p', '{Now}');
            INSERT INTO AssistantChats (Id, CreatedAt, UpdatedAt, LastAccessedAt, WindowMode)
                VALUES ('{ChatId}', '{Now}', '{Now}', '{Now}', 'Assistant');
            INSERT INTO AssistantChatMessages (Id, ChatId, Ordinal, Role, Content, Timestamp)
                VALUES ('cm1', '{ChatId}', 0, 'user', 'hello', '{Now}');
            INSERT INTO AssistantChatsFts (ChatId, Title, Body) VALUES ('{ChatId}', 't', 'hello');
            INSERT INTO Memories (Id, Type, Label, CreatedAt, UpdatedAt, LastAccessedAt)
                VALUES ('mem1', 'note', 'label', '{Now}', '{Now}', '{Now}');
            INSERT INTO Todos (Id, Title, CreatedAt, UpdatedAt) VALUES ('td1', 't', '{Now}', '{Now}');
            UPDATE KanbanColumns SET Name = 'Inbox' WHERE Id = '{SqliteContext.DefaultKanbanColumnId}';
            INSERT INTO KanbanColumns (Id, Name, SortOrder, CreatedAt, UpdatedAt)
                VALUES ('k3', 'Doing', 1, '{Now}', '{Now}');
            INSERT INTO ScheduledJobs (Id, Name, Query, Kind, Recurrence, TimeOfDay, NextFireAt, CreatedAt)
                VALUES ('{ResearchJobId}', 'n', 'q', 'Research', 'Daily', '08:00', '{Now}', '{Now}');
            INSERT INTO ScheduledJobs (Id, Name, Query, Kind, Recurrence, TimeOfDay, NextFireAt, CreatedAt)
                VALUES ('{MeetingJobId}', 'n', 'q', 'MeetingAttendance', 'Once', '08:00', '{Now}', '{Now}');
            INSERT INTO Reminders (Id, Description, Recurrence, TimeOfDay, NextFireAt, CreatedAt)
                VALUES ('r1', 'd', 'Daily', '08:00', '{Now}', '{Now}');
            INSERT INTO Plugins (Id, Kind, Name, IsPreloaded, UserEnabled, UpdatedAt)
                VALUES ('{PreloadedPluginId}', 'builtin_tool_pack', 'memory', 1, 0, '{Now}');
            INSERT INTO Plugins (Id, Kind, Name, ConfigJson, UpdatedAt)
                VALUES ('{ServerPluginId}', 'mcp_server', 'server', '{ServerPluginConfig}', '{Now}');
            """);

        using (var local = _db.GetConnection().CreateCommand())
        {
            local.CommandText = "INSERT INTO Plugins (Id, Kind, Name, ConfigJson, UpdatedAt) VALUES (@Id, 'mcp_server', 'local', @Config, @Now)";
            local.Parameters.AddWithValue("@Id", LocalPluginId);
            local.Parameters.AddWithValue("@Config", localMcp);
            local.Parameters.AddWithValue("@Now", Now);
            local.ExecuteNonQuery();
        }

        File.WriteAllText(Path.Combine(_roaming, "templates.json"), "[]");
        File.WriteAllText(Path.Combine(_roaming, "pending-sync-deletes.json"), "{}");
        File.WriteAllText(Path.Combine(_roaming, "policy-cache.json"), "{}");

        List<AiProvider> providers =
        [
            new() { Id = ProviderService.PiaCloudProviderId, Name = "Pia Cloud", ProviderType = AiProviderType.PiaCloud, Endpoint = "https://cloud" },
            new() { Name = "OpenAI", ProviderType = AiProviderType.OpenAI, Endpoint = "https://openai" },
        ];
        File.WriteAllText(ProvidersPath,
            JsonSerializer.Serialize(providers, JsonPersistenceService<List<AiProvider>>.JsonOptions));

        await NewSettingsService().SaveSettingsAsync(new AppSettings
        {
            Theme = AppTheme.Dark,
            EncryptedRefreshToken = "r",
            SyncEnabled = true,
            HasCompletedFirstRunWizard = true,
            SyncDeviceId = "dev-1",
        });
    }

    private string ProvidersPath => Path.Combine(_roaming, "providers.json");

    private void AssertSyncedTablesEmpty()
    {
        foreach (var table in new[]
        {
            "Personas", "ManagedPersonas", "Sessions", "AssistantChats", "AssistantChatMessages",
            "AssistantChatsFts", "Memories", "MemoriesFts", "Todos",
        })
        {
            Assert.True(Count($"SELECT COUNT(*) FROM {table}") == 0, $"{table} is not empty");
        }
    }

    private void AssertKanbanReseeded()
    {
        var epoch = DateTime.UnixEpoch.ToString("O");
        var rows = new List<(string Id, string Name, long Sort, long IsDefault, long IsClosed, string Created, string Updated)>();
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT Id, Name, SortOrder, IsDefaultView, IsClosedColumn, CreatedAt, UpdatedAt FROM KanbanColumns ORDER BY SortOrder";
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                    reader.GetInt64(4), reader.GetString(5), reader.GetString(6)));
        }

        Assert.Equal(
            [
                ("00000000-0000-0000-0000-000000000001", "To Do", 0L, 1L, 0L, epoch, epoch),
                ("00000000-0000-0000-0000-000000000002", "Closed", 2147483647L, 0L, 1L, epoch, epoch),
            ],
            rows);
    }

    private void AssertDeviceLocalRowsKept()
    {
        Assert.Equal(1, Count("SELECT COUNT(*) FROM Reminders"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM ScheduledJobs"));
        Assert.Equal(1, Count($"SELECT COUNT(*) FROM ScheduledJobs WHERE Id = '{MeetingJobId}'"));
        Assert.Equal(1, Count($"SELECT COUNT(*) FROM Plugins WHERE Id = '{LocalPluginId}'"));
    }

    private void AssertPluginsReset()
    {
        Assert.Equal(0, Count($"SELECT COUNT(*) FROM Plugins WHERE Id = '{ServerPluginId}'"));
        Assert.Equal(1, Count($"SELECT COUNT(*) FROM Plugins WHERE Id = '{PreloadedPluginId}' AND UserEnabled IS NULL"));
    }

    private void AssertFilesCleared()
    {
        Assert.False(File.Exists(Path.Combine(_roaming, "templates.json")));
        Assert.False(File.Exists(Path.Combine(_roaming, "pending-sync-deletes.json")));
        Assert.False(File.Exists(Path.Combine(_roaming, "policy-cache.json")));

        var providers = JsonSerializer.Deserialize<List<AiProvider>>(
            File.ReadAllText(ProvidersPath), JsonPersistenceService<List<AiProvider>>.JsonOptions);
        var single = Assert.Single(providers!);
        Assert.Equal(AiProviderType.PiaCloud, single.ProviderType);
    }

    private static async Task AssertSettingsResetAsync()
    {
        var settings = await NewSettingsService().GetSettingsAsync();

        Assert.False(settings.SyncEnabled);
        Assert.Null(settings.EncryptedRefreshToken);
        Assert.Equal(new AppSettings().Theme, settings.Theme);
        Assert.True(settings.SuppressSettingsPushAfterReset);
        Assert.True(settings.HasCompletedFirstRunWizard);
        Assert.Equal("dev-1", settings.SyncDeviceId);
    }

    private void Execute(string sql)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Count(string sql)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
