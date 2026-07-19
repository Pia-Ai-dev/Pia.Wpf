using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models.Flow;
using Pia.Navigation;
using Pia.Services.Flow;
using Pia.Services.Interfaces;

namespace Pia.ViewModels.Flow;

/// <summary>
/// Per-window presenter VM for the Flow rail (design §7). Mirrors the singleton store into an observable
/// collection, exposes the badge count and expand/pin state, and executes a card's <see cref="FlowAction"/>.
/// Store events arrive on background threads and are marshalled to the UI via the base
/// <see cref="UiThreadViewModel"/> (ViewModels must not reference System.Windows). Unsubscribes on
/// dispose so the singleton store never pins a closed window's VM alive.
/// </summary>
public partial class FlowViewModel : UiThreadViewModel, IDisposable
{
    private readonly IFlowService _flow;
    private readonly IWindowManagerService _windowManager;
    private readonly IReminderService _reminderService;
    private readonly ISettingsService _settingsService;
    private readonly INavigationService _navigationService;
    private readonly ILocalizationService _localizationService;
    private readonly IAgentRunResumeService _resumeService;
    private readonly ILogger<FlowViewModel> _logger;
    private readonly ILogger<FlowItemViewModel> _itemLogger;
    private bool _disposed;

    public FlowViewModel(
        IFlowService flow,
        IWindowManagerService windowManager,
        IReminderService reminderService,
        ISettingsService settingsService,
        INavigationService navigationService,
        ILocalizationService localizationService,
        IAgentRunResumeService resumeService,
        ILogger<FlowViewModel> logger,
        ILogger<FlowItemViewModel> itemLogger)
    {
        _flow = flow;
        _windowManager = windowManager;
        _reminderService = reminderService;
        _settingsService = settingsService;
        _navigationService = navigationService;
        _localizationService = localizationService;
        _resumeService = resumeService;
        _logger = logger;
        _itemLogger = itemLogger;

        _flow.Changed += OnFlowChanged;
        _flow.ItemArrived += OnFlowItemArrived;

        Reconcile();
        _ = LoadPinStateAsync();
    }

    /// <summary>Live item wrappers, newest first (the same instances are reused across reconciles).</summary>
    public ObservableCollection<FlowItemViewModel> Items { get; } = new();

    /// <summary>Raised (on the UI thread) when a new item arrives, so the view can play the arrival peek.</summary>
    public event EventHandler<FlowItemViewModel>? ItemArrived;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    [NotifyPropertyChangedFor(nameof(IsOverlayMode))]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    [NotifyPropertyChangedFor(nameof(IsOverlayMode))]
    private bool _isPinned;

    /// <summary>The rail is shown when expanded (overlay) or pinned (docked).</summary>
    public bool IsOpen => IsExpanded || IsPinned;

    /// <summary>Overlay mode = open but not pinned; a scrim catches outside clicks to collapse.</summary>
    public bool IsOverlayMode => IsOpen && !IsPinned;

    /// <summary>The actionable backlog: count of live persistent items (the badge, design §4).</summary>
    [ObservableProperty]
    private int _liveCount;

    [ObservableProperty]
    private bool _isEmpty = true;

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    /// <summary>Closes the overlay rail (scrim outside-click and header chevron). Pinned mode stays open via IsPinned.</summary>
    [RelayCommand]
    private void Collapse() => IsExpanded = false;

    [RelayCommand]
    private void TogglePin() => IsPinned = !IsPinned;

    // The generated setter already raises IsOpen/IsOverlayMode (NotifyPropertyChangedFor above); just persist.
    partial void OnIsPinnedChanged(bool value) => _ = SavePinStateAsync(value);

    [RelayCommand]
    private void ClearAll() => _flow.Clear();

    private void OnFlowChanged(object? sender, EventArgs e) => Post(Reconcile);

    // Reconcile first so the wrapper for the arrived item exists (the store may raise ItemArrived for
    // an item not yet reflected in our collection), then raise the rail's own wrapper instance so the
    // peek shares its IsBusy/decision state. Falls back to an ad-hoc wrapper if the item is no longer
    // in the snapshot by the time we look (e.g. resolved between the two events).
    private void OnFlowItemArrived(object? sender, FlowItem item) => Post(() =>
    {
        Reconcile();
        var wrapper = Items.FirstOrDefault(w => w.Item.Id == item.Id) ?? CreateWrapper(item);
        ItemArrived?.Invoke(this, wrapper);
    });

    // Id-keyed in-place sync of the snapshot into the wrapper collection (design §5). NEVER clear+re-add:
    // that would tear down a wrapper mid-decision and lose its in-flight IsBusy/re-entrancy guard. Reuse
    // each existing wrapper for an unchanged Id (rebinding its FlowItem), insert/move to the snapshot's
    // newest-first position, and drop wrappers whose Id is gone.
    private void Reconcile()
    {
        var snapshot = _flow.Snapshot;
        var byId = Items.ToDictionary(w => w.Item.Id);
        var live = new HashSet<Guid>(snapshot.Select(s => s.Id));

        // Remove gone wrappers first (back-to-front so indices stay valid).
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!live.Contains(Items[i].Item.Id))
                Items.RemoveAt(i);
        }

        // Walk the snapshot in order; reuse-and-move or insert so Items mirrors the newest-first order.
        for (var i = 0; i < snapshot.Count; i++)
        {
            var item = snapshot[i];
            if (byId.TryGetValue(item.Id, out var wrapper))
            {
                wrapper.Bind(item);
                var current = Items.IndexOf(wrapper);
                if (current != i)
                    Items.Move(current, i);
            }
            else
            {
                Items.Insert(i, CreateWrapper(item));
            }
        }

        LiveCount = snapshot.Count(i => i.Lifetime.IsPersistent);
        IsEmpty = snapshot.Count == 0;
    }

    private FlowItemViewModel CreateWrapper(FlowItem item)
    {
        var wrapper = new FlowItemViewModel(
            _flow,
            _reminderService,
            _windowManager,
            _navigationService,
            _localizationService,
            _resumeService,
            _itemLogger);
        wrapper.Bind(item);
        return wrapper;
    }

    private async Task LoadPinStateAsync()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            Post(() => SetPinnedSilently(settings.FlowPinned));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flow could not load pin state");
        }
    }

    // Set the backing field directly so loading the persisted value doesn't trigger a redundant save.
    private void SetPinnedSilently(bool value)
    {
        if (_isPinned == value)
            return;
        _isPinned = value;
        OnPropertyChanged(nameof(IsPinned));
        RaiseOpenStateChanged();
    }

    // IsOpen and IsOverlayMode are both derived from IsExpanded/IsPinned, so any change to either raises both.
    private void RaiseOpenStateChanged()
    {
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(IsOverlayMode));
    }

    private async Task SavePinStateAsync(bool value)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            settings.FlowPinned = value;
            await _settingsService.SaveSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flow could not save pin state");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _flow.Changed -= OnFlowChanged;
        _flow.ItemArrived -= OnFlowItemArrived;
    }
}
