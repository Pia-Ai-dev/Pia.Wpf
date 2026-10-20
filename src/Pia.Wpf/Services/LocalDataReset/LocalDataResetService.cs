using Microsoft.Data.Sqlite;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services.LocalDataReset;

public sealed class LocalDataResetService(
    SqliteContext db,
    ITemplateService templates,
    IProviderService providers,
    ISettingsService settings,
    IMemoryService memory,
    IAuthService auth,
    ISyncClientService sync,
    IChatSyncFlusher chats,
    IAppRestartService restart) : ILocalDataResetService
{
    private static readonly string[] SyncedTables = ["Personas", "Sessions", "AssistantChats", "Memories", "Todos"];

    public bool IsResetPending => LocalDataResetMarker.IsPending;

    public void ScheduleReset() => LocalDataResetMarker.Write();

    public Task RestartAsync() => restart.RestartAsync();

    public async Task<bool> HasSyncedLocalDataAsync()
    {
        if ((await templates.GetTemplatesAsync()).Any(t => !t.IsBuiltIn))
            return true;

        if ((await providers.GetProvidersAsync()).Any(p => p.ProviderType != AiProviderType.PiaCloud))
            return true;

        var connection = db.GetConnection();
        return SyncedTables.Any(table => Exists(connection, $"SELECT 1 FROM {table}"))
            || Exists(connection, "SELECT 1 FROM ScheduledJobs WHERE Kind <> 'MeetingAttendance'")
            || HasCustomKanbanColumns(connection);
    }

    public async Task<LocalDataNotices> GetNoticesAsync()
    {
        var vaultHasMemories = (await memory.ListMemoriesAsync()).Items.Count > 0;
        return new LocalDataNotices(vaultHasMemories, await GetApiKeyNoticeAsync());
    }

    public async Task<bool> UploadPendingChangesAsync()
    {
        var wasActive = sync.IsSyncActive;
        try
        {
            await sync.StopBackgroundSyncAndWaitAsync();
            var result = await sync.SyncNowAsync();
            var chatsOk = await chats.FlushAsync(TimeSpan.FromSeconds(30));
            return result is { PushSucceeded: true } && chatsOk;
        }
        finally
        {
            if (wasActive)
                sync.StartBackgroundSync();
        }
    }

    private async Task<ApiKeyNotice> GetApiKeyNoticeAsync()
    {
        var hasKeys = (await providers.GetProvidersAsync())
            .Any(p => p.ProviderType != AiProviderType.PiaCloud && !string.IsNullOrEmpty(p.EncryptedApiKey));
        if (!hasKeys)
            return ApiKeyNotice.None;

        // Signed out, ForgetE2EEKey has already reset IsE2EEEnabled, so the account's mode is unknown.
        if (!auth.IsLoggedIn)
            return ApiKeyNotice.MayBeLost;

        return (await settings.GetSettingsAsync()).IsE2EEEnabled ? ApiKeyNotice.None : ApiKeyNotice.WillBeLost;
    }

    private static bool HasCustomKanbanColumns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM KanbanColumns
            WHERE NOT ((Id = @todoId AND Name = @todoName AND SortOrder = @todoSort)
                    OR (Id = @closedId AND Name = @closedName AND SortOrder = @closedSort))
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@todoId", SqliteContext.DefaultKanbanColumnId);
        command.Parameters.AddWithValue("@todoName", SqliteContext.DefaultKanbanColumnName);
        command.Parameters.AddWithValue("@todoSort", SqliteContext.DefaultKanbanColumnSortOrder);
        command.Parameters.AddWithValue("@closedId", SqliteContext.ClosedKanbanColumnId);
        command.Parameters.AddWithValue("@closedName", SqliteContext.ClosedKanbanColumnName);
        command.Parameters.AddWithValue("@closedSort", SqliteContext.ClosedKanbanColumnSortOrder);
        return command.ExecuteScalar() is not null;
    }

    private static bool Exists(SqliteConnection connection, string selectOne)
    {
        using var command = connection.CreateCommand();
        command.CommandText = selectOne + " LIMIT 1";
        return command.ExecuteScalar() is not null;
    }
}
