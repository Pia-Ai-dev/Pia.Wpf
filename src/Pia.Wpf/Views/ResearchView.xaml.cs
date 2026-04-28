using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pia.ViewModels;

namespace Pia.Views;

public partial class ResearchView : UserControl
{
    public static readonly DependencyProperty IsAutoScrollEnabledProperty =
        DependencyProperty.Register(
            nameof(IsAutoScrollEnabled),
            typeof(bool),
            typeof(ResearchView),
            new PropertyMetadata(false));

    public bool IsAutoScrollEnabled
    {
        get => (bool)GetValue(IsAutoScrollEnabledProperty);
        set => SetValue(IsAutoScrollEnabledProperty, value);
    }

    private ResearchViewModel? ViewModel => DataContext as ResearchViewModel;

    public ResearchView()
    {
        InitializeComponent();
        Loaded += (_, _) => QueryTextBox.Focus();
    }

    private void StepsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Detect a user-driven scroll: a vertical movement that isn't from content growth.
        // If the user moved away from the bottom while auto-scroll was on, turn it off.
        if (!IsAutoScrollEnabled) return;
        if (e.VerticalChange == 0) return;
        if (e.ExtentHeightChange != 0) return;

        var atBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 1;
        if (!atBottom)
        {
            IsAutoScrollEnabled = false;
        }
    }

    private void StepsItemsControl_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // Streaming markdown renders bubble RequestBringIntoView up to the ScrollViewer, which
        // would scroll regardless of IsAutoScrollEnabled. Swallow the event here (a descendant
        // of the ScrollViewer) so the ScrollViewer's class handler never sees it when paused.
        if (!IsAutoScrollEnabled)
        {
            e.Handled = true;
        }
    }

    private void ResumeAutoScrollButton_Click(object sender, RoutedEventArgs e)
    {
        IsAutoScrollEnabled = true;
        StepsScrollViewer.ScrollToEnd();
    }

    private void QueryTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (ViewModel?.StartResearchCommand.CanExecute(null) == true)
            {
                ViewModel.StartResearchCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (ViewModel?.CancelResearchCommand.CanExecute(null) == true)
            {
                ViewModel.CancelResearchCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
