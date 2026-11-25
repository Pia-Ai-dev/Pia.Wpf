namespace Pia.Services.Interfaces;

// Split from ILocalDataResetService so SyncClientService, which that service depends on, can take it.
public interface ILocalDataResetIndicator
{
    bool IsResetPending { get; }
}
