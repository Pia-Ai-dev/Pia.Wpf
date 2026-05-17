using CommunityToolkit.Mvvm.ComponentModel;
using Pia.Models;

namespace Pia.ViewModels.Models;

public partial class TodoEditModel : ObservableObject
{
    [ObservableProperty]
    private Guid _id;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private TodoPriority _priority = TodoPriority.Medium;

    [ObservableProperty]
    private DateTime? _dueDate;

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
