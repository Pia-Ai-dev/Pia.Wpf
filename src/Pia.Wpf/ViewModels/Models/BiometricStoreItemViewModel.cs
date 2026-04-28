using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Pia.Services.Consent.Biometric;

namespace Pia.ViewModels.Models;

public partial class BiometricStoreItemViewModel : ObservableObject
{
    private readonly IBiometricConsentStore _store;
    private readonly ILogger _logger;

    public Guid Id { get; }
    public DateTimeOffset GrantedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string PromptVersionHash { get; }

    [ObservableProperty] private string _displayName;

    public BiometricStoreItemViewModel(
        Guid id,
        string displayName,
        DateTimeOffset grantedAt,
        DateTimeOffset expiresAt,
        string promptVersionHash,
        IBiometricConsentStore store,
        ILogger logger)
    {
        Id = id;
        GrantedAt = grantedAt;
        ExpiresAt = expiresAt;
        PromptVersionHash = promptVersionHash;
        _displayName = displayName;
        _store = store;
        _logger = logger;
    }

    partial void OnDisplayNameChanged(string value)
    {
        _ = SaveRenameAsync(value);
    }

    private async Task SaveRenameAsync(string newName)
    {
        try { await _store.RenameAsync(Id, newName); }
        catch (Exception ex) { _logger.LogWarning(ex, "Rename failed for biometric entry {Id}", Id); }
    }
}
