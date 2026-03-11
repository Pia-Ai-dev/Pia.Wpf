using Pia.Models;
using Pia.Shared.E2EE;

namespace Pia.Services.Interfaces;

public interface ISyncClientService
{
    /// <summary>Whether sync is currently active.</summary>
    bool IsSyncActive { get; }

    /// <summary>
    /// Raised when sync detects E2EE is enabled but UMK is not available.
    /// The new device needs to complete the onboarding flow before sync can proceed.
    /// </summary>
    event EventHandler? E2EEOnboardingRequired;

    /// <summary>
    /// Raised when the sync cycle detects a pending device waiting for approval.
    /// Only fires on an active device with E2EE ready.
    /// </summary>
    event EventHandler<PendingDeviceEventArgs>? PendingDeviceDetected;

    /// <summary>Triggers a full sync cycle (push then pull). Returns counts, or null if sync was skipped.</summary>
    Task<SyncResult?> SyncNowAsync();

    /// <summary>Starts the background sync timer.</summary>
    void StartBackgroundSync();

    /// <summary>Stops the background sync timer.</summary>
    void StopBackgroundSync();

    /// <summary>Stops background sync and waits for any in-progress sync to finish.</summary>
    Task StopBackgroundSyncAndWaitAsync();

    /// <summary>Performs first-sync migration (uploads all local data to server).</summary>
    Task PerformFirstSyncMigrationAsync();
}
