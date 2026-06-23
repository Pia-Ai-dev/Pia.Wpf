using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Pia.Models.Flow;
using Pia.ViewModels.Flow;

namespace Pia.Controls.Flow;

/// <summary>
/// The per-window Flow rail: peeking edge handle, expandable/pinnable rail, and arrival peek (design §4).
/// The arrival peek plays only when this control's window is the foreground one (others update silently),
/// keeping the store UI-agnostic. Subscribes to the scoped <see cref="FlowViewModel.ItemArrived"/> and
/// detaches on unload so the singleton store never animates a dead window.
/// </summary>
public partial class FlowView : UserControl
{
    private FlowViewModel? _viewModel;

    public FlowView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _viewModel = e.NewValue as FlowViewModel;
        Attach();
    }

    // Re-attach after an Unloaded/Loaded cycle that keeps the same DataContext (DataContextChanged won't fire).
    private void OnLoaded(object sender, RoutedEventArgs e) => Attach();

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Attach()
    {
        if (_viewModel is null)
            return;
        _viewModel.ItemArrived -= OnItemArrived; // keep idempotent
        _viewModel.ItemArrived += OnItemArrived;
    }

    private void Detach()
    {
        if (_viewModel is not null)
            _viewModel.ItemArrived -= OnItemArrived;
    }

    private void OnItemArrived(object? sender, FlowItem item)
    {
        // Only the foreground window peeks; the peek must never cover the cursor zone of other windows.
        var window = Window.GetWindow(this);
        if (window is null || !window.IsActive)
            return;

        PeekTitle.Text = item.Title;
        PeekBody.Text = item.Body;

        var peekKey = item.Severity is FlowSeverity.Info or FlowSeverity.Success ? "PeekWhisper" : "PeekAssertive";
        if (Resources[peekKey] is Storyboard peek)
            peek.Begin(Peek);

        if (Resources["BadgePulse"] is Storyboard pulse)
            pulse.Begin(Badge);
    }
}
