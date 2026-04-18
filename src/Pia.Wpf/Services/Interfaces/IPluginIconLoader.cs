namespace Pia.Services.Interfaces;

public interface IPluginIconLoader
{
    Task<object?> LoadIconAsync(string? iconUrl, string? serverUrl, CancellationToken ct = default);
}
