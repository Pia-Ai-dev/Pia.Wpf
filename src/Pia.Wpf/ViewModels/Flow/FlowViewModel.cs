using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Navigation;
using Pia.Services.Flow;
using Pia.Services.Interfaces;

namespace Pia.ViewModels.Flow;

/// <summary>
/// Per-window presenter VM for the Flow rail (design §7). Mirrors the singleton store into an observable
/// collection, exposes the badge count and expand/pin state, and executes a card's <see cref="FlowAction"/>.
/// Store events arrive on background threads and are marshalled to the UI via the captured
/// <see cref="SynchronizationContext"/> (ViewModels must not reference System.Windows). Unsubscribes on
/// dispose so the singleton store never pins a closed window's VM alive.
/// </summary>
public partial class FlowViewModel : ObservableObject, IDisposable
{
    private readonly IFlowService _flow;
    private readonly IWindowManagerService _windowManager;
    private readonly IReminderService _reminderService;
    private readonly ISettingsService _settingsService;
    private readonly INavigationService _navigationService;
    private readonly ILogger<FlowViewModel> _logger;
    private readonly SynchronizationContext? _sync;
    private bool _disposed;

    public FlowViewModel(
        IFlowService flow,
        IWindowManagerService windowManager,
        IReminderService reminderService,
        ISettingsService settingsService,
        INavigationService navigationService,
        ILogger<FlowViewModel> logger)
    {
        _flow = flow;
        _windowManager = windowManager;
        _reminderService = reminderService;
        _settingsService = settingsService;
        _navigationService = navigationService;
        _logger = logger;
        _sync = SynchronizationContext.Current;

        _flow.Changed += OnFlowChanged;
        _flow.ItemArrived += OnFlowItemArrived;

        Rebuild();
        _ = LoadPinStateAsync();
    }

    /// <summary>Live items, newest first.</summary>
    public ObservableCollection<FlowItem> Items { get; } = new();

    /// <summary>Raised (on the UI thread) when a new item arrives, so the view can play the arrival peek.</summary>
    public event EventHandler<FlowItem>? ItemArrived;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isPinned;

    /// <summary>The rail is shown when expanded (overlay) or pinned (docked).</summary>
    public bool IsOpen => IsExpanded || IsPinned;

    /// <summary>Overlay mode = open but not pinned; a scrim catches outside clicks to collapse.</summary>
    public bool IsOverlayMode => IsOpen && !IsPinned;

    partial void OnIsExpandedChanged(bool value) => RaiseOpenStateChanged();

    private int _liveCount;
    /// <summary>The actionable backlog: count of live persistent items (the badge, design §4).</summary>
    public int LiveCount
    {
        get => _liveCount;
        private set => SetProperty(ref _liveCount, value);
    }

    private bool _isEmpty = true;
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetProperty(ref _isEmpty, value);
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    /// <summary>Closes the overlay rail (scrim outside-click and header chevron). Pinned mode stays open via IsPinned.</summary>
    [RelayCommand]
    private void Collapse() => IsExpanded = false;

    [RelayCommand]
    private void TogglePin() => IsPinned = !IsPinned;

    partial void OnIsPinnedChanged(bool value)
    {
        RaiseOpenStateChanged();
        _ = SavePinStateAsync(value);
    }

    [RelayCommand]
    private void ClearAll() => _flow.Clear();

    [RelayCommand]
    private void DismissItem(FlowItem? item)
    {
        if (item is not null)
            _flow.Dismiss(item.Id);
    }

    [RelayCommand]
    private async Task ExecuteItemAction(FlowItem? item)
    {
        if (item?.Action is null)
            return;

        try
        {
            switch (item.Action)
            {
                case OpenChatAction chat:
                    _windowManager.ShowAssistantChat(chat.ChatId);
                    RetractByKey(item);
                    break;
                case OpenBriefingAction briefing:
                    // Fall back to the research-history root when there is no entry (design §8).
                    if (briefing.EntryId == Guid.Empty)
                        _windowManager.ShowWindow(WindowMode.Research);
                    else
                        _windowManager.ShowResearchHistoryWithEntry(briefing.EntryId);
                    RetractByKey(item);
                    break;
                case OpenTodoAction:
                    NavigateToTodoBoard();
                    _flow.MarkRead(item.Id); // the deadline auto-retracts when the todo is completed/out of window
                    break;
                case ReminderSnoozeAction snooze:
                    await _reminderService.SnoozeAsync(snooze.ReminderId, TimeSpan.FromMinutes(10));
                    _flow.Dismiss(item.Id);
                    break;
                case ReminderDismissAction dismiss:
                    await _reminderService.DismissAsync(dismiss.ReminderId);
                    _flow.Dismiss(item.Id);
                    break;
                case InvokeAction invoke:
                    invoke.Callback();
                    _flow.Dismiss(item.Id);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flow action failed for item {Id}", item.Id);
        }
    }

    private void RetractByKey(FlowItem item)
    {
        if (item.DedupKey is { } key)
            _flow.Retract(key);
        else
            _flow.Dismiss(item.Id);
    }

    private void NavigateToTodoBoard()
    {
        try
        {
            _navigationService.NavigateTo<TodoViewModel>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flow could not navigate to the todo board");
        }
    }

    private void OnFlowChanged(object? sender, EventArgs e) => Post(Rebuild);

    private void OnFlowItemArrived(object? sender, FlowItem item) => Post(() => ItemArrived?.Invoke(this, item));

    private void Post(Action action)
    {
        if (_sync is not null)
            _sync.Post(_ => action(), null);
        else
            action();
    }

    private void Rebuild()
    {
        var snapshot = _flow.Snapshot;
        Items.Clear();
        foreach (var item in snapshot)
            Items.Add(item);
        LiveCount = snapshot.Count(i => i.Lifetime.IsPersistent);
        IsEmpty = snapshot.Count == 0;
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
