using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Pia.Views.Controls;

/// <summary>
/// Tiny pulsing dot used inside live transcription bubbles to signal that the matching
/// audio stream (mic or loopback) currently has active voice input. The animation runs
/// only while the control is loaded and visible to keep the render thread quiet when
/// the indicator is hidden.
/// </summary>
public partial class ListeningIndicator : UserControl
{
    private Storyboard? _pulse;

    public ListeningIndicator()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _pulse = (Storyboard)Resources["PulseStoryboard"];
        if (IsVisible) _pulse.Begin(this, isControllable: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try { _pulse?.Stop(this); } catch { /* ignore */ }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_pulse is null) return;
        try
        {
            if ((bool)e.NewValue) _pulse.Begin(this, isControllable: true);
            else _pulse.Stop(this);
        }
        catch { /* ignore */ }
    }
}
