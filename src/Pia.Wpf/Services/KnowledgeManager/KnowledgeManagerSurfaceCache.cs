using Microsoft.Extensions.Logging;

namespace Pia.Services.KnowledgeManager;

/// <summary>A cached flag rather than a live probe, because the plugin host reads it on every turn.</summary>
public interface IKnowledgeManagerSurfaceCache
{
    bool IsAvailable { get; }

    /// <summary>Raised when <see cref="IsAvailable"/> flips, not on every refresh.</summary>
    event EventHandler? Changed;

    Task<bool> RefreshAsync(CancellationToken ct = default);

    /// <summary>A 403 mid-session means the role was revoked; the next turn must not offer the tools.</summary>
    void Hide();
}

public sealed class KnowledgeManagerSurfaceCache : IKnowledgeManagerSurfaceCache
{
    private readonly IKnowledgeManagerApiClient _api;
    private readonly ILogger<KnowledgeManagerSurfaceCache> _logger;
    private volatile bool _available;

    public KnowledgeManagerSurfaceCache(IKnowledgeManagerApiClient api, ILogger<KnowledgeManagerSurfaceCache> logger)
    {
        _api = api;
        _logger = logger;
    }

    public bool IsAvailable => _available;

    public event EventHandler? Changed;

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        bool next;
        try
        {
            next = (await _api.ListKnowledgeBasesAsync(ct)).Status == KbManagerCallStatus.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "Could not probe the knowledge-base manager surface; keeping it hidden.");
            next = false;
        }

        Apply(next);
        return next;
    }

    public void Hide() => Apply(false);

    private void Apply(bool next)
    {
        if (_available == next) return;
        _available = next;
        _logger.LogInformation("Knowledge-base manager surface is now {State}.", next ? "available" : "hidden");
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
