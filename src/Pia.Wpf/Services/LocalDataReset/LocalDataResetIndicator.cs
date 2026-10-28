using Pia.Services.Interfaces;

namespace Pia.Services.LocalDataReset;

public sealed class LocalDataResetIndicator : ILocalDataResetIndicator
{
    public bool IsResetPending => LocalDataResetMarker.IsPending;
}
