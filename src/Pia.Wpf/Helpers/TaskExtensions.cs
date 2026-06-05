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

    /// <summary>
    /// Delay by <paramref name="delayMs"/>, then await <paramref name="action"/>.
    /// Caller is expected to cancel via <paramref name="ct"/> to debounce.
    /// </summary>
    public static async Task DebounceAsync(int delayMs, Func<Task> action, CancellationToken ct)
    {
        await Task.Delay(delayMs, ct);
        await action();
    }
}
