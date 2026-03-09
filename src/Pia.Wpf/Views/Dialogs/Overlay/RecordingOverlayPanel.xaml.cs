using System.Windows.Controls;
using Pia.Services.Interfaces;

namespace Pia.Views.Dialogs.Overlay;

public partial class RecordingOverlayPanel : UserControl
{
    private readonly IAudioRecordingService _audioRecordingService;

    public RecordingOverlayPanel(IAudioRecordingService audioRecordingService)
    {
        _audioRecordingService = audioRecordingService;
        InitializeComponent();

        _audioRecordingService.AudioLevelChanged += OnAudioLevelChanged;
    }

    public void Cleanup()
    {
        _audioRecordingService.AudioLevelChanged -= OnAudioLevelChanged;
    }

    private void OnAudioLevelChanged(object? sender, float level)
    {
        Dispatcher.Invoke(() => RecordingIndicatorControl.AudioLevel = level);
    }
}
