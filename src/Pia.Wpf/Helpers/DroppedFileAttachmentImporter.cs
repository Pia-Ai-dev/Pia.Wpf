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
    // Below this a cut file is a few words, not context.
    public const int MinShownTokens = 200;

    /// <summary>A drop target's accepted-extension list is checked against this, so widening one without the
    /// other cannot silently discard a supported file.</summary>
    public static readonly IReadOnlySet<FileKind> ReadableKinds =
        Enum.GetValues<FileKind>().Where(kind => DroppedFileReader.ReaderFor(kind) is not null).ToHashSet();

    public sealed record StageResult(
        IReadOnlyList<PendingFileAttachment> Staged,
        IReadOnlyList<string> ImagePaths);

    /// <summary>Reads the non-image drops into pending chips and hands back the image paths untouched,
    /// so one drop can carry both.</summary>
    public static async Task<StageResult> TryStageAsync(
        IReadOnlyList<string> paths,
        IReadOnlyCollection<PendingFileAttachment> alreadyPending,
        Func<Task<int>> availableTokens,
        ILogger logger,
        ISnackbarService snackbarService,
        ILocalizationService localizationService,
        CancellationToken ct = default)
    {
        var staged = new List<PendingFileAttachment>();
        var images = new List<string>();
        var readable = new List<(string Path, string FileName, FileKind Kind, string Text)>();

        foreach (var path in paths)
        {
            var kind = DroppedFileReader.Classify(path);
            var fileName = Path.GetFileName(path);

            if (kind == FileKind.Image)
            {
                images.Add(path);
                continue;
            }

            if (DroppedFileReader.ReaderFor(kind) is not { } read)
            {
                logger.LogInformation("File attach rejected for kind {Kind}", kind);
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_UnsupportedAttachment", fileName));
                continue;
            }

            if (IsStaged(alreadyPending, readable, path))
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_DuplicateAttachment", fileName));
                continue;
            }

            if (alreadyPending.Count + readable.Count >= MaxPendingFiles)
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_AttachLimit", MaxPendingFiles, fileName));
                continue;
            }

            var result = await read(path, ct);

            if (result.Status == DroppedFileReader.ReadStatus.TooLarge)
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_TooLargeAttachment", fileName, DroppedFileReader.FormatLimit(result.LimitBytes)));
                continue;
            }

            if (result.Status == DroppedFileReader.ReadStatus.Failed
                && result.Error == DroppedFileReader.NoTextLayer)
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_PdfNoText", fileName));
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

            readable.Add((path, fileName, kind, text));
        }

        if (readable.Count == 0) return new StageResult(staged, images);

        var remaining = await availableTokens() - alreadyPending.Sum(p => TokenEstimator.Estimate(p.Text));
        var needs = readable.Select(r => TokenEstimator.Estimate(r.Text)).ToArray();
        var grants = AttachmentBudget.Distribute(needs, remaining);

        for (var i = 0; i < readable.Count; i++)
        {
            var (path, fileName, kind, text) = readable[i];
            var cut = grants[i] >= needs[i] || grants[i] >= MinShownTokens
                ? TokenEstimator.CutToBudget(text, Math.Max(grants[i], 1))
                : default;

            if (string.IsNullOrWhiteSpace(cut.Text))
            {
                logger.LogInformation("File attach skipped: ~{Needed} tokens against {Remaining} left in the chat budget",
                    needs[i], Math.Max(remaining, 0));
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_AttachBudget", fileName));
                continue;
            }

            var attachmentCut = cut.Truncated
                ? new AttachmentCut(cut.ShownLines, cut.TotalLines, cut.EndsMidLine,
                    Math.Clamp((int)Math.Round(100.0 * TokenEstimator.Estimate(cut.Text) / needs[i]), 1, 99))
                : null;

            staged.Add(new PendingFileAttachment
            {
                FullPath = path,
                FileName = fileName,
                Kind = ToPendingKind(kind),
                Text = cut.Text,
                Cut = attachmentCut,
            });

            logger.LogInformation("Attached a {Kind} file (~{Shown} of ~{Needed} tokens, grant {Grant})",
                kind, TokenEstimator.Estimate(cut.Text), needs[i], grants[i]);
            logger.SensitiveDebug("Attached {FileName}", fileName);

            if (attachmentCut is not null)
            {
                Caution(snackbarService, localizationService,
                    localizationService.Format("Msg_File_Truncated", fileName, attachmentCut.ShownPercent));
            }
        }

        return new StageResult(staged, images);
    }

    private static bool IsStaged(
        IReadOnlyCollection<PendingFileAttachment> alreadyPending,
        List<(string Path, string FileName, FileKind Kind, string Text)> readable,
        string path) =>
        alreadyPending.Any(p => string.Equals(p.FullPath, path, StringComparison.OrdinalIgnoreCase))
        || readable.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    // No discard arm, for the same reason as DroppedFileReader.ReaderFor: a new FileKind must pick its chip.
#pragma warning disable CS8524 // an unnamed FileKind value is a caller bug; throwing on it is right
    private static PendingFileKind ToPendingKind(FileKind kind) => kind switch
    {
        FileKind.Text => PendingFileKind.Text,
        FileKind.Email => PendingFileKind.Email,
        FileKind.Docx or FileKind.Xlsx or FileKind.Pdf => PendingFileKind.Document,
        FileKind.Image or FileKind.Audio or FileKind.Unsupported =>
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a staged text kind."),
    };
#pragma warning restore CS8524

    private static void Caution(ISnackbarService snackbarService, ILocalizationService localizationService, string message) =>
        snackbarService.Show(
            localizationService["Msg_Warning"], message, ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
}
