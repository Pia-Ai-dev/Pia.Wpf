using Pia.Models;
using Pia.Shared.E2EE;

namespace Pia.Services.Interfaces;

public interface ISyncClientService
{
    /// <summary>Whether sync is currently active.</summary>
    bool IsSyncActive { get; }

    /// <summary>
    /// Whether this device currently needs to complete E2EE onboarding before
    /// sync can proceed. Set to true when <see cref="E2EEOnboardingRequired"/>
    /// fires and back to false after <see cref="NotifyE2EEOnboardingCompleted"/>.
    /// </summary>
    bool IsE2EEOnboardingRequired { get; }

    /// <summary>
    /// Raised when sync detects E2EE is enabled but UMK is not available.
    /// The new device needs to complete the onboarding flow before sync can proceed.
    /// </summary>
    event EventHandler? E2EEOnboardingRequired;

    /// <summary>
    /// Raised when E2EE onboarding has completed and normal sync can resume.
    /// </summary>
    event EventHandler? E2EEOnboardingCleared;

    /// <summary>
    /// Called by onboarding UI once the device has successfully onboarded so
    /// that app-wide indicators can clear.
    /// </summary>
    void NotifyE2EEOnboardingCompleted();

    /// <summary>
    /// Called by onboarding UI when it detects (outside of sync) that
    /// onboarding is required — e.g. after sign-in against an account with
    /// E2EE already enabled. Ensures listeners can show the warning before
    /// the first sync cycle.
    /// </summary>
    void NotifyE2EEOnboardingRequired();

    /// <summary>
    /// Raised when the sync cycle detects a pending device waiting for approval.
    /// Only fires on an active device with E2EE ready.
    /// </summary>
    event EventHandler<PendingDeviceEventArgs>? PendingDeviceDetected;

    /// <summary>
    /// Raised when the current device is no longer in the server's device list
    /// (or has been revoked). Subscribers should disable E2EE locally.
    /// </summary>
    event EventHandler? CurrentDeviceRevoked;

    /// <summary>
    /// Raised once after a successful pull cycle, regardless of whether anything
    /// changed. Allows view-models to re-validate state (e.g. provider mode
    /// defaults) without depending on per-entity ProvidersChanged notifications,
    /// which do not fire for settings imports.
    /// </summary>
    event EventHandler<SyncCompletedEventArgs>? SyncCompleted;

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

    /// <summary>
    /// Resets the sync cursor and performs a full pull from the server.
    /// Use when a previous sync failed to receive data.
    /// </summary>
    Task ForceFullResyncAsync();
}
