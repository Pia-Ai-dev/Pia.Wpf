using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Pia.ViewModels;
using Pia.Views;

namespace Pia.Controls.AssistantHistory;

public partial class PiaAssistantChatInspector : UserControl
{
    private double? _prependExtent;

    public PiaAssistantChatInspector()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // The DataContext is the selected chat's detail, so this is the one signal that a new transcript is
    // about to be built. Measuring here rather than in the ViewModel because the cost is the layout pass,
    // and only a view can wait for it (ViewModels cannot reach DispatcherPriority).
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is null)
            return;

        // Per-report stopwatch: two quick selections leave two callbacks pending.
        var build = Stopwatch.StartNew();
        Dispatcher.BeginInvoke(
            new Action(() => ReportTranscriptRendered(build.Elapsed)),
            DispatcherPriority.ContextIdle);
    }

    private void ReportTranscriptRendered(TimeSpan elapsed)
    {
        // Resolved late: by now the tree is settled, and nothing holds a ViewModel across a selection change.
        for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is AssistantHistoryView { DataContext: AssistantHistoryViewModel viewModel })
            {
                viewModel.ReportTranscriptRendered(elapsed);
                return;
            }
        }
    }

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
