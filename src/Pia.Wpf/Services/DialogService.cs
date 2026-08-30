using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Pia.Views.Controls;
using Pia.Views.Dialogs;
using Pia.Views.Dialogs.Overlay;
using System.Windows.Automation;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace Pia.Services;

public class DialogService : IDialogService
{
    private readonly IContentDialogService _contentDialogService;
    private readonly IDialogOverlayService _overlayService;
    private readonly IOutputService _outputService;
    private readonly IAudioRecordingService _audioRecordingService;
    private readonly ILocalizationService _localizationService;

    public DialogService(
        IContentDialogService contentDialogService,
        IDialogOverlayService overlayService,
        IOutputService outputService,
        IAudioRecordingService audioRecordingService,
        ILocalizationService localizationService)
    {
        _contentDialogService = contentDialogService;
        _overlayService = overlayService;
        _outputService = outputService;
        _audioRecordingService = audioRecordingService;
        _localizationService = localizationService;
    }

    public async Task<bool> ShowProviderEditDialogAsync(ProviderEditModel provider, IProviderService providerService)
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new ProviderEditContentDialog(dialogHost, provider, providerService);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<bool> ShowTemplateEditDialogAsync(TemplateEditModel template)
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new TemplateEditContentDialog(dialogHost, template);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<bool> ShowPersonaEditDialogAsync(PersonaEditModel persona)
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new PersonaEditContentDialog(dialogHost, persona);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<bool> ShowTodoEditDialogAsync(TodoEditModel todo)
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new TodoEditContentDialog(dialogHost, todo);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<bool> ShowMeetingSaveDialogAsync(MeetingSaveEditModel meeting)
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new MeetingSaveContentDialog(dialogHost, meeting);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<bool> ShowAiFeedbackDialogAsync(AiFeedbackEditModel feedback)
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new AiFeedbackContentDialog(dialogHost, feedback);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<AnswerExportDestination> ShowAnswerExportDialogAsync(AnswerExportEditModel export)
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new AnswerExportContentDialog(dialogHost, export);

        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => AnswerExportDestination.Vault,
            ContentDialogResult.Secondary => AnswerExportDestination.External,
            _ => AnswerExportDestination.Cancel,
        };
    }

    public async Task<bool> ShowAssignmentConsentDialogAsync(ViewModels.AssignmentConsentViewModel viewModel)
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new AssignmentConsentContentDialog(dialogHost, viewModel);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<bool> ShowConfirmationDialogAsync(string title, string message)
    {
        var result = await _contentDialogService.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = title,
                Content = message,
                PrimaryButtonText = _localizationService["Common_Yes"],
                CloseButtonText = _localizationService["Common_No"]
            });

        return result == ContentDialogResult.Primary;
    }

    public async Task<OptOutConfirmation> ShowOptOutConfirmationDialogAsync(
        string title, string message, string confirmText)
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new OptOutConfirmContentDialog(dialogHost, title, message, confirmText);
        var result = await dialog.ShowAsync();
        return new OptOutConfirmation(result == ContentDialogResult.Primary, dialog.DontAskAgain);
    }

    public async Task ShowMessageDialogAsync(string title, string message)
    {
        await _contentDialogService.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = title,
                Content = message,
                CloseButtonText = _localizationService["Common_OK"]
            });
    }

    public async Task ShowMemoryHelpDialogAsync(string vaultRoot)
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new VaultHelpContentDialog(dialogHost, vaultRoot);
        await dialog.ShowAsync();
    }

    public async Task ShowRecoveryCodeDialogAsync(string recoveryCode)
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new RecoveryCodeContentDialog(dialogHost, recoveryCode, _outputService);
        await dialog.ShowAsync();
    }

    public async Task<ModelDownloadResult> ShowModelDownloadDialogAsync(
        string modelName,
        IProgress<ModelDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new ModelDownloadContentDialog(dialogHost, modelName, progress);
        
        cancellationToken.Register(() =>
        {
            dialog.Hide();
        });

        var result = await dialog.ShowAsync();
        var wasCancelled = result == ContentDialogResult.Secondary || cancellationToken.IsCancellationRequested;

        return new ModelDownloadResult(
            Completed: !wasCancelled,
            Cancelled: wasCancelled);
    }

    public async Task ShowFolderMoveDialogAsync(IProgress<FolderMoveProgress> progress, Func<Task> work)
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new FolderMoveContentDialog(dialogHost, progress);

        // Show without awaiting, run the move, then close. The move reports through `progress`,
        // which marshals back to the UI thread inside the dialog.
        var showTask = dialog.ShowAsync();
        try
        {
            await work();
        }
        finally
        {
            dialog.Hide();
        }

        await showTask;
    }

    public async Task<bool> ShowOptimizingDialogAsync(string[] messages, CancellationToken cancellationToken)
    {
        var host = _overlayService.GetOverlayHost();
        var content = new OptimizingOverlayPanel(messages);
        var panel = new OverlayDialogPanel
        {
            Content = content,
            MaxPanelWidth = 400,
            CloseButtonText = _localizationService["Common_Cancel"]
        };
        panel.ResultChosen += _ => content.StopTimer();
        await host.ShowAsync<OverlayDialogResult>(panel, cancellationToken);
        content.StopTimer();
        return cancellationToken.IsCancellationRequested;
    }

    public async Task<KeyboardShortcut?> ShowHotkeyCaptureDialogAsync()
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new HotkeyCaptureContentDialog(dialogHost);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? dialog.CapturedHotkey : null;
    }

    public async Task<bool> ShowRecordingDialogAsync(CancellationToken cancellationToken)
    {
        var host = _overlayService.GetOverlayHost();
        var content = new RecordingOverlayPanel(_audioRecordingService);
        var panel = new OverlayDialogPanel
        {
            Content = content,
            MaxPanelWidth = 400,
            PrimaryButtonText = _localizationService["Common_Finish"]
        };
        panel.ResultChosen += _ => content.Cleanup();
        await host.ShowAsync<OverlayDialogResult>(panel, cancellationToken);
        content.Cleanup();
        return cancellationToken.IsCancellationRequested;
    }

    public async Task<bool> ShowTranscribingDialogAsync(CancellationToken cancellationToken)
    {
        var host = _overlayService.GetOverlayHost();
        var panel = new OverlayDialogPanel
        {
            Content = new TranscribingOverlayPanel(),
            MaxPanelWidth = 400,
            CloseButtonText = _localizationService["Common_Cancel"]
        };
        await host.ShowAsync<OverlayDialogResult>(panel, cancellationToken);
        return cancellationToken.IsCancellationRequested;
    }

    public async Task<string?> ShowInputDialogAsync(string title, string prompt, string? initialValue = null)
    {
        var textBox = new System.Windows.Controls.TextBox
        {
            Margin = new System.Windows.Thickness(0, 8, 0, 0),
            Text = initialValue ?? string.Empty
        };
        // Selecting only lands once the box is in the tree, and a prefill the user has to clear by hand is worse
        // than no prefill at all.
        textBox.Loaded += (_, _) => { textBox.Focus(); textBox.SelectAll(); };
        AutomationProperties.SetAutomationId(textBox, "InputDialog_Value");

        var stackPanel = new System.Windows.Controls.StackPanel();
        stackPanel.Children.Add(new System.Windows.Controls.TextBlock { Text = prompt });
        stackPanel.Children.Add(textBox);

        var result = await _contentDialogService.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = title,
                Content = stackPanel,
                PrimaryButtonText = _localizationService["Common_OK"],
                CloseButtonText = _localizationService["Common_Cancel"]
            });

        return result == ContentDialogResult.Primary ? textBox.Text : null;
    }
}
