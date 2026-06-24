using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Consent;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class SecurityModeViewModel : ObservableObject
{
    private readonly ISecurityModeProvider _provider;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<SecurityModeViewModel> _logger;
    private bool _suppressApply;

    [ObservableProperty] private bool _isStrictSelected;
    [ObservableProperty] private bool _isStandardSelected;
    [ObservableProperty] private bool _isPermissiveSelected;

    public SecurityModeViewModel(
        ISecurityModeProvider provider,
        IDialogService dialogService,
        ILocalizationService localizationService,
        ILogger<SecurityModeViewModel> logger)
    {
        _provider = provider;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _logger = logger;
        SyncFromCurrent();
        _provider.ProfileChanged += (_, _) => SyncFromCurrent();
    }

    private void SyncFromCurrent()
    {
        _suppressApply = true;
        var mode = _provider.Current.Mode;
        IsStrictSelected = mode == SecurityMode.Strict;
        IsStandardSelected = mode == SecurityMode.Standard;
        IsPermissiveSelected = mode == SecurityMode.Permissive;
        _suppressApply = false;
    }

    partial void OnIsStrictSelectedChanged(bool value) { if (value) _ = ApplyAsync(SecurityMode.Strict); }
    partial void OnIsStandardSelectedChanged(bool value) { if (value) _ = ApplyAsync(SecurityMode.Standard); }
    partial void OnIsPermissiveSelectedChanged(bool value) { if (value) _ = ApplyAsync(SecurityMode.Permissive); }

    private async Task ApplyAsync(SecurityMode mode)
    {
        if (_suppressApply) return;
        if (_provider.Current.Mode == mode) return;

        if (mode == SecurityMode.Permissive)
        {
            var ok = await _dialogService.ShowConfirmationDialogAsync(
                _localizationService["SecurityMode_Permissive_Confirm_Title"],
                _localizationService["SecurityMode_Permissive_Confirm_Body"]);
            if (!ok)
            {
                SyncFromCurrent();
                return;
            }
        }

        try
        {
            await _provider.SetModeAsync(mode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply security mode {Mode}", mode);
            SyncFromCurrent();
        }
    }
}
