namespace Pia.Shared.Sync;

using Pia.Shared.Models;

/// <summary>
/// Request body for POST /api/sync/push.
/// Contains all local changes since last sync.
/// </summary>
public class SyncPushRequest
{
    public DateTime ClientTimestamp { get; set; }
    public DateTime LastSyncTimestamp { get; set; }
    public string? DeviceId { get; set; }

    /// <summary>
    /// When true, entity content fields are null and EncryptedPayload/WrappedDek
    /// contain the E2EE-encrypted data. Server must not attempt to read content.
    /// </summary>
    public bool IsE2EEEncrypted { get; set; }

    /// <summary>Settings if modified locally, null otherwise.</summary>
    public SyncSettings? Settings { get; set; }

    public SyncEntityChanges<SyncTemplate> Templates { get; set; } = new();
    public SyncEntityChanges<SyncProvider> Providers { get; set; } = new();
    public SyncSessionChanges Sessions { get; set; } = new();
    public SyncEntityChanges<SyncMemory> Memories { get; set; } = new();
    public SyncEntityChanges<SyncTodo> Todos { get; set; } = new();
}
