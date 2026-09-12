using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Pia.Controls.AssistantHistory;

public partial class PiaAssistantChatInspector : UserControl
{
    private double? _prependExtent;

    public PiaAssistantChatInspector() => InitializeComponent();

    // Inserting above the viewport pushes everything below it down by a height nothing knows until it is
    // measured. Click runs before the button's command, so this extent is the one before the prepend.
    private void LoadOlderMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_prependExtent is not null)
            return;

        _prependExtent = TranscriptScrollViewer.ExtentHeight;
        Dispatcher.BeginInvoke(RestoreOffsetAfterPrepend, DispatcherPriority.Loaded);
    }

    private void RestoreOffsetAfterPrepend()
    {
        if (_prependExtent is not { } before)
            return;
        _prependExtent = null;

        TranscriptScrollViewer.UpdateLayout();
        TranscriptScrollViewer.ScrollToVerticalOffset(
            TranscriptScrollViewer.VerticalOffset + (TranscriptScrollViewer.ExtentHeight - before));
    }
}
