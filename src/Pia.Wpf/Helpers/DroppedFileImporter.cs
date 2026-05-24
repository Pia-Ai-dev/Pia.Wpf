using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Pia.Helpers;

/// <summary>
/// Reads dropped files into a single combined text payload, surfacing localized
/// snackbars for unsupported types, size limits, and read failures. Used by the
/// Assistant and Optimize file-drop flows so both views share identical behavior.
/// </summary>
public static class DroppedFileImporter
{
    public static async Task<string?> TryImportAsync(
        IReadOnlyList<string> paths,
        ILogger logger,
        ISnackbarService snackbarService,
        ILocalizationService localizationService,
        CancellationToken ct = default)
    {
        if (paths.Count == 0) return null;

        var combined = new StringBuilder();

        foreach (var path in paths)
        {
            var kind = DroppedFileReader.Classify(path);
            var fileName = Path.GetFileName(path);

            DroppedFileReader.ReadResult result;
            switch (kind)
            {
                case FileKind.Text:
                    result = await DroppedFileReader.ReadTextAsync(path, ct);
                    break;
                case FileKind.Docx:
                    result = await DroppedFileReader.ReadDocxAsync(path, ct);
                    break;
                case FileKind.Xlsx:
                    result = await DroppedFileReader.ReadXlsxAsync(path, ct);
                    break;
                default:
                    // Image / Pdf / Audio / Unsupported — Stage 2 will replace Image and Pdf
                    // with vision attachments. Stage 1: surface a snackbar so the user gets
                    // immediate feedback that nothing was inserted.
                    logger.LogInformation("File drop rejected for kind {Kind}", kind);
                    snackbarService.Show(
                        localizationService["Msg_Warning"],
                        localizationService.Format("Msg_File_Unsupported", fileName),
                        ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
                    continue;
            }

            switch (result.Status)
            {
                case DroppedFileReader.ReadStatus.Ok when !string.IsNullOrEmpty(result.Text):
                    if (combined.Length > 0)
                        combined.AppendLine().AppendLine("---").AppendLine();
                    combined.Append(result.Text);
                    break;
                case DroppedFileReader.ReadStatus.TooLarge:
                    snackbarService.Show(
                        localizationService["Msg_Warning"],
                        localizationService.Format("Msg_File_TooLarge", fileName),
                        ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
                    break;
                case DroppedFileReader.ReadStatus.Failed:
                    logger.LogError("File drop read failed for {Kind}: {Error}", kind, result.Error);
                    snackbarService.Show(
                        localizationService["Msg_Error"],
                        localizationService.Format("Msg_File_ReadFailed", fileName, result.Error ?? string.Empty),
                        ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
                    break;
            }
        }

        return combined.Length > 0 ? combined.ToString() : null;
    }
}
