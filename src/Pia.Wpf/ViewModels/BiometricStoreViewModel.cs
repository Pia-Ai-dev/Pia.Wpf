using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Services.Consent.Biometric;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

public partial class BiometricStoreViewModel : ObservableObject
{
    private readonly IBiometricConsentStore _store;
    private readonly IDialogService _dialogService;
    private readonly ILogger<BiometricStoreViewModel> _logger;

    public ObservableCollection<BiometricStoreItemViewModel> Entries { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isEmpty;

    public BiometricStoreViewModel(
        IBiometricConsentStore store,
        IDialogService dialogService,
        ILogger<BiometricStoreViewModel> logger)
    {
        _store = store;
        _dialogService = dialogService;
        _logger = logger;
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            var all = await _store.GetAllAsync();
            Entries.Clear();
            foreach (var e in all)
                Entries.Add(new BiometricStoreItemViewModel(
                    e.Id, e.DisplayName, e.GrantedAt, e.ExpiresAt, e.PromptVersionHash, _store, _logger));
            IsEmpty = Entries.Count == 0;
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to load biometric store"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task DeleteAsync(BiometricStoreItemViewModel? item)
    {
        if (item is null) return;
        var ok = await _dialogService.ShowConfirmationDialogAsync(
            "Stimmprofil löschen",
            $"Soll das gespeicherte Stimmprofil „{item.DisplayName}\" wirklich gelöscht werden?");
        if (!ok) return;
        try
        {
            await _store.RemoveAsync(item.Id);
            Entries.Remove(item);
            IsEmpty = Entries.Count == 0;
        }
        catch (Exception ex) { _logger.LogError(ex, "Delete failed for biometric entry {Id}", item.Id); }
    }

    [RelayCommand]
    public async Task DeleteAllAsync()
    {
        var ok = await _dialogService.ShowConfirmationDialogAsync(
            "Alle gespeicherten Stimmen löschen",
            "Sollen alle gespeicherten Stimmprofile unwiderruflich gelöscht werden?");
        if (!ok) return;
        try
        {
            foreach (var entry in Entries.ToList())
                await _store.RemoveAsync(entry.Id);
            Entries.Clear();
            IsEmpty = true;
        }
        catch (Exception ex) { _logger.LogError(ex, "Bulk delete failed for biometric store"); }
    }
}
