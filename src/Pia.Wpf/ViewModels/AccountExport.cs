using Pia.Services.Interfaces;

namespace Pia.ViewModels;

internal static class AccountExport
{
    public static string? PromptPath(IFileDialogService fileDialogs, ILocalizationService localization)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable(Bootstrapper.DebugAccountExportFileEnvVar) is { Length: > 0 } preset)
            return preset;
#endif
        var path = fileDialogs.PromptSaveFile(
            localization["AccountExport_DialogTitle"],
            "ZIP (*.zip)|*.zip",
            $"pia-export-{DateTime.Now:yyyy-MM-dd}.zip",
            initialDirectory: null);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>False when the export failed; cancellation still throws.</summary>
    public static async Task<bool> TrySaveAsync(IAccountDataService accountData, string path, CancellationToken ct)
    {
        try
        {
            await accountData.ExportToFileAsync(path, ct);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
