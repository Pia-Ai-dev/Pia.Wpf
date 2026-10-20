using Microsoft.Extensions.Logging;

namespace Pia.Services.LocalDataReset;

internal static class LocalDataResetStartup
{
    public static async Task<bool> RunIfPendingAsync(Func<Task> wipe, ILogger logger)
    {
        if (!LocalDataResetMarker.IsPending)
        {
            return false;
        }

        try
        {
            await wipe();
            LocalDataResetMarker.Clear();
            logger.LogInformation("Pending local-data clear finished");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pending local-data clear failed; it will be retried at the next start");
            return false;
        }
    }
}
