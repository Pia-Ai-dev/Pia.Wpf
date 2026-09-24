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
    /// <param name="onProblem">Takes the localized message instead of the snackbar, which a caller rendering
    /// behind the dialog backdrop needs: a snackbar raised there is never seen. Supplying it makes
    /// <paramref name="snackbarService"/> unused, so such a caller may pass null.</param>
    public static async Task<string?> TryImportAsync(
        IReadOnlyList<string> paths,
        ILogger logger,
        ISnackbarService? snackbarService,
        ILocalizationService localizationService,
        Action<string>? onProblem = null,
        CancellationToken ct = default)
    {
        if (paths.Count == 0) return null;

        var combined = new StringBuilder();

        void Report(string message, ControlAppearance appearance)
        {
            if (onProblem is not null)
            {
                onProblem(message);
                return;
            }

            var title = appearance == ControlAppearance.Danger ? "Msg_Error" : "Msg_Warning";
            snackbarService?.Show(
                localizationService[title], message, appearance, null, TimeSpan.FromSeconds(4));
        }

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
                case FileKind.Pdf:
                    result = await DroppedFileReader.ReadPdfAsync(path, ct);
                    break;
                case FileKind.Email:
                    result = await DroppedFileReader.ReadEmailAsync(path, ct);
                    break;
                default:
                    // Image / Audio / Unsupported. Images become vision attachments on the
                    // assistant path only; here nothing is inserted, so say so.
                    logger.LogInformation("File drop rejected for kind {Kind}", kind);
                    Report(localizationService.Format("Msg_File_Unsupported", fileName), ControlAppearance.Caution);
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
                    Report(
                        localizationService.Format("Msg_File_TooLarge", fileName, DroppedFileReader.FormatLimit(result.LimitBytes)),
                        ControlAppearance.Caution);
                    break;
                case DroppedFileReader.ReadStatus.Failed when result.Error == DroppedFileReader.NoTextLayer:
                    Report(localizationService.Format("Msg_File_PdfNoText", fileName), ControlAppearance.Caution);
                    break;
                case DroppedFileReader.ReadStatus.Failed:
                    logger.LogError("File drop read failed for {Kind}: {Error}", kind, result.Error);
                    Report(
                        localizationService.Format("Msg_File_ReadFailed", fileName, result.Error ?? string.Empty),
                        ControlAppearance.Danger);
                    break;
            }
        }

        return combined.Length > 0 ? combined.ToString() : null;
    }
}
