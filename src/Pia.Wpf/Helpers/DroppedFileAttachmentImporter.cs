using System.IO;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Pia.Helpers;

/// <summary>Stages dropped files as pending attachment chips; the sibling <see cref="DroppedFileImporter"/>
/// inlines their text into the composer instead.</summary>
public static class DroppedFileAttachmentImporter
{
    public const int MaxPendingFiles = 5;
    public const int MaxFileChars = 20_000;
    public const int MaxTotalChars = 40_000;

    /// <summary>The kinds this staging path can read. A drop target's accepted-extension list is checked
    /// against this, so widening one without the other cannot silently discard a supported file.</summary>
    public static readonly IReadOnlySet<FileKind> ReadableKinds =
        new HashSet<FileKind> { FileKind.Text, FileKind.Docx, FileKind.Xlsx, FileKind.Email };

    public sealed record StageResult(
        IReadOnlyList<PendingFileAttachment> Staged,
        IReadOnlyList<string> ImagePaths);

    /// <summary>Reads the non-image drops into pending chips and hands back the image paths untouched,
    /// so one drop can carry both.</summary>
    public static async Task<StageResult> TryStageAsync(
        IReadOnlyList<string> paths,
        IReadOnlyCollection<PendingFileAttachment> alreadyPending,
        ILogger logger,
        ISnackbarService snackbarService,
        ILocalizationService localizationService,
        CancellationToken ct = default)
    {
        var staged = new List<PendingFileAttachment>();
        var images = new List<string>();
        if (paths.Count == 0) return new StageResult(staged, images);

        var usedChars = alreadyPending.Sum(p => p.Text.Length);

        foreach (var path in paths)
        {
            var kind = DroppedFileReader.Classify(path);
            var fileName = Path.GetFileName(path);

            if (kind == FileKind.Image)
            {
                images.Add(path);
                continue;
            }

            if (!ReadableKinds.Contains(kind))
            {
                logger.LogInformation("File attach rejected for kind {Kind}", kind);
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_UnsupportedAttachment", fileName));
                continue;
            }

            if (IsStaged(alreadyPending, staged, path))
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_DuplicateAttachment", fileName));
                continue;
            }

            if (alreadyPending.Count + staged.Count >= MaxPendingFiles)
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_AttachLimit", MaxPendingFiles, fileName));
                continue;
            }

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
                    result = await DroppedFileReader.ReadEmailAsync(path, ct);
                    break;
            }

            if (result.Status == DroppedFileReader.ReadStatus.TooLarge)
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_TooLargeAttachment", fileName, DroppedFileReader.FormatLimit(result.LimitBytes)));
                continue;
            }

            if (result.Status == DroppedFileReader.ReadStatus.Failed)
            {
                logger.LogError("File attach read failed for kind {Kind}", kind);
                logger.SensitiveDebug("File attach read failed for {FileName}: {Error}", fileName, result.Error);
                snackbarService.Show(
                    localizationService["Msg_Error"],
                    localizationService.Format("Msg_File_ReadFailed", fileName, result.Error ?? string.Empty),
                    ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
                continue;
            }

            var text = result.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_Empty", fileName));
                continue;
            }

            var remaining = MaxTotalChars - usedChars;
            if (remaining <= 0)
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_AttachBudget", fileName));
                continue;
            }

            var originalCharCount = text.Length;
            var allowed = Math.Min(MaxFileChars, remaining);
            if (text.Length > allowed) text = text[..allowed];
            var truncated = text.Length < originalCharCount;

            staged.Add(new PendingFileAttachment
            {
                FullPath = path,
                FileName = fileName,
                Kind = ToPendingKind(kind),
                Text = text,
                Truncated = truncated,
                OriginalCharCount = originalCharCount,
            });
            usedChars += text.Length;

            logger.LogInformation(
                "Attached a {Kind} file ({Chars} chars, truncated={Truncated})", kind, text.Length, truncated);
            logger.SensitiveDebug("Attached {FileName}", fileName);

            if (truncated)
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_Truncated", fileName));
            }
        }

        return new StageResult(staged, images);
    }

    private static bool IsStaged(
        IReadOnlyCollection<PendingFileAttachment> alreadyPending,
        List<PendingFileAttachment> staged,
        string path) =>
        alreadyPending.Any(p => string.Equals(p.FullPath, path, StringComparison.OrdinalIgnoreCase))
        || staged.Any(p => string.Equals(p.FullPath, path, StringComparison.OrdinalIgnoreCase));

    private static PendingFileKind ToPendingKind(FileKind kind) => kind switch
    {
        FileKind.Email => PendingFileKind.Email,
        FileKind.Docx or FileKind.Xlsx => PendingFileKind.Document,
        _ => PendingFileKind.Text,
    };

    private static void Caution(ISnackbarService snackbarService, ILocalizationService localizationService, string message) =>
        snackbarService.Show(
            localizationService["Msg_Warning"], message, ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
}
