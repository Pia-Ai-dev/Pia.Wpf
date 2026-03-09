using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class TodoViewModel : ObservableObject, INavigationAware, IDisposable
{
    private readonly ILogger<TodoViewModel> _logger;
    private readonly ITodoService _todoService;
    private readonly IDialogService _dialogService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly Navigation.INavigationService _navigationService;
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localizationService;
    private bool _disposed;
    private bool _isRefreshing;
    private bool _suppressTodoChanged;

    [ObservableProperty]
    private ObservableCollection<TodoItem> _pendingTodos = new();

    [ObservableProperty]
    private ObservableCollection<TodoItem> _completedTodos = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isCompletedSectionExpanded;

    [ObservableProperty]
    private bool _isTodoPanelOpen;

    [ObservableProperty]
    private bool _isTodoButtonVisible = true;

    [ObservableProperty]
    private string _newTodoTitle = string.Empty;

    [ObservableProperty]
    private TodoPriority _newTodoPriority = TodoPriority.Medium;

    [ObservableProperty]
    private DateTime? _newTodoDueDate;

    [ObservableProperty]
    private int _completedTodayCount;

    [ObservableProperty]
    private int _totalTodayCount;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private TodoItem? _editingTodo;

    [ObservableProperty]
    private string _editTitle = string.Empty;

    [ObservableProperty]
    private string _editNotes = string.Empty;

    [ObservableProperty]
    private TodoPriority _editPriority;

    [ObservableProperty]
    private DateTime? _editDueDate;

    [ObservableProperty]
    private bool _isEditing;

    public IReadOnlyList<TodoPriority> Priorities { get; } =
        [TodoPriority.Low, TodoPriority.Medium, TodoPriority.High];

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand AddTodoCommand { get; }
    public IAsyncRelayCommand<TodoItem> CompleteTodoCommand { get; }
    public IAsyncRelayCommand<TodoItem> UncompleteTodoCommand { get; }
    public IAsyncRelayCommand<TodoItem> DeleteTodoCommand { get; }
    public IRelayCommand<TodoItem> StartEditCommand { get; }
    public IAsyncRelayCommand SaveEditCommand { get; }
    public IRelayCommand CancelEditCommand { get; }
    public IRelayCommand ToggleTodoPanelCommand { get; }
    public IRelayCommand NavigateToTodoViewCommand { get; }

    public TodoViewModel(
        ILogger<TodoViewModel> logger,
        ITodoService todoService,
        IDialogService dialogService,
        Wpf.Ui.ISnackbarService snackbarService,
        Navigation.INavigationService navigationService,
        ISettingsService settingsService,
        ILocalizationService localizationService)
    {
        _logger = logger;
        _todoService = todoService;
        _dialogService = dialogService;
        _snackbarService = snackbarService;
        _navigationService = navigationService;
        _settingsService = settingsService;
        _localizationService = localizationService;

        RefreshCommand = new AsyncRelayCommand(LoadTodosAsync);
        AddTodoCommand = new AsyncRelayCommand(ExecuteAddTodoAsync, CanAddTodo);
        CompleteTodoCommand = new AsyncRelayCommand<TodoItem>(ExecuteCompleteTodoAsync);
        UncompleteTodoCommand = new AsyncRelayCommand<TodoItem>(ExecuteUncompleteTodoAsync);
        DeleteTodoCommand = new AsyncRelayCommand<TodoItem>(ExecuteDeleteTodoAsync);
        StartEditCommand = new RelayCommand<TodoItem>(ExecuteStartEdit);
        SaveEditCommand = new AsyncRelayCommand(ExecuteSaveEditAsync, CanSaveEdit);
        CancelEditCommand = new RelayCommand(ExecuteCancelEdit);
        ToggleTodoPanelCommand = new RelayCommand(() => IsTodoPanelOpen = !IsTodoPanelOpen);
        NavigateToTodoViewCommand = new RelayCommand(ExecuteNavigateToTodoView);

        PropertyChanged += OnPropertyChanged;
        _todoService.TodoChanged += OnTodoChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;

        SafeFireAndForget(LoadVisibilitySettingAsync());
    }

    private async Task LoadVisibilitySettingAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        IsTodoButtonVisible = settings.ShowTodoPanelButton;
    }

    public void OnNavigatedTo(object? parameter) { }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        await LoadTodosAsync();
    }

    public void OnNavigatedFrom() { }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsTodoButtonVisible = settings.ShowTodoPanelButton;
            if (!IsTodoButtonVisible)
                IsTodoPanelOpen = false;
        });
    }

    private void OnTodoChanged(object? sender, EventArgs e)
    {
        if (_isRefreshing || _suppressTodoChanged)
            return;

        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            SafeFireAndForget(LoadTodosAsync()));
    }

    private async void SafeFireAndForget(Task task)
    {
        try { await task; }
        catch (Exception ex) { _logger.LogError(ex, "Background operation failed"); }
    }

    public async Task LoadTodosAsync()
    {
        if (_isRefreshing)
            return;

        try
        {
            _isRefreshing = true;
            IsLoading = true;

            var pending = await _todoService.GetPendingAsync();
            var completed = await _todoService.GetCompletedAsync();
            var completedToday = await _todoService.GetCompletedTodayAsync();

            PendingTodos.Clear();
            foreach (var todo in pending)
                PendingTodos.Add(todo);

            CompletedTodos.Clear();
            foreach (var todo in completed)
                CompletedTodos.Add(todo);

            CompletedTodayCount = completedToday.Count;
            PendingCount = pending.Count;
            TotalTodayCount = CompletedTodayCount + PendingCount;
            UpdateProgress();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load todos");
        }
        finally
        {
            IsLoading = false;
            _isRefreshing = false;
        }
    }

    private async Task ExecuteAddTodoAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTodoTitle))
            return;

        try
        {
            var todo = await _todoService.CreateAsync(
                NewTodoTitle.Trim(),
                NewTodoPriority,
                dueDate: NewTodoDueDate);

            PendingTodos.Insert(GetInsertIndex(todo), todo);

            NewTodoTitle = string.Empty;
            NewTodoPriority = TodoPriority.Medium;
            NewTodoDueDate = null;

            PendingCount = PendingTodos.Count;
            TotalTodayCount = CompletedTodayCount + PendingCount;
            UpdateProgress();

            _snackbarService.Show(_localizationService["Msg_Todo_Added"], _localizationService.Format("Msg_Todo_TodoAdded", todo.Title),
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add todo");
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_Todo_AddFailed", ex.Message));
        }
    }

    private bool CanAddTodo() => !string.IsNullOrWhiteSpace(NewTodoTitle);

    private async Task ExecuteCompleteTodoAsync(TodoItem? todo)
    {
        if (todo is null)
            return;

        try
        {
            _suppressTodoChanged = true;
            await _todoService.CompleteAsync(todo.Id);

            todo.Status = TodoStatus.Completed;
            todo.CompletedAt = DateTime.Now;

            PendingTodos.Remove(todo);
            CompletedTodos.Insert(0, todo);

            CompletedTodayCount++;
            PendingCount = PendingTodos.Count;
            TotalTodayCount = CompletedTodayCount + PendingCount;
            UpdateProgress();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete todo {Id}", todo.Id);
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_Todo_CompleteFailed", ex.Message));
        }
        finally
        {
            _suppressTodoChanged = false;
        }
    }

    private async Task ExecuteUncompleteTodoAsync(TodoItem? todo)
    {
        if (todo is null)
            return;

        try
        {
            _suppressTodoChanged = true;
            await _todoService.UncompleteAsync(todo.Id);

            todo.Status = TodoStatus.Pending;
            todo.CompletedAt = null;

            CompletedTodos.Remove(todo);
            PendingTodos.Insert(GetInsertIndex(todo), todo);

            CompletedTodayCount = Math.Max(0, CompletedTodayCount - 1);
            PendingCount = PendingTodos.Count;
            TotalTodayCount = CompletedTodayCount + PendingCount;
            UpdateProgress();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uncomplete todo {Id}", todo.Id);
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_Todo_UncompleteFailed", ex.Message));
        }
        finally
        {
            _suppressTodoChanged = false;
        }
    }

    private async Task ExecuteDeleteTodoAsync(TodoItem? todo)
    {
        if (todo is null)
            return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Msg_Todo_DeleteTitle"],
            _localizationService.Format("Msg_Todo_DeleteConfirm", todo.Title));

        if (!confirmed)
            return;

        try
        {
            await _todoService.DeleteAsync(todo.Id);

            if (todo.Status == TodoStatus.Pending)
            {
                PendingTodos.Remove(todo);
                PendingCount = PendingTodos.Count;
            }
            else
            {
                CompletedTodos.Remove(todo);
            }

            TotalTodayCount = CompletedTodayCount + PendingCount;
            UpdateProgress();

            if (EditingTodo == todo)
            {
                IsEditing = false;
                EditingTodo = null;
            }

            _snackbarService.Show(_localizationService["Msg_Todo_Deleted"], _localizationService.Format("Msg_Todo_TodoDeleted", todo.Title),
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete todo {Id}", todo.Id);
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_Todo_DeleteFailed", ex.Message));
        }
    }

    private void ExecuteStartEdit(TodoItem? todo)
    {
        if (todo is null)
            return;

        EditingTodo = todo;
        EditTitle = todo.Title;
        EditNotes = todo.Notes ?? string.Empty;
        EditPriority = todo.Priority;
        EditDueDate = todo.DueDate;
        IsEditing = true;
        SaveEditCommand.NotifyCanExecuteChanged();
    }

    private bool CanSaveEdit() => IsEditing && EditingTodo is not null && !string.IsNullOrWhiteSpace(EditTitle);

    private async Task ExecuteSaveEditAsync()
    {
        if (EditingTodo is null)
            return;

        try
        {
            EditingTodo.Title = EditTitle.Trim();
            EditingTodo.Notes = string.IsNullOrWhiteSpace(EditNotes) ? null : EditNotes.Trim();
            EditingTodo.Priority = EditPriority;
            EditingTodo.DueDate = EditDueDate;

            await _todoService.UpdateAsync(EditingTodo);

            IsEditing = false;
            EditingTodo = null;

            // Refresh to reflect priority reordering
            await LoadTodosAsync();

            _snackbarService.Show(_localizationService["Msg_Todo_Updated"], _localizationService["Msg_Todo_TodoUpdated"],
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update todo");
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_Todo_UpdateFailed", ex.Message));
        }
    }

    private void ExecuteCancelEdit()
    {
        IsEditing = false;
        EditingTodo = null;
    }

    private void ExecuteNavigateToTodoView()
    {
        IsTodoPanelOpen = false;
        _navigationService.NavigateTo<TodoViewModel>();
    }

    private void UpdateProgress()
    {
        ProgressPercentage = TotalTodayCount > 0
            ? (double)CompletedTodayCount / TotalTodayCount * 100.0
            : 0;
        ProgressText = _localizationService.Format("Msg_Todo_ProgressText", CompletedTodayCount, TotalTodayCount);
    }

    /// <summary>
    /// Gets the insertion index for a todo item in the pending list,
    /// maintaining priority order (High first) then CreatedAt order.
    /// </summary>
    private int GetInsertIndex(TodoItem item)
    {
        for (var i = 0; i < PendingTodos.Count; i++)
        {
            var existing = PendingTodos[i];
            if (item.Priority > existing.Priority)
                return i;
            if (item.Priority == existing.Priority && item.CreatedAt < existing.CreatedAt)
                return i;
        }
        return PendingTodos.Count;
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NewTodoTitle))
            AddTodoCommand.NotifyCanExecuteChanged();

        if (e.PropertyName is nameof(IsEditing) or nameof(EditTitle))
            SaveEditCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        PropertyChanged -= OnPropertyChanged;
        _todoService.TodoChanged -= OnTodoChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        GC.SuppressFinalize(this);
    }
}
