namespace Pia.Services.Interfaces;

public enum ApiKeyNotice { None, WillBeLost, MayBeLost }

// A struct, so an unstubbed test double answers "no notices" instead of null.
public readonly record struct LocalDataNotices(bool VaultHasMemories, ApiKeyNotice ApiKeys);

public interface ILocalDataResetService
{
    bool IsResetPending { get; }
    Task<bool> HasSyncedLocalDataAsync();
    Task<LocalDataNotices> GetNoticesAsync();
    Task<bool> UploadPendingChangesAsync();
    void ScheduleReset();
    Task RestartAsync();
}
