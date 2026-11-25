namespace Pia.Services.Interfaces;

public interface IChatSyncFlusher
{
    /// <summary>True once every queued chat change and the initial chat upload reached the server, or when chat sync is off this session.</summary>
    Task<bool> FlushAsync(TimeSpan timeout, CancellationToken ct = default);
}
