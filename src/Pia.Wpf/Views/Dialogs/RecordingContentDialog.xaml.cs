using Pia.Services.Interfaces;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class RecordingContentDialog : ContentDialog
{
    private readonly IAudioRecordingService _audioRecordingService;

    public RecordingContentDialog(
        ContentDialogHost dialogHost,
        IAudioRecordingService audioRecordingService)
        : base(dialogHost)
    {
        _audioRecordingService = audioRecordingService;
        InitializeComponent();

        _audioRecordingService.AudioLevelChanged += OnAudioLevelChanged;
        Closing += OnClosing;
    }

    private void OnAudioLevelChanged(object? sender, float level)
    {
        Dispatcher.Invoke(() => RecordingIndicatorControl.AudioLevel = level);
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        _audioRecordingService.AudioLevelChanged -= OnAudioLevelChanged;
    }
}
