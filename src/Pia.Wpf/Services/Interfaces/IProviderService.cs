using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IProviderService
{
    event EventHandler? ProvidersChanged;
    Task<IReadOnlyList<AiProvider>> GetProvidersAsync();
    Task<AiProvider?> GetProviderAsync(Guid id);
    Task<AiProvider?> GetDefaultProviderAsync();
    Task<AiProvider?> GetDefaultProviderForModeAsync(WindowMode mode);
    Task<AiProvider> AddProviderAsync(AiProvider provider, string? apiKey);
    Task UpdateProviderAsync(AiProvider provider, string? newApiKey = null);
    Task DeleteProviderAsync(Guid id);
    string? GetDecryptedApiKey(AiProvider provider);
    Task<TestConnectionResult> TestConnectionAsync(AiProvider provider);
    Task<TestConnectionResult> TestConnectionAsync(AiProvider provider, string? plainApiKey);
    Task EnsureBuiltInProviderAsync();
    Task<List<string>> FetchModelsAsync(string endpoint, string? apiKey, AiProviderType providerType);
}

public record TestConnectionResult(bool Success, bool SupportsToolCalling, bool SupportsStreaming);
