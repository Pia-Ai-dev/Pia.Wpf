using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Pia.Views.Controls;
using Pia.Views.Dialogs;
using Pia.Views.Dialogs.Overlay;
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

    public async Task ShowSessionDetailDialogAsync(OptimizationSession session)
    {
        var dialogHost = _contentDialogService.GetDialogHostEx()
            ?? throw new InvalidOperationException("No dialog host available");
        var dialog = new SessionDetailContentDialog(dialogHost, session, _outputService);
        await dialog.ShowAsync();
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

    public async Task<bool> ShowMessageWithCopyDialogAsync(string title, string message)
    {
        var result = await _contentDialogService.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = title,
                Content = message,
                PrimaryButtonText = _localizationService["Enum_CopyToClipboard"],
                CloseButtonText = _localizationService["Common_OK"]
            });

        return result == ContentDialogResult.Primary;
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

    public async Task<string?> ShowInputDialogAsync(string title, string prompt)
    {
        var textBox = new System.Windows.Controls.TextBox
        {
            Margin = new System.Windows.Thickness(0, 8, 0, 0)
        };

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
