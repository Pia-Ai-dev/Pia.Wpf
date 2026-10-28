using Pia.Services.Interfaces;

namespace Pia.Services.LocalDataReset;

public sealed class LocalDataResetGate : ILocalDataResetGate
{
    public bool IsResetPending => LocalDataResetMarker.IsPending;
}
