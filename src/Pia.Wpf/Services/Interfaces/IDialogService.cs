using Pia.Models;
using Pia.ViewModels.Models;

namespace Pia.Services.Interfaces;

public interface IDialogService
{
    Task<bool> ShowProviderEditDialogAsync(ProviderEditModel provider, IProviderService providerService);
    Task<bool> ShowTemplateEditDialogAsync(TemplateEditModel template);
    Task<bool> ShowPersonaEditDialogAsync(PersonaEditModel persona);
    Task<bool> ShowTodoEditDialogAsync(TodoEditModel todo);
    Task<bool> ShowConfirmationDialogAsync(string title, string message);
    Task ShowMessageDialogAsync(string title, string message);
    Task ShowRecoveryCodeDialogAsync(string recoveryCode);
    Task<ModelDownloadResult> ShowModelDownloadDialogAsync(string modelName, IProgress<ModelDownloadProgress> progress, CancellationToken cancellationToken);
    Task<bool> ShowOptimizingDialogAsync(string[] messages, CancellationToken cancellationToken);
    Task<KeyboardShortcut?> ShowHotkeyCaptureDialogAsync();
    Task<bool> ShowRecordingDialogAsync(CancellationToken cancellationToken);
    Task<bool> ShowTranscribingDialogAsync(CancellationToken cancellationToken);
    Task<string?> ShowInputDialogAsync(string title, string prompt);

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
