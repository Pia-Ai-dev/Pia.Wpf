using Pia.Models;
using Pia.Services.Screen;
using Pia.ViewModels;
using Pia.ViewModels.Models;

namespace Pia.Services.Interfaces;

public interface IDialogService
{
    Task<bool> ShowProviderEditDialogAsync(ProviderEditModel provider, IProviderService providerService);
    Task<bool> ShowTodoEditDialogAsync(TodoEditModel todo);
    Task<bool> ShowMeetingSaveDialogAsync(MeetingSaveEditModel meeting);
    /// <summary>True when the user chose Send; the report itself is built by <see cref="IAiFeedbackService"/>.</summary>
    Task<bool> ShowAiFeedbackDialogAsync(AiFeedbackEditModel feedback);

    /// <summary>Asks for a filename and a destination; the edit model carries the name and the open-after flag back.</summary>
    Task<AnswerExportDestination> ShowAnswerExportDialogAsync(AnswerExportEditModel export);
    /// <summary>True when the user confirmed the deletion; the caller then sends it with the dialog's password.</summary>
    Task<bool> ShowAccountDeletionDialogAsync(AccountDeletionViewModel viewModel);
    Task<bool> ShowConfirmationDialogAsync(string title, string message);

    /// <summary>A confirmation that also carries back a "don't ask again" tick — where the suppression is
    /// stored is the caller's business. Declining is the answer for a dialog that could not be shown.</summary>
    Task<OptOutConfirmation> ShowOptOutConfirmationDialogAsync(string title, string message, string confirmText);

    Task ShowMessageDialogAsync(string title, string message);
    Task ShowRecoveryCodeDialogAsync(string recoveryCode);
    Task<ModelDownloadResult> ShowModelDownloadDialogAsync(string modelName, IProgress<ModelDownloadProgress> progress, CancellationToken cancellationToken);
    Task<bool> ShowOptimizingDialogAsync(string[] messages, CancellationToken cancellationToken);
    Task<KeyboardShortcut?> ShowHotkeyCaptureDialogAsync();
    Task<bool> ShowRecordingDialogAsync(CancellationToken cancellationToken);
    Task<bool> ShowTranscribingDialogAsync(CancellationToken cancellationToken);
    Task<string?> ShowInputDialogAsync(string title, string prompt, string? initialValue = null);

    /// <summary>True once the user affirmed the selection; the caller then awaits
    /// <see cref="AssignmentConsentViewModel.SendAsync"/> and surfaces its <c>ResultMessage</c>.</summary>
    Task<bool> ShowAssignmentConsentDialogAsync(AssignmentConsentViewModel viewModel);

    /// <summary>True when the user pressed Capture; the caller then awaits
    /// <see cref="ScreenCapturePickerViewModel.CaptureSelectedAsync"/> for the frame.</summary>
    Task<bool> ShowScreenCapturePickerDialogAsync(ScreenCapturePickerViewModel viewModel);

    /// <summary>The window the user named, or null when they backed out. Nothing is captured — the same
    /// picker is used only to spell a window that is already open.</summary>
    Task<CaptureTarget?> ShowScreenCaptureWindowPickerAsync();

    /// <summary>
    /// Shows the Memory-vault help as a modal dialog overlay (rather than an inline card that reflows
    /// the page). <paramref name="vaultRoot"/> backs the dialog's "open memory vault" affordance.
    /// </summary>
    Task ShowMemoryHelpDialogAsync(string vaultRoot);

    /// <summary>
    /// Shows a determinate folder-move progress dialog driven by <paramref name="progress"/> while
    /// <paramref name="work"/> runs, then closes it. Used by the assistant-folder relocation flow.
    /// </summary>
    Task ShowFolderMoveDialogAsync(IProgress<FolderMoveProgress> progress, Func<Task> work);
}

public record ModelDownloadResult(bool Completed, bool Cancelled);

/// <summary>A struct, not a record class, so an unstubbed test double answers "declined" instead of null.</summary>
public readonly record struct OptOutConfirmation(bool Confirmed, bool DontAskAgain);
