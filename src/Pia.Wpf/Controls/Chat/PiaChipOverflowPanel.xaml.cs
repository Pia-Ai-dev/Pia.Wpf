using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pia.Controls.Chat;

/// <summary>
/// Chip row that renders the first three items inline and folds the rest behind a "+N" dropdown.
/// </summary>
public partial class PiaChipOverflowPanel : UserControl
{
    private const int InlineSlots = 3;

    private const int ReopenGuardMs = 250;

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(PiaChipOverflowPanel),
            new PropertyMetadata(null, OnItemsSourceChanged));

    /// <summary>Applied to the inline slots and to the dropdown rows alike.</summary>
    public static readonly DependencyProperty ItemTemplateProperty =
        DependencyProperty.Register(nameof(ItemTemplate), typeof(DataTemplate), typeof(PiaChipOverflowPanel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty Slot1Property =
        DependencyProperty.Register(nameof(Slot1), typeof(object), typeof(PiaChipOverflowPanel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty Slot2Property =
        DependencyProperty.Register(nameof(Slot2), typeof(object), typeof(PiaChipOverflowPanel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty Slot3Property =
        DependencyProperty.Register(nameof(Slot3), typeof(object), typeof(PiaChipOverflowPanel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty OverflowItemsProperty =
        DependencyProperty.Register(nameof(OverflowItems), typeof(IReadOnlyList<object>),
            typeof(PiaChipOverflowPanel), new PropertyMetadata(null));

    public static readonly DependencyProperty HasOverflowProperty =
        DependencyProperty.Register(nameof(HasOverflow), typeof(bool), typeof(PiaChipOverflowPanel),
            new PropertyMetadata(false));

    public static readonly DependencyProperty OverflowLabelProperty =
        DependencyProperty.Register(nameof(OverflowLabel), typeof(string), typeof(PiaChipOverflowPanel),
            new PropertyMetadata(null));

    private ScrollViewer? _scrollHost;
    private long _closedAtTicks;

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public object? Slot1
    {
        get => GetValue(Slot1Property);
        private set => SetValue(Slot1Property, value);
    }

    public object? Slot2
    {
        get => GetValue(Slot2Property);
        private set => SetValue(Slot2Property, value);
    }

    public object? Slot3
    {
        get => GetValue(Slot3Property);
        private set => SetValue(Slot3Property, value);
    }

    public IReadOnlyList<object>? OverflowItems
    {
        get => (IReadOnlyList<object>?)GetValue(OverflowItemsProperty);
        private set => SetValue(OverflowItemsProperty, value);
    }

    public bool HasOverflow
    {
        get => (bool)GetValue(HasOverflowProperty);
        private set => SetValue(HasOverflowProperty, value);
    }

    public string? OverflowLabel
    {
        get => (string?)GetValue(OverflowLabelProperty);
        private set => SetValue(OverflowLabelProperty, value);
    }

    public PiaChipOverflowPanel() => InitializeComponent();

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (PiaChipOverflowPanel)d;

        // The message this panel is bound to outlives the panel when its container is re-hosted onto
        // another message, so the old collection must be released before the new one is observed.
        if (e.OldValue is INotifyCollectionChanged previous)
            previous.CollectionChanged -= panel.OnItemsCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged current)
            current.CollectionChanged += panel.OnItemsCollectionChanged;

        panel.Rebuild();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        var items = ItemsSource?.Cast<object>().ToList() ?? [];

        Slot1 = items.Count > 0 ? items[0] : null;
        Slot2 = items.Count > 1 ? items[1] : null;
        Slot3 = items.Count > 2 ? items[2] : null;

        var overflow = items.Count > InlineSlots
            ? items.GetRange(InlineSlots, items.Count - InlineSlots)
            : [];

        OverflowItems = overflow;
        HasOverflow = overflow.Count > 0;
        OverflowLabel = $"+{overflow.Count}";

        // Chips arrive while the turn streams, so the dropdown can empty out under an open popup.
        if (!HasOverflow)
            MorePopup.IsOpen = false;
    }

    // StaysOpen=False dismisses on the mouse-DOWN that precedes this button's Click, so without the
    // guard the click meant to close the dropdown reopens it.
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _closedAtTicks < ReopenGuardMs) return;
        MorePopup.IsOpen = true;
    }

    private void MorePopup_Closed(object? sender, EventArgs e)
    {
        DetachScrollHost();
        _closedAtTicks = Environment.TickCount64;
    }

    private void MorePopup_Opened(object? sender, EventArgs e)
    {
        _scrollHost = FindScrollHost(this);
        if (_scrollHost is not null)
            _scrollHost.ScrollChanged += OnHostScrollChanged;
    }

    // A Popup does not follow its placement target when an ancestor scrolls — left open it would hang
    // over unrelated messages.
    private void OnHostScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.HorizontalChange != 0)
            MorePopup.IsOpen = false;
    }

    private void PopupContent_Click(object sender, RoutedEventArgs e) => MorePopup.IsOpen = false;

    private void DetachScrollHost()
    {
        if (_scrollHost is null) return;
        _scrollHost.ScrollChanged -= OnHostScrollChanged;
        _scrollHost = null;
    }

    // The OUTERMOST scroller, because ScrollChanged bubbles: subscribing to a nearer nested one would
    // never see the chat list scroll past it.
    private static ScrollViewer? FindScrollHost(DependencyObject start)
    {
        ScrollViewer? outermost = null;
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer scrollViewer)
                outermost = scrollViewer;

        return outermost;
    }
}
