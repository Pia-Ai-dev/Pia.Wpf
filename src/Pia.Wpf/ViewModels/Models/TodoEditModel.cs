using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pia.Models;

namespace Pia.ViewModels.Models;

public partial class TodoEditModel : ObservableObject
{
    [ObservableProperty]
    private Guid _id;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    private string _notes = string.Empty;

    [ObservableProperty]
    private TodoPriority _priority = TodoPriority.Medium;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DueDateLabel))]
    private DateTime? _dueDate;

    /// <summary>The dialog opens reading and only switches on the pencil, so opening a task cannot edit it.</summary>
    [ObservableProperty]
    private bool _isEditing;

    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    public string DueDateLabel => DueDate is { } due ? due.ToString("d") : "—";

    [RelayCommand]
    private void BeginEdit() => IsEditing = true;

    public IReadOnlyList<TodoPriority> Priorities { get; } =
        [TodoPriority.Low, TodoPriority.Medium, TodoPriority.High];

    public static TodoEditModel FromTodo(TodoItem todo) => new()
    {
        Id = todo.Id,
        Title = todo.Title,
        Notes = todo.Notes ?? string.Empty,
        Priority = todo.Priority,
        DueDate = todo.DueDate,
    };

    public void ApplyTo(TodoItem todo)
    {
        todo.Title = Title.Trim();
        todo.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
        todo.Priority = Priority;
        todo.DueDate = DueDate;
    }
}
