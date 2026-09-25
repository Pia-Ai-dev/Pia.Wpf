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
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private string _password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isUnderstood;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isExporting;

    [ObservableProperty]
    private string? _exportStatus;

    public bool CanDelete => IsUnderstood && !IsExporting && (!RequiresPassword || Password.Length > 0);

    public static string? PromptExportPath(IFileDialogService fileDialogs, ILocalizationService localization) =>
        fileDialogs.PromptSaveFile(
            localization["AccountExport_DialogTitle"],
            "ZIP (*.zip)|*.zip",
            $"pia-export-{DateTime.Now:yyyy-MM-dd}.zip",
            initialDirectory: null);

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = PromptExportPath(_fileDialogs, _localization);
        if (string.IsNullOrWhiteSpace(path)) return;

        IsExporting = true;
        try
        {
            await _accountData.ExportToFileAsync(path);
            ExportStatus = _localization["AccountDeletion_ExportDone"];
        }
        catch (Exception)
        {
            ExportStatus = _localization["AccountDeletion_ExportFailed"];
        }
        finally
        {
            IsExporting = false;
        }
    }
}
