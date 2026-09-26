using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public sealed partial class AccountDeletionViewModel : ObservableObject
{
    private readonly IAccountDataService _accountData;
    private readonly IFileDialogService _fileDialogs;
    private readonly ILocalizationService _localization;

    public AccountDeletionViewModel(
        IAccountDataService accountData,
        IFileDialogService fileDialogs,
        ILocalizationService localization,
        bool requiresPassword)
    {
        _accountData = accountData;
        _fileDialogs = fileDialogs;
        _localization = localization;
        RequiresPassword = requiresPassword;
    }

    public bool RequiresPassword { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete), nameof(IsConfirmationIncomplete))]
    private string _password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete), nameof(IsConfirmationIncomplete))]
    private bool _isUnderstood;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isExporting;

    [ObservableProperty]
    private string? _exportStatus;

    public bool IsConfirmationIncomplete => !IsUnderstood || (RequiresPassword && Password.Length == 0);

    public bool CanDelete => !IsConfirmationIncomplete && !IsExporting;

    [RelayCommand]
    private async Task ExportAsync(CancellationToken ct)
    {
        var path = AccountExport.PromptPath(_fileDialogs, _localization);
        if (path is null) return;

        IsExporting = true;
        try
        {
            var saved = await AccountExport.TrySaveAsync(_accountData, path, ct);
            ExportStatus = _localization[saved ? "AccountDeletion_ExportDone" : "AccountDeletion_ExportFailed"];
        }
        catch (OperationCanceledException)
        {
            // Only closing the dialog cancels, so nobody is left to tell.
        }
        finally
        {
            IsExporting = false;
        }
    }
}
