namespace Pia.Services.Interfaces;

/// <summary>
/// One-shot probe of the Pia Cloud /api/capabilities endpoint.
/// See docs/server/assistant-chat-history.md §3.
/// </summary>
public interface ICloudCapabilityService
{
    /// <summary>
    /// Returns true if the server supports the assistant-chat sync endpoints
    /// (/api/v1/chats family). Cached for the lifetime of the service.
    /// Any failure (network error, 404, missing flag) returns false.
    /// </summary>
    Task<bool> ChatsSupportedAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the server-advertised chats schema version, or null if the
    /// server didn't report one (or the probe failed).
    /// </summary>
    Task<int?> ChatsSchemaVersionAsync(CancellationToken ct = default);

    /// <summary>
    /// Discard the cached probe result. The next call to <see cref="ChatsSupportedAsync"/>
    /// will re-probe /api/capabilities. Use when a sync operation surfaces a hard signal
    /// that the cache is stale (e.g. 404 from /api/v1/chats after a previous success).
    /// </summary>
    void Invalidate();
}
