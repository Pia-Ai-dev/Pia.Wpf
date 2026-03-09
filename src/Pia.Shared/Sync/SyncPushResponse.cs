namespace Pia.Shared.Sync;

/// <summary>
/// Response from POST /api/sync/push.
/// </summary>
public class SyncPushResponse
{
    public DateTime ServerTimestamp { get; set; }
    public List<SyncConflict> Conflicts { get; set; } = [];
}
