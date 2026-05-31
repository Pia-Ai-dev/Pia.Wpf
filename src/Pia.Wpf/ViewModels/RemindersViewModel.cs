using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Localization;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

public partial class RemindersViewModel : ObservableObject, INavigationAware, IDisposable
{
    private readonly ILogger<RemindersViewModel> _logger;
    private readonly IReminderService _reminderService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<Reminder> _reminders = new();

    [ObservableProperty]
    private ObservableCollection<ReminderGroupViewModel> _reminderGroups = new();

    [ObservableProperty]
    private Reminder? _selectedReminder;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusFilter = "All";

    [ObservableProperty]
    private int _activeCount;

    [ObservableProperty]
    private int _snoozedCount;

    [ObservableProperty]
    private int _disabledCount;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _overdueCount;

    public IReadOnlyList<string> StatusFilters { get; } = ["All", "Active", "Snoozed", "Disabled", "Completed"];

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<Reminder?> DeleteCommand { get; }
    public IAsyncRelayCommand<Reminder?> ToggleEnableCommand { get; }
    public IAsyncRelayCommand<Reminder?> SnoozeCommand { get; }
    public IAsyncRelayCommand<Reminder?> DismissCommand { get; }
    public IAsyncRelayCommand DismissAllCommand { get; }
    public IAsyncRelayCommand DisableAllCommand { get; }
    public IAsyncRelayCommand DeleteAllCommand { get; }

    public RemindersViewModel(
        ILogger<RemindersViewModel> logger,
        IReminderService reminderService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        Wpf.Ui.ISnackbarService snackbarService)
    {
        _logger = logger;
        _reminderService = reminderService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _snackbarService = snackbarService;

        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        DeleteCommand = new AsyncRelayCommand<Reminder?>(ExecuteDeleteAsync, CanExecuteAction);
        ToggleEnableCommand = new AsyncRelayCommand<Reminder?>(ExecuteToggleEnableAsync, CanExecuteAction);
        SnoozeCommand = new AsyncRelayCommand<Reminder?>(ExecuteSnoozeAsync, CanExecuteSnooze);
        DismissCommand = new AsyncRelayCommand<Reminder?>(ExecuteDismissAsync, CanExecuteDismiss);
        DismissAllCommand = new AsyncRelayCommand(ExecuteDismissAllAsync, CanExecuteBulk);
        DisableAllCommand = new AsyncRelayCommand(ExecuteDisableAllAsync, CanExecuteBulk);
        DeleteAllCommand = new AsyncRelayCommand(ExecuteDeleteAllAsync, CanExecuteBulk);

        PropertyChanged += OnPropertyChanged;
    }

    public void OnNavigatedTo(object? parameter) { }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        await LoadRemindersAsync();
    }

    public void OnNavigatedFrom() { }

    private async Task LoadRemindersAsync()
    {
        try
        {
            IsLoading = true;

            var all = (await _reminderService.GetAllAsync()).ToList();

            ActiveCount = all.Count(r => r.Status == ReminderStatus.Active);
            SnoozedCount = all.Count(r => r.Status == ReminderStatus.Snoozed);
            DisabledCount = all.Count(r => r.Status == ReminderStatus.Disabled);
            CompletedCount = all.Count(r => r.Status == ReminderStatus.Completed);
            OverdueCount = all.Count(r => r.Status == ReminderStatus.Active && r.NextFireAt < DateTime.Now);

            var filtered = StatusFilter switch
            {
                "Active" => all.Where(r => r.Status == ReminderStatus.Active).ToList(),
                "Snoozed" => all.Where(r => r.Status == ReminderStatus.Snoozed).ToList(),
                "Disabled" => all.Where(r => r.Status == ReminderStatus.Disabled).ToList(),
                "Completed" => all.Where(r => r.Status == ReminderStatus.Completed).ToList(),
                _ => all.ToList()
            };

            var sorted = filtered.OrderBy(r => r.NextFireAt).ToList();

            Reminders.Clear();
            foreach (var reminder in sorted)
                Reminders.Add(reminder);

            RebuildGroups(sorted);
            UpdateCommandStates();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load reminders");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RebuildGroups(IReadOnlyList<Reminder> sorted)
    {
        var now = DateTime.Now;
        var today = now.Date;
        var tomorrow = today.AddDays(1);
        var endOfWeek = today.AddDays(7);

        var buckets = new Dictionary<ReminderBucket, List<Reminder>>();

        foreach (var r in sorted)
        {
            ReminderBucket bucket;
            if (r.Status == ReminderStatus.Active && r.NextFireAt < now)
                bucket = ReminderBucket.Overdue;
            else if (r.NextFireAt.Date == today)
                bucket = ReminderBucket.Today;
            else if (r.NextFireAt.Date == tomorrow)
                bucket = ReminderBucket.Tomorrow;
            else if (r.NextFireAt.Date > tomorrow && r.NextFireAt.Date <= endOfWeek)
                bucket = ReminderBucket.ThisWeek;
            else
                bucket = ReminderBucket.Later;

            if (!buckets.TryGetValue(bucket, out var list))
            {
                list = new List<Reminder>();
                buckets[bucket] = list;
            }
            list.Add(r);
        }

        var order = new[]
        {
            ReminderBucket.Overdue,
            ReminderBucket.Today,
            ReminderBucket.Tomorrow,
            ReminderBucket.ThisWeek,
            ReminderBucket.Later,
        };

        ReminderGroups.Clear();
        foreach (var kind in order)
        {
            if (!buckets.TryGetValue(kind, out var list) || list.Count == 0)
                continue;

            var group = new ReminderGroupViewModel
            {
                BucketKind = kind,
                DisplayName = LocalizationSource.Instance[BucketKey(kind)],
                ItemCount = list.Count,
                IsExpanded = kind is ReminderBucket.Overdue or ReminderBucket.Today or ReminderBucket.Tomorrow,
                IsOverdueBucket = kind == ReminderBucket.Overdue,
            };
            foreach (var r in list)
                group.Items.Add(r);

            ReminderGroups.Add(group);
        }
    }

    private static string BucketKey(ReminderBucket kind) => kind switch
    {
        ReminderBucket.Overdue => "Reminders_Bucket_Overdue",
        ReminderBucket.Today => "Reminders_Bucket_Today",
        ReminderBucket.Tomorrow => "Reminders_Bucket_Tomorrow",
        ReminderBucket.ThisWeek => "Reminders_Bucket_ThisWeek",
        ReminderBucket.Later => "Reminders_Bucket_Later",
        _ => "Reminders_Bucket_Later",
    };

    private async Task ExecuteRefreshAsync()
    {
        await LoadRemindersAsync();
    }

    private Reminder? Resolve(Reminder? parameter) => parameter ?? SelectedReminder;

    private async Task ExecuteDeleteAsync(Reminder? parameter)
    {
        var target = Resolve(parameter);
        if (target is null)
            return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            "Delete Reminder",
            $"Delete reminder \"{target.Description}\"? This cannot be undone.");

        if (!confirmed)
            return;

        try
        {
            await _reminderService.DeleteAsync(target.Id);
            if (ReferenceEquals(SelectedReminder, target))
                SelectedReminder = null;
            await LoadRemindersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete reminder");
            await _dialogService.ShowMessageDialogAsync("Error", $"Failed to delete reminder: {ex.Message}");
        }
    }

    private async Task ExecuteToggleEnableAsync(Reminder? parameter)
    {
        var target = Resolve(parameter);
        if (target is null)
            return;

        try
        {
            if (target.Status == ReminderStatus.Disabled)
                await _reminderService.EnableAsync(target.Id);
            else
                await _reminderService.DisableAsync(target.Id);

            await LoadRemindersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle reminder");
            await _dialogService.ShowMessageDialogAsync("Error", $"Failed to update reminder: {ex.Message}");
        }
    }

    private async Task ExecuteSnoozeAsync(Reminder? parameter)
    {
        var target = Resolve(parameter);
        if (target is null)
            return;

        try
        {
            await _reminderService.SnoozeAsync(target.Id, TimeSpan.FromMinutes(15));
            await LoadRemindersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to snooze reminder");
            await _dialogService.ShowMessageDialogAsync("Error", $"Failed to snooze reminder: {ex.Message}");
        }
    }

    private async Task ExecuteDismissAsync(Reminder? parameter)
    {
        var target = Resolve(parameter);
        if (target is null)
            return;

        try
        {
            await _reminderService.DismissAsync(target.Id);
            ShowSnackbar(
                _localizationService["Msg_Reminders_DismissedTitle"],
                _localizationService["Msg_Reminders_DismissedSingle"],
                Wpf.Ui.Controls.ControlAppearance.Success);
            await LoadRemindersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dismiss reminder");
            await _dialogService.ShowMessageDialogAsync("Error", $"Failed to dismiss reminder: {ex.Message}");
        }
    }

    private async Task ExecuteDismissAllAsync()
    {
        var targets = Reminders
            .Where(r => r.Status is ReminderStatus.Active or ReminderStatus.Snoozed)
            .Select(r => r.Id)
            .ToList();

        if (targets.Count == 0)
        {
            ShowSnackbar(
                _localizationService["Msg_Reminders_NothingToDismissTitle"],
                _localizationService["Msg_Reminders_NothingToDismissBody"],
                Wpf.Ui.Controls.ControlAppearance.Info);
            return;
        }

        var dismissed = 0;
        try
        {
            IsLoading = true;
            foreach (var id in targets)
            {
                await _reminderService.DismissAsync(id);
                dismissed++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dismiss all reminders");
            await _dialogService.ShowMessageDialogAsync("Error", $"Failed to dismiss reminders: {ex.Message}");
        }
        finally
        {
            await LoadRemindersAsync();
        }

        if (dismissed > 0)
        {
            ShowSnackbar(
                _localizationService["Msg_Reminders_DismissedTitle"],
                _localizationService.Format("Msg_Reminders_DismissedCount", dismissed),
                Wpf.Ui.Controls.ControlAppearance.Success);
        }
    }

    private void ShowSnackbar(string title, string body, Wpf.Ui.Controls.ControlAppearance appearance)
    {
        try
        {
            _snackbarService.Show(title, body, appearance, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show snackbar");
        }
    }

    private async Task ExecuteDisableAllAsync()
    {
        var targets = Reminders
            .Where(r => r.Status != ReminderStatus.Disabled)
            .Select(r => r.Id)
            .ToList();

        if (targets.Count == 0)
            return;

        try
        {
            IsLoading = true;
            foreach (var id in targets)
                await _reminderService.DisableAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disable all reminders");
            await _dialogService.ShowMessageDialogAsync("Error", $"Failed to disable reminders: {ex.Message}");
        }
        finally
        {
            await LoadRemindersAsync();
        }
    }

    private async Task ExecuteDeleteAllAsync()
    {
        if (Reminders.Count == 0)
            return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            LocalizationSource.Instance["Reminders_DeleteAllConfirmTitle"],
            LocalizationSource.Instance["Reminders_DeleteAllConfirmBody"]);

        if (!confirmed)
            return;

        var targets = Reminders.Select(r => r.Id).ToList();

        try
        {
            IsLoading = true;
            foreach (var id in targets)
                await _reminderService.DeleteAsync(id);
            SelectedReminder = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete all reminders");
            await _dialogService.ShowMessageDialogAsync("Error", $"Failed to delete reminders: {ex.Message}");
        }
        finally
        {
            await LoadRemindersAsync();
        }
    }

    private bool CanExecuteAction(Reminder? parameter) => Resolve(parameter) is not null && !IsLoading;

    private bool CanExecuteSnooze(Reminder? parameter)
    {
        var target = Resolve(parameter);
        return target is not null && !IsLoading
            && target.Status is ReminderStatus.Active or ReminderStatus.Snoozed;
    }

    private bool CanExecuteDismiss(Reminder? parameter)
    {
        var target = Resolve(parameter);
        return target is not null && !IsLoading
            && target.Status is ReminderStatus.Active or ReminderStatus.Snoozed;
    }

    private bool CanExecuteBulk() => !IsLoading && Reminders.Count > 0;

    private void UpdateCommandStates()
    {
        DeleteCommand.NotifyCanExecuteChanged();
        ToggleEnableCommand.NotifyCanExecuteChanged();
        SnoozeCommand.NotifyCanExecuteChanged();
        DismissCommand.NotifyCanExecuteChanged();
        DismissAllCommand.NotifyCanExecuteChanged();
        DisableAllCommand.NotifyCanExecuteChanged();
        DeleteAllCommand.NotifyCanExecuteChanged();
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectedReminder))
            UpdateCommandStates();

        if (e.PropertyName == nameof(IsLoading))
            UpdateCommandStates();

        if (e.PropertyName == nameof(StatusFilter))
            _ = LoadRemindersAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        PropertyChanged -= OnPropertyChanged;
        GC.SuppressFinalize(this);
    }
}
