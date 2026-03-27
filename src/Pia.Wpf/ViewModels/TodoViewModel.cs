using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;

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
    private readonly IVoiceInputService _voiceInputService;
    private readonly IKanbanColumnService _columnService;
    private bool _disposed;
    private bool _isRefreshing;
    private bool _suppressTodoChanged;

    [ObservableProperty]
    private ObservableCollection<TodoItem> _pendingTodos = new();

    [ObservableProperty]
    private ObservableCollection<TodoItem> _completedTodos = new();

    [ObservableProperty]
    private ObservableCollection<KanbanColumnViewModel> _columns = new();

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
    public IAsyncRelayCommand RecordTodoCommand { get; }
    public IAsyncRelayCommand<TodoItem> CompleteTodoCommand { get; }
    public IAsyncRelayCommand<TodoItem> UncompleteTodoCommand { get; }
    public IAsyncRelayCommand<TodoItem> DeleteTodoCommand { get; }
    public IRelayCommand<TodoItem> StartEditCommand { get; }
    public IAsyncRelayCommand SaveEditCommand { get; }
    public IRelayCommand CancelEditCommand { get; }
    public IRelayCommand ToggleTodoPanelCommand { get; }
    public IRelayCommand NavigateToTodoViewCommand { get; }
    public IAsyncRelayCommand AddColumnCommand { get; }
    public IAsyncRelayCommand<KanbanColumnViewModel> DeleteColumnCommand { get; }
    public IAsyncRelayCommand<KanbanColumnViewModel> SetDefaultViewColumnCommand { get; }

    public TodoViewModel(
        ILogger<TodoViewModel> logger,
        ITodoService todoService,
        IDialogService dialogService,
        Wpf.Ui.ISnackbarService snackbarService,
        Navigation.INavigationService navigationService,
        ISettingsService settingsService,
        ILocalizationService localizationService,
        IVoiceInputService voiceInputService,
        IKanbanColumnService columnService)
    {
        _logger = logger;
        _todoService = todoService;
        _dialogService = dialogService;
        _snackbarService = snackbarService;
        _navigationService = navigationService;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _voiceInputService = voiceInputService;
        _columnService = columnService;

        RefreshCommand = new AsyncRelayCommand(LoadTodosAsync);
        AddTodoCommand = new AsyncRelayCommand(ExecuteAddTodoAsync, CanAddTodo);
        RecordTodoCommand = new AsyncRelayCommand(ExecuteRecordTodoAsync);
        CompleteTodoCommand = new AsyncRelayCommand<TodoItem>(ExecuteCompleteTodoAsync);
        UncompleteTodoCommand = new AsyncRelayCommand<TodoItem>(ExecuteUncompleteTodoAsync);
        DeleteTodoCommand = new AsyncRelayCommand<TodoItem>(ExecuteDeleteTodoAsync);
        StartEditCommand = new RelayCommand<TodoItem>(ExecuteStartEdit);
        SaveEditCommand = new AsyncRelayCommand(ExecuteSaveEditAsync, CanSaveEdit);
        CancelEditCommand = new RelayCommand(ExecuteCancelEdit);
        ToggleTodoPanelCommand = new RelayCommand(() => IsTodoPanelOpen = !IsTodoPanelOpen);
        NavigateToTodoViewCommand = new RelayCommand(ExecuteNavigateToTodoView);
        AddColumnCommand = new AsyncRelayCommand(ExecuteAddColumnAsync);
        DeleteColumnCommand = new AsyncRelayCommand<KanbanColumnViewModel>(ExecuteDeleteColumnAsync);
        SetDefaultViewColumnCommand = new AsyncRelayCommand<KanbanColumnViewModel>(ExecuteSetDefaultViewAsync);

        PropertyChanged += OnPropertyChanged;
        _todoService.TodoChanged += OnTodoChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _columnService.ColumnsChanged += OnColumnsChanged;

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

    private void OnColumnsChanged(object? sender, EventArgs e)
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

            var allColumns = await _columnService.GetAllAsync();
            var completedToday = await _todoService.GetCompletedTodayAsync();

            Columns.Clear();
            PendingTodos.Clear();
            CompletedTodos.Clear();

            KanbanColumnViewModel? defaultViewColumnVm = null;
            KanbanColumnViewModel? closedColumnVm = null;

            foreach (var column in allColumns)
            {
                var todos = await _todoService.GetByColumnAsync(column.Id);
                var vm = new KanbanColumnViewModel(column);
                foreach (var todo in todos)
                    vm.Todos.Add(todo);

                Columns.Add(vm);

                if (column.IsDefaultView)
                    defaultViewColumnVm = vm;
                if (column.IsClosedColumn)
                    closedColumnVm = vm;
            }

            // Populate PendingTodos for the side panel (default view column)
            if (defaultViewColumnVm is not null)
            {
                foreach (var todo in defaultViewColumnVm.Todos)
                    PendingTodos.Add(todo);
            }

            // Populate CompletedTodos from the closed column
            if (closedColumnVm is not null)
            {
                foreach (var todo in closedColumnVm.Todos)
                    CompletedTodos.Add(todo);
            }

            CompletedTodayCount = completedToday.Count;
            PendingCount = PendingTodos.Count;
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

            // Add to the default view column in the kanban board
            var defaultColumnVm = Columns.FirstOrDefault(c => c.IsDefaultView);
            if (defaultColumnVm is not null)
                defaultColumnVm.Todos.Add(todo);

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

            // Move in kanban columns
            var sourceColumnVm = Columns.FirstOrDefault(c => c.Todos.Contains(todo));
            var closedColumnVm = Columns.FirstOrDefault(c => c.IsClosedColumn);
            sourceColumnVm?.Todos.Remove(todo);
            closedColumnVm?.Todos.Insert(0, todo);

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

            // Move in kanban columns
            var closedColumnVm = Columns.FirstOrDefault(c => c.IsClosedColumn);
            var defaultColumnVm = Columns.FirstOrDefault(c => c.IsDefaultView);
            closedColumnVm?.Todos.Remove(todo);
            defaultColumnVm?.Todos.Add(todo);

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

            // Remove from kanban column
            var columnVm = Columns.FirstOrDefault(c => c.Todos.Contains(todo));
            columnVm?.Todos.Remove(todo);

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
    /// New todos are appended to the end of the list (SortOrder-based).
    /// </summary>
    private int GetInsertIndex(TodoItem item)
    {
        return PendingTodos.Count;
    }

    private async Task ExecuteRecordTodoAsync()
    {
        try
        {
            var transcription = await _voiceInputService.CaptureVoiceInputAsync();
            if (!string.IsNullOrWhiteSpace(transcription))
            {
                NewTodoTitle = string.IsNullOrWhiteSpace(NewTodoTitle)
                    ? transcription
                    : $"{NewTodoTitle.TrimEnd()} {transcription}";
                AddTodoCommand.NotifyCanExecuteChanged();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to capture voice input for todo");
        }
    }

    public async Task ReorderTodosAsync(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex || oldIndex < 0 || newIndex < 0
            || oldIndex >= PendingTodos.Count || newIndex >= PendingTodos.Count)
            return;

        // Save original sort orders for revert
        var originalOrders = PendingTodos.Select(t => (t.Id, t.SortOrder)).ToList();

        PendingTodos.Move(oldIndex, newIndex);

        // Recalculate sequential sort order
        var updates = new List<(Guid Id, int SortOrder)>();
        for (var i = 0; i < PendingTodos.Count; i++)
        {
            PendingTodos[i].SortOrder = i;
            updates.Add((PendingTodos[i].Id, i));
        }

        try
        {
            await _todoService.UpdateSortOrderAsync(updates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist reorder");
            // Revert the move and restore original sort orders
            PendingTodos.Move(newIndex, oldIndex);
            foreach (var (id, sortOrder) in originalOrders)
            {
                var todo = PendingTodos.FirstOrDefault(t => t.Id == id);
                if (todo is not null)
                    todo.SortOrder = sortOrder;
            }
        }
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NewTodoTitle))
            AddTodoCommand.NotifyCanExecuteChanged();

        if (e.PropertyName is nameof(IsEditing) or nameof(EditTitle))
            SaveEditCommand.NotifyCanExecuteChanged();
    }

    private async Task ExecuteAddColumnAsync()
    {
        var name = await _dialogService.ShowInputDialogAsync(
            _localizationService["Kanban_AddColumn"],
            _localizationService["Kanban_ColumnNamePrompt"]);

        if (string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            _suppressTodoChanged = true;
            var column = await _columnService.CreateAsync(name.Trim());
            var vm = new KanbanColumnViewModel(column);

            // Insert before the Closed column
            var closedIndex = -1;
            for (var i = 0; i < Columns.Count; i++)
            {
                if (Columns[i].IsClosedColumn)
                {
                    closedIndex = i;
                    break;
                }
            }

            if (closedIndex >= 0)
                Columns.Insert(closedIndex, vm);
            else
                Columns.Add(vm);

            _snackbarService.Show(
                _localizationService["Kanban_ColumnAdded"],
                _localizationService.Format("Kanban_ColumnAddedDetail", name.Trim()),
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add column");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"], ex.Message);
        }
        finally
        {
            _suppressTodoChanged = false;
        }
    }

    private async Task ExecuteDeleteColumnAsync(KanbanColumnViewModel? columnVm)
    {
        if (columnVm is null || columnVm.IsClosedColumn)
            return;

        if (columnVm.Todos.Count > 0)
        {
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService["Kanban_ColumnNotEmpty"]);
            return;
        }

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Kanban_DeleteColumn"],
            _localizationService.Format("Kanban_DeleteColumnConfirm", columnVm.Name));

        if (!confirmed)
            return;

        try
        {
            await _columnService.DeleteAsync(columnVm.Id);
            Columns.Remove(columnVm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete column {Id}", columnVm.Id);
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"], ex.Message);
        }
    }

    private async Task ExecuteSetDefaultViewAsync(KanbanColumnViewModel? columnVm)
    {
        if (columnVm is null || columnVm.IsClosedColumn || columnVm.IsDefaultView)
            return;

        try
        {
            await _columnService.SetDefaultViewAsync(columnVm.Id);

            foreach (var col in Columns)
                col.IsDefaultView = col.Id == columnVm.Id;

            // Refresh PendingTodos for the side panel
            PendingTodos.Clear();
            foreach (var todo in columnVm.Todos)
                PendingTodos.Add(todo);

            PendingCount = PendingTodos.Count;
            TotalTodayCount = CompletedTodayCount + PendingCount;
            UpdateProgress();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set default view column");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"], ex.Message);
        }
    }

    public async Task MoveTodoToColumnAsync(TodoItem todo, Guid targetColumnId, int newIndex)
    {
        try
        {
            _suppressTodoChanged = true;
            await _todoService.MoveToColumnAsync(todo.Id, targetColumnId);

            // Update in-memory kanban columns (use ID-based lookup to handle collection reloads)
            var sourceColumnVm = Columns.FirstOrDefault(c => c.Todos.Any(t => t.Id == todo.Id));
            var targetColumnVm = Columns.FirstOrDefault(c => c.Id == targetColumnId);
            if (sourceColumnVm is not null)
            {
                var itemInSource = sourceColumnVm.Todos.FirstOrDefault(t => t.Id == todo.Id);
                if (itemInSource is not null)
                    sourceColumnVm.Todos.Remove(itemInSource);
            }

            if (targetColumnVm is not null)
            {
                // Update todo status based on target column
                if (targetColumnVm.IsClosedColumn)
                {
                    todo.Status = TodoStatus.Completed;
                    todo.CompletedAt = DateTime.Now;
                }
                else if (sourceColumnVm?.IsClosedColumn == true)
                {
                    todo.Status = TodoStatus.Pending;
                    todo.CompletedAt = null;
                }

                todo.ColumnId = targetColumnId;
                var insertAt = Math.Min(newIndex, targetColumnVm.Todos.Count);
                targetColumnVm.Todos.Insert(insertAt, todo);
            }

            // Update PendingTodos/CompletedTodos for side panel
            PendingTodos.Clear();
            CompletedTodos.Clear();
            var defaultVm = Columns.FirstOrDefault(c => c.IsDefaultView);
            var closedVm = Columns.FirstOrDefault(c => c.IsClosedColumn);
            if (defaultVm is not null)
                foreach (var t in defaultVm.Todos)
                    PendingTodos.Add(t);
            if (closedVm is not null)
                foreach (var t in closedVm.Todos)
                    CompletedTodos.Add(t);

            PendingCount = PendingTodos.Count;
            TotalTodayCount = CompletedTodayCount + PendingCount;
            UpdateProgress();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move todo {Id} to column {ColumnId}", todo.Id, targetColumnId);
            await LoadTodosAsync(); // Reload on error
        }
        finally
        {
            _suppressTodoChanged = false;
        }
    }

    public async Task ReorderWithinColumnAsync(Guid columnId, int oldIndex, int newIndex)
    {
        var columnVm = Columns.FirstOrDefault(c => c.Id == columnId);
        if (columnVm is null || oldIndex == newIndex || oldIndex < 0 || newIndex < 0
            || oldIndex >= columnVm.Todos.Count || newIndex >= columnVm.Todos.Count)
            return;

        var originalOrders = columnVm.Todos.Select(t => (t.Id, t.SortOrder)).ToList();

        columnVm.Todos.Move(oldIndex, newIndex);

        var updates = new List<(Guid Id, int SortOrder)>();
        for (var i = 0; i < columnVm.Todos.Count; i++)
        {
            columnVm.Todos[i].SortOrder = i;
            updates.Add((columnVm.Todos[i].Id, i));
        }

        try
        {
            await _todoService.UpdateSortOrderAsync(updates);

            // If this is the default view column, also update PendingTodos
            if (columnVm.IsDefaultView)
            {
                PendingTodos.Clear();
                foreach (var todo in columnVm.Todos)
                    PendingTodos.Add(todo);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist reorder in column {ColumnId}", columnId);
            columnVm.Todos.Move(newIndex, oldIndex);
            foreach (var (id, sortOrder) in originalOrders)
            {
                var todo = columnVm.Todos.FirstOrDefault(t => t.Id == id);
                if (todo is not null)
                    todo.SortOrder = sortOrder;
            }
        }
    }

    public async Task RenameColumnAsync(KanbanColumnViewModel columnVm, string newName)
    {
        if (columnVm.IsClosedColumn || string.IsNullOrWhiteSpace(newName))
            return;

        try
        {
            await _columnService.RenameAsync(columnVm.Id, newName.Trim());
            columnVm.Name = newName.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename column {Id}", columnVm.Id);
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"], ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        PropertyChanged -= OnPropertyChanged;
        _todoService.TodoChanged -= OnTodoChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _columnService.ColumnsChanged -= OnColumnsChanged;
        GC.SuppressFinalize(this);
    }
}
