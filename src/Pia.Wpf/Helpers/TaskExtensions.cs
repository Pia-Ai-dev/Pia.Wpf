using Microsoft.Extensions.Logging;

namespace Pia.Helpers;

public static class TaskExtensions
{
    /// <summary>
    /// Fire-and-forget a task with error logging.
    /// OperationCanceledException is silently suppressed.
    /// </summary>
    public static async void SafeFireAndForget(this Task task, ILogger logger)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background operation failed");
        }
    }
}
