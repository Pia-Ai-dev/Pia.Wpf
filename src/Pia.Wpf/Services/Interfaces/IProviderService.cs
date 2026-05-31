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
    Task<bool> IsProviderActiveAsync(AiProvider provider);

    /// <summary>
    /// Replaces the row identified by <paramref name="oldId"/> with <paramref name="merged"/>
    /// and assigns it the Guid <paramref name="newId"/>, atomically rewriting any
    /// <c>AppSettings.ModeProviderDefaults</c> references from old to new. Does NOT
    /// emit a sync-delete for the old Id — used during pull-side fingerprint dedupe
    /// where deleting would clobber the row on other devices.
    /// </summary>
    Task ReassignProviderIdAsync(Guid oldId, Guid newId, AiProvider merged);

    /// <summary>
    /// Validates that every mode in <c>AppSettings.ModeProviderDefaults</c> still
    /// references an existing provider. Stale references are replaced with PiaCloud
    /// when present, otherwise with the first available provider, otherwise removed.
    /// Persists settings only if anything changed.
    /// </summary>
    Task RepairModeDefaultsAsync();

    /// <summary>
    /// Collapses any locally-stored duplicate providers that share the same
    /// content fingerprint (<see cref="ProviderFingerprint"/>). Used as a one-shot
    /// startup pass so users already broken by prior sync runs are self-healed
    /// on the next launch without needing a fresh server delta.
    /// </summary>
    Task ConsolidateLocalDuplicatesAsync();
}

public record TestConnectionResult(bool Success, bool SupportsToolCalling, bool SupportsStreaming);
