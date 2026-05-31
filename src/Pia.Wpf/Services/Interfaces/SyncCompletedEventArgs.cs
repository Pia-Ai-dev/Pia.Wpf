namespace Pia.Services.Interfaces;

public class SyncCompletedEventArgs : EventArgs
{
    public required int MergeInserted { get; init; }
    public required int MergeUpdated { get; init; }
    public required int MergeDeleted { get; init; }
    public required int DecryptionErrors { get; init; }
    public required bool SettingsChanged { get; init; }
    public required bool ProvidersChanged { get; init; }
}
