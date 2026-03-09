using Pia.Models;
using Pia.ViewModels.Models;

namespace Pia.Services.Interfaces;

public interface IDialogService
{
    Task<bool> ShowProviderEditDialogAsync(ProviderEditModel provider, IProviderService providerService);
    Task<bool> ShowTemplateEditDialogAsync(TemplateEditModel template);
    Task ShowSessionDetailDialogAsync(OptimizationSession session);
    Task<bool> ShowConfirmationDialogAsync(string title, string message);
    Task ShowMessageDialogAsync(string title, string message);
    Task<bool> ShowMessageWithCopyDialogAsync(string title, string message);
    Task<ModelDownloadResult> ShowModelDownloadDialogAsync(string modelName, IProgress<ModelDownloadProgress> progress, CancellationToken cancellationToken);
    Task<bool> ShowOptimizingDialogAsync(string[] messages, CancellationToken cancellationToken);
    Task<KeyboardShortcut?> ShowHotkeyCaptureDialogAsync();
    Task<bool> ShowRecordingDialogAsync(CancellationToken cancellationToken);
    Task<bool> ShowTranscribingDialogAsync(CancellationToken cancellationToken);
}

public record ModelDownloadResult(bool Completed, bool Cancelled);
