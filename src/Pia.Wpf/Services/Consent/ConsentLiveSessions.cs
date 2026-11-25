using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent;

public sealed class ConsentLiveSessions : IConsentLiveSessions
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ConsentLiveSessions> _logger;

    public ConsentLiveSessions(ILogger<ConsentLiveSessions> logger)
    {
        _logger = logger;
    }

    public event EventHandler<string>? SessionEnded;

    public void Register(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) _live.Add(sessionId);
    }

    public void Unregister(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            if (!_live.Remove(sessionId)) return;
        }

        var handler = SessionEnded;
        if (handler is null) return;

        // Each subscriber on its own: one that throws must not keep the next from cleaning up its session.
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<string>)subscriber).Invoke(this, sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SessionEnded subscriber threw");
            }
        }
    }

    public IReadOnlyCollection<string> Snapshot()
    {
        lock (_gate) return _live.ToArray();
    }
}
