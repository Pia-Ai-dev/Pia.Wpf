using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class RemindersViewModel : ObservableObject, INavigationAware, IDisposable
{
    private readonly ILogger<RemindersViewModel> _logger;
    private readonly IReminderService _reminderService;
    private readonly IDialogService _dialogService;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<Reminder> _reminders = new();

    [ObservableProperty]
    private Reminder? _selectedReminder;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusFilter = "All";

    public IReadOnlyList<string> StatusFilters { get; } = ["All", "Active", "Snoozed", "Disabled", "Completed"];

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand ToggleEnableCommand { get; }
    public IAsyncRelayCommand SnoozeCommand { get; }
    public IAsyncRelayCommand DismissCommand { get; }

    public RemindersViewModel(
        ILogger<RemindersViewModel> logger,
        IReminderService reminderService,
        IDialogService dialogService)
    {
        _logger = logger;
        _reminderService = reminderService;
        _dialogService = dialogService;

        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        DeleteCommand = new AsyncRelayCommand(ExecuteDeleteAsync, CanExecuteAction);
        ToggleEnableCommand = new AsyncRelayCommand(ExecuteToggleEnableAsync, CanExecuteAction);
        SnoozeCommand = new AsyncRelayCommand(ExecuteSnoozeAsync, CanExecuteSnooze);
        DismissCommand = new AsyncRelayCommand(ExecuteDismissAsync, CanExecuteDismiss);

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

            var all = await _reminderService.GetAllAsync();

            var filtered = StatusFilter switch
            {
                "Active" => all.Where(r => r.Status == ReminderStatus.Active).ToList(),
                "Snoozed" => all.Where(r => r.Status == ReminderStatus.Snoozed).ToList(),
                "Disabled" => all.Where(r => r.Status == ReminderStatus.Disabled).ToList(),
                "Completed" => all.Where(r => r.Status == ReminderStatus.Completed).ToList(),
                _ => all.ToList()
            };

            Reminders.Clear();
            foreach (var reminder in filtered.OrderBy(r => r.NextFireAt))
                Reminders.Add(reminder);

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

    private async Task ExecuteRefreshAsync()
    {
        await LoadRemindersAsync();
    }

    private async Task ExecuteDeleteAsync()
    {
        if (SelectedReminder is null)
            return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            "Delete Reminder",
            $"Delete reminder \"{SelectedReminder.Description}\"? This cannot be undone.");

        if (!confirmed)
            return;

        try
        {
            await _reminderService.DeleteAsync(SelectedReminder.Id);
            Reminders.Remove(SelectedReminder);
            SelectedReminder = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete reminder");
            await _dialogService.ShowMessageDialogAsync("Error", $"Failed to delete reminder: {ex.Message}");
        }
    }

    private async Task ExecuteToggleEnableAsync()
    {
        if (SelectedReminder is null)
            return;

        try
        {
            if (SelectedReminder.Status == ReminderStatus.Disabled)
                await _reminderService.EnableAsync(SelectedReminder.Id);
            else
                await _reminderService.DisableAsync(SelectedReminder.Id);

            await LoadRemindersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle reminder");
            await _dialogService.ShowMessageDialogAsync("Error", $"Failed to update reminder: {ex.Message}");
        }
    }

    private async Task ExecuteSnoozeAsync()
    {
        if (SelectedReminder is null)
            return;

        try
        {
            await _reminderService.SnoozeAsync(SelectedReminder.Id, TimeSpan.FromMinutes(15));
            await LoadRemindersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to snooze reminder");
            await _dialogService.ShowMessageDialogAsync("Error", $"Failed to snooze reminder: {ex.Message}");
        }
    }

    private async Task ExecuteDismissAsync()
    {
        if (SelectedReminder is null)
            return;

        try
        {
            await _reminderService.DismissAsync(SelectedReminder.Id);
            await LoadRemindersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dismiss reminder");
            await _dialogService.ShowMessageDialogAsync("Error", $"Failed to dismiss reminder: {ex.Message}");
        }
    }

    private bool CanExecuteAction() => SelectedReminder is not null && !IsLoading;

    private bool CanExecuteSnooze() =>
        SelectedReminder is not null && !IsLoading &&
        SelectedReminder.Status is ReminderStatus.Active or ReminderStatus.Snoozed;

    private bool CanExecuteDismiss() =>
        SelectedReminder is not null && !IsLoading &&
        SelectedReminder.Status is ReminderStatus.Active or ReminderStatus.Snoozed;

    private void UpdateCommandStates()
    {
        DeleteCommand.NotifyCanExecuteChanged();
        ToggleEnableCommand.NotifyCanExecuteChanged();
        SnoozeCommand.NotifyCanExecuteChanged();
        DismissCommand.NotifyCanExecuteChanged();
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
