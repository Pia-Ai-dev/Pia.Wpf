using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly Storyboard? _peekWhisper;
    private readonly Storyboard? _peekAssertive;
    private Storyboard? _activePeek;

    public FlowView()
    {
        InitializeComponent();

        // Both peek storyboards drive the same PeekHost element, so a new arrival stops the in-flight one
        // before starting its own; clearing the rendered card is hung off their (one-shot) Completed.
        _peekWhisper = Resources["PeekWhisper"] as Storyboard;
        _peekAssertive = Resources["PeekAssertive"] as Storyboard;
        if (_peekWhisper is not null)
            _peekWhisper.Completed += OnPeekCompleted;
        if (_peekAssertive is not null)
            _peekAssertive.Completed += OnPeekCompleted;

        // Hovering the card freezes its retract so the now-clickable action/dismiss don't slide away
        // mid-reach; leaving resumes it (mirrors how a hovered toast holds).
        PeekHost.MouseEnter += OnPeekMouseEnter;
        PeekHost.MouseLeave += OnPeekMouseLeave;

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

    private void OnItemArrived(object? sender, FlowItemViewModel item)
    {
        // Only the foreground window peeks; the peek must never cover the cursor zone of other windows.
        var window = Window.GetWindow(this);
        if (window is null || !window.IsActive)
            return;

        // When the rail is open the item already shows as a card in the list; an extra peek would just
        // double it over the rail. Peek only while collapsed — that is exactly what this surface is for.
        if (_viewModel?.IsOpen == true)
            return;

        // Stop the in-flight peek (the only storyboard that can be running, and definitely begun
        // controllable) before re-targeting PeekHost, then render the arrival as its real rail card at
        // the docked position so a collapsed-rail arrival looks identical to the opened rail.
        _activePeek?.Stop(PeekHost);
        PeekItems.ItemsSource = new[] { item };

        // Make the card interactive (hand cursor + clickable action/dismiss, like the rail) only while
        // it is on screen; OnPeekCompleted turns this back off so the faded-out card can't eat clicks.
        // Only the card paints a background, so the host's empty regions still pass clicks through.
        PeekHost.IsHitTestVisible = true;

        // Warning and up peek more assertively — follows FlowSeverity's documented ascending ordering,
        // so a new severity slots in by its rank instead of needing a new case here.
        _activePeek = item.Severity >= FlowSeverity.Warning ? _peekAssertive : _peekWhisper;
        _activePeek?.Begin(PeekHost, isControllable: true);

        if (Resources["BadgePulse"] is Storyboard pulse)
            pulse.Begin(Badge);
    }

    // Once the peek retracts: stop catching clicks (the held-at-zero-opacity card is still present until
    // cleared) and drop the rendered card so the superseded item doesn't linger in the visual tree.
    private void OnPeekCompleted(object? sender, EventArgs e)
    {
        _activePeek = null;
        PeekHost.IsHitTestVisible = false;
        PeekItems.ItemsSource = null;
    }

    private void OnPeekMouseEnter(object sender, MouseEventArgs e) => _activePeek?.Pause(PeekHost);

    private void OnPeekMouseLeave(object sender, MouseEventArgs e) => _activePeek?.Resume(PeekHost);
}
