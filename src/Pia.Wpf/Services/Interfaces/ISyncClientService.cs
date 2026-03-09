namespace Pia.Services.Interfaces;

public interface ISyncClientService
{
    /// <summary>Whether sync is currently active.</summary>
    bool IsSyncActive { get; }

    /// <summary>Triggers a full sync cycle (push then pull).</summary>
    Task SyncNowAsync();

    /// <summary>Starts the background sync timer.</summary>
    void StartBackgroundSync();

    /// <summary>Stops the background sync timer.</summary>
    void StopBackgroundSync();

    /// <summary>Stops background sync and waits for any in-progress sync to finish.</summary>
    Task StopBackgroundSyncAndWaitAsync();

    /// <summary>Performs first-sync migration (uploads all local data to server).</summary>
    Task PerformFirstSyncMigrationAsync();
}
