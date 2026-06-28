using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Pia.Models;

namespace Pia.Controls.Chat;

/// <summary>
/// Renders an assistant message's reasoning in two phases: a live, auto-scrolling
/// rolling window while the model is thinking, then a collapsed "Thought for Ns"
/// toggle (above the answer) once the answer begins. Bound to <see cref="AssistantMessage"/>
/// via its DataContext.
/// </summary>
public partial class PiaReasoningView : UserControl
{
    private INotifyPropertyChanged? _observed;

    public PiaReasoningView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (DataContext is INotifyPropertyChanged npc)
        {
            _observed = npc;
            npc.PropertyChanged += OnMessagePropertyChanged;
        }
    }

    private void Detach()
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnMessagePropertyChanged;
            _observed = null;
        }
    }

    // Keep the rolling window pinned to the newest reasoning as it streams.
    // PropertyChanged is raised on the UI thread (the run loop is UI-thread-affine).
    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssistantMessage.ThinkingContent))
            LiveScroller.ScrollToEnd();
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AssistantMessage message)
            message.IsReasoningExpanded = !message.IsReasoningExpanded;
    }
}
