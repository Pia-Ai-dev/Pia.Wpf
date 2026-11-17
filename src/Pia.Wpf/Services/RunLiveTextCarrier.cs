using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

public sealed class RunLiveTextCarrier : IRunLiveTextCarrier
{
    private readonly ILogger<RunLiveTextCarrier> _logger;

    public RunLiveTextCarrier(ILogger<RunLiveTextCarrier> logger)
    {
        _logger = logger;
    }

    public event Action<RunLiveTextChunk>? TextAppended;

    public void Publish(RunLiveTextChunk chunk)
    {
        if (TextAppended is not { } handlers) return;

        // A faulting panel must never fail the job that streams into it.
        foreach (var handler in handlers.GetInvocationList().Cast<Action<RunLiveTextChunk>>())
        {
            try
            {
                handler(chunk);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Live text subscriber for run {RunId} threw {ExceptionType}",
                    chunk.RunId, ex.GetType().Name);
            }
        }
    }
}
